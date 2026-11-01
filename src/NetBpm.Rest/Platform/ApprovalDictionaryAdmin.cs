using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;
using NetBpm.Workflow;
using BpmnProcess = NetBpm.Bpmn.Model.Process;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 管理面：<b>审批语义字典的写路径</b>（<c>/admin/v1/dictionaries</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 字典读路径在 <see cref="IApprovalVocabulary"/>（把三层压成一个答案），
/// 这里管的是「源数据」：某个流程的某个选项在业务上算什么。
/// </para>
/// <para>
/// 🔑 <b>写入前要按流程定义校验，这不是洁癖</b>。字典按 optionId 配，而解析时只按 BPMN
/// 声明的选项逐项查字典——写进一个定义里不存在的 optionId，它永远不会被读到：
/// 管理端看到的是一张「配置完整」的表，实际行为一点没变。这种「配好了但不工作」
/// 比没配更难发现，所以在写入时就拒掉。同理，节点 id 也必须是定义里真实存在的用户任务。
/// </para>
/// <para>
/// <b>缓存失效只在本地进程</b>：写成功后清掉本进程的 <see cref="IApprovalVocabulary"/> 缓存，
/// 别的副本最迟一个 TTL（10 分钟）后生效。字典是低频运营配置，
/// 为它上分布式失效通道（Redis pub/sub 或版本号）不划算；要立刻全量生效就重启或等 TTL。
/// </para>
/// </remarks>
public interface IApprovalDictionaryAdmin
{
    /// <summary>
    /// 查某流程当前生效版本声明的审批选项（字典维护的「可选范围」）。
    /// </summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>流程不存在或没有生效定义时返回 <see langword="null"/>。</returns>
    ValueTask<DeclaredApprovalOptionsView?> GetDeclaredOptionsAsync(
        string processKey,
        CancellationToken ct = default);

    /// <summary>
    /// 新增或覆盖一条映射。
    /// </summary>
    /// <param name="tenantId">租户（取当前管理身份所属租户）。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="optionId">选项 id。</param>
    /// <param name="request">映射内容。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="PlatformActionRejectedException">
    /// 流程不存在（<c>process.not_found</c>）、没有生效定义（<c>process.not_deployed</c>）、
    /// 节点或选项不在定义里（<c>dictionary.*</c>）、语义被平台保留（<c>dictionary.semantic_reserved</c>）。
    /// </exception>
    ValueTask<ApprovalDictionaryEntry> UpsertAsync(
        string tenantId,
        string processKey,
        string optionId,
        ApprovalDictionaryUpsertRequest request,
        CancellationToken ct = default);

    /// <summary>删除一条映射；不存在返回 <see langword="false"/>。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="nodeId">环节 id；为空表示流程级默认。</param>
    /// <param name="optionId">选项 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<bool> DeleteAsync(
        string tenantId,
        string processKey,
        string? nodeId,
        string optionId,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ApprovalDictionaryAdmin : IApprovalDictionaryAdmin
{
    /// <summary>按钮文案上限（与列宽一致：<c>label varchar(128)</c>）。</summary>
    private const int MaxLabelLength = 128;

    /// <summary>附加属性条数上限（防「把整份业务配置塞进字典」）。</summary>
    private const int MaxAttributes = 32;

    private readonly IApprovalDictionaryStore _store;
    private readonly IProcessEngine _engine;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ApprovalDictionaryAdmin> _logger;

    /// <summary>构造。</summary>
    /// <param name="store">字典表。</param>
    /// <param name="engine">引擎（读流程定义与模型：校验选项与节点是否存在）。</param>
    /// <param name="cache">语义解析的缓存（写完要立刻失效）。</param>
    /// <param name="logger">日志。</param>
    public ApprovalDictionaryAdmin(
        IApprovalDictionaryStore store,
        IProcessEngine engine,
        IMemoryCache cache,
        ILogger<ApprovalDictionaryAdmin> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _engine = engine;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<DeclaredApprovalOptionsView?> GetDeclaredOptionsAsync(
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        var definition = await FindCurrentDefinitionAsync(processKey, tenantId: null, ct).ConfigureAwait(false);

        if (definition is null)
        {
            return null;
        }

        var process = await ReadProcessAsync(definition, ct).ConfigureAwait(false);

        return new DeclaredApprovalOptionsView(
            definition.Key,
            definition.Id,
            definition.Version,
            process is null ? [] : ReadDeclaredOptions(process));
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalDictionaryEntry> UpsertAsync(
        string tenantId,
        string processKey,
        string optionId,
        ApprovalDictionaryUpsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Semantic == ApprovalSemantic.Closed)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.DictionarySemanticReserved,
                "语义被平台保留",
                422,
                "CLOSED 只由平台在「结论三级回落」的最后一级产生（流程结束但没有任何审批决定），"
                + "不能在字典里声明——否则审计上分不清「流程说通过了」与「平台只知道它结束了」。");
        }

        var label = Requests.TrimToNull(request.Label);

        if (label is { Length: > MaxLabelLength })
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.RequestMalformed,
                "按钮文案过长",
                400,
                $"label 最多 {MaxLabelLength} 个字符（与 nb_approval_dictionary.label 列宽一致）。");
        }

        var attributes = NormalizeAttributes(request.Attributes);
        var nodeId = Requests.TrimToNull(request.NodeId);

        var definition = await FindCurrentDefinitionAsync(processKey, tenantId, ct).ConfigureAwait(false)
            ?? throw new PlatformActionRejectedException(
                ErrorCodes.ProcessNotFound,
                "流程不存在",
                404,
                $"流程 '{processKey}' 没有生效定义。字典的粒度是「某流程的审批口径」，"
                + "key 打错时写进去也没人读。");

        var process = await ReadProcessAsync(definition, ct).ConfigureAwait(false)
            ?? throw new PlatformActionRejectedException(
                ErrorCodes.ProcessNotDeployed,
                "流程定义无法解析",
                409,
                $"流程 '{processKey}' 的定义 '{definition.Id}' 里没有可解析的主流程，无法校验选项。");

        var declared = ReadDeclaredOptions(process);

        // 节点校验：必须是真的用户任务，且该节点上确实有审批选项
        if (nodeId is not null && declared.All(node => !string.Equals(node.ActivityId, nodeId, StringComparison.Ordinal)))
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.DictionaryNodeNotFound,
                "环节不存在或没有审批选项",
                422,
                $"环节 '{nodeId}' 不是流程 '{processKey}' 里带 approvalResult 的用户任务。"
                + $"可用的环节：{DescribeDeclaredNodes(declared)}。"
                + "不限定环节时请省略 nodeId（流程级默认）。");
        }

        // 选项校验：流程级要求「任一节点声明过」，节点级要求「该节点声明过」
        var candidates = nodeId is null
            ? declared.SelectMany(node => node.Options)
            : declared.Where(node => string.Equals(node.ActivityId, nodeId, StringComparison.Ordinal)).SelectMany(node => node.Options);

        if (candidates.All(option => !string.Equals(option.OptionId, optionId, StringComparison.Ordinal)))
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.DictionaryOptionNotDeclared,
                "选项不在流程定义里",
                422,
                $"流程 '{processKey}' 的{(nodeId is null ? "任何环节" : $"环节 '{nodeId}'")}都没有声明选项 '{optionId}'"
                + "（来源是 activiti:formProperty[@id='approvalResult']/activiti:value）。"
                + "写进去永远不会被读到——先改流程定义，再配语义。");
        }

        var allowedTargets = NormalizeTargets(request.AllowedTargetNodes, process, processKey);

        var now = DateTimeOffset.UtcNow;

        var stored = await _store
            .UpsertAsync(
                new ApprovalDictionaryEntry(
                    tenantId,
                    processKey,
                    nodeId,
                    optionId,
                    request.Semantic,
                    // 统计口径由语义推导：接收它就等于允许「语义是通过、口径是负向」
                    ApprovalSemantics.OutcomeClassOf(request.Semantic),
                    label,
                    request.RequiresComment,
                    request.RequiresTargetNode,
                    allowedTargets,
                    attributes,
                    now,
                    now),
                ct)
            .ConfigureAwait(false);

        ApprovalVocabularyCache.Invalidate(_cache, tenantId, processKey);

        _logger.LogInformation(
            "字典已写入：{TenantId}/{ProcessKey}/{NodeId}/{OptionId} → {Semantic}。",
            tenantId,
            processKey,
            nodeId ?? "(流程级)",
            optionId,
            request.Semantic);

        return stored;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(
        string tenantId,
        string processKey,
        string? nodeId,
        string optionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);

        var removed = await _store
            .DeleteAsync(tenantId, processKey, Requests.TrimToNull(nodeId), optionId, ct)
            .ConfigureAwait(false);

        if (removed)
        {
            // 删掉一行也是「解析结果变了」，不失效缓存会让调用方继续按旧语义跑最多一个 TTL
            ApprovalVocabularyCache.Invalidate(_cache, tenantId, processKey);
        }

        return removed;
    }

    /// <summary>
    /// 取当前生效定义：版本最大者；同版本时优先本租户的定义（与目录的合并口径一致）。
    /// </summary>
    private async ValueTask<DeployedProcessDefinition?> FindCurrentDefinitionAsync(
        string processKey,
        string? tenantId,
        CancellationToken ct)
    {
        var all = await _engine.FindProcessDefinitionsAsync(tenantId: null, ct).ConfigureAwait(false);

        return all
            .Where(definition => string.Equals(definition.Key, processKey, StringComparison.Ordinal))
            .GroupBy(definition => definition.Version)
            .Select(group => group
                // 同版本多租户时，本租户的定义胜出（否则「我改的字典配的是别人租户的定义」）
                .OrderByDescending(definition => tenantId is not null
                    && string.Equals(definition.TenantId, tenantId, StringComparison.Ordinal))
                .First())
            .OrderByDescending(definition => definition.Version)
            .FirstOrDefault();
    }

    private async ValueTask<BpmnProcess?> ReadProcessAsync(DeployedProcessDefinition definition, CancellationToken ct)
    {
        var model = await _engine.GetBpmnModelAsync(definition.Id, ct).ConfigureAwait(false);

        return model?.GetMainProcess();
    }

    /// <summary>
    /// 列出定义里声明的审批选项（按环节）。
    /// </summary>
    /// <remarks>
    /// 只收带 <c>approvalResult</c> 表单属性的用户任务：别的环节没有「审批选项」这个概念。
    /// 与 <c>TaskCatalog</c> 读待办选项集用的是同一个约定（<see cref="ApprovalResults.FormPropertyId"/>），
    /// 两处口径必须一致——否则会出现「管理端能配、待办里看不到」的映射。
    /// </remarks>
    private static List<DeclaredNodeApprovalOptions> ReadDeclaredOptions(BpmnProcess process)
    {
        var nodes = new List<DeclaredNodeApprovalOptions>();

        foreach (var userTask in process.FindFlowElementsOfType<UserTask>())
        {
            var property = userTask.FormProperties
                .FirstOrDefault(item => string.Equals(
                    item.Id, ApprovalResults.FormPropertyId, StringComparison.Ordinal));

            if (property is null)
            {
                continue;
            }

            var activityId = userTask.Id;

            if (string.IsNullOrWhiteSpace(activityId))
            {
                continue;
            }

            var options = property.Values
                .Where(value => !string.IsNullOrWhiteSpace(value.Id))
                .Select(value => new DeclaredApprovalOption(value.Id!, value.Name))
                .ToArray();

            if (options.Length == 0)
            {
                continue;
            }

            nodes.Add(new DeclaredNodeApprovalOptions(activityId, userTask.Name, options));
        }

        return nodes;
    }

    /// <summary>目标节点白名单：必须是定义里真实存在的元素（拼错的节点 id 只会让提交时 422）。</summary>
    private static List<string>? NormalizeTargets(
        IReadOnlyList<string>? targets,
        BpmnProcess process,
        string processKey)
    {
        if (targets is null || targets.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>(targets.Count);
        var unknown = new List<string>();

        foreach (var target in targets)
        {
            var trimmed = Requests.TrimToNull(target);

            if (trimmed is null || normalized.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            if (process.GetFlowElement(trimmed) is null)
            {
                unknown.Add(trimmed);
                continue;
            }

            normalized.Add(trimmed);
        }

        if (unknown.Count > 0)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.DictionaryTargetNodeUnknown,
                "目标节点不存在",
                422,
                $"流程 '{processKey}' 里没有这些节点：{string.Join("、", unknown)}。"
                + "白名单里的 id 必须与 BPMN 元素 id 一致。");
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static Dictionary<string, string>? NormalizeAttributes(
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return null;
        }

        if (attributes.Count > MaxAttributes)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.RequestMalformed,
                "附加属性过多",
                400,
                $"attributes 最多 {MaxAttributes} 条。字典是审批口径的声明，不是业务配置仓库。");
        }

        return new Dictionary<string, string>(attributes, StringComparer.Ordinal);
    }

    private static string DescribeDeclaredNodes(List<DeclaredNodeApprovalOptions> declared) =>
        declared.Count == 0
            ? "（定义里没有任何环节声明审批选项）"
            : string.Join("、", declared.Select(node => node.ActivityId));
}

/// <summary>请求字段的清洗（空串与纯空白一律视为「没填」）。</summary>
internal static class Requests
{
    /// <summary>去空白；空则返回 <see langword="null"/>。</summary>
    internal static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

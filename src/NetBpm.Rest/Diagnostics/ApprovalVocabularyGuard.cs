using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.Rest.Diagnostics;

/// <summary>
/// 部署后自检：流程定义里 <c>approvalResult</c> 的每一个选项，都能解析出平台语义。
/// </summary>
/// <remarks>
/// <para>
/// 这是方案 §4.2 里「标准可执行而非靠自觉」的落点：调用方永远按 <c>semantic</c> /
/// <c>outcomeClass</c> 分支，而这两个值要么来自默认字典（<see cref="ApprovalSemantics"/>），
/// 要么来自流程在 <c>nb_approval_dictionary</c> 里的声明。一个选项两处都没有，
/// 就会静默落成 <see cref="ApprovalSemantic.Custom"/>——「退回」被当成自定义就会走错分支。
/// </para>
/// <para>
/// 两级口径：
/// <list type="bullet">
///   <item><description><b>默认（宽容）</b>：只告警不拦截，保证旧 BPMN 不改一字仍能跑（回归红线）。</description></item>
///   <item><description><b>严格（rejectUnmapped=true）</b>：凡是无映射的选项一律拒绝部署，
///     报错含节点 id 与选项 id。</description></item>
/// </list>
/// </para>
/// <para>
/// 放在 Rest 而不是 Workflow 的原因：守卫要读 <c>nb_approval_dictionary</c>
/// （<see cref="IApprovalDictionaryStore"/>，在 Persistence）与 <see cref="ApprovalSemantics"/>
/// （在 Platform.Contracts），而 <c>NetBpm.Workflow</c> 的分层约束是「只依赖 Bpmn.Model + Engine + Runtime」。
/// </para>
/// </remarks>
public static class ApprovalVocabularyGuard
{
    /// <summary>审批结果表单字段名（与 BPMN 的 <c>activiti:formProperty id="approvalResult"</c> 一致）。</summary>
    public const string ApprovalFormPropertyId = "approvalResult";

    /// <summary>自检规则名（与部署期其它校验规则同一命名风格）。</summary>
    public const string RuleName = "approval-vocabulary";

    /// <summary>
    /// 校验流程定义里全部审批选项都能解析出语义。
    /// </summary>
    /// <param name="services">服务提供者（取字典 store）。</param>
    /// <param name="engine">引擎门面。</param>
    /// <param name="processDefinitionId">刚部署的流程定义 id。</param>
    /// <param name="processKey">流程 key（= BPMN <c>process/@id</c>）。</param>
    /// <param name="tenantId">部署租户；为空表示不查流程声明（只用默认字典）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="rejectUnmapped">严格模式：无映射的选项拒绝部署（抛异常）。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>无映射的选项（提示级）；全部有映射时返回 <see langword="null"/>。</returns>
    /// <exception cref="InvalidOperationException">严格模式下存在无映射的选项。</exception>
    public static async ValueTask<IReadOnlyList<ValidationError>?> VerifyAsync(
        IServiceProvider services,
        IProcessEngine engine,
        string processDefinitionId,
        string processKey,
        string? tenantId,
        ILogger logger,
        bool rejectUnmapped = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(logger);

        var model = await engine.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false);
        var process = model?.GetMainProcess();

        // 声明 = 「环节 id → 该环节 approvalResult 声明的选项 id 列表」。
        var declared = (process?
                .FindFlowElementsOfType<UserTask>()
                .SelectMany(task => task.FormProperties
                    .Where(property => string.Equals(
                        property.Id, ApprovalFormPropertyId, StringComparison.Ordinal))
                    .Select(property => new
                    {
                        ActivityId = task.Id,
                        OptionIds = property.Values
                            .Select(value => value.Id ?? string.Empty)
                            .Where(id => id.Length > 0)
                            .ToArray(),
                    }))
                .ToArray())
            ?? [];

        if (declared.Length == 0)
        {
            // 没有审批环节（或没有声明 approvalResult）——不是错误，可能只是普通流程
            return null;
        }

        var store = services.GetRequiredService<IApprovalDictionaryStore>();

        IReadOnlyList<ApprovalDictionaryEntry> declarations = [];

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            try
            {
                declarations = await store
                    .ListAsync(tenantId, processKey, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "审批语义字典表不可读（{ProcessDefinitionId}）：严格模式下自定义选项会被判为无映射。",
                    processDefinitionId);
            }
        }

        var unmapped = new List<ValidationError>();
        var resolved = new List<string>();

        foreach (var declaration in declared)
        {
            foreach (var optionId in declaration.OptionIds)
            {
                var semantic = ResolveSemantic(declarations, declaration.ActivityId, optionId);
                var hasDeclaration = declarations.Any(entry =>
                    string.Equals(entry.OptionId, optionId, StringComparison.Ordinal)
                    && (string.Equals(entry.NodeId, declaration.ActivityId, StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(entry.NodeId)));

                if (semantic == ApprovalSemantic.Custom && !hasDeclaration)
                {
                    unmapped.Add(new ValidationError(
                        RuleName,
                        declaration.ActivityId,
                        "approvalResult",
                        $"选项 '{optionId}' 没有语义映射：请在其所属流程的 nb_approval_dictionary 声明 semantic，"
                        + "或改用默认字典认识的选项 id（1/2/3 与常见英文词）。",
                        ValidationSeverity.Warning));
                }
                else
                {
                    resolved.Add($"{declaration.ActivityId}:{optionId} → {semantic}");
                }
            }
        }

        if (unmapped.Count > 0)
        {
            var summary = $"流程定义 {processDefinitionId} 里 {unmapped.Count} 个审批选项没有语义映射："
                + string.Join("、", unmapped.Select(error => error.ElementId + ":" + error.Message));

            if (rejectUnmapped)
            {
                throw new InvalidOperationException(summary);
            }

            logger.LogWarning("{Message}", summary);
        }
        else
        {
            logger.LogInformation(
                "审批语义自检通过（{Count} 个选项）：{Resolved}",
                resolved.Count,
                string.Join(" / ", resolved));
        }

        return unmapped.Count > 0 ? unmapped : null;
    }

    /// <summary>解析选项语义：字典声明优先，其次默认字典，最后 CUSTOM（不猜）。</summary>
    private static ApprovalSemantic ResolveSemantic(
        IReadOnlyList<ApprovalDictionaryEntry> declarations,
        string? activityId,
        string optionId)
    {
        var entry = declarations
            .FirstOrDefault(item => string.Equals(item.OptionId, optionId, StringComparison.Ordinal)
                && string.Equals(item.NodeId, activityId, StringComparison.Ordinal))
            ?? declarations
                .FirstOrDefault(item => string.Equals(item.OptionId, optionId, StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(item.NodeId));

        return entry?.Semantic ?? ApprovalSemantics.Resolve(optionId);
    }
}

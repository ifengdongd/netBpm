using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 流程目录（<c>/workflow/v1/processes</c>）的读模型。
/// </summary>
/// <remarks>
/// <para>
/// 目录是外部系统接入时<b>第一个</b>要调的东西：先拿到 <see cref="ProcessDescriptor.ProcessKey"/>，
/// 才能发起实例、查待办、拉历史。因此它必须在「什么都没配置」的情况下也能用——
/// 新流程部署完就出现，不需要有人先去登记名片。
/// </para>
/// <para>
/// 合并口径（三层，后层覆盖前层）：
/// <list type="number">
///   <item><description>引擎部署（唯一真源）：版本号、挂起状态、定义原文与校验和；</description></item>
///   <item><description>BPMN 派生：名称、发起入口（startEvent）、支持的审批语义、是否有表单字段；</description></item>
///   <item><description><c>nb_process_catalog</c> 覆盖行：分类、图标、业务页面地址、显式状态、排序、标签。</description></item>
/// </list>
/// </para>
/// <para>
/// 白名单在这里生效：调用方看不到自己未被授权的流程（<b>不是</b>看到但标灰）。
/// 未授权的 key 按「不存在」处理（404），不返回 403——403 会泄露「这条流程存在」。
/// </para>
/// </remarks>
public interface IProcessCatalog
{
    /// <summary>分页查流程目录（已按调用方白名单过滤）。</summary>
    /// <param name="query">查询条件。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="FormatException">游标格式不正确。</exception>
    ValueTask<CursorPage<ProcessDescriptor>> ListAsync(
        ProcessQuery query,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>查单条流程名片；未授权或不存在都返回 <see langword="null"/>。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ProcessDescriptor?> FindAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>查当前生效的定义（原文按需返回）。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="includeXml">是否返回 BPMN 原文。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ProcessDefinitionInfo?> GetDefinitionAsync(
        string processKey,
        bool includeXml,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>查某流程的全部版本（新到旧）。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ProcessVersionInfo>?> ListVersionsAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>查参数规范；未声明参数时返回空 schema（<see cref="ParameterSchema.Parameters"/> 为空）。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ParameterSchema?> GetParameterSchemaAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 查某一版定义的参数规范（历史实例按当时契约渲染）。
    /// </summary>
    /// <remarks>
    /// <paramref name="version"/> 为空等同于查当前生效版本。索引里没有该版本时退回当前派生结果：
    /// 「查不到那一版」不该让参数面板整体空白。
    /// </remarks>
    /// <param name="processKey">流程 key。</param>
    /// <param name="version">定义版本；为空表示当前生效版本。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ParameterSchema?> GetParameterSchemaAsync(
        string processKey,
        int? version,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>渲染流程图（SVG）。<paramref name="version"/> 为空表示当前生效版本。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="version">版本号。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<string?> GetDiagramSvgAsync(
        string processKey,
        int? version,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 各环节的表单规范（当前版本或指定版本）。未授权 / 不存在返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GetParameterSchemaAsync"/> 并列但回答不同问题：参数是<b>业务单据的数据</b>
    /// （单号、金额、天数），表单是<b>审批页的输入项</b>（意见、附件、部门）。两者都从
    /// <c>activiti:formProperty</c> 派生，但一套给发起/写回用，一套给渲染用。
    /// </remarks>
    /// <param name="processKey">流程 key。</param>
    /// <param name="version">版本号；为空表示当前生效版本。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ProcessFormSchema?> GetFormSchemaAsync(
        string processKey,
        int? version,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed partial class ProcessCatalog : IProcessCatalog, IProcessCatalogAdmin
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    /// <summary>派生结果的缓存时长。定义 id 含版本号，重部署会换 id，因此不会读到陈旧内容。</summary>
    private static readonly TimeSpan DeriveCacheLifetime = TimeSpan.FromMinutes(30);

    private readonly IProcessEngine _engine;
    private readonly IProcessCatalogStore _store;
    private readonly IProcessParameterStore _parameters;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProcessCatalog> _logger;

    /// <summary>构造。</summary>
    /// <param name="engine">引擎门面（定义真源）。</param>
    /// <param name="store">目录覆盖表。</param>
    /// <param name="parameters">参数规范索引（<c>nb_process_parameter</c>：派生结果的物化）。</param>
    /// <param name="cache">派生结果缓存（BPMN 解析不便宜，列表页会反复用到）。</param>
    /// <param name="logger">日志。</param>
    public ProcessCatalog(
        IProcessEngine engine,
        IProcessCatalogStore store,
        IProcessParameterStore parameters,
        IMemoryCache cache,
        ILogger<ProcessCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _store = store;
        _parameters = parameters;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<CursorPage<ProcessDescriptor>> ListAsync(
        ProcessQuery query,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
        var items = await LoadItemsAsync(context, ct).ConfigureAwait(false);

        // 过滤顺序：关键字 → 分类 → 状态 → 游标 → 截断。
        // 游标必须在过滤之后用：先把全量过滤完，再按「上一页最后一条」的位置往后取，
        // 否则中途插入的流程会让分页漏行。
        IEnumerable<CatalogItem> filtered = items;

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            filtered = filtered.Where(item =>
                item.Descriptor.ProcessKey.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (item.Descriptor.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category.Trim();
            filtered = filtered.Where(item =>
                string.Equals(item.Descriptor.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Status is { } status)
        {
            filtered = filtered.Where(item => item.Descriptor.Status == status);
        }

        var ordered = filtered
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Descriptor.ProcessKey, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrEmpty(query.Cursor))
        {
            var position = DecodeCursor(query.Cursor);
            ordered = [.. ordered.Where(item => IsAfter(item, position))];
        }

        var page = ordered.Take(limit).Select(item => item.Descriptor).ToArray();
        var hasMore = ordered.Count > limit;

        // 游标只暴露位置，不暴露内部字段：编码成不透明串，调用方不得解析
        var nextCursor = hasMore && page.Length > 0
            ? EncodeCursor(ordered[page.Length - 1])
            : null;

        return new CursorPage<ProcessDescriptor>(page, nextCursor, hasMore);
    }

    /// <inheritdoc />
    public async ValueTask<ProcessDescriptor?> FindAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(context);

        var items = await LoadItemsAsync(context, ct).ConfigureAwait(false);

        return items
            .FirstOrDefault(item => string.Equals(item.Descriptor.ProcessKey, processKey, StringComparison.Ordinal))
            ?.Descriptor;
    }

    /// <inheritdoc />
    public async ValueTask<ProcessDefinitionInfo?> GetDefinitionAsync(
        string processKey,
        bool includeXml,
        RequestContext context,
        CancellationToken ct = default)
    {
        var current = await FindCurrentAsync(processKey, context, ct).ConfigureAwait(false);

        if (current is null)
        {
            return null;
        }

        var derived = current.Derived;
        var xml = includeXml ? await ReadXmlAsync(current.Definition.Id, ct).ConfigureAwait(false) : null;

        return new ProcessDefinitionInfo(
            current.Definition.Key,
            current.Definition.Id,
            current.Definition.Version,
            derived.Checksum,
            current.Definition.DeployTime is { } deployedAt ? AsUtcOffset(deployedAt) : null,
            current.Definition.IsSuspended,
            xml);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ProcessVersionInfo>?> ListVersionsAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(context);

        var versions = await LoadVersionsAsync(processKey, context, ct).ConfigureAwait(false);

        if (versions.Count == 0)
        {
            return null;
        }

        var currentVersion = versions.Max(definition => definition.Version);

        var result = new List<ProcessVersionInfo>(versions.Count);

        foreach (var definition in versions)
        {
            var derived = await DeriveAsync(definition, ct).ConfigureAwait(false);

            result.Add(new ProcessVersionInfo(
                definition.Version,
                definition.Id,
                derived.Checksum,
                definition.DeployTime is { } deployedAt ? AsUtcOffset(deployedAt) : null,
                definition.IsSuspended,
                definition.Version == currentVersion));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<ParameterSchema?> GetParameterSchemaAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(context);

        var current = await FindCurrentAsync(processKey, context, ct).ConfigureAwait(false);

        if (current is null)
        {
            return null;
        }

        // 顺带把派生结果物化到 nb_process_parameter：这张表是「索引」，不是真源，
        // 因此这里**返回派生值**（BPMN 改了就必须立刻生效），落库只是让历史版本有据可查。
        await MaterializeAsync(
            context.EffectiveTenantId,
            current.Definition.Version,
            current.Derived.ParameterSchema,
            ct).ConfigureAwait(false);

        return current.Derived.ParameterSchema;
    }

    /// <inheritdoc />
    public async ValueTask<ParameterSchema?> GetParameterSchemaAsync(
        string processKey,
        int? version,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(context);

        if (version is not { } wanted)
        {
            return await GetParameterSchemaAsync(processKey, context, ct).ConfigureAwait(false);
        }

        // 历史实例的参数面板必须按**它当时那版定义**的声明渲染（v3 新加的参数不能出现在 v1 的单据上）。
        // 索引里查得到就用它；查不到（老库 / 该版本还没被物化过）退回当前派生——
        // 宁可多显示几个字段，也不能让参数面板整体空白。
        var indexed = await TryFindIndexedAsync(
            context.EffectiveTenantId,
            processKey,
            wanted,
            ct).ConfigureAwait(false);

        return indexed ?? await GetParameterSchemaAsync(processKey, context, ct).ConfigureAwait(false);
    }

    /// <summary>读索引里的某一版参数规范；没有或读失败都返回 null（索引是加速与留痕，不是必需品）。</summary>
    private async ValueTask<ParameterSchema?> TryFindIndexedAsync(
        string? tenantId,
        string processKey,
        int definitionVersion,
        CancellationToken ct)
    {
        try
        {
            return await _parameters
                .FindAsync(tenantId ?? DefaultTenantId, processKey, definitionVersion, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 老库可能还没跑补表脚本。参数面板不该因为「索引表不存在」而不可用：
            // 派生路径一直是在的，索引只是加速与留痕。
            _logger.LogWarning(
                exception,
                "参数规范索引不可读（{ProcessKey} v{Version}）：本次退回 BPMN 派生。"
                + "请执行 db/{Dialect}/netbpm_init.sql 补齐 nb_process_parameter。",
                processKey,
                definitionVersion,
                "mysql|sqlserver");

            return null;
        }
    }

    /// <summary>把某一版定义的参数规范写进索引（整组重建，失败只记日志）。</summary>
    /// <remarks>
    /// 写失败<b>不影响</b>本次查询：索引不可用只是让治理查询与历史渲染少一份依据，
    /// 而参数校验走的是派生结果，不会因为索引写不进去就放过一张不合规的单据。
    /// </remarks>
    private async ValueTask MaterializeAsync(
        string? tenantId,
        int definitionVersion,
        ParameterSchema schema,
        CancellationToken ct)
    {
        // 没声明参数的流程不落任何行：一张全是空索引的表既不能回答问题，还会让「有没有参数」
        // 这个判断从「查不到」变成「查到了 0 行」，两件事在 SQL 层分不清。
        if (definitionVersion <= 0 || schema.Parameters.Count == 0)
        {
            return;
        }

        var effectiveTenant = tenantId ?? DefaultTenantId;

        try
        {
            // 已经物化过就跳过：目录列表页每次都会走到这里，没必要每次都开一个事务删了再插
            var existing = await _parameters
                .FindAsync(effectiveTenant, schema.ProcessKey, definitionVersion, ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return;
            }

            await _parameters
                .ReplaceAsync(effectiveTenant, definitionVersion, schema, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "参数规范索引写入失败（{ProcessKey} v{Version}）：本次仍然按 BPMN 派生结果返回。",
                schema.ProcessKey,
                definitionVersion);
        }
    }

    /// <summary>无租户上下文时落库用的租户（平台表一律带租户）。</summary>
    private const string DefaultTenantId = "default";

    /// <inheritdoc />
    public async ValueTask<string?> GetDiagramSvgAsync(
        string processKey,
        int? version,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(context);

        DeployedProcessDefinition? target;

        if (version is { } wanted)
        {
            var versions = await LoadVersionsAsync(processKey, context, ct).ConfigureAwait(false);
            target = versions.FirstOrDefault(definition => definition.Version == wanted);
        }
        else
        {
            target = (await FindCurrentAsync(processKey, context, ct).ConfigureAwait(false))?.Definition;
        }

        if (target is null)
        {
            return null;
        }

        var xml = await ReadXmlAsync(target.Id, ct).ConfigureAwait(false);

        return string.IsNullOrEmpty(xml) ? null : BpmnDiagramSvgRenderer.RenderSvg(xml, DiagramHighlight.None);
    }

    /// <inheritdoc />
    public async ValueTask<ProcessFormSchema?> GetFormSchemaAsync(
        string processKey,
        int? version,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(context);

        DeployedProcessDefinition? target;

        if (version is { } wanted)
        {
            var versions = await LoadVersionsAsync(processKey, context, ct).ConfigureAwait(false);
            target = versions.FirstOrDefault(definition => definition.Version == wanted);
        }
        else
        {
            target = (await FindCurrentAsync(processKey, context, ct).ConfigureAwait(false))?.Definition;
        }

        if (target is null)
        {
            return null;
        }

        var model = await _engine.GetBpmnModelAsync(target.Id, ct).ConfigureAwait(false);
        var process = model?.GetMainProcess();

        if (process is null)
        {
            // 定义在、但解析不出主流程（XML 缺 bpmn:process）：给空规范而不是 404 ——
            // 「这个流程没有表单」与「这个流程不存在」是两件事，调用方要能区分。
            return new ProcessFormSchema(processKey, target.Id, target.Version, []);
        }

        var activities = new List<FormSchema>();

        // 只收用户任务：本引擎的表单属性挂在 <c>Activity</c> 上（<c>activiti:formProperty</c>），
        // 而发起时填的东西走的是<b>参数</b>（param.*，见 GetParameterSchemaAsync），
        // 不是开始事件的表单属性。两者混在一起会让「发起页」与「审批页」的字段来源分不清。
        foreach (var userTask in process.FindFlowElementsOfType<UserTask>())
        {
            var fields = FormSchemaBuilder.BuildFields(userTask);

            // 没字段的环节不出现：空字段集合会被读成「这个环节有表单但是空的」
            if (fields is not { Count: > 0 })
            {
                continue;
            }

            activities.Add(new FormSchema(processKey, userTask.Id, fields));
        }

        return new ProcessFormSchema(processKey, target.Id, target.Version, activities);
    }

    // ─────────────────────────── 载入与合并 ───────────────────────────

    /// <summary>目录中的一条（已合并三层）。</summary>
    private sealed record CatalogItem(
        DeployedProcessDefinition Definition,
        DerivedProcess Derived,
        ProcessDescriptor Descriptor,
        int SortOrder);

    /// <summary>
    /// 从 BPMN 派生出来的部分（引擎 + 定义原文能回答的问题）。
    /// </summary>
    /// <param name="Name">流程名（BPMN <c>process/@name</c>）。</param>
    /// <param name="EntryPoints">发起入口（每个 startEvent 一个）。</param>
    /// <param name="SupportedSemantics">支持审批语义（从 <c>approvalResult</c> 的选项推导）。</param>
    /// <param name="HasFormSchema">是否存在任意表单字段。</param>
    /// <param name="ParameterSchema">参数规范（从 <c>formProperty</c> 派生；无参数则为空 schema）。</param>
    /// <param name="Checksum">定义原文的 <c>sha256:{hex}</c>。</param>
    private sealed record DerivedProcess(
        string? Name,
        IReadOnlyList<ProcessEntryPoint> EntryPoints,
        IReadOnlyList<ApprovalSemantic> SupportedSemantics,
        bool HasFormSchema,
        ParameterSchema ParameterSchema,
        string Checksum);

    private async ValueTask<IReadOnlyList<CatalogItem>> LoadItemsAsync(
        RequestContext context,
        CancellationToken ct)
    {
        var tenantId = context.EffectiveTenantId;
        var current = await LoadCurrentDefinitionsAsync(tenantId, ct).ConfigureAwait(false);
        var overrides = await LoadOverridesAsync(tenantId, ct).ConfigureAwait(false);

        var items = new List<CatalogItem>(current.Count);

        foreach (var definition in current)
        {
            // 未授权的流程直接不出现：白名单为空视为不限
            if (!context.CanUseProcess(definition.Key))
            {
                continue;
            }

            var derived = await DeriveAsync(definition, ct).ConfigureAwait(false);
            var entry = overrides.GetValueOrDefault(definition.Key);

            items.Add(BuildItem(definition, derived, entry, context));
        }

        return items;
    }

    private async ValueTask<CatalogItem?> FindCurrentAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct)
    {
        var items = await LoadItemsAsync(context, ct).ConfigureAwait(false);

        return items.FirstOrDefault(item =>
            string.Equals(item.Definition.Key, processKey, StringComparison.Ordinal));
    }

    private static CatalogItem BuildItem(
        DeployedProcessDefinition definition,
        DerivedProcess derived,
        ProcessCatalogEntry? entry,
        RequestContext context)
    {
        // 状态：引擎的「挂起」永远优先。
        // 覆盖行只允许把流程标成草稿 / 已下线，不允许把挂起的流程说成可发起——
        // 那个闸门在引擎里，目录盖过去只会让调用方在提交时才吃到 409。
        var status = definition.IsSuspended
            ? ProcessStatus.Suspended
            : entry?.Status ?? ProcessStatus.Active;

        var canRead = PlatformAuthorization.HasScope(context, PlatformScopes.ProcessRead);
        var canStart = status == ProcessStatus.Active
            && definition.Version > 0
            && PlatformAuthorization.HasScope(context, PlatformScopes.InstanceStart);

        var descriptor = new ProcessDescriptor(
            ProcessKey: definition.Key,
            Name: FirstNonEmpty(entry?.Name, derived.Name, definition.Name, definition.Key) ?? definition.Key,
            Category: entry?.Category,
            Description: entry?.Description,
            Icon: entry?.Icon,
            PageUrls: entry?.PageUrls,
            Status: status,
            DeployedVersion: definition.Version,
            DefinitionChecksum: derived.Checksum,
            ParameterSchemaVersion: entry?.ParameterSchemaVersion ?? 0,
            EntryPoints: entry?.EntryPoints ?? derived.EntryPoints,
            Labels: entry?.Labels,
            Capabilities: new ProcessCapabilities(
                CanStart: canStart,
                CanReadDefinition: canRead,
                CanReadDiagram: canRead,
                HasParameterSchema: derived.ParameterSchema.Parameters.Count > 0,
                HasFormSchema: derived.HasFormSchema,
                SupportedSemantics: derived.SupportedSemantics));

        return new CatalogItem(definition, derived, descriptor, entry?.SortOrder ?? 0);
    }

    /// <summary>
    /// 取「每个流程 key 的当前生效定义」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 引擎原语按租户取值，而平台把<b>无租户部署</b>（<c>TENANT_ID_ = ''</c>）视为全局共享流程：
    /// 单租户部署与多租户部署会同时存在（历史部署都不带租户），不这样桥接的话
    /// 「客户端属于 default 租户」会看不到任何流程。
    /// </para>
    /// <para>
    /// 同一 key 既有租户自己的部署又有全局部署时，<b>租户自己的优先</b>（覆盖语义，不是合并）。
    /// </para>
    /// </remarks>
    private async ValueTask<IReadOnlyList<DeployedProcessDefinition>> LoadCurrentDefinitionsAsync(
        string? tenantId,
        CancellationToken ct)
    {
        var all = await _engine.FindProcessDefinitionsAsync(tenantId: null, ct).ConfigureAwait(false);

        var visible = all.Where(definition =>
            string.IsNullOrEmpty(definition.TenantId)
            || tenantId is null
            || string.Equals(definition.TenantId, tenantId, StringComparison.Ordinal));

        return
        [
            // 先按（租户, key）取各自的最新版本，再按 key 合并，租户自己的胜出
            .. visible
                .GroupBy(definition => (Tenant: definition.TenantId ?? string.Empty, definition.Key))
                .Select(group => group.OrderByDescending(definition => definition.Version).First())
                .GroupBy(definition => definition.Key, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(definition => string.IsNullOrEmpty(definition.TenantId) ? 0 : 1)
                    .ThenByDescending(definition => definition.Version)
                    .First())
                .OrderBy(definition => definition.Key, StringComparer.Ordinal),
        ];
    }

    private async ValueTask<IReadOnlyList<DeployedProcessDefinition>> LoadVersionsAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct)
    {
        if (!context.CanUseProcess(processKey))
        {
            return [];
        }

        var all = await _engine.FindProcessDefinitionsAsync(tenantId: null, ct).ConfigureAwait(false);
        var tenantId = context.EffectiveTenantId;

        return
        [
            .. all
                .Where(definition => string.Equals(definition.Key, processKey, StringComparison.Ordinal)
                    && (string.IsNullOrEmpty(definition.TenantId)
                        || tenantId is null
                        || string.Equals(definition.TenantId, tenantId, StringComparison.Ordinal)))
                // 同一租户同一版本只应有一行；跨租户同版本时租户自己的胜出（与当前生效口径一致）
                .GroupBy(definition => (definition.TenantId ?? string.Empty, definition.Version))
                .Select(group => group.First())
                .GroupBy(definition => definition.Version)
                .Select(group => group
                    .OrderByDescending(definition => string.IsNullOrEmpty(definition.TenantId) ? 0 : 1)
                    .First())
                .OrderByDescending(definition => definition.Version),
        ];
    }

    private async ValueTask<Dictionary<string, ProcessCatalogEntry>> LoadOverridesAsync(
        string? tenantId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return [];
        }

        try
        {
            var entries = await _store.ListAsync(tenantId, ct).ConfigureAwait(false);

            return entries.ToDictionary(entry => entry.ProcessKey, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 老库可能还没跑补表脚本。目录是接入第一步，不能因为「精修表不存在」就整体不可用：
            // 派生信息已经足够让调用方工作，只是没有分类 / 图标 / 业务页面地址。
            _logger.LogWarning(
                exception,
                "流程目录覆盖表不可读（{TenantId}）：本次按「无覆盖」返回，"
                + "请执行 db/{Dialect}/netbpm_init.sql 补齐 nb_process_catalog。",
                tenantId,
                "mysql|sqlserver");

            return [];
        }
    }

    // ─────────────────────────── BPMN 派生 ───────────────────────────

    private async ValueTask<DerivedProcess> DeriveAsync(
        DeployedProcessDefinition definition,
        CancellationToken ct)
    {
        var cacheKey = "netbpm:catalog:derived:" + definition.Id;

        if (_cache.TryGetValue(cacheKey, out DerivedProcess? cached) && cached is not null)
        {
            return cached;
        }

        var model = await _engine.GetBpmnModelAsync(definition.Id, ct).ConfigureAwait(false);
        var xml = await ReadXmlAsync(definition.Id, ct).ConfigureAwait(false);

        var process = model?.GetMainProcess();

        IReadOnlyList<StartEvent> startEvents = process is null
            ? []
            : [.. process.FindFlowElementsOfType<StartEvent>()];

        var entryPoints = startEvents
            .Select((start, index) => new ProcessEntryPoint(
                Key: string.IsNullOrEmpty(start.Id) ? $"start{index + 1}" : start.Id,
                Name: start.Name,
                StartActivityId: start.Id,
                DefaultParameters: null))
            .ToArray();

        IReadOnlyList<UserTask> userTasks = process is null
            ? []
            : [.. process.FindFlowElementsOfType<UserTask>()];

        var hasFormSchema = userTasks.Any(task => task.FormProperties.Count > 0);

        var semantics = userTasks
            .SelectMany(task => task.FormProperties)
            .Where(property => string.Equals(
                property.Id, ApprovalFormPropertyId, StringComparison.Ordinal))
            .SelectMany(property => property.Values)
            .Select(value => value.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => ApprovalSemantics.Resolve(id!))
            .Distinct()
            .OrderBy(semantic => (int)semantic)
            .ToArray();

        var parameterSchema = DeriveParameterSchema(process, definition);

        var derived = new DerivedProcess(
            process?.Name ?? definition.Name,
            entryPoints,
            semantics,
            hasFormSchema,
            parameterSchema,
            ComputeChecksum(xml));

        _cache.Set(cacheKey, derived, DeriveCacheLifetime);

        return derived;
    }

    private ValueTask<string?> ReadXmlAsync(string processDefinitionId, CancellationToken ct) =>
        _engine.GetProcessDefinitionXmlAsync(processDefinitionId, ct);

    // ─────────────────────────── 参数规范派生 ───────────────────────────

    /// <summary>发起与审批各环节共享的业务参数命名空间（<c>param.</c> 前缀）。</summary>
    private const string ParameterApiPrefix = "param.";

    /// <summary>平台参数扩展元素名（<c>extensionElements/netbpm:parameters</c>）。</summary>
    private const string PlatformParametersElement = "parameters";

    /// <summary>单条参数声明的元素名（<c>netbpm:parameter</c>）。</summary>
    private const string PlatformParameterElement = "parameter";

    /// <summary>
    /// 从 BPMN 的 <c>formProperty</c> 派生参数规范，再被 <c>netbpm:parameters</c> 覆盖。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个来源，后者覆盖前者：<c>formProperty</c>（<c>activiti:extensionElements</c> 下挂在各
    /// userTask 上的表单字段）派生出基础契约（<c>id</c>/<c>type</c>/<c>required</c>/<c>variable</c>/<c>value</c>），
    /// 再由 <c>netbpm:parameters</c> 平台扩展按 <c>parameter</c> 逐条覆盖 <c>apiName</c>/<c>type</c>/
    /// <c>required</c>/<c>displayName</c>/<c>description</c>/<c>constraints</c>/<c>writableBy</c>/<c>visibleTo</c>。
    /// </para>
    /// <para>
    /// 两个刻意排除：<c>approvalResult</c> 字段是审批选项，属于语义字典（三码）范畴，不是业务参数；
    /// 同一 <c>variable</c> 在多个环节重复出现（如「申请人修改」复用了「提交申请」的字段）按
    /// <c>variable</c> 去重，保留第一次出现（首个环节）的定义。
    /// </para>
    /// </remarks>
    /// <summary><c>netbpm:parameter</c> 里的一条声明（全部字段可选，未声明的不覆盖派生结果）。</summary>
    private sealed record ParameterOverrideValue(
        string? ApiName,
        string? Type,
        bool? Required,
        string? DisplayName,
        string? Description,
        string? DefaultValue,
        decimal? Min,
        decimal? Max,
        int? MinLength,
        int? MaxLength,
        string? Pattern,
        IReadOnlyList<string>? EnumValues,
        IReadOnlyList<string>? WritableBy,
        IReadOnlyList<string>? VisibleTo);

    /// <summary>
    /// 从 BPMN 的 <c>formProperty</c> 派生参数规范，再被 <c>netbpm:parameters</c> 覆盖。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个来源的分工：<c>formProperty</c> 是 Activiti 的老规矩（也是 Demo 与既有流程在用的），
    /// <c>netbpm:parameters</c> 是平台自己的扩展——它表达的正是 formProperty 说不出来的那几件事：
    /// 对外叫什么（<c>apiName</c>）、谁能写（<c>writableBy</c>）、谁能看（<c>visibleTo</c>）、
    /// 以及长度 / 范围 / 正则这些约束。
    /// </para>
    /// <para>
    /// <b>没有声明就是放行</b>：<c>parameters</c> 扩展可选。写了的字段才覆盖，
    /// 没写的一律沿用派生值（<c>required=false</c> 之类不会因为「声明里没提」就被重置）。
    /// 老 BPMN 不改一个字也能接着跑，这是兼容性的底线。
    /// </para>
    /// <para>
    /// 覆盖还能<b>凭空造</b>出派生里没有的参数（例如纯由后台任务写入的字段）：
    /// 按这里的规矩它能出现在 schema 里，从而进入校验与契约。
    /// </para>
    /// </remarks>
    private static ParameterSchema DeriveParameterSchema(
        Process? process,
        DeployedProcessDefinition definition)
    {
        if (process is null)
        {
            return new ParameterSchema(definition.Key, 0, [], ReservedPrefixes);
        }

        var overrides = ReadParameterOverrides(process);
        var userTasks = process.FindFlowElementsOfType<UserTask>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<ParameterDefinition>();

        foreach (var property in userTasks.SelectMany(task => task.FormProperties))
        {
            if (string.Equals(property.Id, ApprovalFormPropertyId, StringComparison.Ordinal))
            {
                continue;
            }

            var variable = FirstNonEmpty(property.Variable, property.Id);

            if (string.IsNullOrWhiteSpace(variable) || !seen.Add(variable))
            {
                continue;
            }

            var derived = BuildParameterDefinition(property, variable, definitions.Count);

            definitions.Add(overrides.TryGetValue(variable, out var declared)
                ? Merge(derived, declared)
                : derived);
        }

        // 只在扩展里声明过的参数（BPMN 环节上没有对应 formProperty）
        foreach (var (variable, declared) in overrides)
        {
            if (seen.Contains(variable))
            {
                continue;
            }

            definitions.Add(BuildDeclaredParameter(definition.Key, variable, declared, definitions.Count));
        }

        // 都按 sortOrder 稳定排序，让 schema 的输出顺序与迭代顺序无关（否则换版本就抖动）
        definitions.Sort(static (left, right) => left.SortOrder.CompareTo(right.SortOrder));

        return new ParameterSchema(
            definition.Key,
            definitions.Count > 0 ? 1 : 0,
            definitions,
            ReservedPrefixes);
    }

    /// <summary>
    /// 读 <c>netbpm:parameters</c> 扩展（流程级 <c>extensionElements</c> 下）。
    /// </summary>
    /// <remarks>
    /// 找不到或写得不对时返回空字典：平台扩展是<b>可选增强</b>，
    /// 一份没法解析的扩展不该把整条流程挡在门外（那是部署期校验的事，这里只认声明）。
    /// </remarks>
    private static Dictionary<string, ParameterOverrideValue> ReadParameterOverrides(Process process)
    {
        var result = new Dictionary<string, ParameterOverrideValue>(StringComparer.Ordinal);

        foreach (var element in process.ExtensionElements)
        {
            if (!string.Equals(element.ElementName, PlatformParametersElement, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var parameter in element.ExtensionElements)
            {
                if (!string.Equals(parameter.ElementName, PlatformParameterElement, StringComparison.Ordinal))
                {
                    continue;
                }

                var name = AttrOf(parameter, "name");

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                result[name!] = new ParameterOverrideValue(
                    ApiName: AttrOf(parameter, "apiName"),
                    Type: AttrOf(parameter, "type"),
                    Required: ParseBool(AttrOf(parameter, "required")),
                    DisplayName: AttrOf(parameter, "displayName"),
                    Description: AttrOf(parameter, "description"),
                    DefaultValue: AttrOf(parameter, "default"),
                    Min: ParseDecimal(AttrOf(parameter, "min")),
                    Max: ParseDecimal(AttrOf(parameter, "max")),
                    MinLength: ParseInt(AttrOf(parameter, "minLength")),
                    MaxLength: ParseInt(AttrOf(parameter, "maxLength")),
                    Pattern: AttrOf(parameter, "pattern"),
                    EnumValues: ParseList(AttrOf(parameter, "enumValues")),
                    WritableBy: ParseList(AttrOf(parameter, "writableBy")),
                    VisibleTo: ParseList(AttrOf(parameter, "visibleTo")));
            }
        }

        return result;
    }

    /// <summary>把扩展声明合并到派生出来的参数定义上（未声明的字段保持原值）。</summary>
    private static ParameterDefinition Merge(ParameterDefinition derived, ParameterOverrideValue declared)
    {
        var type = declared.Type is { Length: > 0 } text && TryMapParameterType(text, out var mapped)
            ? mapped
            : derived.Type;

        var enumValues = declared.EnumValues ?? derived.Constraints?.EnumValues;
        var constraints = new ParameterConstraints(
            declared.MinLength ?? derived.Constraints?.MinLength,
            declared.MaxLength ?? derived.Constraints?.MaxLength,
            declared.Min ?? derived.Constraints?.Minimum,
            declared.Max ?? derived.Constraints?.Maximum,
            declared.Pattern ?? derived.Constraints?.Pattern,
            enumValues);

        return derived with
        {
            ApiName = string.IsNullOrWhiteSpace(declared.ApiName) ? derived.ApiName : declared.ApiName!,
            DisplayName = declared.DisplayName ?? derived.DisplayName,
            Description = declared.Description ?? derived.Description,
            Type = type,
            Required = declared.Required ?? derived.Required,
            DefaultValue = declared.DefaultValue ?? derived.DefaultValue,
            Constraints = HasAny(constraints) ? constraints : null,
            WritableBy = declared.WritableBy,
            VisibleTo = declared.VisibleTo,
        };
    }

    /// <summary>按扩展声明造一个 BPMN 环节上没有的参数定义。</summary>
    private static ParameterDefinition BuildDeclaredParameter(
        string processKey,
        string variable,
        ParameterOverrideValue declared,
        int sortOrder)
    {
        TryMapParameterType(declared.Type, out var type);

        var enumValues = declared.EnumValues;
        var constraints = new ParameterConstraints(
            declared.MinLength,
            declared.MaxLength,
            declared.Min,
            declared.Max,
            declared.Pattern,
            enumValues);

        return new ParameterDefinition(
            ApiName: declared.ApiName ?? ParameterApiPrefix + variable,
            VariableName: variable,
            DisplayName: declared.DisplayName ?? variable,
            Type: type,
            Required: declared.Required ?? false,
            DefaultValue: declared.DefaultValue,
            Description: declared.Description,
            Constraints: HasAny(constraints) ? constraints : null,
            WritableBy: declared.WritableBy,
            VisibleTo: declared.VisibleTo,
            SortOrder: sortOrder);
    }

    /// <summary>取扩展元素上的属性值（忽略命名空间：作者的 BPMN 里前缀写法不统一）。</summary>
    private static string? AttrOf(ExtensionElement element, string name)
    {
        foreach (var list in element.Attributes.Values)
        {
            foreach (var attribute in list)
            {
                if (string.Equals(attribute.Name, name, StringComparison.Ordinal))
                {
                    return string.IsNullOrWhiteSpace(attribute.Value) ? null : attribute.Value;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return [.. value!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)];
    }

    private static bool? ParseBool(string? value) =>
        bool.TryParse(value, out var parsed) ? parsed : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>约束里一个限制都没写就不返回对象（空 xmlns 式的噪声字段会污染契约）。</summary>
    private static bool HasAny(ParameterConstraints constraints) =>
        constraints.MinLength is not null
        || constraints.MaxLength is not null
        || constraints.Minimum is not null
        || constraints.Maximum is not null
        || constraints.Pattern is { Length: > 0 }
        || constraints.EnumValues is { Count: > 0 };

    private static bool TryMapParameterType(string? raw, out ParameterType type)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            type = MapParameterType(raw);

            return true;
        }

        type = ParameterType.String;

        return false;
    }

    /// <summary>把一条 <c>formProperty</c> 转成参数定义（对外名 = <c>param.{variable}</c>）。</summary>
    private static ParameterDefinition BuildParameterDefinition(
        FormProperty property,
        string variable,
        int sortOrder)
    {
        var type = MapParameterType(property.Type);
        var enumValues = property.Values.Count > 0
            ? property.Values.Select(value => value.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToArray()
            : null;

        return new ParameterDefinition(
            ApiName: ParameterApiPrefix + variable,
            VariableName: variable,
            DisplayName: FirstNonEmpty(property.Name, property.Id),
            Type: type,
            Required: property.Required == true,
            DefaultValue: property.Default,
            Description: null,
            Constraints: type == ParameterType.Enum && enumValues is { Length: > 0 }
                ? new ParameterConstraints(null, null, null, null, null, enumValues)
                : null,
            WritableBy: null,
            VisibleTo: null,
            SortOrder: sortOrder);
    }

    /// <summary>把 Activiti 表单属性类型映射到平台参数类型。</summary>
    private static ParameterType MapParameterType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "long" or "integer" or "int" => ParameterType.Long,
        "double" or "decimal" => ParameterType.Decimal,
        "boolean" or "bool" => ParameterType.Boolean,
        "date" => ParameterType.Date,
        "datetime" or "dateTime" => ParameterType.DateTime,
        "enum" => ParameterType.Enum,
        "json" => ParameterType.Json,
        _ => ParameterType.String,
    };

    /// <summary>平台保留的命名空间前缀（调用方自定义参数不得占用）。</summary>
    private static readonly IReadOnlyList<string> ReservedPrefixes = ["param.", "form."];

    /// <summary>
    /// 审批结果表单字段名（与 BPMN 的 <c>activiti:formProperty id="approvalResult"</c> 一致）。
    /// </summary>
    /// <remarks>
    /// 刻意用字面量而不是引用 <c>ApprovalResults.FormPropertyId</c>：那是 Demo 侧的常量，
    /// 平台目录不应该依赖某个业务样例工程的类型。这个字段名属于 BPMN 约定，写在这里是唯一事实。
    /// </remarks>
    private const string ApprovalFormPropertyId = "approvalResult";

    private static string ComputeChecksum(string? xml) =>
        // 定义原文缺失时返回空摘要：调用方据此知道「无法判断缓存是否过期」，
        // 而不是拿到一个看起来正常的假校验和
        string.IsNullOrWhiteSpace(xml) ? "sha256:" : ComputeChecksumCore(xml);

    private static string ComputeChecksumCore(string xml)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml));

        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    // ─────────────────────────── 游标 ───────────────────────────

    /// <summary>游标位置：排序键（<c>sort_order</c>）+ 流程 key。</summary>
    private readonly record struct CursorPosition(int SortOrder, string ProcessKey);

    private static bool IsAfter(CatalogItem item, CursorPosition position)
    {
        var sortOrder = item.SortOrder;

        return sortOrder > position.SortOrder
            || (sortOrder == position.SortOrder
                && string.CompareOrdinal(item.Descriptor.ProcessKey, position.ProcessKey) > 0);
    }

    private static string EncodeCursor(CatalogItem item) =>
        Base64UrlEncode($"{item.SortOrder}\u001f{item.Descriptor.ProcessKey}");

    private static CursorPosition DecodeCursor(string cursor)
    {
        var raw = Base64UrlDecode(cursor);
        var separator = raw.IndexOf('\u001f', StringComparison.Ordinal);

        if (separator <= 0 || !int.TryParse(raw[..separator], out var sortOrder))
        {
            throw new FormatException("分页游标格式不正确：请原样回传响应里的 nextCursor。");
        }

        return new CursorPosition(sortOrder, raw[(separator + 1)..]);
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("分页游标格式不正确。"),
        };

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>
    /// 数据库读回的 <see cref="DateTime"/> 是 <see cref="DateTimeKind.Unspecified"/>，
    /// 直接构造 <see cref="DateTimeOffset"/> 会按本地时区解释——统一按 UTC 处理。
    /// </summary>
    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

using Microsoft.AspNetCore.Http;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 流程目录覆盖行的<b>管理面</b>（<c>/admin/v1/processes</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IProcessCatalog"/> 分开两个接口，而不是在同一个接口上加写方法：
/// 读接口被端点、配置校验、宿主初始化到处注入，一旦它带上写方法，
/// 任何一个拿到只读依赖的地方都能改名片。接口分开之后，「谁能写」这件事在类型上就看得见。
/// </para>
/// <para>
/// 所有方法都<b>不带白名单过滤</b>：管理面看的是系统的真实配置，不是某个接入方被授权的子集。
/// 因此这里的返回<b>不能</b>直接暴露给业务面——两边的可见性口径不同。
/// </para>
/// </remarks>
public interface IProcessCatalogAdmin
{
    /// <summary>列出当前租户的全部覆盖行（没有覆盖行的流程不出现）。</summary>
    /// <param name="context">调用方身份（取租户）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ProcessCatalogOverride>> ListOverridesAsync(
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>按流程 key 查覆盖行；没有覆盖返回 <see langword="null"/>。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ProcessCatalogOverride?> FindOverrideAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 写入覆盖行（整行替换）。
    /// </summary>
    /// <remarks>
    /// 流程 key 必须在引擎里有定义，或已有覆盖行，否则返回 <see langword="null"/>（端点转 404）：
    /// 拼错一个 key 就在库里留一行谁也看不见的孤儿，是最难发现的一类脏数据。
    /// </remarks>
    /// <param name="processKey">流程 key。</param>
    /// <param name="request">覆盖内容（整行）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="PlatformActionRejectedException">请求里没有任何覆盖字段（422）。</exception>
    ValueTask<ProcessCatalogOverride?> UpsertOverrideAsync(
        string processKey,
        ProcessCatalogOverrideRequest request,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>删除覆盖行；本来就没有返回 <see langword="false"/>。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<bool> DeleteOverrideAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default);
}

/// <summary>
/// 目录覆盖行的管理面实现。
/// </summary>
/// <remarks>
/// <para>
/// 写完之后<b>不需要</b>清缓存：BPMN 派生的缓存键是定义 id（含版本），覆盖行则是每次现查
/// （<see cref="LoadOverridesAsync"/>）。也就是说覆盖行的读取路径本来就没有缓存层，
/// 改完下一次读就能看到——这也是当初不给它加缓存的原因：一个几十行的表，
/// 换来的却是「改完要记得失效」这种只在生产环境才暴露的坑。
/// </para>
/// </remarks>
public sealed partial class ProcessCatalog
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ProcessCatalogOverride>> ListOverridesAsync(
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tenantId = ResolveAdminTenant(context);
        var entries = await _store.ListAsync(tenantId, ct).ConfigureAwait(false);

        return [.. entries.Select(ToOverrideView)];
    }

    /// <inheritdoc />
    public async ValueTask<ProcessCatalogOverride?> FindOverrideAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(context);

        var entry = await _store
            .FindAsync(ResolveAdminTenant(context), processKey, ct)
            .ConfigureAwait(false);

        return entry is null ? null : ToOverrideView(entry);
    }

    /// <inheritdoc />
    public async ValueTask<ProcessCatalogOverride?> UpsertOverrideAsync(
        string processKey,
        ProcessCatalogOverrideRequest request,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // 空覆盖 == 没有覆盖，写进去只是一行什么都不做的记录。要清掉请用 DELETE。
        if (!request.HasAnyOverride)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.CatalogOverrideEmpty,
                "没有要覆盖的字段",
                StatusCodes.Status422UnprocessableEntity,
                "请求里没有任何覆盖字段。整行全空等价于「没有覆盖行」，"
                + "因此这里不做写入；要清除这条流程的覆盖请用 DELETE /admin/v1/processes/{processKey}。");
        }

        var tenantId = ResolveAdminTenant(context);
        var existing = await _store.FindAsync(tenantId, processKey, ct).ConfigureAwait(false);

        // 流程必须真的存在：要么引擎里有定义，要么已经有一行覆盖（后者兜住「先精修后部署」的历史数据）
        if (existing is null && !await ExistsInEngineAsync(processKey, tenantId, ct).ConfigureAwait(false))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        var entry = new ProcessCatalogEntry(
            tenantId,
            processKey,
            request.Name,
            request.Category,
            request.Description,
            request.Icon,
            request.PageUrls,
            request.EntryPoints,
            request.Status,
            request.SortOrder ?? 0,
            request.Labels,
            request.ParameterSchemaVersion ?? 0,
            existing?.CreatedAt ?? now,
            now);

        var stored = await _store.UpsertAsync(entry, ct).ConfigureAwait(false);

        return ToOverrideView(stored);
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteOverrideAsync(
        string processKey,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(context);

        return await _store
            .DeleteAsync(ResolveAdminTenant(context), processKey, ct)
            .ConfigureAwait(false);
    }

    /// <summary>管理面用的租户：没有租户上下文时落平台默认租户（与参数索引同口径）。</summary>
    private static string ResolveAdminTenant(RequestContext context) =>
        string.IsNullOrWhiteSpace(context.EffectiveTenantId) ? DefaultTenantId : context.EffectiveTenantId;

    /// <summary>引擎里是否存在这个 key 的定义（<b>不过白名单</b>：管理面看全量）。</summary>
    private async ValueTask<bool> ExistsInEngineAsync(
        string processKey,
        string tenantId,
        CancellationToken ct)
    {
        var definitions = await LoadCurrentDefinitionsAsync(tenantId, ct).ConfigureAwait(false);

        return definitions.Any(definition =>
            string.Equals(definition.Key, processKey, StringComparison.Ordinal));
    }

    private static ProcessCatalogOverride ToOverrideView(ProcessCatalogEntry entry) => new(
        entry.TenantId,
        entry.ProcessKey,
        entry.Name,
        entry.Category,
        entry.Description,
        entry.Icon,
        entry.PageUrls,
        entry.EntryPoints,
        entry.Status,
        entry.SortOrder,
        entry.Labels,
        entry.ParameterSchemaVersion,
        entry.CreatedAt,
        entry.UpdatedAt);
}

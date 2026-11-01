using NetBpm.Platform.Contracts;

namespace NetBpm.Platform.Client;

/// <summary>
/// 管理面：流程目录覆盖行（<c>/admin/v1/processes</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 需要 <c>workflow:admin:process</c>（<c>workflow:*</c> 不覆盖它）。
/// 返回的是<b>覆盖行本身</b>，不是合并后的名片：管理端要判断「这一项是精修过的还是 BPMN 派生的」，
/// 只有覆盖行能回答。
/// </para>
/// <para>
/// 写是<b>整行替换</b>：只传 <c>category</c> 的第二次 PUT 会把上次的 <c>name</c> 清回「跟随 BPMN」。
/// 想清除整条覆盖用 <see cref="DeleteAsync"/>，不要用「全空的 PUT」——那会被 422 拒绝。
/// </para>
/// </remarks>
public sealed class AdminProcessesClient
{
    private const string BasePath = "admin/v1/processes";

    private readonly PlatformTransport _transport;

    internal AdminProcessesClient(PlatformTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
    }

    /// <summary>列出被精修过的流程（没有覆盖行的流程不出现）。</summary>
    /// <param name="ct">取消令牌。</param>
    public Task<IReadOnlyList<ProcessCatalogOverride>> ListAsync(CancellationToken ct = default) =>
        _transport.GetJsonAsync<IReadOnlyList<ProcessCatalogOverride>>(BasePath, null, ct);

    /// <summary>查单条覆盖行；没有覆盖抛 404（<see cref="ErrorCodes.CatalogOverrideNotFound"/>）。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<ProcessCatalogOverride> GetAsync(string processKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        return _transport.GetJsonAsync<ProcessCatalogOverride>(Path(processKey), null, ct);
    }

    /// <summary>写入覆盖行（整行替换）。流程 key 必须已部署，否则 404。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="request">覆盖内容；至少要有一个字段，否则 422。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<ProcessCatalogOverride> UpsertAsync(
        string processKey,
        ProcessCatalogOverrideRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(request);

        return _transport.PutJsonAsync<ProcessCatalogOverride>(Path(processKey), request, ct);
    }

    /// <summary>删除覆盖行（名片回到 BPMN 派生）；本来就没有返回 <see langword="false"/>。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<bool> DeleteAsync(string processKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        try
        {
            await _transport.SendAsync(HttpMethod.Delete, Path(processKey), ct).ConfigureAwait(false);

            return true;
        }
        catch (NetBpmApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string Path(string processKey) =>
        BasePath + "/" + Uri.EscapeDataString(processKey);
}

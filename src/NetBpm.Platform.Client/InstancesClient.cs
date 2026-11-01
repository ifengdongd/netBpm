using System.Globalization;
using System.Runtime.CompilerServices;
using NetBpm.Platform.Contracts;

namespace NetBpm.Platform.Client;

/// <summary>
/// 流程实例（<c>/workflow/v1/instances</c>）：发起、列表、详情、历史、参数、参数修订、流程图、终止。
/// </summary>
/// <remarks>
/// <para>
/// 写操作只有两个（发起 / 终止），且都支持幂等键：<b>网络超时不等于没执行</b>，
/// 发起尤其危险——重试一次就多一张单。带同一个 <c>Idempotency-Key</c> 重试时，
/// 服务端直接回放首次响应。
/// </para>
/// <para>
/// 幂等键的生成责任在调用方（通常是「业务单据号 + 操作」），SDK 不自动生成：
/// 自动生成只能保证「同一个 client 实例内不重复」，而重试往往发生在进程重启之后。
/// </para>
/// </remarks>
public sealed class InstancesClient
{
    private const string BasePath = "workflow/v1/instances";

    private readonly PlatformTransport _transport;

    internal InstancesClient(PlatformTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
    }

    /// <summary>分页查实例（服务端按调用方可见范围收窄）。</summary>
    /// <param name="query">查询条件；为空表示默认第一页（倒序）。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<CursorPage<InstanceSummary>> ListAsync(
        InstanceQuery? query = null,
        CancellationToken ct = default)
    {
        query ??= new InstanceQuery();

        List<KeyValuePair<string, string?>> parameters =
        [
            new("processKey", query.ProcessKey),
            new("businessKey", query.BusinessKey),
            new("status", PlatformTransport.EnumText(query.Status)),
            new("startedBy", query.StartedBy),
            new("involvedUser", query.InvolvedUser),
            new("startedFrom", PlatformTransport.FormatTimestamp(query.StartedFrom)),
            new("startedTo", PlatformTransport.FormatTimestamp(query.StartedTo)),
            new("cursor", query.Cursor),
            new("limit", query.Limit?.ToString(CultureInfo.InvariantCulture)),
            new("direction", PlatformTransport.EnumText<SortDirection>(query.Direction)),
        ];

        return _transport.GetJsonAsync<CursorPage<InstanceSummary>>(BasePath, parameters, ct);
    }

    /// <summary>逐页拉完符合条件的实例（自动跟游标）。</summary>
    /// <param name="query">查询条件（<see cref="InstanceQuery.Cursor"/> 会被忽略）。</param>
    /// <param name="ct">取消令牌。</param>
    public async IAsyncEnumerable<InstanceSummary> EnumerateAsync(
        InstanceQuery? query = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        query ??= new InstanceQuery();

        string? cursor = null;

        do
        {
            var page = await ListAsync(query with { Cursor = cursor }, ct).ConfigureAwait(false);

            foreach (var item in page.Items)
            {
                yield return item;
            }

            cursor = page.HasMore ? page.NextCursor : null;
        }
        while (cursor is not null);
    }

    /// <summary>查实例详情（状态 / 当前环节 / 待办 / 参数 / 聚合 / 结论，一次拿全）。</summary>
    /// <param name="instanceId">实例 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<InstanceView> GetAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        return _transport.GetJsonAsync<InstanceView>(Path(instanceId), null, ct);
    }

    /// <summary>查审批记录（时间线 + 环节聚合）。</summary>
    /// <param name="instanceId">实例 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<HistoryView> GetHistoryAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        return _transport.GetJsonAsync<HistoryView>(Path(instanceId) + "/history", null, ct);
    }

    /// <summary>
    /// 查参数面板（声明 + 值，按实例停在的那一版定义渲染，已按 <c>visibleTo</c> 过滤）。
    /// </summary>
    /// <param name="instanceId">实例 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<InstanceParameterView> GetParametersAsync(
        string instanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        return _transport.GetJsonAsync<InstanceParameterView>(Path(instanceId) + "/parameters", null, ct);
    }

    /// <summary>
    /// 查参数修订历史（谁在第几步把哪些参数改成了什么），修订号倒序。
    /// </summary>
    /// <param name="instanceId">实例 id。</param>
    /// <param name="limit">本页条数（服务端默认 20，上限 200）。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<IReadOnlyList<ParameterSnapshot>> GetParameterHistoryAsync(
        string instanceId,
        int? limit = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        List<KeyValuePair<string, string?>> parameters =
        [
            new("limit", limit?.ToString(CultureInfo.InvariantCulture)),
        ];

        return _transport.GetJsonAsync<IReadOnlyList<ParameterSnapshot>>(
            Path(instanceId) + "/parameters/history",
            parameters,
            ct);
    }

    /// <summary>取实例流程图 SVG（已走过的环节与当前环节着色）。</summary>
    /// <param name="instanceId">实例 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<string> GetDiagramSvgAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        return _transport.GetTextAsync(Path(instanceId) + "/diagram.svg", ct);
    }

    /// <summary>
    /// 发起流程（201）。
    /// </summary>
    /// <remarks>
    /// 请求体里<b>没有</b>发起人字段：发起人只来自令牌的 <c>sub</c>（代客调用走 <c>X-Subject-Id</c>）。
    /// 这是刻意的不对称——一旦允许请求体声明发起人，调用方就能替任意人开单。
    /// </remarks>
    /// <param name="request">发起请求。</param>
    /// <param name="idempotencyKey">幂等键（建议用「业务单据号 + 操作」）。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<StartInstanceResult> StartAsync(
        StartInstanceRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _transport.PostJsonAsync<StartInstanceResult>(BasePath, request, idempotencyKey, ct);
    }

    /// <summary>终止实例（撤销单据）。原因必填，进审批记录。</summary>
    /// <param name="instanceId">实例 id。</param>
    /// <param name="request">终止原因与可选的期望修订号。</param>
    /// <param name="idempotencyKey">幂等键；重复终止本身会 409，通常不需要。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<InstanceView> TerminateAsync(
        string instanceId,
        TerminateInstanceRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(request);

        return _transport.PostJsonAsync<InstanceView>(
            Path(instanceId) + "/terminate",
            request,
            idempotencyKey,
            ct);
    }

    private static string Path(string instanceId) =>
        BasePath + "/" + Uri.EscapeDataString(instanceId);
}

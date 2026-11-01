using System.Globalization;
using System.Runtime.CompilerServices;
using NetBpm.Platform.Contracts;

namespace NetBpm.Platform.Client;

/// <summary>
/// 待办（<c>/workflow/v1/tasks</c>）：列表、详情、表单、附件、提交、认领、取消认领、转办。
/// </summary>
/// <remarks>
/// <para>
/// 待办与已办共用同一个列表方法（<c>scope=DONE</c>）：两者的形状完全一致，
/// 只用 <see cref="TaskSummary.CompletedAt"/> 是否有值区分。分两个方法会让调用方
/// 在「切换到已办」时换一套代码路径，而这一步恰恰最容易漏参数。
/// </para>
/// <para>
/// 提交审批需要<b>真人主体</b>：纯服务令牌（不带 <c>X-Subject-Id</c> 也没有 session 主体）会被 403。
/// 这是「审批结论必须落在一个人身上」的结构性要求，不是配置问题——
/// 遇到 <c>auth.subject_required</c> 请检查是否漏了 <see cref="NetBpmClientOptions.SubjectId"/>。
/// </para>
/// </remarks>
public sealed class TasksClient
{
    private const string BasePath = "workflow/v1/tasks";

    private readonly PlatformTransport _transport;

    internal TasksClient(PlatformTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
    }

    /// <summary>分页查待办 / 已办。</summary>
    /// <param name="query">查询条件；为空表示「我的待办」第一页。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<CursorPage<TaskSummary>> ListAsync(
        TaskQuery? query = null,
        CancellationToken ct = default)
    {
        query ??= new TaskQuery();

        List<KeyValuePair<string, string?>> parameters =
        [
            new("scope", PlatformTransport.EnumText<TaskScope>(query.Scope)),
            new("processKey", query.ProcessKey),
            new("activityId", query.ActivityId),
            new("keyword", query.Keyword),
            new("overdueOnly", query.OverdueOnly is { } overdue ? (overdue ? "true" : "false") : null),
            new("cursor", query.Cursor),
            new("limit", query.Limit?.ToString(CultureInfo.InvariantCulture)),
        ];

        return _transport.GetJsonAsync<CursorPage<TaskSummary>>(BasePath, parameters, ct);
    }

    /// <summary>逐页拉完符合条件的待办（自动跟游标）。</summary>
    /// <param name="query">查询条件（<see cref="TaskQuery.Cursor"/> 会被忽略）。</param>
    /// <param name="ct">取消令牌。</param>
    public async IAsyncEnumerable<TaskSummary> EnumerateAsync(
        TaskQuery? query = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        query ??= new TaskQuery();

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

    /// <summary>查待办详情（含可选项、当前办理人、候选范围）。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<TaskView> GetAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return _transport.GetJsonAsync<TaskView>(Path(taskId), null, ct);
    }

    /// <summary>
    /// 查本环节的表单<b>快照</b>（提交当时的字段定义，不是当前定义）。
    /// </summary>
    /// <remarks>没有快照说明这个环节还没提交过：服务端返回 404，而不是空表单。</remarks>
    /// <param name="taskId">任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<FormSnapshot> GetFormAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return _transport.GetJsonAsync<FormSnapshot>(Path(taskId) + "/form", null, ct);
    }

    /// <summary>查本环节提交时带的附件引用（只读回放，平台不存文件本体）。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<IReadOnlyList<AttachmentRef>> GetAttachmentsAsync(
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return _transport.GetJsonAsync<IReadOnlyList<AttachmentRef>>(
            Path(taskId) + "/attachments",
            null,
            ct);
    }

    /// <summary>
    /// 提交审批（推进流程）。
    /// </summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="request">审批决定与写入的参数 / 表单值 / 附件。</param>
    /// <param name="idempotencyKey">幂等键（建议用「任务 id + 决定」）。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<SubmitTaskResult> SubmitAsync(
        string taskId,
        SubmitTaskRequest request,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(request);

        return _transport.PostJsonAsync<SubmitTaskResult>(
            Path(taskId) + "/submit",
            request,
            idempotencyKey,
            ct);
    }

    /// <summary>认领待办（把候选池里的任务「抢」到自己名下）。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="request">可带期望的当前办理人，用于抢单场景的乐观并发。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<TaskView> ClaimAsync(
        string taskId,
        ClaimTaskRequest? request = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return _transport.PostJsonAsync<TaskView>(
            Path(taskId) + "/claim",
            request ?? new ClaimTaskRequest(),
            idempotencyKey: null,
            ct);
    }

    /// <summary>取消认领（把待办退回候选池）；已未认领时幂等。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<TaskView> UnclaimAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return _transport.PostJsonAsync<TaskView>(
            Path(taskId) + "/unclaim",
            body: null,
            idempotencyKey: null,
            ct);
    }

    /// <summary>转办（把待办交给指定的人）。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="request">新的办理人与说明。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<TaskView> TransferAsync(
        string taskId,
        TransferTaskRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(request);

        return _transport.PostJsonAsync<TaskView>(
            Path(taskId) + "/transfer",
            request,
            idempotencyKey: null,
            ct);
    }

    private static string Path(string taskId) =>
        BasePath + "/" + Uri.EscapeDataString(taskId);
}

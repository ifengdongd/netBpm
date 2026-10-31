using System.Text.Json;
using NetBpm.Engine.Jobs;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 外部任务端点（M12 / S4）：worker 的<b>拉模式</b>执行通道。
/// <para>
/// 为什么需要它：独立部署下引擎进程里没有客户的业务代码，<c>activiti:class</c> 无从解析。
/// 标记了 <c>activiti:topic</c> 的 <c>serviceTask</c> 于是只登记一条外部任务，
/// 由 worker 拉走、干完、回执，引擎再推进流程。
/// </para>
/// <para>
/// <b>三条端点各自的契约</b>：
/// <list type="bullet">
///   <item><description><c>fetch</c>：按主题领取并加锁，返回该时刻的变量<b>快照</b>；</description></item>
///   <item><description><c>complete</c>：锁校验 → 写回变量 → 推进流程（<b>204</b>）；</description></item>
///   <item><description><c>failure</c>：扣重试 + 退避，耗尽进死信，<b>流程原地不动</b>（<b>204</b>）。</description></item>
/// </list>
/// </para>
/// <para>
/// 🔑 <b>worker 必须自己带 <c>workerId</c></b>：它是锁的持有者标识，回执时要对得上。
/// 不靠令牌反推 —— 同一家客户的多个 worker 实例经常共用一套凭据。
/// </para>
/// </summary>
public static partial class RuntimeEndpoints
{
    /// <summary>外部任务：领取 / 回执 / 释放 / 查询。</summary>
    private static RouteGroupBuilder MapExternalTaskEndpoints(this RouteGroupBuilder group)
    {
        // scope 只有一层（没有 instanceId / taskId 可解析，走不了 RequireInstanceAccess 那套数据级过滤器）：
        // 能力上限即全部，worker 凭 workflow:external:task 领取并回执
        group.MapPost("/external-tasks/fetch", FetchExternalTasksAsync)
            .RequireEngineScope(PlatformScopes.ExternalTask, "领取外部任务")
            .Produces<IReadOnlyList<ExternalTaskResponse>>();

        group.MapPost("/external-tasks/{taskId}/complete", CompleteExternalTaskAsync)
            .RequireEngineScope(PlatformScopes.ExternalTask, "回执外部任务")
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/external-tasks/{taskId}/failure", FailExternalTaskAsync)
            .RequireEngineScope(PlatformScopes.ExternalTask, "回执外部任务失败")
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/external-tasks/{taskId}/unlock", UnlockExternalTaskAsync)
            .RequireEngineScope(PlatformScopes.ExternalTask, "释放外部任务锁")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/external-tasks", FindExternalTasksAsync)
            .RequireEngineScope(PlatformScopes.ExternalTask, "查询外部任务")
            .Produces<IReadOnlyList<ExternalTaskResponse>>();

        return group;
    }

    /// <summary>
    /// 领取并加锁。<c>waitSeconds</c> 大于 0 时做<b>短轮询</b>：没有任务就每隔 1 秒再看，
    /// 直到有结果或超时（上限 30 秒）—— worker 不必用高频空轮询把接口打满。
    /// </summary>
    private static async Task<IResult> FetchExternalTasksAsync(
        FetchExternalTasksRequest? request,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (request is null)
        {
            throw new BadHttpRequestException("请求体不能为空（需要 workerId 与 topics）。");
        }

        var workerId = request.WorkerId;

        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new BadHttpRequestException("请求体必须带 workerId：它是锁的持有者，回执时要对得上。");
        }

        var topics = request.Topics?.Where(topic => !string.IsNullOrWhiteSpace(topic)).ToArray() ?? [];

        if (topics.Length == 0)
        {
            throw new BadHttpRequestException("请求体必须带至少一个 topic。");
        }

        var maxTasks = request.MaxTasks is > 0 ? Math.Min(request.MaxTasks.Value, 100) : 10;
        var waitSeconds = request.WaitSeconds is > 0 ? Math.Min(request.WaitSeconds.Value, 30) : 0;
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);

        List<ExternalTask> locked = [];

        // 短轮询而不是长轮询：长轮询要占住连接与事务，MySQL 下代价明显；
        // 1 秒间隔对 worker 吞吐没有可感知影响，却省掉了长事务
        while (true)
        {
            var batch = await engine.FetchExternalTasksAsync(
                workerId,
                topics,
                maxTasks,
                request.LockDurationMs is > 0 ? TimeSpan.FromMilliseconds(request.LockDurationMs.Value) : null,
                context.EffectiveTenantId,
                ct).ConfigureAwait(false);

            if (batch.Count > 0)
            {
                locked.AddRange(batch);
                break;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        return Results.Ok(locked.Select(ToResponse).ToArray());
    }

    /// <summary>成功回执：变量写回流程实例并推进。</summary>
    private static async Task<IResult> CompleteExternalTaskAsync(
        string taskId,
        CompleteExternalTaskRequest? request,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var workerId = request?.WorkerId;

        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new BadHttpRequestException("请求体必须带 workerId。");
        }

        await engine.CompleteExternalTaskAsync(
            taskId,
            workerId,
            JsonVariableReader.Read(request?.Variables),
            context.EffectiveTenantId,
            null,
            ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>失败回执：扣重试 + 退避；耗尽进死信（流程不推进）。</summary>
    private static async Task<IResult> FailExternalTaskAsync(
        string taskId,
        FailExternalTaskRequest? request,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (request is null)
        {
            throw new BadHttpRequestException("请求体不能为空（需要 workerId）。");
        }

        var workerId = request.WorkerId;

        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new BadHttpRequestException("请求体必须带 workerId。");
        }

        await engine.FailExternalTaskAsync(
            taskId,
            workerId,
            new ExternalTaskFailure(
                request.ErrorMessage,
                request.ErrorDetails,
                request.Retries,
                request.RetryAfter?.UtcDateTime),
            context.EffectiveTenantId,
            null,
            ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>主动释放锁：worker 放弃处理，任务立刻回到可领取。</summary>
    private static async Task<IResult> UnlockExternalTaskAsync(
        string taskId,
        UnlockExternalTaskRequest? request,
        RequestContext context,
        IProcessEngine engine,
        IExternalTaskBroker broker,
        CancellationToken ct)
    {
        var workerId = request?.WorkerId;

        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new BadHttpRequestException("请求体必须带 workerId。");
        }

        var task = await broker.FindByIdAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new BadHttpRequestException($"外部任务 '{taskId}' 不存在。");

        if (context.EffectiveTenantId is { Length: > 0 } tenantId
            && !string.Equals(task.TenantId ?? "default", tenantId, StringComparison.Ordinal))
        {
            throw new BadHttpRequestException($"外部任务 '{taskId}' 不存在。");
        }

        if (!await broker.UnlockAsync(taskId, workerId, ct).ConfigureAwait(false))
        {
            throw new BadHttpRequestException(
                $"外部任务 '{taskId}' 的锁不属于 worker '{workerId}'（可能锁已过期被别人领取）。");
        }

        return Results.NoContent();
    }

    /// <summary>按条件查询（管理端列表 / 死信排查）。</summary>
    private static async Task<IResult> FindExternalTasksAsync(
        string? topic,
        string? status,
        string? processInstanceId,
        int? maxResults,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var tasks = await engine.FindExternalTasksAsync(
            new ExternalTaskQuery(
                topic,
                processInstanceId,
                status,
                null,
                false,
                maxResults is > 0 ? Math.Min(maxResults.Value, 200) : 50,
                context.EffectiveTenantId),
            ct).ConfigureAwait(false);

        return Results.Ok(tasks.Select(ToResponse).ToArray());
    }

    private static ExternalTaskResponse ToResponse(ExternalTask task) => new(
        task.Id,
        task.ProcessInstanceId,
        task.ExecutionId,
        task.ProcessDefinitionId,
        task.ProcessDefinitionKey,
        task.ActivityId,
        task.ActivityName,
        task.Topic,
        task.TenantId,
        task.BusinessKey,
        task.Payload,
        task.Priority,
        task.Retries,
        task.Status,
        task.WorkerId,
        task.LockExpiresAt,
        task.NextDueAt,
        task.ErrorMessage,
        task.ErrorDetails,
        task.CreatedAt,
        task.CompletedAt);
}

/// <summary>领取请求。</summary>
/// <param name="WorkerId">worker 标识（锁持有者）。</param>
/// <param name="Topics">订阅的主题。</param>
/// <param name="MaxTasks">本次最多领几条（默认 10，上限 100）。</param>
/// <param name="LockDurationMs">锁时长（毫秒）；为空用服务端默认。</param>
/// <param name="WaitSeconds">没有任务时最多等几秒（长轮询，上限 30）。</param>
public sealed record FetchExternalTasksRequest(
    string WorkerId,
    IReadOnlyList<string>? Topics = null,
    int? MaxTasks = null,
    int? LockDurationMs = null,
    int? WaitSeconds = null);

/// <summary>成功回执。</summary>
/// <param name="WorkerId">锁持有者。</param>
/// <param name="Variables">要写回流程实例的变量。</param>
public sealed record CompleteExternalTaskRequest(
    string WorkerId,
    IDictionary<string, JsonElement>? Variables = null);

/// <summary>失败回执。</summary>
/// <param name="WorkerId">锁持有者。</param>
/// <param name="ErrorMessage">失败原因摘要。</param>
/// <param name="ErrorDetails">失败详情。</param>
/// <param name="Retries">指定<b>剩余</b>重试次数；为空表示当前值减一，减到 0 进死信。</param>
/// <param name="RetryAfter">多久后可再领；为空走服务端指数退避。</param>
public sealed record FailExternalTaskRequest(
    string WorkerId,
    string? ErrorMessage = null,
    string? ErrorDetails = null,
    int? Retries = null,
    DateTimeOffset? RetryAfter = null);

/// <summary>释放锁。</summary>
/// <param name="WorkerId">锁持有者。</param>
public sealed record UnlockExternalTaskRequest(string WorkerId);

/// <summary>
/// 外部任务视图。
/// </summary>
/// <param name="Id">任务 id。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ExecutionId">停留节点的执行 id。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="ProcessDefinitionKey">流程 key。</param>
/// <param name="ActivityId">节点 id。</param>
/// <param name="ActivityName">节点名。</param>
/// <param name="Topic">主题。</param>
/// <param name="TenantId">租户。</param>
/// <param name="BusinessKey">业务键。</param>
/// <param name="Payload">领取时的变量快照。</param>
/// <param name="Priority">优先级。</param>
/// <param name="Retries">剩余重试次数。</param>
/// <param name="Status">状态（PENDING / LOCKED / COMPLETED / DEAD_LETTER）。</param>
/// <param name="WorkerId">锁持有者。</param>
/// <param name="LockExpiresAt">锁到期时间。</param>
/// <param name="NextDueAt">下次可领时间（失败退避）。</param>
/// <param name="ErrorMessage">最近失败原因。</param>
/// <param name="ErrorDetails">最近失败详情。</param>
/// <param name="CreatedAt">登记时间。</param>
/// <param name="CompletedAt">回执时间。</param>
public sealed record ExternalTaskResponse(
    string Id,
    string ProcessInstanceId,
    string? ExecutionId,
    string? ProcessDefinitionId,
    string? ProcessDefinitionKey,
    string? ActivityId,
    string? ActivityName,
    string Topic,
    string? TenantId,
    string? BusinessKey,
    IDictionary<string, object?> Payload,
    int Priority,
    int Retries,
    string Status,
    string? WorkerId,
    DateTime? LockExpiresAt,
    DateTime? NextDueAt,
    string? ErrorMessage,
    string? ErrorDetails,
    DateTime CreatedAt,
    DateTime? CompletedAt);

using System.Collections.Frozen;

namespace NetBpm.ServerTaskWorker;

/// <summary>
/// 一个 topic 的业务实现。
/// </summary>
/// <remarks>
/// 真实项目里这里就是「3007 自己的 <c>/api/serverTask/{name}</c> 背后那段逻辑」——
/// worker 领到任务后不重复实现业务，而是调用同一个处理器（见 <see cref="ServerTaskDispatcher"/>），
/// 所以不存在「HTTP 一套、worker 一套」的漂移。
/// </remarks>
public interface IServerTaskHandler
{
    /// <summary>关心的主题（与 BPMN 里的 <c>activiti:topic</c> 对应）。</summary>
    string Topic { get; }

    /// <summary>执行业务逻辑。</summary>
    /// <param name="task">领取到的任务（含变量快照）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>要写回流程实例的变量。</returns>
    /// <exception cref="BusinessRuleException">业务口径的失败：按 <c>retryable</c> 决定是否消耗重试额度。</exception>
    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(ExternalTask task, CancellationToken ct);
}

/// <summary>
/// 「审批结论回写」类处理器的共同骨架。
/// </summary>
/// <remarks>
/// 通过 / 拒绝只差三个常量（期望的 <c>result</c>、落账状态、通道名），
/// 把幂等、校验、变量回写放在一处，免得两边改漏一个。
/// </remarks>
public abstract class LeaveCompletionHandlerBase : IServerTaskHandler
{
    private readonly ILeaveBillStore _store;
    private readonly ILogger _logger;

    /// <summary>构造。</summary>
    /// <param name="store">单据仓储。</param>
    /// <param name="logger">日志。</param>
    protected LeaveCompletionHandlerBase(ILeaveBillStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public abstract string Topic { get; }

    /// <summary>该节点语义上只可能收到的 <c>result</c> 值。</summary>
    protected abstract int ExpectedResult { get; }

    /// <summary>落账状态。</summary>
    protected abstract string TargetStatus { get; }

    /// <summary>回执里 <c>externalResult</c> 的取值。</summary>
    protected abstract string ResultLabel { get; }

    /// <summary>落账时记下的通道名。</summary>
    protected virtual string Via => "netbpm-external-task";

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        ExternalTask task,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        var payload = task.Payload;

        // 一致性自检：BPMN 的网关已经把 result=1 的流引到这里，值不对说明定义被改过。
        // 这类错误重试一百次也一样，直接 0 次重试进死信。
        if (TaskPayload.TryGetInt(payload, "result", out var result) && result != ExpectedResult)
        {
            throw new BusinessRuleException(
                $"{Topic} 收到了 result={result}，与节点语义不符（期望 {ExpectedResult}）",
                retryable: false,
                details: new { activityId = task.ActivityId, payload });
        }

        if (string.IsNullOrWhiteSpace(task.ProcessInstanceId))
        {
            throw new BusinessRuleException(
                $"{Topic} 的外部任务没有 processInstanceId，无法关联业务单据",
                retryable: false,
                details: new { activityId = task.ActivityId, taskId = task.Id });
        }

        // 🔑 业务单号：发起流程时传的 businessKey。
        // 它才是业务表的主键；实例 id 只是「这一次发起」，重新发起会变，所以只做兜底。
        var businessId = string.IsNullOrWhiteSpace(task.BusinessKey)
            ? $"LEAVE-{task.ProcessInstanceId}"
            : task.BusinessKey.Trim();

        var upsert = await _store
            .MarkProcessedAsync(
                businessId,
                task.ProcessInstanceId,
                TargetStatus,
                new LeaveBillPatch(
                    Applicant: TaskPayload.GetString(payload, "applyUser"),
                    Days: TaskPayload.TryGetInt(payload, "days", out var days) ? days : null,
                    Reason: TaskPayload.GetString(payload, "reason"),
                    Comment: TaskPayload.GetString(payload, "comment"),
                    Via: Via),
                ct)
            .ConfigureAwait(false);

        // 落账结果要能被看见：出问题时「业务号对不对、状态改没改」是第一现场。
        _logger.LogInformation(
            "外部任务 {TaskId} topic={Topic} 实例={InstanceId} 业务号={BusinessId} 目标状态={Target} → {Result}（当前 {Current}）",
            task.Id,
            Topic,
            task.ProcessInstanceId,
            businessId,
            TargetStatus,
            upsert.Conflict ? "终态冲突，未覆盖" : upsert.Duplicated ? "重复投递，已幂等" : "已落账",
            upsert.Bill.Status);

        // 回执变量会写回流程实例，后续节点（含结束之后的历史变量）都能看到。
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["externalResult"] = ResultLabel,
            ["externalBusinessId"] = upsert.Bill.Id,
            ["externalRef"] = upsert.Bill.Id,
            ["externalDuplicate"] = upsert.Duplicated,
            ["externalStatusConflict"] = upsert.Conflict,
            ["externalHandledAt"] = upsert.Bill.HandledAt,
        };
    }
}

/// <summary>「审批通过」的处理。</summary>
public sealed class ApproveCompleteHandler : LeaveCompletionHandlerBase
{
    /// <summary>构造。</summary>
    /// <param name="store">单据仓储。</param>
    /// <param name="logger">日志。</param>
    public ApproveCompleteHandler(ILeaveBillStore store, ILogger<ApproveCompleteHandler> logger)
        : base(store, logger)
    {
    }

    /// <inheritdoc />
    public override string Topic => "approveComplete";

    /// <inheritdoc />
    protected override int ExpectedResult => 1;

    /// <inheritdoc />
    protected override string TargetStatus => LeaveBillStatus.Approved;

    /// <inheritdoc />
    protected override string ResultLabel => "APPROVED";
}

/// <summary>「拒绝结束」的处理：结构与通过一致，只是状态与结论不同。</summary>
public sealed class RefuseCompleteHandler : LeaveCompletionHandlerBase
{
    /// <summary>构造。</summary>
    /// <param name="store">单据仓储。</param>
    /// <param name="logger">日志。</param>
    public RefuseCompleteHandler(ILeaveBillStore store, ILogger<RefuseCompleteHandler> logger)
        : base(store, logger)
    {
    }

    /// <inheritdoc />
    public override string Topic => "refuseComplete";

    /// <inheritdoc />
    protected override int ExpectedResult => 2;

    /// <inheritdoc />
    protected override string TargetStatus => LeaveBillStatus.Rejected;

    /// <inheritdoc />
    protected override string ResultLabel => "REJECTED";
}

/// <summary>
/// topic → 处理器。
/// </summary>
/// <remarks>
/// BPMN 里 <c>activiti:topic</c> 填什么，这里就注册什么（大小写敏感）。
/// 没注册的 topic 是一等错误：说明 BPMN 和 worker 的约定不一致，重试没有意义。
/// </remarks>
public sealed class ServerTaskDispatcher
{
    private readonly FrozenDictionary<string, IServerTaskHandler> _handlers;

    /// <summary>构造。</summary>
    /// <param name="handlers">所有已注册的处理器。</param>
    /// <exception cref="InvalidOperationException">同一个 topic 注册了两次。</exception>
    public ServerTaskDispatcher(IEnumerable<IServerTaskHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var map = new Dictionary<string, IServerTaskHandler>(StringComparer.Ordinal);

        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.Topic, handler))
            {
                throw new InvalidOperationException(
                    $"topic '{handler.Topic}' 注册了多次：{map[handler.Topic].GetType().Name} 与 {handler.GetType().Name}");
            }
        }

        _handlers = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>已注册的主题（健康检查与日志用）。</summary>
    public IReadOnlyCollection<string> RegisteredTopics => _handlers.Keys;

    /// <summary>按 topic 分派。</summary>
    /// <param name="topic">主题。</param>
    /// <param name="task">任务。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>要写回实例的变量。</returns>
    public Task<IReadOnlyDictionary<string, object?>> DispatchAsync(
        string topic,
        ExternalTask task,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!_handlers.TryGetValue(topic, out var handler))
        {
            throw new BusinessRuleException(
                $"未注册的 topic '{topic}'（已注册：{string.Join(", ", _handlers.Keys)}）",
                retryable: false,
                details: new { topic, activityId = task.ActivityId });
        }

        return handler.ExecuteAsync(task, ct);
    }
}

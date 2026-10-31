using Microsoft.Extensions.Logging;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Command;

/// <summary>
/// 外部任务失败回执。对应 Camunda 的 <c>HandleExternalTaskFailureCmd</c>。
/// <para>
/// <b>失败不推进流程</b>：节点原地不动，任务回到待领取（或退避后再可领）。
/// 这点是外部任务与异步作业最大的语义差别 —— 作业失败引擎自己重跑，
/// 外部任务失败要 worker 决定「还剩几次」，耗尽则进<b>死信</b>停在原地等管理干预。
/// </para>
/// <para>
/// ⚠️ 绝不「失败即跳过节点」：流程会看起来办完了，而那一步从没执行过。
/// 死信的意义就是让这个状态显式可见。
/// </para>
/// </summary>
public sealed class FailExternalTaskCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly string _workerId;
    private readonly ExternalTaskFailure _failure;
    private readonly string? _tenantId;

    public FailExternalTaskCommand(
        string taskId,
        string workerId,
        ExternalTaskFailure failure,
        string? tenantId = null,
        string? commandId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(failure);

        _taskId = taskId;
        _workerId = workerId;
        _failure = failure;
        _tenantId = tenantId;
        CommandId = commandId;
    }

    /// <inheritdoc />
    public string? CommandId { get; }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;

        var broker = services.ExternalTaskBroker
            ?? throw new EngineException("未注册 IExternalTaskBroker，无法回执外部任务失败。");

        var externalTask = await broker.FindByIdAsync(_taskId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"外部任务 '{_taskId}' 不存在（可能已被清理）。");

        // 跨租户按「不存在」处理（404 而不是 403）：403 会泄露「这个 id 在别的租户下存在」
        if (_tenantId is { Length: > 0 } tenantId
            && !string.Equals(externalTask.TenantId ?? "default", tenantId, StringComparison.Ordinal))
        {
            throw new EngineObjectNotFoundException($"外部任务 '{_taskId}' 不存在（可能已被清理或不属于当前租户）。");
        }

        var nowUtc = services.Clock.UtcNow;

        if (!await broker.FailureAsync(_taskId, _workerId, _failure, nowUtc, ct).ConfigureAwait(false))
        {
            throw new EngineConflictException(
                $"外部任务 '{_taskId}' 的锁不属于 worker '{_workerId}'（可能锁已过期被别的 worker 领取）。"
                + "失败回执只对当前持锁者生效。");
        }

        services.Logger?.LogWarning(
            "外部任务 {TaskId}（topic={Topic}，实例 {InstanceId}）回执失败，剩余重试 {Retries}：{Message}",
            _taskId,
            externalTask.Topic,
            externalTask.ProcessInstanceId,
            _failure.Retries ?? externalTask.Retries - 1,
            _failure.ErrorMessage);

        return Unit.Value;
    }
}

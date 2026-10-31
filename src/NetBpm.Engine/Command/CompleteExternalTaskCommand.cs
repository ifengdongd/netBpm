using NetBpm.Engine.Execution;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Command;

/// <summary>
/// 外部任务成功回执。对应 Camunda 的 <c>CompleteExternalTaskCmd</c>。
/// <para><b>顺序（与 <see cref="CompleteTaskCommand"/> 同构，不能调）</b>：</para>
/// <list type="number">
///   <item><description>任务存在性（<c>404</c>）→ <b>锁校验</b>（<c>409</c>）；</description></item>
///   <item><description>标记 <see cref="ExternalTaskStatus.Completed"/>；</description></item>
///   <item><description>回执变量写回执行（经作用域链落到流程实例）；</description></item>
///   <item><description><c>planTriggerExecutionOperation</c> —— 由议程把节点推过去。</description></item>
/// </list>
/// <para>
/// 🔑 <b>锁校验必须在这里做，不能只靠端点</b>：worker A 领取后锁过期、worker B 重新领走，
/// 此时 A 的回执必须被拒。若只看「任务存在」，A 会把 B 的执行结果写进流程，
/// 而 B 随后再回执时又会写一次 —— 一次调用变成两次副作用。
/// </para>
/// </summary>
public sealed class CompleteExternalTaskCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly string _workerId;
    private readonly IDictionary<string, object?>? _variables;
    private readonly string? _tenantId;

    public CompleteExternalTaskCommand(
        string taskId,
        string workerId,
        IDictionary<string, object?>? variables = null,
        string? tenantId = null,
        string? commandId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        _taskId = taskId;
        _workerId = workerId;
        _variables = variables;
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
            ?? throw new EngineException("未注册 IExternalTaskBroker，无法完成外部任务。");

        var externalTask = await broker.FindByIdAsync(_taskId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException(
                $"外部任务 '{_taskId}' 不存在（可能已回执、已被清理，或所属流程实例已删除）。");

        // 跨租户按「不存在」处理（404 而不是 403）：403 会泄露「这个 id 在别的租户下存在」
        if (_tenantId is { Length: > 0 } tenantId
            && !string.Equals(externalTask.TenantId ?? "default", tenantId, StringComparison.Ordinal))
        {
            throw new EngineObjectNotFoundException(
                $"外部任务 '{_taskId}' 不存在（可能已回执、已被清理，或不属于当前租户）。");
        }

        if (externalTask.Status == ExternalTaskStatus.DeadLetter)
        {
            throw new EngineConflictException(
                $"外部任务 '{_taskId}'（topic='{externalTask.Topic}'）已进死信：重试耗尽，"
                + "需由管理端点重投后才能再回执。");
        }

        var nowUtc = services.Clock.UtcNow;

        if (!await broker.CompleteAsync(_taskId, _workerId, nowUtc, ct).ConfigureAwait(false))
        {
            throw new EngineConflictException(
                $"外部任务 '{_taskId}' 的锁不属于 worker '{_workerId}'（可能锁已过期被别的 worker 领取，"
                + "或该任务已完成）。请重新领取后再回执。");
        }

        var execution = await services.ExecutionManager
            .FindByIdAsync(externalTask.ExecutionId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException(
                $"外部任务 '{_taskId}' 关联的执行 '{externalTask.ExecutionId}' 不存在，无法继续流程。");

        if (_variables is { Count: > 0 })
        {
            await execution.SetVariablesAsync(_variables, ct).ConfigureAwait(false);
        }

        services.Agenda.PlanTriggerExecutionOperation(execution);

        return Unit.Value;
    }
}

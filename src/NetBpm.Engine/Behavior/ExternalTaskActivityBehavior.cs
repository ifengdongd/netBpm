using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 外部任务行为（<c>activiti:topic</c>）。对应 Camunda 的 <c>ExternalTaskActivityBehavior</c>。
/// <para>
/// <b>执行语义</b>：登记一条外部任务后<b>原地等待</b>，不解析任何委托、不离开节点。
/// 这是它与 <see cref="ServiceTaskDelegateBehavior"/> 唯一的、也是最关键的区别 ——
/// 后者在进程内跑完就 <c>LeaveAsync</c>，前者要等 worker 回执
/// （<c>CompleteExternalTaskCommand</c>）才离开。
/// </para>
/// <para>
/// ⚠️ 未注入 <see cref="IExternalTaskBroker"/> 时<b>响亮失败</b>：独立部署下缺了它等于
/// 「这个节点永远没人执行」，静默跳过会让流程看起来办完了、其实那一步从没跑过。
/// </para>
/// </summary>
public sealed class ExternalTaskActivityBehavior(IEngineServices services) : FlowNodeActivityBehavior(services)
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var serviceTask = execution.CurrentFlowElement as ServiceTask
            ?? throw new EngineException(
                "Programmatic error: external task behaviour can only be applied to a ServiceTask instance, "
                + $"but got '{execution.CurrentFlowElement?.GetType().Name ?? "null"}'.");

        if (string.IsNullOrWhiteSpace(serviceTask.Topic))
        {
            throw new EngineException(
                $"服务任务 '{serviceTask.Id}' 被判为外部任务，但 activiti:topic 为空（部署期校验应已拒绝）。");
        }

        var broker = Services.ExternalTaskBroker
            ?? throw new EngineException(
                $"服务任务 '{serviceTask.Id}'（topic='{serviceTask.Topic}'）需要外部任务支撑，"
                + "但当前宿主未注册 IExternalTaskBroker。嵌入式宿主请改用 activiti:class / "
                + "activiti:delegateExpression；独立部署请注册外部任务存储。");

        // 变量快照：worker 拿到的是「领走那一刻」的值。之后引擎侧的变化不回灌给 worker，
        // worker 的回执变量才会写回流程实例 —— 两侧各写各的，不会互相覆盖
        var payload = await execution.GetVariablesAsync(ct).ConfigureAwait(false);

        var now = Services.Clock.UtcNow;

        await broker.CreateAsync(
            new ExternalTask
            {
                Id = Services.IdGenerator.NextId(),
                ProcessInstanceId = execution.ProcessInstanceId ?? execution.Id,
                ExecutionId = execution.Id,
                ProcessDefinitionId = execution.ProcessDefinitionId,
                // 定义 key 需要回查定义存储，而外部任务本身不靠它推进（靠 ExecutionId + ActivityId），
                // 这里不为一个回显字段多查一次库；端点需要时按 ProcessDefinitionId 补
                ProcessDefinitionKey = null,
                ActivityId = serviceTask.Id ?? execution.CurrentActivityId ?? string.Empty,
                ActivityName = serviceTask.Name,
                Topic = serviceTask.Topic,
                TenantId = ResolveTenantId(execution),
                BusinessKey = ResolveBusinessKey(execution),
                Payload = new Dictionary<string, object?>(payload, StringComparer.Ordinal),
                Priority = 0,
                Retries = ExternalTaskRetryPolicy.DefaultRetries,
                Status = ExternalTaskStatus.Pending,
                CreatedAt = now,
            },
            ct).ConfigureAwait(false);

        // 不离开节点：等 worker 回执（CompleteExternalTaskCommand 会 planTriggerExecutionOperation）
    }

    /// <inheritdoc />
    public override ValueTask TriggerAsync(
        IDelegateExecution execution,
        string? signalName,
        object? signalData,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return LeaveAsync(execution, ct);
    }

    private static string? ResolveTenantId(IDelegateExecution execution) =>
        execution is IExecutionEntity entity ? entity.TenantId : null;

    private static string? ResolveBusinessKey(IDelegateExecution execution) =>
        execution is IExecutionEntity entity ? entity.BusinessKey : null;
}

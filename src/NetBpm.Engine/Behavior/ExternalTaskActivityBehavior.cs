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

        var businessKey = await ResolveBusinessKeyAsync(execution, ct).ConfigureAwait(false);

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
                BusinessKey = businessKey,
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

    /// <summary>
    /// 取业务键（<c>BUSINESS_KEY_</c>）。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>只读当前执行是拿不到的</b>：业务键只写在<b>根执行</b>上，而流程走到服务任务时执行已经
    /// 下钻了一层（子执行、<c>PARENT_ID_</c> 指向根），子执行那一行的 <c>BUSINESS_KEY_</c> 是空的。
    /// 直接读会让 <c>nb_external_task.business_key</c> 变成一列永远填不上的死字段，
    /// worker 也就没法把回执对回业务单据 —— 这是外部任务唯一可靠的业务关联键。
    /// </remarks>
    /// <param name="execution">当前执行。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>业务键；没设过则为 <see langword="null"/>。</returns>
    private async ValueTask<string?> ResolveBusinessKeyAsync(
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (execution is not IExecutionEntity entity)
        {
            return null;
        }

        // 沿父链上溯（带深度上限：数据异常成环时不能把引擎卡死在这里）。
        var current = entity;

        for (var depth = 0; current is not null && depth < 64; depth++)
        {
            if (!string.IsNullOrWhiteSpace(current.BusinessKey))
            {
                return current.BusinessKey;
            }

            current = current.Parent;
        }

        // 跨命令从库里读回的子执行只带 PARENT_ID_，父链没被加载时上溯会空转；
        // 这时按流程实例 id 直接把根执行取回来再确认一次。
        var instanceId = execution.ProcessInstanceId;

        if (string.IsNullOrWhiteSpace(instanceId) || string.Equals(instanceId, entity.Id, StringComparison.Ordinal))
        {
            return null;
        }

        var root = await Services.ExecutionManager.FindByIdAsync(instanceId, ct).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(root?.BusinessKey) ? null : root.BusinessKey;
    }
}

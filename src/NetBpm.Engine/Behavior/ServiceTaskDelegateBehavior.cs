using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 服务任务行为：把三种写法（<c>class</c> / <c>delegateExpression</c> / <c>expression</c>）
/// 统一路由到 <see cref="IServiceTaskDelegate"/> 并调用。
/// 对应 Activiti 的 <c>ServiceTask*ActivityBehavior</c>（3 个 → 本方案 1 个）。
/// <para>
/// ⚠️ 事务约束（§5.3）：委托实现内部<b>不得</b> await 外部系统 IO，否则事务时长不可控。
/// 需要跨系统调用时应走「异步作业 + 回调」模型（本子集未支持，见 §1.3 第 7 条）。
/// </para>
/// </summary>
public sealed class ServiceTaskDelegateBehavior : FlowNodeActivityBehavior
{
    public ServiceTaskDelegateBehavior(IEngineServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var serviceTask = execution.CurrentFlowElement as ServiceTask
            ?? throw new EngineException(
                "Programmatic error: service task behaviour can only be applied to a ServiceTask instance, " +
                $"but got an instance of {execution.CurrentFlowElement}");

        var (target, expressionResult) = serviceTask.ImplementationType switch
        {
            ServiceTaskImplementationType.Class => (ResolveByClassName(serviceTask), (object?)null),
            ServiceTaskImplementationType.DelegateExpression => (ResolveByExpressionName(serviceTask), null),
            ServiceTaskImplementationType.Expression => await EvaluateExpressionAsync(serviceTask, execution, ct)
                .ConfigureAwait(false),
            ServiceTaskImplementationType.ExternalTopic => throw new EngineException(
                $"服务任务 '{serviceTask.Id}' 声明了 activiti:topic（topic='{serviceTask.Topic}'）："
                + "它应由外部任务行为处理、等 worker 回执，不该走委托解析路径（分发见 ActivityBehaviorFactory）。"),
            _ => throw new EngineException(
                $"服务任务 '{serviceTask.Id}' 未指定实现方式（activiti:class / delegateExpression / expression）。"),
        };

        // activiti:expression 的结果：可能是委托本身，也可能只是要存入变量的值
        if (target is IServiceTaskDelegate serviceDelegate)
        {
            // activiti:field 注入（B2.1）：委托执行前完成（字段可能带表达式）
            if (serviceTask.Fields.Count > 0)
            {
                await ServiceTaskFieldInjector
                    .InjectAsync(
                        serviceDelegate,
                        serviceTask.Fields,
                        execution,
                        Services.ExpressionManager,
                        serviceTask.Id ?? execution.CurrentActivityId ?? "(未知)",
                        ct)
                    .ConfigureAwait(false);
            }

            await serviceDelegate.ExecuteAsync(execution, ct).ConfigureAwait(false);
            await LeaveAsync(execution, ct).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(serviceTask.ResultVariableName))
        {
            await execution.SetVariableAsync(serviceTask.ResultVariableName!, expressionResult, ct).ConfigureAwait(false);
            await LeaveAsync(execution, ct).ConfigureAwait(false);
            return;
        }

        throw new EngineException(
            $"服务任务 '{serviceTask.Id}' 解析出的对象不是 IServiceTaskDelegate（实际类型：{target?.GetType().FullName ?? "null"}）。");
    }

    private object? ResolveByClassName(ServiceTask task)
    {
        if (string.IsNullOrEmpty(task.ClassName))
        {
            throw new EngineException($"服务任务 '{task.Id}' 的 activiti:class 为空。");
        }

        return Resolve(resolver => resolver.ResolveByClassName(task.ClassName!), task, "activiti:class");
    }

    private object? ResolveByExpressionName(ServiceTask task)
    {
        if (string.IsNullOrEmpty(task.DelegateExpression))
        {
            throw new EngineException($"服务任务 '{task.Id}' 的 activiti:delegateExpression 为空。");
        }

        return Resolve(resolver => resolver.ResolveByExpressionName(task.DelegateExpression!), task, "activiti:delegateExpression");
    }

    private object? Resolve(Func<IActivityDelegateResolver, object?> resolve, ServiceTask task, string attribute)
    {
        var resolver = Services.ActivityDelegateResolver
            ?? throw new EngineException(
                $"服务任务 '{task.Id}' 使用了 {attribute}，但未注册 IActivityDelegateResolver。");

        return resolve(resolver)
            ?? throw new EngineException(
                $"服务任务 '{task.Id}' 的 {attribute} 无法解析为已注册的实现。");
    }

    private async ValueTask<(object? Target, object? ExpressionResult)> EvaluateExpressionAsync(
        ServiceTask task,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(task.Expression))
        {
            throw new EngineException($"服务任务 '{task.Id}' 的 activiti:expression 为空。");
        }

        var expression = Services.ExpressionManager.CreateExpression(task.Expression);
        var value = await expression.GetValueAsync(execution, ct).ConfigureAwait(false);
        return (value, value);
    }
}

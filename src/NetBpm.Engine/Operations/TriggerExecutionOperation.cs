using NetBpm.Bpmn.Model;
using NetBpm.Engine.Agenda;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Operations;

/// <summary>
/// 触发当前节点上的行为（等待型节点被外部信号唤醒）。
/// 对应 Activiti 的 <c>TriggerExecutionOperation</c>。
/// <para>用户任务完成（<c>CompleteTaskCommand</c>）后就是通过本操作继续流程的。</para>
/// </summary>
public sealed class TriggerExecutionOperation : IOperation
{
    private readonly IExecutionEntity _execution;
    private readonly string? _signalName;
    private readonly object? _signalData;
    private readonly IEngineServices _services;

    public TriggerExecutionOperation(
        IExecutionEntity execution,
        IEngineServices services,
        string? signalName = null,
        object? signalData = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(services);

        _execution = execution;
        _services = services;
        _signalName = signalName;
        _signalData = signalData;
    }

    /// <inheritdoc />
    public string Name => nameof(TriggerExecutionOperation);

    /// <inheritdoc />
    public async ValueTask RunAsync(CancellationToken ct = default)
    {
        var currentFlowElement = await ResolveCurrentFlowElementAsync(ct).ConfigureAwait(false);

        switch (currentFlowElement)
        {
            case IFlowNode flowNode:
                {
                    // 与 ContinueProcessOperation 一致：行为实例统一由工厂创建
                    var behavior = _services.ActivityBehaviorFactory.Create(flowNode);
                    await behavior.TriggerAsync(_execution, _signalName, _signalData, ct).ConfigureAwait(false);
                    break;
                }

            // 对应 Activiti：当前元素是顺序流时，直接按条件继续
            case SequenceFlow:
                _services.Agenda.PlanTakeOutgoingSequenceFlowsOperation(_execution, evaluateConditions: true);
                break;

            default:
                throw new EngineException(
                    $"不支持的流程元素类型 '{currentFlowElement.GetType().Name}'（执行 '{_execution.Id}'）。");
        }
    }

    /// <summary>
    /// 取当前流程元素。执行实体只带 <c>ACT_ID_</c> 字符串时（跨命令从库里读回来的情况），
    /// 必须靠流程定义把它解析回对象 —— 对应 Activiti
    /// <c>ExecutionEntityImpl.getCurrentFlowElement()</c> 的懒解析。
    /// </summary>
    private async ValueTask<IFlowElement> ResolveCurrentFlowElementAsync(CancellationToken ct)
    {
        if (_execution.CurrentFlowElement is { } cached)
        {
            return cached;
        }

        var activityId = _execution.CurrentActivityId;

        if (string.IsNullOrEmpty(activityId))
        {
            throw new EngineException(
                $"执行 '{_execution.Id}' 没有当前流程元素（ACT_ID_ 为空），无法触发。");
        }

        var resolved = _services.ProcessDefinitionResolver is { } resolver
            ? await resolver
                .ResolveFlowElementAsync(_execution.ProcessDefinitionId, activityId, ct)
                .ConfigureAwait(false)
            : null;

        if (resolved is null)
        {
            throw new EngineException(
                $"执行 '{_execution.Id}' 的当前元素 '{activityId}' 无法在流程定义 " +
                $"'{_execution.ProcessDefinitionId}' 中解析到。" +
                "请确认宿主已注册 IProcessDefinitionResolver（数据库宿主由 NetBpm.Persistence 提供）。");
        }

        _execution.CurrentFlowElement = resolved;
        return resolved;
    }
}

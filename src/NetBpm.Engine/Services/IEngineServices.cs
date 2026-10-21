using Microsoft.Extensions.Logging;
using NetBpm.El;
using NetBpm.Engine.Agenda;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;

namespace NetBpm.Engine.Services;

/// <summary>
/// 引擎运行期依赖聚合。对应 Activiti 的 <c>CommandContext</c> 所暴露的一组 Manager。
/// </summary>
public interface IEngineServices
{
    IEngineAgenda Agenda { get; }

    IExecutionManager ExecutionManager { get; }

    IHistoryManager HistoryManager { get; }

    IExpressionManager ExpressionManager { get; }

    IIdGenerator IdGenerator { get; }

    IActivityBehaviorFactory ActivityBehaviorFactory { get; }

    /// <summary>服务任务实现解析器（<c>activiti:class</c> / <c>delegateExpression</c>）。</summary>
    IActivityDelegateResolver? ActivityDelegateResolver { get; }

    /// <summary>
    /// 任务管理器（P7）。宿主必须注入实现，否则用户任务无法创建/完成。
    /// </summary>
    Tasks.ITaskManager? TaskManager { get; }

    /// <summary>
    /// 流程元素解析器。跨命令恢复等待型节点时必须注入（见 <see cref="IProcessDefinitionResolver"/>）。
    /// </summary>
    IProcessDefinitionResolver? ProcessDefinitionResolver { get; }

    /// <summary>
    /// 定时作业存储（边界定时器）。未注入时用户任务<b>不会</b>创建定时器
    /// （宿主必须注册实现才能启用定时能力，见 <c>AddNetBpm</c>）。
    /// </summary>
    Jobs.ITimerJobManager? TimerJobManager { get; }

    /// <summary>
    /// 异步作业存储（<c>asyncBefore</c> / <c>asyncAfter</c>）。未注入时流程模型一旦使用异步标记，
    /// 运行到该节点会显式报错，而不是静默退化为同步执行。
    /// </summary>
    Jobs.IAsyncJobManager? AsyncJobManager { get; }

    /// <summary>
    /// 事务性 outbox。未启用时事件只走进程内监听器（<c>activiti:eventListener</c>），
    /// 不产生跨进程投递记录。
    /// </summary>
    Events.IEventOutbox? EventOutbox { get; }

    /// <summary>
    /// 引擎日志（可空）。事件监听器 throwOnFailure=false 的失败通过它记警告；
    /// 未注入时此类失败会响亮抛出，避免静默丢失语义。
    /// </summary>
    ILogger? Logger { get; }
}

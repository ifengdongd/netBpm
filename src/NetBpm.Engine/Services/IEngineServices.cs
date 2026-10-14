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
    /// 引擎日志（可空）。事件监听器 throwOnFailure=false 的失败通过它记警告；
    /// 未注入时此类失败会响亮抛出，避免静默丢失语义。
    /// </summary>
    ILogger? Logger { get; }
}

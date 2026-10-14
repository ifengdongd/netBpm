using Microsoft.Extensions.Logging;
using NetBpm.El;
using NetBpm.Engine.Agenda;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Services;

/// <summary><see cref="IEngineServices"/> 的默认实现。</summary>
public sealed class EngineServices : IEngineServices
{
    public EngineServices(
        IEngineAgenda agenda,
        IExecutionManager executionManager,
        IHistoryManager historyManager,
        IExpressionManager expressionManager,
        IIdGenerator idGenerator,
        IActivityBehaviorFactory activityBehaviorFactory,
        IActivityDelegateResolver? activityDelegateResolver = null,
        ITaskManager? taskManager = null,
        IProcessDefinitionResolver? processDefinitionResolver = null,
        ILogger? logger = null)
    {
        Agenda = agenda ?? throw new ArgumentNullException(nameof(agenda));
        ExecutionManager = executionManager ?? throw new ArgumentNullException(nameof(executionManager));
        HistoryManager = historyManager ?? throw new ArgumentNullException(nameof(historyManager));
        ExpressionManager = expressionManager ?? throw new ArgumentNullException(nameof(expressionManager));
        IdGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        ActivityBehaviorFactory = activityBehaviorFactory ?? throw new ArgumentNullException(nameof(activityBehaviorFactory));
        ActivityDelegateResolver = activityDelegateResolver;
        TaskManager = taskManager;
        ProcessDefinitionResolver = processDefinitionResolver;
        Logger = logger;
    }

    public IEngineAgenda Agenda { get; }

    public IExecutionManager ExecutionManager { get; }

    public IHistoryManager HistoryManager { get; }

    public IExpressionManager ExpressionManager { get; }

    public IIdGenerator IdGenerator { get; }

    public IActivityBehaviorFactory ActivityBehaviorFactory { get; }

    public IActivityDelegateResolver? ActivityDelegateResolver { get; }

    public ITaskManager? TaskManager { get; }

    public IProcessDefinitionResolver? ProcessDefinitionResolver { get; }

    public ILogger? Logger { get; }

    /// <summary>
    /// 装配入口。<see cref="Agenda.EngineAgenda"/> 需要在构造操作时拿到
    /// <see cref="IEngineServices"/>，而 <see cref="IEngineServices"/> 又暴露议程 ——
    /// 这里用延迟绑定的持有者打破该构造循环。
    /// </summary>
    public static EngineServices Create(
        IExecutionManager executionManager,
        IHistoryManager historyManager,
        IExpressionManager expressionManager,
        IIdGenerator idGenerator,
        IActivityBehaviorFactory activityBehaviorFactory,
        IActivityDelegateResolver? activityDelegateResolver = null,
        ITaskManager? taskManager = null,
        IProcessDefinitionResolver? processDefinitionResolver = null,
        ILogger? logger = null)
    {
        IEngineServices? holder = null;
        var agenda = new EngineAgenda(
            () => holder ?? throw new EngineException("引擎服务尚未完成装配，议程无法创建操作。"));

        var services = new EngineServices(
            agenda,
            executionManager,
            historyManager,
            expressionManager,
            idGenerator,
            activityBehaviorFactory,
            activityDelegateResolver,
            taskManager,
            processDefinitionResolver,
            logger);

        holder = services;
        return services;
    }
}

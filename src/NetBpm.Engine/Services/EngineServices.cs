using Microsoft.Extensions.Logging;
using NetBpm.El;
using NetBpm.Engine.Agenda;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Services;

/// <summary><see cref="IEngineServices"/> 的默认实现。</summary>
/// <remarks>
/// 🔑 本类只承载<b>无状态</b>依赖。议程（Agenda）是每命令一份的有状态对象，
/// 归属于 <see cref="CommandContext"/>；<see cref="Agenda"/> 在命令内解析到当前上下文的议程，
/// 命令外（诊断 / 测试）才退回一个兜底实例。
/// </remarks>
public sealed class EngineServices : IEngineServices
{
    /// <summary>命令外（诊断 / 测试）使用的兜底议程；命令内绝不使用它。</summary>
    private readonly EngineAgenda _commandlessAgenda;

    public EngineServices(
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
        ExecutionManager = executionManager ?? throw new ArgumentNullException(nameof(executionManager));
        HistoryManager = historyManager ?? throw new ArgumentNullException(nameof(historyManager));
        ExpressionManager = expressionManager ?? throw new ArgumentNullException(nameof(expressionManager));
        IdGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        ActivityBehaviorFactory = activityBehaviorFactory ?? throw new ArgumentNullException(nameof(activityBehaviorFactory));
        ActivityDelegateResolver = activityDelegateResolver;
        TaskManager = taskManager;
        ProcessDefinitionResolver = processDefinitionResolver;
        Logger = logger;

        _commandlessAgenda = new EngineAgenda(() => this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 命令内返回 <see cref="CommandContext.Agenda"/>（并发命令各用各的队列）；
    /// 命令外返回兜底议程，供 <c>Services.Agenda.PlanXxx</c> + <c>DrainAsync</c> 的诊断用法。
    /// </remarks>
    public IEngineAgenda Agenda => CommandContext.Current?.Agenda ?? _commandlessAgenda;

    public IExecutionManager ExecutionManager { get; }

    public IHistoryManager HistoryManager { get; }

    public IExpressionManager ExpressionManager { get; }

    public IIdGenerator IdGenerator { get; }

    public IActivityBehaviorFactory ActivityBehaviorFactory { get; }

    public IActivityDelegateResolver? ActivityDelegateResolver { get; }

    public ITaskManager? TaskManager { get; }

    public IProcessDefinitionResolver? ProcessDefinitionResolver { get; }

    public ILogger? Logger { get; }

    /// <summary>装配入口（保留工厂形态，宿主与测试统一走这里）。</summary>
    public static EngineServices Create(
        IExecutionManager executionManager,
        IHistoryManager historyManager,
        IExpressionManager expressionManager,
        IIdGenerator idGenerator,
        IActivityBehaviorFactory activityBehaviorFactory,
        IActivityDelegateResolver? activityDelegateResolver = null,
        ITaskManager? taskManager = null,
        IProcessDefinitionResolver? processDefinitionResolver = null,
        ILogger? logger = null) =>
        new(
            executionManager,
            historyManager,
            expressionManager,
            idGenerator,
            activityBehaviorFactory,
            activityDelegateResolver,
            taskManager,
            processDefinitionResolver,
            logger);
}

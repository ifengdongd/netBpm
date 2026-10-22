using System.Text;
using Dapper;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;
using NetBpm.El;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Command;
using NetBpm.Engine.Command.Interceptors;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Services;
using NetBpm.Identity;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Events;
using NetBpm.Persistence.Execution;
using NetBpm.Persistence.History;
using NetBpm.Persistence.Idempotency;
using NetBpm.Persistence.Jobs;
using NetBpm.Persistence.Migration;
using NetBpm.Persistence.Tasks;
using NetBpm.Runtime;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.IntegrationTests;

/// <summary>某次服务任务执行时刻的执行树快照。</summary>
/// <param name="ActivityId">触发快照的节点 id。</param>
/// <param name="Rows">当时的 <c>ACT_RU_EXECUTION</c> 全量行。</param>
internal sealed record ExecutionSnapshot(string ActivityId, List<ExecutionRow> Rows);

/// <summary>
/// 数据库宿主下的引擎装配（与 <c>NetBpm.Runtime</c> 的 DI 组合根保持一致，
/// 只是显式 new 出来，便于测试期间抓取中间态）。
/// </summary>
internal sealed class DatabaseEngineHarness
{
    /// <summary>默认构造：MySQL（既有测试的直连语义不变）。</summary>
    public DatabaseEngineHarness()
        : this(new TestDatabaseSpec(
            DatabaseType.MySql,
            MySqlTestDatabase.ConnectionString,
            "MySQL(localhost)"))
    {
    }

    /// <summary>按方言构造（双库功能矩阵用）。</summary>
    /// <param name="spec">目标库。</param>
    /// <param name="retryPolicy">重试策略；缺省用引擎默认（3 次）。并发用例可抬高上限。</param>
    /// <param name="xmlConverter">XML 转换器；缺省用真实实现，性能用例可注入计数器。</param>
    /// <param name="sqlCounter">
    /// SQL 语句计数器；缺省不计数（零开销，绝大多数用例用不到）。
    /// 传入后本 harness 建出的所有会话都会经过可计数的连接代理，
    /// 用例即可断言「跑完一个流程实例的语句条数上限」来防 N+1 回归。
    /// </param>
    public DatabaseEngineHarness(
        TestDatabaseSpec spec,
        CommandRetryPolicy? retryPolicy = null,
        IBpmnXmlConverter? xmlConverter = null,
        SqlStatementCounter? sqlCounter = null)
    {
        // 与生产组合根同策略：乐观锁 + 数据库死锁/锁等待超时
        var effectivePolicy = (retryPolicy ?? TransientDatabaseErrors.CreateRetryPolicy())
            .WithOnRetry((_, _) => Interlocked.Increment(ref _retryCount));

        SessionFactory = sqlCounter is null
            ? TestDatabases.CreateSessionFactory(spec)
            : new DbSessionFactory(
                new CountingDbConnectionFactory(
                    new DbConnectionFactory(spec.Type, spec.ConnectionString),
                    sqlCounter));

        var sessions = new CommandScopedDbSessionProvider(SessionFactory);
        Sessions = sessions;

        IdGenerator = new DbIdGenerator(new DapperPropertyStore(sessions));

        // 与 NetBpm.Runtime 的 DI 组合根保持同一张依赖图（含历史与变量）
        HistoryManager = new DapperHistoryManager(sessions, IdGenerator);
        VariableInstances = new DapperVariableInstanceManager(sessions, IdGenerator, HistoryManager);
        TaskManager = new DapperTaskManager(sessions, IdGenerator, HistoryManager, VariableInstances);
        TimerJobs = new DapperTimerJobManager(sessions);
        AsyncJobs = new DapperAsyncJobManager(sessions);
        Outbox = new DapperEventOutbox(sessions);
        MigrationLog = new DapperMigrationLog(sessions);
        CommandIdempotency = new DapperCommandIdempotencyStore(sessions, IdGenerator);
        Executions = new DapperExecutionManager(
            sessions, IdGenerator, TaskManager, VariableInstances, HistoryManager, TimerJobs);
        XmlConverter = xmlConverter ?? new BpmnXmlConverter();
        Definitions = new DapperProcessDefinitionStore(sessions, IdGenerator, XmlConverter);
        Recorder = new ExecutionSnapshotRecorder(sessions);

        IEngineServices? holder = null;
        var behaviorFactory = new ActivityBehaviorFactory(() => holder!);

        var services = EngineServices.Create(
            Executions,
            HistoryManager,
            new ExpressionManager(),
            IdGenerator,
            behaviorFactory,
            new HarnessDelegateResolver(Delegates, Recorder),
            TaskManager,
            new ProcessDefinitionResolver(Definitions),
            logger: null,
            TimerJobs,
            AsyncJobs,
            Outbox,
            CommandIdempotency);

        holder = services;

        // 与生产同序：Retry 在最外（重试换新会话）、Transaction 其次、CommandContext（排空议程）在内
        Executor = new CommandExecutor(
            [
                new RetryInterceptor(effectivePolicy),
                new TransactionInterceptor(),
                new CommandContextInterceptor(),
            ],
            () => new CommandContext(services, transactionFactory: () => SessionFactory.Create()));
    }

    private int _retryCount;

    /// <summary>本 harness 内实际发生的重试次数（并发用例取证：证明真的撞过锁）。</summary>
    public int RetryCount => Volatile.Read(ref _retryCount);

    public IBpmnXmlConverter XmlConverter { get; }

    public IDbSessionFactory SessionFactory { get; }

    public IDbSessionProvider Sessions { get; }

    public IIdGenerator IdGenerator { get; }

    public DapperExecutionManager Executions { get; }

    public DapperTaskManager TaskManager { get; }

    public DapperVariableInstanceManager VariableInstances { get; }

    public DapperHistoryManager HistoryManager { get; }

    public DapperTimerJobManager TimerJobs { get; }

    public DapperAsyncJobManager AsyncJobs { get; }

    public DapperEventOutbox Outbox { get; }

    public DapperMigrationLog MigrationLog { get; }

    public DapperCommandIdempotencyStore CommandIdempotency { get; }

    public DapperProcessDefinitionStore Definitions { get; }

    public ExecutionSnapshotRecorder Recorder { get; }

    /// <summary>
    /// 按名字注册的自定义服务任务委托（字段注入等场景用）；
    /// 未注册的名字沿用<b>快照委托</b>（既有测试的 delegateExpression 语义不变）。
    /// </summary>
    public IDictionary<string, IServiceTaskDelegate> Delegates { get; }
        = new Dictionary<string, IServiceTaskDelegate>(StringComparer.Ordinal);

    public ICommandExecutor Executor { get; }

    /// <summary>
    /// 与 harness 共用同一套 manager / executor 的引擎门面。
    /// <para>
    /// 用于验收「门面层语义」（挂起 / 激活、删除部署、历史分页）：它们都在门面里组合多个 manager，
    /// 只测 manager 会漏掉组合逻辑（例如挂起时漏改任务表）。
    /// </para>
    /// </summary>
    public IProcessEngine Engine => _engine ??= new ProcessEngine(
        Executor,
        XmlConverter,
        new ProcessValidator(),
        Definitions,
        TaskManager,
        new EmptyUserGroupManager(),
        Executions,
        HistoryManager,
        TimerJobs,
        MigrationLog,
        asyncJobs: AsyncJobs);

    private ProcessEngine? _engine;

    public void Reset()
    {
        using var session = SessionFactory.Create();
        MySqlTestDatabase.Reset(session);
    }

    /// <summary>直接读库取当前执行树（不走一级缓存，看的是库里的真实内容）。</summary>
    public List<ExecutionRow> ReadExecutions()
    {
        using var session = SessionFactory.Create();
        return ExecutionRowReader.ReadAll(session);
    }

    public long CountRows(string table)
    {
        using var session = SessionFactory.Create();
        return session.Connection.ExecuteScalar<long>($"select count(*) from {table}");
    }

    /// <summary>完成任务（走完整命令链路）。</summary>
    public async Task CompleteTaskAsync(
        string taskId,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        await Executor.ExecuteAsync(new CompleteTaskCommand(taskId, variables), ct).ConfigureAwait(false);
    }

    /// <summary>认领 / 取消认领任务（走完整命令链路）。</summary>
    public async Task ClaimTaskAsync(string taskId, string? userId, CancellationToken ct = default)
    {
        await Executor.ExecuteAsync(new ClaimTaskCommand(taskId, userId), ct).ConfigureAwait(false);
    }

    /// <summary>部署 + 启动实例 + 排空议程（= 一个命令的完整生命周期）。</summary>
    public Task<(ProcessDefinition Definition, string ProcessInstanceId)> DeployAndStartAsync(
        string bpmnXml,
        CancellationToken ct = default) => DeployAndStartAsync(bpmnXml, variables: null, ct);

    /// <summary>同上，并带上启动变量（用于变量持久化验收）。</summary>
    public async Task<(ProcessDefinition Definition, string ProcessInstanceId)> DeployAndStartAsync(
        string bpmnXml,
        IDictionary<string, object?>? variables,
        CancellationToken ct = default)
    {
        var model = XmlConverter.ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        var process = model.GetMainProcess()!;

        var definition = await Definitions.DeployAsync(model, "test.bpmn20.xml", ct: ct).ConfigureAwait(false);

        var command = new StartProcessInstanceCommand(process, definition.Id, "BIZ-DB-001", "tester", variables);
        var processInstanceId = await Executor.ExecuteAsync(command, ct).ConfigureAwait(false);

        return (definition, processInstanceId);
    }

    /// <summary>并行网关流程：start → fork → {taskA, taskB} → join → taskAfterJoin → end。</summary>
    public static string ParallelGatewayProcessXml => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="parallel-db" name="并行网关（库级验证）" isExecutable="true">
            <startEvent id="start" />
            <parallelGateway id="fork" />
            <serviceTask id="taskA" activiti:delegateExpression="recorder" />
            <serviceTask id="taskB" activiti:delegateExpression="recorder" />
            <parallelGateway id="join" />
            <serviceTask id="taskAfterJoin" activiti:delegateExpression="recorder" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
            <sequenceFlow id="f2" sourceRef="fork" targetRef="taskA" />
            <sequenceFlow id="f3" sourceRef="fork" targetRef="taskB" />
            <sequenceFlow id="f4" sourceRef="taskA" targetRef="join" />
            <sequenceFlow id="f5" sourceRef="taskB" targetRef="join" />
            <sequenceFlow id="f6" sourceRef="join" targetRef="taskAfterJoin" />
            <sequenceFlow id="f7" sourceRef="taskAfterJoin" targetRef="end" />
          </process>
        </definitions>
        """;
}

/// <summary>
/// 在服务任务执行时刻抓取 <c>ACT_RU_EXECUTION</c> 快照。
/// <para>
/// 之所以能抓到「命令中途」的状态：它取的是<b>当前命令的会话</b>，
/// 因此看到的是同一事务内未提交的行 —— 这正好把 Join 的中间态钉死。
/// </para>
/// </summary>
internal sealed class ExecutionSnapshotRecorder : IServiceTaskDelegate
{
    private readonly IDbSessionProvider _sessions;

    public ExecutionSnapshotRecorder(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    public List<ExecutionSnapshot> Snapshots { get; } = [];

    public async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();

        // 先 flush 一级缓存：否则读到的是库里尚未被内存改动覆盖的陈旧行
        // （Activiti 的 DbSqlSession.flush() 同样是查询前必经的一步）
        await lease.Session.FlushAsync(ct).ConfigureAwait(false);

        Snapshots.Add(new ExecutionSnapshot(
            execution.CurrentActivityId ?? "<null>",
            ExecutionRowReader.ReadAll(lease.Session)));
    }
}

/// <summary>把所有 <c>activiti:delegateExpression</c> 都解析到同一个委托实例。</summary>
internal sealed class HarnessDelegateResolver : IActivityDelegateResolver
{
    private readonly IDictionary<string, IServiceTaskDelegate> _delegates;
    private readonly IServiceTaskDelegate _fallback;

    public HarnessDelegateResolver(
        IDictionary<string, IServiceTaskDelegate> delegates,
        IServiceTaskDelegate fallback)
    {
        ArgumentNullException.ThrowIfNull(delegates);
        ArgumentNullException.ThrowIfNull(fallback);

        _delegates = delegates;
        _fallback = fallback;
    }

    public object? ResolveByClassName(string className) => null;

    public object? ResolveByExpressionName(string name) =>
        _delegates.TryGetValue(name, out var registered) ? registered : _fallback;
}

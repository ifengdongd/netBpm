using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.El;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Command;
using NetBpm.Engine.Command.Interceptors;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;

namespace NetBpm.UnitTests;

/// <summary>
/// 测试用引擎装配：内存执行树 + 内存任务表 + 内存 ID + 过渡版表达式管理器。
/// 目的与开发计划 §9.3 一致 —— 用自建语料把网关语义与用户任务生命周期钉死（风险 R1）。
/// </summary>
internal sealed class TestEngine
{
    private TestEngine(
        ICommandExecutor commandExecutor,
        IEngineServices services,
        InMemoryExecutionManager executionManager,
        InMemoryTaskManager taskManager,
        TestDelegateResolver delegateResolver,
        TestDefinitionResolver definitionResolver)
    {
        CommandExecutor = commandExecutor;
        Services = services;
        ExecutionManager = executionManager;
        TaskManager = taskManager;
        DelegateResolver = delegateResolver;
        Resolver = definitionResolver;
    }

    public ICommandExecutor CommandExecutor { get; }

    public IEngineServices Services { get; }

    public InMemoryExecutionManager ExecutionManager { get; }

    public InMemoryTaskManager TaskManager { get; }

    public TestDelegateResolver DelegateResolver { get; }

    public TestDefinitionResolver Resolver { get; }

    /// <summary>已执行的服务任务名，按执行顺序记录。</summary>
    public IReadOnlyList<string> ExecutedDelegates => DelegateResolver.Executed;

    public static TestEngine Create(IHistoryManager? historyManager = null)
    {
        IEngineServices? holder = null;
        var history = historyManager ?? new NullHistoryManager();

        // ⚠️ 执行树与任务表必须共用同一个 ID 生成器，否则会撞 id
        var idGenerator = new InMemoryIdGenerator();
        var taskManager = new InMemoryTaskManager(idGenerator, history);
        var executionManager = new InMemoryExecutionManager(idGenerator, taskManager, history);
        var delegateResolver = new TestDelegateResolver();
        var definitionResolver = new TestDefinitionResolver();
        var behaviorFactory = new ActivityBehaviorFactory(() => holder!);

        var services = EngineServices.Create(
            executionManager,
            history,
            new ExpressionManager(),
            idGenerator,
            behaviorFactory,
            delegateResolver,
            taskManager,
            definitionResolver);

        holder = services;

        var executor = new CommandExecutor(
            [new TransactionInterceptor(), new CommandContextInterceptor()],
            () => new CommandContext(services));

        return new TestEngine(executor, services, executionManager, taskManager, delegateResolver, definitionResolver);
    }

    /// <summary>解析 XML → 启动流程实例 → 排空议程。</summary>
    public async ValueTask<string> StartProcessAsync(
        string bpmnXml,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        var model = new BpmnXmlConverter().ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        var process = model.GetMainProcess()!;

        Resolver.Register("test:1:1", process);
        var command = new StartProcessInstanceCommand(process, "test:1:1", "biz-key", "tester", variables);
        return await CommandExecutor.ExecuteAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>完成任务（走完整命令链路）。</summary>
    public async ValueTask CompleteTaskAsync(
        string taskId,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default) =>
        await CommandExecutor.ExecuteAsync(new CompleteTaskCommand(taskId, variables), ct).ConfigureAwait(false);

    /// <summary>认领 / 取消认领任务。</summary>
    public async ValueTask ClaimTaskAsync(string taskId, string? userId) =>
        await CommandExecutor.ExecuteAsync(new ClaimTaskCommand(taskId, userId)).ConfigureAwait(false);

    /// <summary>改派任务（转办）。</summary>
    public async ValueTask SetAssigneeAsync(string taskId, string? assignee) =>
        await CommandExecutor.ExecuteAsync(new SetAssigneeCommand(taskId, assignee)).ConfigureAwait(false);

    /// <summary>流程实例跳转。</summary>
    public async ValueTask MoveActivityToAsync(string processInstanceId, string targetActivityId, string? reason = null) =>
        await CommandExecutor.ExecuteAsync(new MoveActivityCommand(processInstanceId, targetActivityId, reason)).ConfigureAwait(false);

    /// <summary>构建一个带指定服务的流程 XML（start → 顺序流 → end）。</summary>
    public static string SequentialProcess(string delegateName) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="sequential" name="顺序流" isExecutable="true">
            <startEvent id="start" />
            <serviceTask id="task" name="服务任务" activiti:delegateExpression="{{delegateName}}" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="task" />
            <sequenceFlow id="f2" sourceRef="task" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>
    /// 用户任务后再接一个服务任务：用户任务完成时写入的变量，可以在后续服务任务里观察到。
    /// </summary>
    public static string UserTaskThenServiceProcess(string delegateName) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="user-task-then-service" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="审批" activiti:assignee="kermit" />
            <serviceTask id="after" activiti:delegateExpression="{{delegateName}}" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="after" />
            <sequenceFlow id="f3" sourceRef="after" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>构建排他网关流程（条件分支 + 默认流）。</summary>
    public static string ExclusiveGatewayProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="exclusive" name="排他网关" isExecutable="true">
            <startEvent id="start" />
            <exclusiveGateway id="gw" default="flowDefault" />
            <serviceTask id="approvedTask" activiti:delegateExpression="approved" />
            <serviceTask id="rejectedTask" activiti:delegateExpression="rejected" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="gw" />
            <sequenceFlow id="flowApproved" sourceRef="gw" targetRef="approvedTask">
              <conditionExpression xsi:type="tFormalExpression" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">${approved}</conditionExpression>
            </sequenceFlow>
            <sequenceFlow id="flowDefault" sourceRef="gw" targetRef="rejectedTask" />
            <sequenceFlow id="f2" sourceRef="approvedTask" targetRef="end" />
            <sequenceFlow id="f3" sourceRef="rejectedTask" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>
    /// 构建包含网关流程（Fork 两条条件分支 + 一条默认分支 → Join → End）。
    /// <para>
    /// 关键差异用例：<c>a=true, b=false</c> 时只有一条分支产生令牌，
    /// Join 必须<b>不等待</b>从未激活的分支（并行网关在这里会死等）。
    /// </para>
    /// <para>默认分支存在的意义：校验规则要求包含网关「有默认流或至少一条无条件出流」。</para>
    /// </summary>
    public static string InclusiveGatewayProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     targetNamespace="http://netbpm/test">
          <process id="inclusive" name="包含网关" isExecutable="true">
            <startEvent id="start" />
            <inclusiveGateway id="inclFork" default="flowDefault" />
            <serviceTask id="branchA" activiti:delegateExpression="branchA" />
            <serviceTask id="branchB" activiti:delegateExpression="branchB" />
            <serviceTask id="noBranch" activiti:delegateExpression="noBranch" />
            <inclusiveGateway id="inclJoin" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="inclFork" />
            <sequenceFlow id="flowA" sourceRef="inclFork" targetRef="branchA">
              <conditionExpression xsi:type="tFormalExpression">${a}</conditionExpression>
            </sequenceFlow>
            <sequenceFlow id="flowB" sourceRef="inclFork" targetRef="branchB">
              <conditionExpression xsi:type="tFormalExpression">${b}</conditionExpression>
            </sequenceFlow>
            <sequenceFlow id="flowDefault" sourceRef="inclFork" targetRef="noBranch" />
            <sequenceFlow id="f2" sourceRef="branchA" targetRef="inclJoin" />
            <sequenceFlow id="f3" sourceRef="branchB" targetRef="inclJoin" />
            <sequenceFlow id="f5" sourceRef="noBranch" targetRef="inclJoin" />
            <sequenceFlow id="f4" sourceRef="inclJoin" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>构建并行网关流程（Fork → 两条服务任务 → Join → End）。</summary>
    public static string ParallelGatewayProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="parallel" name="并行网关" isExecutable="true">
            <startEvent id="start" />
            <parallelGateway id="fork" />
            <serviceTask id="branchA" activiti:delegateExpression="branchA" />
            <serviceTask id="branchB" activiti:delegateExpression="branchB" />
            <parallelGateway id="join" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
            <sequenceFlow id="f2" sourceRef="fork" targetRef="branchA" />
            <sequenceFlow id="f3" sourceRef="fork" targetRef="branchB" />
            <sequenceFlow id="f4" sourceRef="branchA" targetRef="join" />
            <sequenceFlow id="f5" sourceRef="branchB" targetRef="join" />
            <sequenceFlow id="f6" sourceRef="join" targetRef="end" />
          </process>
        </definitions>
        """;
}

/// <summary>记录执行顺序与可见变量的测试委托。</summary>
internal sealed class TestServiceTaskDelegate : IServiceTaskDelegate
{
    private readonly List<string> _log;
    private readonly List<IDictionary<string, object?>> _variables;
    private readonly string _name;

    public TestServiceTaskDelegate(string name, List<string> log, List<IDictionary<string, object?>> variables)
    {
        _name = name;
        _log = log;
        _variables = variables;
    }

    public async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        _log.Add(_name);
        _variables.Add(await execution.GetVariablesAsync(ct).ConfigureAwait(false));
    }
}

/// <summary>按名称解析测试委托。</summary>
internal sealed class TestDelegateResolver : IActivityDelegateResolver
{
    private readonly List<string> _executed = [];
    private readonly List<IDictionary<string, object?>> _capturedVariables = [];
    private readonly Dictionary<string, IServiceTaskDelegate> _delegates = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Executed => _executed;

    /// <summary>每次服务任务执行时它「看到」的变量快照。</summary>
    public IReadOnlyList<IDictionary<string, object?>> CapturedVariables => _capturedVariables;

    /// <summary>注册一个可被 <c>activiti:delegateExpression</c> 引用的委托。</summary>
    public TestDelegateResolver Register(string name)
    {
        _delegates[name] = new TestServiceTaskDelegate(name, _executed, _capturedVariables);
        return this;
    }

    /// <summary>注册一个自定义委托实例（字段注入等需要真实类型的场景）。</summary>
    public TestDelegateResolver Register(string name, IServiceTaskDelegate instance)
    {
        _delegates[name] = instance;
        return this;
    }

    public object? ResolveByClassName(string className) => null;

    public object? ResolveByExpressionName(string name)
    {
        if (_delegates.TryGetValue(name, out var existing))
        {
            return existing;
        }

        // 允许测试直接使用未显式注册的名字
        var created = new TestServiceTaskDelegate(name, _executed, _capturedVariables);
        _delegates[name] = created;
        return created;
    }
}

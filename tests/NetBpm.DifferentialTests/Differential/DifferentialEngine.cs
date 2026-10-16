using System.Globalization;
using Dapper;
using NetBpm.Bpmn.Converter;
using NetBpm.El;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Command;
using NetBpm.Engine.Command.Interceptors;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Execution;
using NetBpm.Persistence.History;
using NetBpm.Persistence.Tasks;
using NetBpm.Runtime;

namespace NetBpm.DifferentialTests.Differential;

/// <summary>
/// 对拍用的数据库宿主引擎（开发计划 P10）。
/// <para>
/// 与 <c>NetBpm.IntegrationTests.DatabaseEngineHarness</c> 同构（同一张依赖图、同样的拦截器顺序），
/// 差别只有两处，都是为了对拍：
/// <list type="number">
///   <item><description>历史管理器被 <see cref="TraceRecordingHistoryManager"/> 装饰 ⇒ 维度③ 有数据；</description></item>
///   <item><description>额外提供 <see cref="CaptureAsync"/>：把当前状态采成三维度快照。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>语料约束</b>：对拍只用「行为完全由引擎决定」的元素（事件 / 网关 / 用户任务 / 手工任务 / 顺序流）。
/// 服务任务与脚本任务依赖两端各自的委托实现与脚本引擎，属"实现差异"而非"引擎语义差异"，
/// 放进对拍会引入不可判定的差异 —— 因此这里的委托解析器**故意抛错**，语料一旦用到就立刻失败。
/// </para>
/// </summary>
public sealed class DifferentialEngine
{
    /// <summary>
    /// 参与维度②的表。
    /// <para>
    /// <c>ACT_GE_PROPERTY</c> 不在其中：它的 <c>next.dbid</c> 由 ID 分配进度决定，
    /// 属 §9.4 明确允许的"ID 具体值"差异（<c>schema.version</c> 另有专门的启动校验）。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> SnapshotTables =
    [
        "ACT_RU_EXECUTION",
        "ACT_RU_TASK",
        "ACT_RU_IDENTITYLINK",
        "ACT_RU_VARIABLE",
        "ACT_HI_PROCINST",
        "ACT_HI_ACTINST",
        "ACT_HI_TASKINST",
        "ACT_HI_VARINST",
        "ACT_HI_IDENTITYLINK",
        "ACT_RE_PROCDEF",
        "ACT_RE_DEPLOYMENT",
        "ACT_GE_BYTEARRAY",
    ];

    /// <summary>
    /// 维度②允许差异的列。§9.4 原文只允许两类：「时间戳」与「ID 具体值（若区块分配时序不同）」，
    /// 这里再加上已登记的例外 <c>ENGINE_VERSION_</c>（版本串必然不同）与 <c>DURATION_</c>
    /// （毫秒数，由执行时刻算得，与时间戳同族）。
    /// <para>
    /// ⚠️ 忽略 ID 具体值的代价：<b>丢掉"历史行引用对了哪个运行时行"这一类检查</b>
    /// （例如 <c>ACT_HI_ACTINST.TASK_ID_</c> 是否指向本次创建的 Task）。换来的收益是
    /// 行序稳定 —— 规范排序键里若含 ID，两端 ID 一不同，行序就被打乱，产生大面积假差异。
    /// 行数与"非 ID 列"的内容仍然全量比对，执行树多建/少建照样能抓到。
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> IgnoredColumns = new HashSet<string>(
    [
        // 时间戳与由它派生的耗时
        "START_TIME_", "END_TIME_", "DUE_DATE_", "CREATE_TIME_", "CLAIM_TIME_",
        "LAST_UPDATED_TIME_", "LOCK_TIME_", "DEPLOY_TIME_", "DURATION_",
        // 已登记的例外：引擎版本串
        "ENGINE_VERSION_",
        // ID 具体值：Activiti 用 UUID，NetBpm 用 ACT_GE_PROPERTY 计数器 ⇒ 必然不同
        "ID_", "PROC_DEF_ID_", "PROC_INST_ID_", "EXECUTION_ID_", "TASK_ID_",
        "DEPLOYMENT_ID_", "BYTEARRAY_ID_", "PARENT_ID_", "SUPER_EXEC_",
        "ROOT_PROC_INST_ID_",
        // 已登记的有意偏离（§15.10）：ACT_GE_BYTEARRAY.BYTES_
        // Activiti 存**原始 XML 字节**；我们的部署接口拿到的是已解析的模型，存的是重新序列化的 XML
        // （内容保真 —— P8 往返测试保证 DI 坐标与扩展属性零丢失，但不是字节一致）⇒ 列内容不可比
        "BYTES_",
    ],
        StringComparer.OrdinalIgnoreCase);

    private readonly DifferentialDelegateResolver _delegates = new();

    public DifferentialEngine(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        SessionFactory = new DbSessionFactory(new DbConnectionFactory(DatabaseType.MySql, connectionString));

        var sessions = new CommandScopedDbSessionProvider(SessionFactory);
        IdGenerator = new DbIdGenerator(new DapperPropertyStore(sessions));

        var history = new DapperHistoryManager(sessions, IdGenerator);
        Trace = new TraceRecordingHistoryManager(history);
        VariableInstances = new DapperVariableInstanceManager(sessions, IdGenerator, Trace);
        TaskManager = new DapperTaskManager(sessions, IdGenerator, Trace, VariableInstances);
        Executions = new DapperExecutionManager(sessions, IdGenerator, TaskManager, VariableInstances, Trace);
        Definitions = new DapperProcessDefinitionStore(sessions, IdGenerator, new BpmnXmlConverter());

        IEngineServices? holder = null;
        var behaviorFactory = new ActivityBehaviorFactory(() => holder!);

        var services = EngineServices.Create(
            Executions,
            Trace,
            new ExpressionManager(),
            IdGenerator,
            behaviorFactory,
            _delegates,
            TaskManager,
            new ProcessDefinitionResolver(Definitions));

        holder = services;

        Executor = new CommandExecutor(
            [new TransactionInterceptor(), new CommandContextInterceptor()],
            () => new CommandContext(services, SessionFactory.Create()));

        Engine = new ProcessEngine(
            Executor,
            new BpmnXmlConverter(),
            new NetBpm.Bpmn.Validation.ProcessValidator(),
            Definitions,
            TaskManager,
            new NetBpm.Identity.EmptyUserGroupManager(),
            Executions,
            Trace);
    }

    public IDbSessionFactory SessionFactory { get; }

    public IIdGenerator IdGenerator { get; }

    public DapperExecutionManager Executions { get; }

    public DapperTaskManager TaskManager { get; }

    public DapperVariableInstanceManager VariableInstances { get; }

    public TraceRecordingHistoryManager Trace { get; }

    public DapperProcessDefinitionStore Definitions { get; }

    public ICommandExecutor Executor { get; }

    /// <summary>引擎门面（对拍走它 = 走被测的 API 层）。</summary>
    public IProcessEngine Engine { get; }

    /// <summary>回到确定状态：清空引擎表 + 清空事件采集器。</summary>
    public void Reset()
    {
        using (var session = SessionFactory.Create())
        {
            EngineTableCleaner.Purge(session);
        }

        // 两个都要清：表清了但轨迹还在，就会把上一个语料的事件带进下一个
        Trace.Reset();
    }

    /// <summary>把当前状态采成三维度快照。</summary>
    public async Task<OracleSnapshot> CaptureAsync(
        string caseName,
        string processInstanceId,
        CancellationToken ct = default)
    {
        var api = await BuildApiViewAsync(processInstanceId, ct).ConfigureAwait(false);

        return new OracleSnapshot
        {
            CaseName = caseName,
            Api = api,
            Tables = ReadTables(),
            Events = Trace.Events,
        };
    }

    /// <summary>维度①：一律走 API（门面 / 历史管理器），不直接读表。</summary>
    private async Task<ApiView> BuildApiViewAsync(string processInstanceId, CancellationToken ct)
    {
        var execution = await Executions.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);
        var isEnded = execution is null;

        // 「未结束的活动」：两端同口径（C# 用历史查询；Java 用 HistoricActivityInstanceQuery.unfinished()）
        var activities = await Trace.FindActivityInstancesAsync(processInstanceId, ct).ConfigureAwait(false);

        var unfinished = activities
            .Where(activity => activity.IsActive)
            .Select(activity => activity.ActivityId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var businessKey = execution?.BusinessKey
            ?? (await Trace.FindProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false))?.BusinessKey;

        // 待办：该实例当前的全部待办（TaskQuery.processInstanceId）——
        // 两端同一条 API 语义，且比"逐个办理人查"更完整（不会漏掉未被列出的办理人）。
        // ⚠️ 必须排序：并行分支下待办多于一个时，DB 返回顺序不可依赖（Java 预言机同规则排序）
        var found = await Engine
            .FindTasksAsync(new TaskQuery(ProcessInstanceId: processInstanceId), ct)
            .ConfigureAwait(false);

        var tasks = found
            .OrderBy(task => task.TaskDefinitionKey, StringComparer.Ordinal)
            .ThenBy(task => task.Assignee, StringComparer.Ordinal)
            .Select(task => new ApiTask(task.TaskDefinitionKey, task.Assignee))
            .ToList();

        // 变量：实例结束后运行时变量已清除，此查询会失败 —— 那正是"已结束"的语义，记为无变量
        var variables = new List<ApiVariable>();

        if (!isEnded)
        {
            var values = await Engine.GetVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

            foreach (var (name, value) in values)
            {
                variables.Add(new ApiVariable(name, TypeNameOf(value), StringValueOf(value)));
            }
        }

        return new ApiView
        {
            ProcessInstances = [new ApiProcessInstance(businessKey, isEnded, unfinished)],
            Tasks = [.. tasks
                .OrderBy(task => task.TaskDefinitionKey, StringComparer.Ordinal)
                .ThenBy(task => task.Assignee, StringComparer.Ordinal)],
            Variables = [.. variables.OrderBy(variable => variable.Name, StringComparer.Ordinal)],
        };
    }

    /// <summary>维度②：dump 全部参与比对表（行按规范序排列，见 <see cref="RowOrdering"/>）。</summary>
    private Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadTables()
    {
        using var session = SessionFactory.Create();
        var tables = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in SnapshotTables)
        {
            var rows = new List<IReadOnlyDictionary<string, string?>>();

            foreach (IDictionary<string, object> row in session.Connection.Query($"select * from {table}"))
            {
                rows.Add(ToRow(row));
            }

            tables[table] = RowOrdering.Canonical(rows, IgnoredColumns);
        }

        return tables;
    }

    private static Dictionary<string, string?> ToRow(IDictionary<string, object> row)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (column, value) in row)
        {
            values[column] = StringValueOf(value);
        }

        return values;
    }

    /// <summary>
    /// 列值的规范化（🔒 两端必须一致，Java 侧见 <c>tools/Oracle</c> 的 <c>ValueNormalizer</c>）：
    /// NULL → <see langword="null"/>；布尔 → <c>1</c>/<c>0</c>；数值 → 不变文化；
    /// <c>DateTime</c> → <c>yyyy-MM-dd HH:mm:ss.fff</c>；<c>byte[]</c> → Base64；其余 → <c>ToString()</c>。
    /// </summary>
    public static string? StringValueOf(object? value) => value switch
    {
        null or DBNull => null,
        bool flag => flag ? "1" : "0",
        DateTime moment => moment.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>变量类型名（与 <c>ACT_RU_VARIABLE.TYPE_</c> 同口径）。</summary>
    private static string? TypeNameOf(object? value) => value switch
    {
        null => "null",
        string => "string",
        int => "integer",
        long => "long",
        double => "double",
        bool => "boolean",
        DateTime => "date",
        _ => value.GetType().Name,
    };

    private sealed class DifferentialDelegateResolver : IActivityDelegateResolver
    {
        public object? ResolveByClassName(string className) => throw new EngineException(
            $"对拍语料不得使用服务任务（activiti:class='{className}'）：委托实现属两端各自的实现细节，" +
            "纳入对拍会产生不可判定的差异。请改用用户任务 / 手工任务 / 网关来表达流程。");

        public object? ResolveByExpressionName(string name) => throw new EngineException(
            $"对拍语料不得使用服务任务（activiti:delegateExpression='{name}'）：理由同 ResolveByClassName。");
    }
}

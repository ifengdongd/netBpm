using System.Globalization;
using Dapper;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Database;
using NetBpm.Persistence.Execution;

namespace NetBpm.Persistence.History;

/// <summary>
/// 5 张历史表的 Dapper 写入实现（P5）。对应 Activiti 的 <c>DefaultHistoryManager</c> +
/// 各 <c>HistoricXxxEntityManager</c>，SQL 逐条对应 <c>db/mapping/entity/Historic*.xml</c>。
/// <para>
/// 🔑 <b>必须照搬的 id 与列语义</b>（对拍按行内容比对，错一处就整列偏移）：
/// <list type="bullet">
///   <item><description><c>ACT_HI_PROCINST.ID_ = PROC_INST_ID_ =</c> 流程实例 id（<b>不是</b>新生成）；</description></item>
///   <item><description><c>ACT_HI_TASKINST.ID_ = ACT_RU_TASK.ID_</c>；</description></item>
///   <item><description><c>ACT_HI_VARINST.ID_ = ACT_RU_VARIABLE.ID_</c>；</description></item>
///   <item><description><c>ACT_HI_IDENTITYLINK.ID_ = ACT_RU_IDENTITYLINK.ID_</c>；</description></item>
///   <item><description>只有 <c>ACT_HI_ACTINST.ID_</c> 是<b>新生成</b>的 id；</description></item>
///   <item><description><c>ACT_TYPE_</c> = 元素类名首字母小写（<c>userTask</c> / <c>serviceTask</c> /
///     <c>exclusiveGateway</c> …），不是 <c>getProperty("type")</c>；</description></item>
///   <item><description><b>顺序流不产生自己的历史活动行</b>：执行到顺序流时收口的是
///     <b>源节点</b>的那一行（<c>findActivityInstance</c> 对 SequenceFlow 取 <c>sourceFlowElement</c>）；</description></item>
///   <item><description><c>ACT_HI_ACTINST.TASK_ID_</c> / <c>ASSIGNEE_</c> 在任务创建/指派后<b>回填</b>。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 与 Activiti 的差异（已登记）：Activiti 的历史实体进 <c>DbSqlSession</c> 一级缓存、在 flush 时统一落库；
/// 本实现在<b>同一事务内即时写库</b>。提交后的可见结果一致，差别仅在事务内的写入时刻。
/// </para>
/// </summary>
public sealed class DapperHistoryManager : IHistoryManager
{
    private readonly IDbSessionProvider _sessions;
    private readonly IIdGenerator _idGenerator;

    public DapperHistoryManager(IDbSessionProvider sessions, IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idGenerator);

        _sessions = sessions;
        _idGenerator = idGenerator;
    }

    /// <inheritdoc />
    public bool IsHistoryEnabled => true;

    // ─────────────────────────── ACT_HI_PROCINST ───────────────────────────

    /// <inheritdoc />
    public async ValueTask RecordProcessInstanceStartAsync(
        IExecutionEntity processInstance,
        string? startActivityId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(processInstance);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                InsertHistoricProcessInstanceSql,
                new
                {
                    id = processInstance.Id,
                    processInstanceId = processInstance.Id,
                    businessKey = processInstance.BusinessKey,
                    processDefinitionId = processInstance.ProcessDefinitionId,
                    startTime = processInstance.StartTime ?? DateTime.UtcNow,
                    startUserId = processInstance.StartUserId,
                    startActivityId,
                    name = processInstance.Name,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordProcessInstanceEndAsync(
        string processInstanceId,
        string? deleteReason,
        string? currentFlowElementId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var startTime = await session.Connection.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(
                SelectHistoricProcessInstanceStartTimeSql,
                new { processInstanceId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        var endTime = DateTime.UtcNow;

        // markEnded 的守卫：只在 END_TIME_ 仍为空时才收口（可重复调用）
        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateHistoricProcessInstanceEndSql,
                new
                {
                    processInstanceId,
                    endTime,
                    duration = startTime is { } start ? (long)(endTime - start).TotalMilliseconds : (long?)null,
                    endActivityId = currentFlowElementId,
                    deleteReason,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    // ─────────────────────────── ACT_HI_ACTINST ───────────────────────────

    /// <inheritdoc />
    public async ValueTask RecordActivityStartAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var element = execution.CurrentFlowElement
            ?? throw new EngineException(
                $"执行 '{execution.Id}' 缺少当前流程元素，无法记录活动开始。" +
                "调用点（ContinueProcessOperation）必须保证元素已就绪。");

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                InsertHistoricActivityInstanceSql,
                new
                {
                    id = _idGenerator.NextId(),
                    processDefinitionId = execution.ProcessDefinitionId,
                    processInstanceId = execution.ProcessInstanceId ?? execution.Id,
                    executionId = execution.Id,
                    activityId = element.Id,
                    activityName = (element as FlowElement)?.Name,
                    activityType = ActivityTypeOf(element),
                    startTime = DateTime.UtcNow,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask RecordActivityEndAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default) =>
        MarkActivityEndedAsync(execution, deleteReason, ct);

    /// <inheritdoc />
    public ValueTask RecordActivityEndQuietlyAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default) =>
        MarkActivityEndedAsync(execution, deleteReason, ct);

    /// <summary>
    /// 收口历史活动行（两个端口的共同实现 —— 在持久化层它们做的事完全一样，
    /// 区别只在"是否代表执行离开了活动"，那是事件层（采集器）关心的事）。
    /// </summary>
    private async ValueTask MarkActivityEndedAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var activityId = ResolveActivityId(execution);

        if (activityId is null)
        {
            // 根执行的 ACT_ID_ 恒为空（它不参与流程推进），没有可收口的历史活动行
            return;
        }

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var startTime = await session.Connection.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(
                SelectUnfinishedActivityStartTimeSql,
                new { executionId = execution.Id, activityId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        var endTime = DateTime.UtcNow;

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateHistoricActivityEndSql,
                new
                {
                    executionId = execution.Id,
                    activityId,
                    endTime,
                    duration = startTime is { } start ? (long)(endTime - start).TotalMilliseconds : (long?)null,
                    deleteReason,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    // ─────────────────────────── ACT_HI_TASKINST ───────────────────────────

    /// <inheritdoc />
    public async ValueTask RecordTaskCreatedAsync(
        TaskEntity task,
        IExecutionEntity execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(execution);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                InsertHistoricTaskInstanceSql,
                new
                {
                    // id 与 ACT_RU_TASK 相同
                    id = task.Id,
                    processDefinitionId = task.ProcessDefinitionId,
                    processInstanceId = task.ProcessInstanceId ?? execution.ProcessInstanceId ?? execution.Id,
                    executionId = execution.Id,
                    name = task.Name,
                    parentTaskId = task.ParentTaskId,
                    description = task.Description,
                    owner = task.Owner,
                    assignee = task.Assignee,
                    startTime = DateTime.UtcNow,
                    claimTime = task.ClaimTime,
                    taskDefinitionKey = task.TaskDefinitionKey,
                    formKey = task.FormKey,
                    priority = task.Priority,
                    dueDate = task.DueDate,
                    category = task.Category,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordTaskIdAsync(TaskEntity task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (string.IsNullOrEmpty(task.ExecutionId) || string.IsNullOrEmpty(task.TaskDefinitionKey))
        {
            return;
        }

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateHistoricActivityTaskIdSql,
                new { executionId = task.ExecutionId, activityId = task.TaskDefinitionKey, taskId = task.Id },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordTaskAssignmentAsync(TaskEntity task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (string.IsNullOrEmpty(task.ExecutionId) || string.IsNullOrEmpty(task.TaskDefinitionKey))
        {
            return;
        }

        using var lease = _sessions.Acquire();

        // ACT_HI_ACTINST.ASSIGNEE_ 回填（对应 recordTaskAssignment）
        await lease.Session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateHistoricActivityAssigneeSql,
                new { executionId = task.ExecutionId, activityId = task.TaskDefinitionKey, assignee = task.Assignee },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordTaskFieldsChangedAsync(TaskEntity task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var lease = _sessions.Acquire();

        // 对应 recordTaskAssigneeChange / recordTaskOwnerChange / recordTaskClaim 等
        // —— 本子集把这些「任务字段变更」收敛成一次同步（见接口注释）
        await lease.Session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateHistoricTaskFieldsSql,
                new
                {
                    id = task.Id,
                    name = task.Name,
                    description = task.Description,
                    owner = task.Owner,
                    assignee = task.Assignee,
                    claimTime = task.ClaimTime,
                    dueDate = task.DueDate,
                    priority = task.Priority,
                    category = task.Category,
                    formKey = task.FormKey,
                },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordTaskEndAsync(string taskId, string? deleteReason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var startTime = await session.Connection.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(SelectHistoricTaskStartTimeSql, new { taskId }, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var endTime = DateTime.UtcNow;

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateHistoricTaskEndSql,
                new
                {
                    taskId,
                    endTime,
                    duration = startTime is { } start ? (long)(endTime - start).TotalMilliseconds : (long?)null,
                    deleteReason,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    // ─────────────────────────── ACT_HI_VARINST ───────────────────────────

    /// <inheritdoc />
    public async ValueTask RecordVariableCreateAsync(VariableInstance variable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);

        var columns = VariableTypeMapper.ToColumns(variable.Value);
        var now = DateTime.UtcNow;

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(
            new CommandDefinition(
                InsertHistoricVariableInstanceSql,
                new
                {
                    id = variable.Id,
                    processInstanceId = variable.ProcessInstanceId,
                    executionId = variable.ExecutionId,
                    taskId = variable.TaskId,
                    name = variable.Name,

                    // ⚠️ 历史行的 REV_ = Activiti 的 copyAndInsert 里的
                    // `setRevision(variableInstance.getRevision())` —— 复制的是**运行时实体的内存值**，
                    // 而创建那一刻内存里是 0（DB 的 REV_=1 是 INSERT 语句写死的，flush 才落库）。
                    // 因此历史行的 REV_ 恒为 0，与运行行无关（真值比对 2026-10-12：期望 0 / 实际 1）。
                    revision = 0,
                    type = columns.TypeName,
                    longValue = columns.LongValue,
                    doubleValue = columns.DoubleValue,
                    textValue = columns.TextValue,
                    textValue2 = columns.TextValue2,
                    createTime = now,
                    lastUpdatedTime = now,
                },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordVariableUpdateAsync(VariableInstance variable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);

        var columns = VariableTypeMapper.ToColumns(variable.Value);

        using var lease = _sessions.Acquire();

        // 同一行 UPDATE：只刷新值列与 LAST_UPDATED_TIME_（CREATE_TIME_ 不动）。
        // ⚠️ REV_ **不改**：Activiti 的 copyVariableValue 不碰 revision，历史行保持创建时的 0
        await lease.Session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateHistoricVariableInstanceSql,
                new
                {
                    id = variable.Id,
                    type = columns.TypeName,
                    longValue = columns.LongValue,
                    doubleValue = columns.DoubleValue,
                    textValue = columns.TextValue,
                    textValue2 = columns.TextValue2,
                    lastUpdatedTime = DateTime.UtcNow,
                },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricActivityInstance>> FindActivityInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(SelectActivityInstancesSql, new { processInstanceId }, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<HistoricActivityInstance>();

        foreach (IDictionary<string, object> row in rows)
        {
            result.Add(new HistoricActivityInstance(
                Text(row, "ID_")!,
                Text(row, "PROC_INST_ID_")!,
                Text(row, "EXECUTION_ID_"),
                Text(row, "ACT_ID_")!,
                Text(row, "ACT_NAME_"),
                Text(row, "ACT_TYPE_"),
                Text(row, "TASK_ID_"),
                Text(row, "ASSIGNEE_"),
                Date(row, "START_TIME_"),
                Date(row, "END_TIME_")));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<HistoricProcessInstance?> FindProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(SelectHistoricProcessInstanceSql, new { processInstanceId }, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        foreach (IDictionary<string, object> row in rows)
        {
            return new HistoricProcessInstance(
                Text(row, "ID_")!,
                Text(row, "PROC_INST_ID_")!,
                Text(row, "BUSINESS_KEY_"),
                Text(row, "PROC_DEF_ID_")!,
                Date(row, "START_TIME_"),
                Date(row, "END_TIME_"),
                Number(row, "DURATION_"),
                Text(row, "START_USER_ID_"),
                Text(row, "START_ACT_ID_"),
                Text(row, "END_ACT_ID_"),
                Text(row, "DELETE_REASON_"),
                Text(row, "NAME_"));
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricTaskInstance>> FindTaskInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(SelectHistoricTaskInstancesSql, new { processInstanceId }, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<HistoricTaskInstance>();

        foreach (IDictionary<string, object> row in rows)
        {
            result.Add(new HistoricTaskInstance(
                Text(row, "ID_")!,
                Text(row, "PROC_DEF_ID_"),
                Text(row, "TASK_DEF_KEY_"),
                Text(row, "PROC_INST_ID_"),
                Text(row, "EXECUTION_ID_"),
                Text(row, "NAME_"),
                Text(row, "ASSIGNEE_"),
                Text(row, "OWNER_"),
                (int?)Number(row, "PRIORITY_"),
                Text(row, "FORM_KEY_"),
                Date(row, "START_TIME_"),
                Date(row, "END_TIME_"),
                Number(row, "DURATION_"),
                Text(row, "DELETE_REASON_")));
        }

        return result;
    }

    // ─────────────────────────── ACT_HI_IDENTITYLINK ───────────────────────────

    /// <inheritdoc />
    public async ValueTask RecordIdentityLinkCreatedAsync(
        IdentityLinkRecord identityLink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identityLink);

        // 守卫与 Activiti 一致：只记流程实例级或任务级（纯流程定义级关系不记）
        if (identityLink.ProcessInstanceId is null && identityLink.TaskId is null)
        {
            return;
        }

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(
            new CommandDefinition(
                InsertHistoricIdentityLinkSql,
                new
                {
                    id = identityLink.Id,
                    type = identityLink.Type,
                    userId = identityLink.UserId,
                    groupId = identityLink.GroupId,
                    taskId = identityLink.TaskId,
                    processInstanceId = identityLink.ProcessInstanceId,
                },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// 顺序流不产生任何历史行（§15.4 H4：执行到顺序流时收口的是<b>源节点</b>那一行），
    /// 因此该端口**只承载事件语义**（Activiti 的 <c>SEQUENCEFLOW_TAKEN</c>），不写库。
    /// </summary>
    public ValueTask RecordSequenceFlowTakenAsync(IExecutionEntity execution, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    // ─────────────────────────── 辅助 ───────────────────────────

    /// <summary>
    /// 取「当前活动 id」。对应 Activiti <c>DefaultHistoryManager.findActivityInstance</c>：
    /// <b>顺序流用源节点 id</b>（注释原文：executing sequence flow listeners, we don't want historic activities）。
    /// </summary>
    private static string? ResolveActivityId(IExecutionEntity execution) =>
        execution.CurrentFlowElement switch
        {
            SequenceFlow sequenceFlow => sequenceFlow.SourceRef,
            { } element => element.Id,
            _ => execution.CurrentActivityId,
        };

    private static string? Text(IDictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value as string : null;

    private static DateTime? Date(IDictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is DateTime time ? time : null;

    private static long? Number(IDictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// <c>ACT_TYPE_</c> = 元素类名首字母小写（对应 <c>DefaultHistoryManager.parseActivityType</c>）。
    /// </summary>
    private static string ActivityTypeOf(IFlowElement element)
    {
        var name = element.GetType().Name;

        return name.Length == 0
            ? name
            : string.Concat(char.ToLowerInvariant(name[0]), name[1..]);
    }

    // ─────────────────────────── SQL（对应 Historic*.xml）───────────────────────────

    private const string InsertHistoricProcessInstanceSql = """
        insert into ACT_HI_PROCINST (
            ID_, PROC_INST_ID_, BUSINESS_KEY_, PROC_DEF_ID_, START_TIME_, END_TIME_, DURATION_,
            START_USER_ID_, START_ACT_ID_, END_ACT_ID_, SUPER_PROCESS_INSTANCE_ID_, DELETE_REASON_,
            TENANT_ID_, NAME_)
        values (
            @id, @processInstanceId, @businessKey, @processDefinitionId, @startTime, null, null,
            @startUserId, @startActivityId, null, null, null,
            '', @name)
        """;

    private const string SelectHistoricProcessInstanceStartTimeSql =
        "select START_TIME_ from ACT_HI_PROCINST where PROC_INST_ID_ = @processInstanceId";

    private const string UpdateHistoricProcessInstanceEndSql = """
        update ACT_HI_PROCINST set
            END_TIME_ = @endTime,
            DURATION_ = @duration,
            END_ACT_ID_ = @endActivityId,
            DELETE_REASON_ = @deleteReason
        where PROC_INST_ID_ = @processInstanceId and END_TIME_ is null
        """;

    private const string InsertHistoricActivityInstanceSql = """
        insert into ACT_HI_ACTINST (
            ID_, PROC_DEF_ID_, PROC_INST_ID_, EXECUTION_ID_, ACT_ID_, TASK_ID_, CALL_PROC_INST_ID_,
            ACT_NAME_, ACT_TYPE_, ASSIGNEE_, START_TIME_, END_TIME_, DURATION_, DELETE_REASON_, TENANT_ID_)
        values (
            @id, @processDefinitionId, @processInstanceId, @executionId, @activityId, null, null,
            @activityName, @activityType, null, @startTime, null, null, null, '')
        """;

    private const string SelectUnfinishedActivityStartTimeSql = """
        select START_TIME_ from ACT_HI_ACTINST
        where EXECUTION_ID_ = @executionId and ACT_ID_ = @activityId and END_TIME_ is null
        """;

    private const string UpdateHistoricActivityEndSql = """
        update ACT_HI_ACTINST set
            END_TIME_ = @endTime,
            DURATION_ = @duration,
            DELETE_REASON_ = @deleteReason
        where EXECUTION_ID_ = @executionId and ACT_ID_ = @activityId and END_TIME_ is null
        """;

    private const string UpdateHistoricActivityTaskIdSql = """
        update ACT_HI_ACTINST set TASK_ID_ = @taskId
        where EXECUTION_ID_ = @executionId and ACT_ID_ = @activityId and END_TIME_ is null
        """;

    private const string UpdateHistoricActivityAssigneeSql = """
        update ACT_HI_ACTINST set ASSIGNEE_ = @assignee
        where EXECUTION_ID_ = @executionId and ACT_ID_ = @activityId and END_TIME_ is null
        """;

    private const string InsertHistoricTaskInstanceSql = """
        insert into ACT_HI_TASKINST (
            ID_, PROC_DEF_ID_, TASK_DEF_KEY_, PROC_INST_ID_, EXECUTION_ID_, NAME_, PARENT_TASK_ID_,
            DESCRIPTION_, OWNER_, ASSIGNEE_, START_TIME_, CLAIM_TIME_, END_TIME_, DURATION_,
            DELETE_REASON_, PRIORITY_, DUE_DATE_, FORM_KEY_, CATEGORY_, TENANT_ID_)
        values (
            @id, @processDefinitionId, @taskDefinitionKey, @processInstanceId, @executionId, @name, @parentTaskId,
            @description, @owner, @assignee, @startTime, @claimTime, null, null,
            null, @priority, @dueDate, @formKey, @category, '')
        """;

    private const string SelectHistoricTaskStartTimeSql =
        "select START_TIME_ from ACT_HI_TASKINST where ID_ = @taskId";

    private const string UpdateHistoricTaskEndSql = """
        update ACT_HI_TASKINST set
            END_TIME_ = @endTime,
            DURATION_ = @duration,
            DELETE_REASON_ = @deleteReason
        where ID_ = @taskId and END_TIME_ is null
        """;

    private const string UpdateHistoricTaskFieldsSql = """
        update ACT_HI_TASKINST set
            NAME_ = @name,
            DESCRIPTION_ = @description,
            OWNER_ = @owner,
            ASSIGNEE_ = @assignee,
            CLAIM_TIME_ = @claimTime,
            DUE_DATE_ = @dueDate,
            PRIORITY_ = @priority,
            CATEGORY_ = @category,
            FORM_KEY_ = @formKey
        where ID_ = @id
        """;

    private const string InsertHistoricVariableInstanceSql = """
        insert into ACT_HI_VARINST (
            ID_, PROC_INST_ID_, EXECUTION_ID_, TASK_ID_, NAME_, REV_, VAR_TYPE_,
            BYTEARRAY_ID_, DOUBLE_, LONG_, TEXT_, TEXT2_, CREATE_TIME_, LAST_UPDATED_TIME_)
        values (
            @id, @processInstanceId, @executionId, @taskId, @name, @revision, @type,
            null, @doubleValue, @longValue, @textValue, @textValue2, @createTime, @lastUpdatedTime)
        """;

    private const string UpdateHistoricVariableInstanceSql = """
        update ACT_HI_VARINST set
            VAR_TYPE_ = @type,
            BYTEARRAY_ID_ = null,
            DOUBLE_ = @doubleValue,
            LONG_ = @longValue,
            TEXT_ = @textValue,
            TEXT2_ = @textValue2,
            LAST_UPDATED_TIME_ = @lastUpdatedTime
        where ID_ = @id
        """;

    /// <summary>流程图着色数据：按开始时间排序，便于前端按顺序播放。</summary>
    private const string SelectActivityInstancesSql = """
        select ID_, PROC_INST_ID_, EXECUTION_ID_, ACT_ID_, ACT_NAME_, ACT_TYPE_, TASK_ID_, ASSIGNEE_, START_TIME_, END_TIME_
        from ACT_HI_ACTINST
        where PROC_INST_ID_ = @processInstanceId
        order by START_TIME_, ID_
        """;

    private const string SelectHistoricProcessInstanceSql = """
        select ID_, PROC_INST_ID_, BUSINESS_KEY_, PROC_DEF_ID_, START_TIME_, END_TIME_, DURATION_,
               START_USER_ID_, START_ACT_ID_, END_ACT_ID_, DELETE_REASON_, NAME_
        from ACT_HI_PROCINST
        where PROC_INST_ID_ = @processInstanceId
        """;

    private const string SelectHistoricTaskInstancesSql = """
        select ID_, PROC_DEF_ID_, TASK_DEF_KEY_, PROC_INST_ID_, EXECUTION_ID_, NAME_, ASSIGNEE_, OWNER_,
               PRIORITY_, FORM_KEY_, START_TIME_, END_TIME_, DURATION_, DELETE_REASON_
        from ACT_HI_TASKINST
        where PROC_INST_ID_ = @processInstanceId
        order by START_TIME_, ID_
        """;

    private const string InsertHistoricIdentityLinkSql = """
        insert into ACT_HI_IDENTITYLINK (ID_, TYPE_, USER_ID_, GROUP_ID_, TASK_ID_, PROC_INST_ID_, DETAILS_)
        values (@id, @type, @userId, @groupId, @taskId, @processInstanceId, null)
        """;
}

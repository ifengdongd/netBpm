using System.Runtime.CompilerServices;
using Dapper;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Database;
using NetBpm.Persistence.Execution;
using static NetBpm.Persistence.RowValues;

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
public sealed class DapperHistoryManager : IHistoryManager, IHistoricQueryManager
{
    private readonly IDbSessionProvider _sessions;
    private readonly IIdGenerator _idGenerator;

    /// <summary>
    /// 命令内已写入的起始时间。收口历史行（结束时间/耗时）本来要先 SELECT 一次取 START_TIME_，
    /// 对于「同一命令内创建、同一命令内结束」的行（绝大多数活动与任务），这里直接命中内存，
    /// 省掉每次收口的一次查询；跨命令的行仍回落到 SELECT。
    /// </summary>
    private readonly ConditionalWeakTable<IDbSession, HistoryTimings> _timings = new();

    public DapperHistoryManager(IDbSessionProvider sessions, IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idGenerator);

        _sessions = sessions;
        _idGenerator = idGenerator;
    }

    private sealed class HistoryTimings
    {
        public Dictionary<string, DateTime> ProcessInstanceStarts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, DateTime> ActivityStarts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, DateTime> TaskStarts { get; } = new(StringComparer.Ordinal);
    }

    private static string ActivityStartKey(string executionId, string? activityId) =>
        string.Concat(executionId, "\u001f", activityId ?? string.Empty);

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

        var startTime = processInstance.StartTime ?? DateTime.UtcNow;
        _timings.GetOrCreateValue(session).ProcessInstanceStarts[processInstance.Id] = startTime;

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                InsertHistoricProcessInstanceSql,
                new
                {
                    id = processInstance.Id,
                    processInstanceId = processInstance.Id,
                    businessKey = processInstance.BusinessKey,
                    processDefinitionId = processInstance.ProcessDefinitionId,
                    startTime,
                    startUserId = processInstance.StartUserId,
                    startActivityId,
                    name = processInstance.Name,
                    tenantId = processInstance.TenantId ?? string.Empty,
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
        var timings = _timings.GetOrCreateValue(session);

        DateTime? startTime;

        if (timings.ProcessInstanceStarts.TryGetValue(processInstanceId, out var remembered))
        {
            startTime = remembered;
            timings.ProcessInstanceStarts.Remove(processInstanceId);
        }
        else
        {
            startTime = await session.Connection.ExecuteScalarAsync<DateTime?>(
                new CommandDefinition(
                    SelectHistoricProcessInstanceStartTimeSql,
                    new { processInstanceId },
                    session.Transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
        }

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

        var startTime = DateTime.UtcNow;
        _timings.GetOrCreateValue(session).ActivityStarts[ActivityStartKey(execution.Id, element.Id)] = startTime;

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
                    startTime,
                    tenantId = execution.TenantId ?? string.Empty,
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
        var timings = _timings.GetOrCreateValue(session);
        var key = ActivityStartKey(execution.Id, activityId);

        DateTime? startTime;

        if (timings.ActivityStarts.TryGetValue(key, out var remembered))
        {
            startTime = remembered;
            timings.ActivityStarts.Remove(key);
        }
        else
        {
            startTime = await session.Connection.ExecuteScalarAsync<DateTime?>(
                new CommandDefinition(
                    SelectUnfinishedActivityStartTimeSql,
                    new { executionId = execution.Id, activityId },
                    session.Transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
        }

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

        var startTime = DateTime.UtcNow;
        _timings.GetOrCreateValue(session).TaskStarts[task.Id] = startTime;

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
                    startTime,
                    claimTime = task.ClaimTime,
                    taskDefinitionKey = task.TaskDefinitionKey,
                    formKey = task.FormKey,
                    priority = task.Priority,
                    dueDate = task.DueDate,
                    category = task.Category,
                    tenantId = task.TenantId ?? string.Empty,
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
        var timings = _timings.GetOrCreateValue(session);

        DateTime? startTime;

        if (timings.TaskStarts.TryGetValue(taskId, out var remembered))
        {
            startTime = remembered;
            timings.TaskStarts.Remove(taskId);
        }
        else
        {
            startTime = await session.Connection.ExecuteScalarAsync<DateTime?>(
                new CommandDefinition(SelectHistoricTaskStartTimeSql, new { taskId }, session.Transaction, cancellationToken: ct))
                .ConfigureAwait(false);
        }

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
                    bytearrayId = variable.ByteArrayId,
                    longValue = columns.LongValue,
                    doubleValue = columns.DoubleValue,
                    textValue = columns.TextValue,
                    textValue2 = columns.TextValue2,
                    createTime = now,
                    lastUpdatedTime = now,
                    tenantId = variable.TenantId ?? string.Empty,
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
                    bytearrayId = variable.ByteArrayId,
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

        foreach (IDictionary<string, object?> row in rows)
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
                NullableDate(row, "START_TIME_"),
                NullableDate(row, "END_TIME_")));
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

        foreach (IDictionary<string, object?> row in rows)
        {
            return new HistoricProcessInstance(
                Text(row, "ID_")!,
                Text(row, "PROC_INST_ID_")!,
                Text(row, "BUSINESS_KEY_"),
                Text(row, "PROC_DEF_ID_")!,
                NullableDate(row, "START_TIME_"),
                NullableDate(row, "END_TIME_"),
                Long(row, "DURATION_"),
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

        foreach (IDictionary<string, object?> row in rows)
        {
            result.Add(MapTaskInstance(row));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricProcessInstance>> FindProcessInstancesAsync(
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(SelectAllHistoricProcessInstancesSql, null, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<HistoricProcessInstance>();

        foreach (IDictionary<string, object?> row in rows)
        {
            result.Add(MapProcessInstance(row));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricTaskInstance>> FindAllTaskInstancesAsync(
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(SelectAllHistoricTaskInstancesSql, null, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<HistoricTaskInstance>();

        foreach (IDictionary<string, object?> row in rows)
        {
            result.Add(MapTaskInstance(row));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricVariableInstance>> FindVariableInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(
                SelectHistoricVariableInstancesSql,
                new { processInstanceId },
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<HistoricVariableInstance>();

        foreach (IDictionary<string, object?> row in rows)
        {
            var typeName = Text(row, "VAR_TYPE_");
            var value = VariableTypeMapper.FromColumns(
                typeName,
                Long(row, "LONG_"),
                Double(row, "DOUBLE_"),
                Text(row, "TEXT_"),
                Bytes(row, "BYTES_"));

            result.Add(new HistoricVariableInstance(
                Text(row, "ID_")!,
                Text(row, "PROC_INST_ID_"),
                Text(row, "EXECUTION_ID_"),
                Text(row, "TASK_ID_"),
                Text(row, "NAME_")!,
                value,
                typeName,
                NullableDate(row, "CREATE_TIME_"),
                NullableDate(row, "LAST_UPDATED_TIME_")));
        }

        return result;
    }

    // ─────────────────────────── 服务端下推查询 ───────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricProcessInstance>> QueryProcessInstancesAsync(
        HistoricProcessInstanceFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var parameters = new DynamicParameters();
        var where = BuildProcessInstanceWhere(filter, parameters);
        var direction = filter.Descending ? "desc" : "asc";
        var paging = filter.MaxResults is { } take
            ? SqlPaging.Build(session.DatabaseType, filter.FirstResult, take, parameters)
            : SqlPaging.BuildUnlimited(session.DatabaseType, filter.FirstResult, parameters);

        var sql = $"""
            select ID_, PROC_INST_ID_, BUSINESS_KEY_, PROC_DEF_ID_, START_TIME_, END_TIME_, DURATION_,
                   START_USER_ID_, START_ACT_ID_, END_ACT_ID_, DELETE_REASON_, NAME_, TENANT_ID_
            from ACT_HI_PROCINST
            {where}
            order by START_TIME_ {direction}, ID_ asc
            {paging}
            """;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<HistoricProcessInstance>();

        foreach (IDictionary<string, object?> row in rows)
        {
            result.Add(MapProcessInstance(row));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<int> CountProcessInstancesAsync(
        HistoricProcessInstanceFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var parameters = new DynamicParameters();
        var where = BuildProcessInstanceWhere(filter, parameters);

        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_HI_PROCINST {where}",
            parameters,
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricTaskInstance>> QueryTaskInstancesAsync(
        HistoricTaskFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var parameters = new DynamicParameters();
        var where = BuildHistoricTaskWhere(filter, parameters);
        var direction = filter.Descending ? "desc" : "asc";
        var paging = filter.MaxResults is { } take
            ? SqlPaging.Build(session.DatabaseType, filter.FirstResult, take, parameters)
            : SqlPaging.BuildUnlimited(session.DatabaseType, filter.FirstResult, parameters);

        var sql = $"""
            select ID_, PROC_DEF_ID_, TASK_DEF_KEY_, PROC_INST_ID_, EXECUTION_ID_, NAME_, ASSIGNEE_, OWNER_,
                   PRIORITY_, FORM_KEY_, START_TIME_, END_TIME_, DURATION_, DELETE_REASON_, TENANT_ID_
            from ACT_HI_TASKINST
            {where}
            order by START_TIME_ {direction}, ID_ {direction}
            {paging}
            """;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<HistoricTaskInstance>();

        foreach (IDictionary<string, object?> row in rows)
        {
            result.Add(MapTaskInstance(row));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<int> CountTaskInstancesAsync(
        HistoricTaskFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var parameters = new DynamicParameters();
        var where = BuildHistoricTaskWhere(filter, parameters);

        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_HI_TASKINST {where}",
            parameters,
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricIdentityLink>> QueryIdentityLinksAsync(
        HistoricIdentityLinkFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var parameters = new DynamicParameters();
        var where = BuildIdentityLinkWhere(filter, parameters);
        var paging = filter.MaxResults is { } take
            ? SqlPaging.Build(session.DatabaseType, filter.FirstResult, take, parameters)
            : SqlPaging.BuildUnlimited(session.DatabaseType, filter.FirstResult, parameters);

        // ⚠️ 为什么必须 join ACT_HI_TASKINST：候选关系的行是「任务级」的，
        // PROC_INST_ID_ 为空、只有 TASK_ID_（见 DapperTaskManager.AddCandidateUsersAsync）。
        // 直接按 USER_ID_ 查表会拿到一堆实例 id 为空的行——尤其候选组只落任务级行，
        // 少了这层解析，「我所在组待批的单据」会被整类漏掉。
        var sql = $"""
            select l.ID_, l.TYPE_, l.USER_ID_, l.GROUP_ID_, l.TASK_ID_,
                   coalesce(l.PROC_INST_ID_, t.PROC_INST_ID_) as PROC_INST_ID_
            from ACT_HI_IDENTITYLINK l
            left join ACT_HI_TASKINST t on t.ID_ = l.TASK_ID_
            {where}
            order by l.ID_ asc
            {paging}
            """;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<HistoricIdentityLink>();

        foreach (IDictionary<string, object?> row in rows)
        {
            result.Add(MapIdentityLink(row));
        }

        return result;
    }

    private static HistoricIdentityLink MapIdentityLink(IDictionary<string, object?> row) =>
        new(
            Text(row, "ID_")!,
            Text(row, "TYPE_") ?? string.Empty,
            Text(row, "USER_ID_"),
            Text(row, "GROUP_ID_"),
            Text(row, "TASK_ID_"),
            Text(row, "PROC_INST_ID_"));

    /// <summary>
    /// 拼接历史身份关系的 WHERE：用户与组之间是 OR（命中任一即算参与），其余条件 AND。
    /// </summary>
    private static string BuildIdentityLinkWhere(
        HistoricIdentityLinkFilter filter,
        DynamicParameters parameters)
    {
        var conditions = new List<string>();
        var userOrGroup = new List<string>();

        // 解析不出实例的行（流程定义级关系）对「参与者索引」毫无意义，直接挡掉
        conditions.Add("coalesce(l.PROC_INST_ID_, t.PROC_INST_ID_) is not null");

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            userOrGroup.Add("l.USER_ID_ = @userId");
            parameters.Add("userId", filter.UserId);
        }

        if (filter.GroupIds is { Count: > 0 } groups)
        {
            userOrGroup.Add("l.GROUP_ID_ in @groupIds");
            parameters.Add("groupIds", groups);
        }

        if (userOrGroup.Count > 0)
        {
            conditions.Add($"({string.Join(" or ", userOrGroup)})");
        }

        if (!string.IsNullOrWhiteSpace(filter.ProcessInstanceId))
        {
            conditions.Add("coalesce(l.PROC_INST_ID_, t.PROC_INST_ID_) = @processInstanceId");
            parameters.Add("processInstanceId", filter.ProcessInstanceId);
        }

        if (!string.IsNullOrWhiteSpace(filter.TaskId))
        {
            conditions.Add("l.TASK_ID_ = @taskId");
            parameters.Add("taskId", filter.TaskId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            conditions.Add("l.TYPE_ = @type");
            parameters.Add("type", filter.Type);
        }

        return " where " + string.Join(" and ", conditions);
    }

    private static string BuildProcessInstanceWhere(
        HistoricProcessInstanceFilter filter,
        DynamicParameters parameters)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter.ProcessInstanceId))
        {
            conditions.Add("PROC_INST_ID_ = @processInstanceId");
            parameters.Add("processInstanceId", filter.ProcessInstanceId);
        }

        if (!string.IsNullOrWhiteSpace(filter.ProcessDefinitionId))
        {
            conditions.Add("PROC_DEF_ID_ = @processDefinitionId");
            parameters.Add("processDefinitionId", filter.ProcessDefinitionId);
        }

        if (filter.ProcessDefinitionIds is { Count: > 0 })
        {
            conditions.Add("PROC_DEF_ID_ in @definitionIds");
            parameters.Add("definitionIds", filter.ProcessDefinitionIds);
        }

        if (!string.IsNullOrWhiteSpace(filter.BusinessKey))
        {
            conditions.Add("BUSINESS_KEY_ = @businessKey");
            parameters.Add("businessKey", filter.BusinessKey);
        }

        if (!string.IsNullOrWhiteSpace(filter.StartUserId))
        {
            conditions.Add("START_USER_ID_ = @startUserId");
            parameters.Add("startUserId", filter.StartUserId);
        }

        if (filter.TenantId is not null)
        {
            // 等值比较保证 TENANT_ID_ 索引可用；历史 NULL 由初始化脚本 0012 段归一
            conditions.Add("TENANT_ID_ = @tenantId");
            parameters.Add("tenantId", filter.TenantId);
        }

        if (filter.TenantIds is { Count: > 0 })
        {
            // 「本租户 + 全局共享」用 in 表达：or 形式（= @t or = ''）在多数执行计划里会退化成扫索引全表
            conditions.Add("TENANT_ID_ in @tenantIds");
            parameters.Add("tenantIds", filter.TenantIds);
        }

        if (filter.Finished is { } finished)
        {
            conditions.Add(finished ? "END_TIME_ is not null" : "END_TIME_ is null");
        }

        if (filter.Deleted is { } deleted)
        {
            conditions.Add(deleted ? "DELETE_REASON_ is not null" : "DELETE_REASON_ is null");
        }

        AddRange(conditions, parameters, "START_TIME_", "startedAfter", ">=", filter.StartedAfter);
        AddRange(conditions, parameters, "START_TIME_", "startedBefore", "<=", filter.StartedBefore);
        AddRange(conditions, parameters, "END_TIME_", "endedAfter", ">=", filter.EndedAfter);
        AddRange(conditions, parameters, "END_TIME_", "endedBefore", "<=", filter.EndedBefore);

        return conditions.Count == 0 ? string.Empty : " where " + string.Join(" and ", conditions);
    }

    private static string BuildHistoricTaskWhere(HistoricTaskFilter filter, DynamicParameters parameters)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter.ProcessInstanceId))
        {
            conditions.Add("PROC_INST_ID_ = @processInstanceId");
            parameters.Add("processInstanceId", filter.ProcessInstanceId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Assignee))
        {
            conditions.Add("ASSIGNEE_ = @assignee");
            parameters.Add("assignee", filter.Assignee);
        }

        if (!string.IsNullOrWhiteSpace(filter.ProcessDefinitionId))
        {
            conditions.Add("PROC_DEF_ID_ = @processDefinitionId");
            parameters.Add("processDefinitionId", filter.ProcessDefinitionId);
        }

        if (!string.IsNullOrWhiteSpace(filter.TaskDefinitionKey))
        {
            conditions.Add("TASK_DEF_KEY_ = @taskDefinitionKey");
            parameters.Add("taskDefinitionKey", filter.TaskDefinitionKey);
        }

        if (filter.TenantId is not null)
        {
            // 等值比较保证 TENANT_ID_ 索引可用；历史 NULL 由初始化脚本 0012 段归一
            conditions.Add("TENANT_ID_ = @tenantId");
            parameters.Add("tenantId", filter.TenantId);
        }

        if (filter.OnlyFinished)
        {
            conditions.Add("END_TIME_ is not null");
        }

        AddRange(conditions, parameters, "END_TIME_", "finishedAfter", ">=", filter.FinishedAfter);
        AddRange(conditions, parameters, "END_TIME_", "finishedBefore", "<=", filter.FinishedBefore);

        return conditions.Count == 0 ? string.Empty : " where " + string.Join(" and ", conditions);
    }

    private static void AddRange(
        List<string> conditions,
        DynamicParameters parameters,
        string column,
        string parameterName,
        string comparison,
        DateTime? value)
    {
        if (value is null)
        {
            return;
        }

        conditions.Add($"{column} {comparison} @{parameterName}");
        parameters.Add(parameterName, value);
    }



    // ─────────────────────────── 行映射 ───────────────────────────

    private static HistoricProcessInstance MapProcessInstance(IDictionary<string, object?> row) =>
        new(
            Text(row, "ID_")!,
            Text(row, "PROC_INST_ID_")!,
            Text(row, "BUSINESS_KEY_"),
            Text(row, "PROC_DEF_ID_")!,
            NullableDate(row, "START_TIME_"),
            NullableDate(row, "END_TIME_"),
            Long(row, "DURATION_"),
            Text(row, "START_USER_ID_"),
            Text(row, "START_ACT_ID_"),
            Text(row, "END_ACT_ID_"),
            Text(row, "DELETE_REASON_"),
            Text(row, "NAME_"),
            TenantId: Text(row, "TENANT_ID_"));

    private static HistoricTaskInstance MapTaskInstance(IDictionary<string, object?> row) =>
        new(
            Text(row, "ID_")!,
            Text(row, "PROC_DEF_ID_"),
            Text(row, "TASK_DEF_KEY_"),
            Text(row, "PROC_INST_ID_"),
            Text(row, "EXECUTION_ID_"),
            Text(row, "NAME_"),
            Text(row, "ASSIGNEE_"),
            Text(row, "OWNER_"),
            Number(row, "PRIORITY_"),
            Text(row, "FORM_KEY_"),
            NullableDate(row, "START_TIME_"),
            NullableDate(row, "END_TIME_"),
            Long(row, "DURATION_"),
            Text(row, "DELETE_REASON_"),
            TenantId: Text(row, "TENANT_ID_"));

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
            @tenantId, @name)
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
            @activityName, @activityType, null, @startTime, null, null, null, @tenantId)
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
            null, @priority, @dueDate, @formKey, @category, @tenantId)
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
            BYTEARRAY_ID_, DOUBLE_, LONG_, TEXT_, TEXT2_, CREATE_TIME_, LAST_UPDATED_TIME_, TENANT_ID_)
        values (
            @id, @processInstanceId, @executionId, @taskId, @name, @revision, @type,
            @bytearrayId, @doubleValue, @longValue, @textValue, @textValue2, @createTime, @lastUpdatedTime, @tenantId)
        """;

    private const string UpdateHistoricVariableInstanceSql = """
        update ACT_HI_VARINST set
            VAR_TYPE_ = @type,
            BYTEARRAY_ID_ = @bytearrayId,
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
               START_USER_ID_, START_ACT_ID_, END_ACT_ID_, DELETE_REASON_, NAME_, TENANT_ID_
        from ACT_HI_PROCINST
        where PROC_INST_ID_ = @processInstanceId
        """;

    private const string SelectHistoricTaskInstancesSql = """
        select ID_, PROC_DEF_ID_, TASK_DEF_KEY_, PROC_INST_ID_, EXECUTION_ID_, NAME_, ASSIGNEE_, OWNER_,
               PRIORITY_, FORM_KEY_, START_TIME_, END_TIME_, DURATION_, DELETE_REASON_, TENANT_ID_
        from ACT_HI_TASKINST
        where PROC_INST_ID_ = @processInstanceId
        order by START_TIME_, ID_
        """;

    /// <summary>实例列表：按开始时间倒序（门面还会再排序 / 分页，这里先给稳定顺序）。</summary>
    private const string SelectAllHistoricProcessInstancesSql = """
        select ID_, PROC_INST_ID_, BUSINESS_KEY_, PROC_DEF_ID_, START_TIME_, END_TIME_, DURATION_,
               START_USER_ID_, START_ACT_ID_, END_ACT_ID_, DELETE_REASON_, NAME_, TENANT_ID_
        from ACT_HI_PROCINST
        order by START_TIME_ desc, ID_
        """;

    /// <summary>跨实例已办列表：按开始时间升序，与单实例查询同口径。</summary>
    private const string SelectAllHistoricTaskInstancesSql = """
        select ID_, PROC_DEF_ID_, TASK_DEF_KEY_, PROC_INST_ID_, EXECUTION_ID_, NAME_, ASSIGNEE_, OWNER_,
               PRIORITY_, FORM_KEY_, START_TIME_, END_TIME_, DURATION_, DELETE_REASON_, TENANT_ID_
        from ACT_HI_TASKINST
        order by START_TIME_, ID_
        """;

    /// <summary>
    /// 历史变量：<c>longString</c> 的值在 <c>ACT_GE_BYTEARRAY.BYTES_</c>，必须 LEFT JOIN 才能解码。
    /// </summary>
    private const string SelectHistoricVariableInstancesSql = """
        select V.ID_, V.PROC_INST_ID_, V.EXECUTION_ID_, V.TASK_ID_, V.NAME_, V.VAR_TYPE_,
               V.LONG_, V.DOUBLE_, V.TEXT_, V.CREATE_TIME_, V.LAST_UPDATED_TIME_, B.BYTES_
        from ACT_HI_VARINST V
        left join ACT_GE_BYTEARRAY B on B.ID_ = V.BYTEARRAY_ID_
        where V.PROC_INST_ID_ = @processInstanceId
        order by V.NAME_, V.ID_
        """;

    private const string InsertHistoricIdentityLinkSql = """
        insert into ACT_HI_IDENTITYLINK (ID_, TYPE_, USER_ID_, GROUP_ID_, TASK_ID_, PROC_INST_ID_, DETAILS_)
        values (@id, @type, @userId, @groupId, @taskId, @processInstanceId, null)
        """;
}

using System.Text;
using Dapper;
using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence.Tasks;

/// <summary>
/// <c>ACT_RU_TASK</c> + <c>ACT_RU_IDENTITYLINK</c> 的 Dapper 实现（P7）。
/// SQL 逐条对应 Activiti 的 <c>Task.xml</c> 与 <c>IdentityLink.xml</c>。
/// <para>
/// 与执行树仓储的设计差异（<b>有意为之</b>）：任务不做一级缓存与脏检查，写操作<b>即时落库</b>。
/// 原因是任务的写路径很窄（插入 / 改办理人 / 改所有者 / 认领 / 删除），
/// 且 Activiti 侧的脏检查同样只在 flush 点体现；即时写让「完成任务后立刻查询」这类场景天然一致。
/// 代价是「只改了 <c>CLAIM_TIME_</c>」的场景需要显式调用
/// <see cref="UpdateTaskAsync"/>（见 <see cref="ITaskManager.UpdateTaskAsync"/> 的说明）。
/// </para>
/// </summary>
public sealed class DapperTaskManager : ITaskManager
{
    /// <summary>单条多行 INSERT 的关系行数上限（8 列 × 50 = 400 个参数，远低于两方言上限）。</summary>
    private const int MaxIdentityLinksPerStatement = 50;

    private readonly IDbSessionProvider _sessions;
    private readonly IIdGenerator _idGenerator;
    private readonly IHistoryManager _historyManager;
    private readonly IVariableInstanceManager _variableInstances;

    public DapperTaskManager(
        IDbSessionProvider sessions,
        IIdGenerator idGenerator,
        IHistoryManager historyManager,
        NetBpm.Engine.Execution.IVariableInstanceManager variableInstances)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(historyManager);
        ArgumentNullException.ThrowIfNull(variableInstances);

        _sessions = sessions;
        _idGenerator = idGenerator;
        _historyManager = historyManager;
        _variableInstances = variableInstances;
    }

    /// <inheritdoc />
    public TaskEntity CreateTask() => new()
    {
        // 对应 TaskEntityManagerImpl.create()
        CreateTime = DateTime.UtcNow,
        Priority = TaskEntity.DefaultPriority,
        SuspensionState = TaskEntity.ActiveSuspensionState,
    };

    /// <inheritdoc />
    public async ValueTask InsertTaskAsync(
        TaskEntity task,
        IExecutionEntity execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(execution);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        if (string.IsNullOrEmpty(task.Id))
        {
            task.Id = _idGenerator.NextId();
        }

        // 对应 insert(task, execution)：从执行上继承上下文
        task.TenantId ??= execution.TenantId;
        task.ExecutionId = execution.Id;
        task.ProcessInstanceId = execution.ProcessInstanceId ?? execution.Id;
        task.ProcessDefinitionId = execution.ProcessDefinitionId;

        await session.Connection.ExecuteAsync(
            new CommandDefinition(InsertTaskSql, BuildTaskParameters(task), session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        task.Revision = 1;

        // 对应 TaskEntityManagerImpl.insert 的两行历史记录（AUDIT 级别）
        await _historyManager.RecordTaskCreatedAsync(task, execution, ct).ConfigureAwait(false);
        await _historyManager.RecordTaskIdAsync(task, ct).ConfigureAwait(false);

        // 对应 insert(task, fireCreateEvent)：若创建时已带 owner / assignee，补流程实例级 'participant'
        if (task.Owner is not null && task.ProcessInstanceId is not null)
        {
            await InvolveUserAsync(session, task.ProcessInstanceId, task.Owner, task.ProcessDefinitionId, ct)
                .ConfigureAwait(false);
        }

        if (task.Assignee is not null && task.ProcessInstanceId is not null)
        {
            await InvolveUserAsync(session, task.ProcessInstanceId, task.Assignee, task.ProcessDefinitionId, ct)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<TaskEntity?> FindTaskByIdAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var states = await QueryTasksAsync(session, SelectByIdSql, new { taskId }, ct).ConfigureAwait(false);

        return states.Count == 0 ? null : states[0].ToEntity();
    }

    /// <inheritdoc />
    public async ValueTask<IList<TaskEntity>> FindTasksByExecutionIdAsync(
        string executionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var states = await QueryTasksAsync(session, SelectByExecutionIdSql, new { executionId }, ct)
            .ConfigureAwait(false);

        return states.Select(state => state.ToEntity()).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IList<TaskEntity>> FindTasksByAssigneeAsync(
        string assignee,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assignee);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var states = await QueryTasksAsync(session, SelectByAssigneeSql, new { assignee }, ct).ConfigureAwait(false);

        return states.Select(state => state.ToEntity()).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IList<TaskEntity>> FindAllOpenTasksAsync(CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var states = await QueryTasksAsync(session, SelectAllSql, null, ct).ConfigureAwait(false);

        return states.Select(state => state.ToEntity()).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IList<TaskEntity>> FindTasksByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var states = await QueryTasksAsync(session, SelectByProcessInstanceIdSql, new { processInstanceId }, ct)
            .ConfigureAwait(false);

        return states.Select(state => state.ToEntity()).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IList<TaskEntity>> FindTasksByCandidateUserAsync(
        string userId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(groupIds);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var groupsClause = groupIds.Count > 0 ? " or L.GROUP_ID_ in @groupIds" : string.Empty;

        var sql = $"""
            select T.* from ACT_RU_TASK T
            where T.ASSIGNEE_ is null
              and exists (
                select 1 from ACT_RU_IDENTITYLINK L
                where L.TASK_ID_ = T.ID_
                  and L.TYPE_ = '{IdentityLinkType.Candidate}'
                  and (L.USER_ID_ = @userId{groupsClause})
              )
            """;

        var states = await QueryTasksAsync(session, sql, new { userId, groupIds }, ct).ConfigureAwait(false);

        return states.Select(state => state.ToEntity()).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IList<TaskEntity>> FindTasksByCandidateGroupAsync(
        string groupId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 与候选人口径一致：只取未签收的任务；候选组只存在任务级身份关系里
        const string sql = """
            select T.* from ACT_RU_TASK T
            where T.ASSIGNEE_ is null
              and exists (
                select 1 from ACT_RU_IDENTITYLINK L
                where L.TASK_ID_ = T.ID_
                  and L.TYPE_ = 'candidate'
                  and L.GROUP_ID_ = @groupId
              )
            """;

        var states = await QueryTasksAsync(session, sql, new { groupId }, ct).ConfigureAwait(false);

        return states.Select(state => state.ToEntity()).ToList();
    }

    /// <inheritdoc />
    public async ValueTask UpdateTaskAsync(TaskEntity task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var lease = _sessions.Acquire();
        await WriteTaskAsync(lease.Session, task, ct).ConfigureAwait(false);

        // 认领等「显式更新」要同步历史任务行的可变列（对应 recordTaskClaim 等）
        await _historyManager.RecordTaskFieldsChangedAsync(task, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ChangeTaskAssigneeAsync(TaskEntity task, string? assignee, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var changed = (task.Assignee is not null && !string.Equals(task.Assignee, assignee, StringComparison.Ordinal))
            || (task.Assignee is null && assignee is not null);

        if (!changed)
        {
            return;
        }

        task.Assignee = assignee;

        if (task.Id.Length == 0)
        {
            return;
        }

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // addAssigneeIdentityLinks：只在流程实例上记 'participant'（不写任务级 'assignee' 行）
        if (assignee is not null && task.ProcessInstanceId is not null)
        {
            await InvolveUserAsync(session, task.ProcessInstanceId, assignee, task.ProcessDefinitionId, ct)
                .ConfigureAwait(false);
        }

        await WriteTaskAsync(session, task, ct).ConfigureAwait(false);

        // 对应 changeTaskAssignee → recordTaskAssignment（回填 ACT_HI_ACTINST.ASSIGNEE_）
        // 与 recordTaskAssigneeChange（更新 ACT_HI_TASKINST.ASSIGNEE_）
        await _historyManager.RecordTaskAssignmentAsync(task, ct).ConfigureAwait(false);
        await _historyManager.RecordTaskFieldsChangedAsync(task, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ChangeTaskOwnerAsync(TaskEntity task, string? owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var changed = (task.Owner is not null && !string.Equals(task.Owner, owner, StringComparison.Ordinal))
            || (task.Owner is null && owner is not null);

        if (!changed)
        {
            return;
        }

        task.Owner = owner;

        if (task.Id.Length == 0)
        {
            return;
        }

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        if (owner is not null && task.ProcessInstanceId is not null)
        {
            await InvolveUserAsync(session, task.ProcessInstanceId, owner, task.ProcessDefinitionId, ct)
                .ConfigureAwait(false);
        }

        await WriteTaskAsync(session, task, ct).ConfigureAwait(false);

        // 对应 recordTaskOwnerChange
        await _historyManager.RecordTaskFieldsChangedAsync(task, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AddCandidateUsersAsync(
        TaskEntity task,
        IEnumerable<string> candidateUsers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(candidateUsers);

        var users = candidateUsers as IReadOnlyList<string> ?? candidateUsers.ToList();

        if (users.Count == 0)
        {
            return;
        }

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 候选关系一次多行插入（原来每人一条 insert）
        await AddIdentityLinksInternalAsync(
            session,
            users.Select(user => new IdentityLinkRecord
            {
                Type = IdentityLinkType.Candidate,
                UserId = user,
                TaskId = task.Id,
            }).ToList(),
            ct).ConfigureAwait(false);

        if (task.ProcessInstanceId is null)
        {
            return;
        }

        // participant 查重从「每用户一次 count(*)」降为一次 in 查询，再批量补插
        var distinctUsers = users.Distinct(StringComparer.Ordinal).ToList();
        var existing = await QueryExistingParticipantUserIdsAsync(
            session,
            task.ProcessInstanceId,
            distinctUsers,
            ct).ConfigureAwait(false);

        var participants = distinctUsers
            .Where(user => !existing.Contains(user))
            .Select(user => new IdentityLinkRecord
            {
                Type = IdentityLinkType.Participant,
                UserId = user,
                ProcessInstanceId = task.ProcessInstanceId,
                ProcessDefinitionId = task.ProcessDefinitionId,
            })
            .ToList();

        await AddIdentityLinksInternalAsync(session, participants, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AddCandidateGroupsAsync(
        TaskEntity task,
        IEnumerable<string> candidateGroups,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(candidateGroups);

        var groups = candidateGroups as IReadOnlyList<string> ?? candidateGroups.ToList();

        if (groups.Count == 0)
        {
            return;
        }

        using var lease = _sessions.Acquire();

        await AddIdentityLinksInternalAsync(
            lease.Session,
            groups.Select(group => new IdentityLinkRecord
            {
                Type = IdentityLinkType.Candidate,
                GroupId = group,
                TaskId = task.Id,
            }).ToList(),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RemoveCandidateUsersAsync(
        TaskEntity task,
        IEnumerable<string> candidateUsers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(candidateUsers);

        var users = candidateUsers.Distinct(StringComparer.Ordinal).ToList();

        if (users.Count == 0)
        {
            return;
        }

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(
            new CommandDefinition(
                DeleteCandidateUsersSql,
                new { taskId = task.Id, userIds = users },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RemoveCandidateGroupsAsync(
        TaskEntity task,
        IEnumerable<string> candidateGroups,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(candidateGroups);

        var groups = candidateGroups.Distinct(StringComparer.Ordinal).ToList();

        if (groups.Count == 0)
        {
            return;
        }

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(
            new CommandDefinition(
                DeleteCandidateGroupsSql,
                new { taskId = task.Id, groupIds = groups },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AddIdentityLinkAsync(IdentityLinkRecord link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        using var lease = _sessions.Acquire();
        await AddIdentityLinkInternalAsync(lease.Session, link, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteTaskAsync(TaskEntity task, string? deleteReason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 先摘关联数据（ACT_FK_TSKASS_TASK 要求身份关系先删）
        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                "delete from ACT_RU_IDENTITYLINK where TASK_ID_ = @taskId",
                new { taskId = task.Id },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        // 任务局部变量：先取字节行 id → 删变量行 → 按 id 删字节行（RESTRICT 外键）
        var byteArrayIds = (await session.Connection.QueryAsync<string>(
            new CommandDefinition(
                "select BYTEARRAY_ID_ from ACT_RU_VARIABLE where TASK_ID_ = @taskId and BYTEARRAY_ID_ is not null",
                new { taskId = task.Id },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false)).ToList();

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                "delete from ACT_RU_VARIABLE where TASK_ID_ = @taskId",
                new { taskId = task.Id },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (byteArrayIds.Count > 0)
        {
            await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    "delete from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is null and ID_ in @ids",
                    new { ids = byteArrayIds },
                    session.Transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
        }

        // 对应 deleteTask：历史任务行收口（END_TIME_ / DURATION_ / DELETE_REASON_）
        // 注意历史身份关系行**不删**（Activiti 的 deleteIdentityLinksByTaskId 传 cascadeHistory=false）
        await _historyManager.RecordTaskEndAsync(task.Id, deleteReason, ct).ConfigureAwait(false);

        if (task.Revision > 0)
        {
            var affected = await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    DeleteTaskSql,
                    new { id = task.Id, revision = task.Revision },
                    session.Transaction,
                    cancellationToken: ct)).ConfigureAwait(false);

            if (affected != 1)
            {
                throw new OptimisticLockingException(
                    $"删除任务 '{task.Id}' 失败：预期影响 1 行，实际 {affected} 行（REV_ = {task.Revision}）。" +
                    "该行可能已被其它命令修改或删除。");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteTasksByProcessInstanceAsync(
        string processInstanceId,
        string? deleteReason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 逐个任务走 deleteTask（对应 deleteTasksByProcessInstanceId → deleteTask），
        // 以保证每个任务都收口历史行（recordTaskEnd）
        var taskStates = await QueryTasksAsync(
            session,
            SelectByProcessInstanceIdSql,
            new { processInstanceId },
            ct).ConfigureAwait(false);

        foreach (var state in taskStates)
        {
            // 删除原因顺着传到任务历史（ACT_HI_TASKINST.DELETE_REASON_）
            await DeleteTaskAsync(state.ToEntity(), deleteReason, ct).ConfigureAwait(false);
        }

        // 顺序受 ACT_FK_TSKASS_TASK 约束：身份关系 → 任务
        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                "delete from ACT_RU_IDENTITYLINK where PROC_INST_ID_ = @processInstanceId",
                new { processInstanceId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        // 任务局部变量（按实例批量）：先取字节行 id → 删变量行 → 按 id 删字节行（RESTRICT 外键）
        var byteArrayIds = (await session.Connection.QueryAsync<string>(
            new CommandDefinition(
                "select V.BYTEARRAY_ID_ from ACT_RU_VARIABLE V "
                + "inner join ACT_RU_TASK T on T.ID_ = V.TASK_ID_ "
                + "where T.PROC_INST_ID_ = @processInstanceId and V.BYTEARRAY_ID_ is not null",
                new { processInstanceId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false)).ToList();

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                "delete from ACT_RU_VARIABLE where TASK_ID_ in (select ID_ from ACT_RU_TASK where PROC_INST_ID_ = @processInstanceId)",
                new { processInstanceId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (byteArrayIds.Count > 0)
        {
            await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    "delete from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is null and ID_ in @ids",
                    new { ids = byteArrayIds },
                    session.Transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
        }

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                "delete from ACT_RU_TASK where PROC_INST_ID_ = @processInstanceId",
                new { processInstanceId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> UpdateSuspensionStateByProcessInstanceIdAsync(
        string processInstanceId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 批量 UPDATE 不碰 REV_（与 Activiti 的 updateTaskSuspensionState 批量路径一致）：
        // 挂起是运维动作，不应让并发的任务更新因版本号漂移而失败
        return await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateSuspensionStateByProcessInstanceIdSql,
                new { processInstanceId, state = SuspensionStateOf(suspended) },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> UpdateSuspensionStateByProcessDefinitionIdAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        return await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateSuspensionStateByProcessDefinitionIdSql,
                new { processDefinitionId, state = SuspensionStateOf(suspended) },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    private static int SuspensionStateOf(bool suspended) =>
        suspended ? TaskEntity.SuspendedSuspensionState : TaskEntity.ActiveSuspensionState;

    /// <inheritdoc />
    public async ValueTask<IList<IdentityLinkRecord>> FindIdentityLinksByTaskIdAsync(
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        using var lease = _sessions.Acquire();

        return await QueryIdentityLinksAsync(
            lease.Session,
            "select ID_, TYPE_, USER_ID_, GROUP_ID_, TASK_ID_, PROC_INST_ID_, PROC_DEF_ID_ " +
            "from ACT_RU_IDENTITYLINK where TASK_ID_ = @taskId order by ID_",
            new { taskId },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IList<IdentityLinkRecord>> FindIdentityLinksByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();

        return await QueryIdentityLinksAsync(
            lease.Session,
            "select ID_, TYPE_, USER_ID_, GROUP_ID_, TASK_ID_, PROC_INST_ID_, PROC_DEF_ID_ " +
            "from ACT_RU_IDENTITYLINK where PROC_INST_ID_ = @processInstanceId order by ID_",
            new { processInstanceId },
            ct).ConfigureAwait(false);
    }

    // ─────────────────────────── 任务局部变量（B2.4）───────────────────────────
    //
    // 借道 IVariableInstanceManager：任务局部变量就是 TASK_ID_ 非空的变量行，
    // 类型映射 / longString 字节行 / 历史记录全部复用同一链路。

    /// <inheritdoc />
    public async ValueTask SetTaskVariableAsync(
        TaskEntity task,
        string name,
        object? value,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Id);

        var existing = (await _variableInstances.FindByTaskIdAsync(task.Id, ct).ConfigureAwait(false))
            .FirstOrDefault(variable => string.Equals(variable.Name, name, StringComparison.Ordinal));

        if (existing is not null)
        {
            await _variableInstances.UpdateAsync(existing, value, ct).ConfigureAwait(false);
            return;
        }

        var variable = _variableInstances.Create(
            task.ExecutionId ?? throw new EngineException($"任务 '{task.Id}' 没有关联执行，无法写任务局部变量。"),
            task.ProcessInstanceId ?? task.ExecutionId,
            name,
            value);
        variable.TaskId = task.Id;

        await _variableInstances.InsertAsync(variable, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<object?> GetTaskVariableAsync(string taskId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var variables = await _variableInstances.FindByTaskIdAsync(taskId, ct).ConfigureAwait(false);

        return variables
            .FirstOrDefault(variable => string.Equals(variable.Name, name, StringComparison.Ordinal))
            ?.Value;
    }

    /// <inheritdoc />
    public async ValueTask<IDictionary<string, object?>> GetTaskVariablesAsync(
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var variables = await _variableInstances.FindByTaskIdAsync(taskId, ct).ConfigureAwait(false);

        return variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveTaskVariableAsync(string taskId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var existing = (await _variableInstances.FindByTaskIdAsync(taskId, ct).ConfigureAwait(false))
            .FirstOrDefault(variable => string.Equals(variable.Name, name, StringComparison.Ordinal));

        if (existing is null)
        {
            return false;
        }

        await _variableInstances.DeleteAsync(existing, ct).ConfigureAwait(false);
        return true;
    }

    // ─────────────────────────── 内部实现 ───────────────────────────

    private static async ValueTask WriteTaskAsync(IDbSession session, TaskEntity task, CancellationToken ct)
    {
        var revision = task.Revision;
        var affected = await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateTaskSql,
                BuildTaskParameters(task, revisionNext: revision + 1, revision: revision),
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (affected != 1)
        {
            throw new OptimisticLockingException(
                $"更新任务 '{task.Id}' 失败：REV_ = {revision} 的行不存在。" +
                "该行可能已被其它命令并发修改（乐观锁冲突）。");
        }

        task.Revision = revision + 1;
    }

    private ValueTask AddIdentityLinkInternalAsync(
        IDbSession session,
        IdentityLinkRecord link,
        CancellationToken ct) =>
        AddIdentityLinksInternalAsync(session, new[] { link }, ct);

    /// <summary>
    /// 批量写入关系行：一次多行 INSERT（每 50 行一条语句），再逐条记历史。
    /// 对应 Activiti 的 <c>IdentityLinkEntityManagerImpl.insert</c>，
    /// 但把「每个候选人一次 insert」合并为一条语句（P1 性能项：候选关系 N+1）。
    /// </summary>
    private async ValueTask AddIdentityLinksInternalAsync(
        IDbSession session,
        IReadOnlyList<IdentityLinkRecord> links,
        CancellationToken ct)
    {
        if (links.Count == 0)
        {
            return;
        }

        var persisted = new List<IdentityLinkRecord>(links.Count);

        foreach (var link in links)
        {
            persisted.Add(string.IsNullOrEmpty(link.Id) ? link with { Id = _idGenerator.NextId() } : link);
        }

        for (var offset = 0; offset < persisted.Count; offset += MaxIdentityLinksPerStatement)
        {
            var count = Math.Min(MaxIdentityLinksPerStatement, persisted.Count - offset);
            var sql = new StringBuilder(InsertIdentityLinksSqlPrefix, 64 + (count * 96));
            var parameters = new DynamicParameters();

            for (var i = 0; i < count; i++)
            {
                var link = persisted[offset + i];

                sql.Append(i == 0 ? "\n(" : ",\n(");
                sql.Append("@id").Append(i)
                    .Append(", 1, @type").Append(i)
                    .Append(", @userId").Append(i)
                    .Append(", @groupId").Append(i)
                    .Append(", @taskId").Append(i)
                    .Append(", @processInstanceId").Append(i)
                    .Append(", @processDefinitionId").Append(i)
                    .Append(", null)");

                parameters.Add($"id{i}", link.Id);
                parameters.Add($"type{i}", link.Type);
                parameters.Add($"userId{i}", link.UserId);
                parameters.Add($"groupId{i}", link.GroupId);
                parameters.Add($"taskId{i}", link.TaskId);
                parameters.Add($"processInstanceId{i}", link.ProcessInstanceId);
                parameters.Add($"processDefinitionId{i}", link.ProcessDefinitionId);
            }

            sql.Append(';');

            await session.Connection.ExecuteAsync(
                new CommandDefinition(sql.ToString(), parameters, session.Transaction, cancellationToken: ct))
                .ConfigureAwait(false);
        }

        // 对应 IdentityLinkEntityManagerImpl.insert → recordIdentityLinkCreated
        foreach (var link in persisted)
        {
            await _historyManager.RecordIdentityLinkCreatedAsync(link, ct).ConfigureAwait(false);
        }
    }

    /// <summary>一次查出这些用户在流程实例上已有的关系（原来每人一次 count(*)）。</summary>
    private static async Task<HashSet<string>> QueryExistingParticipantUserIdsAsync(
        IDbSession session,
        string processInstanceId,
        IReadOnlyList<string> userIds,
        CancellationToken ct)
    {
        var rows = await session.Connection.QueryAsync<string>(
            new CommandDefinition(
                SelectExistingParticipantUserIdsSql,
                new { processInstanceId, userIds },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        return rows
            .Where(userId => !string.IsNullOrEmpty(userId))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>对应 <c>involveUser</c>：同一用户在同一流程实例上只保留一条 <c>'participant'</c> 关系。</summary>
    private async ValueTask InvolveUserAsync(
        IDbSession session,
        string processInstanceId,
        string userId,
        string? processDefinitionId,
        CancellationToken ct)
    {
        var exists = await session.Connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "select count(*) from ACT_RU_IDENTITYLINK where PROC_INST_ID_ = @processInstanceId and USER_ID_ = @userId",
                new { processInstanceId, userId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (exists > 0)
        {
            return;
        }

        await AddIdentityLinkInternalAsync(
            session,
            new IdentityLinkRecord
            {
                Type = IdentityLinkType.Participant,
                UserId = userId,
                ProcessInstanceId = processInstanceId,
                ProcessDefinitionId = processDefinitionId,
            },
            ct).ConfigureAwait(false);
    }

    private static async Task<List<TaskState>> QueryTasksAsync(
        IDbSession session,
        string sql,
        object? parameters,
        CancellationToken ct)
    {
        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var states = new List<TaskState>();

        foreach (IDictionary<string, object> row in rows)
        {
            states.Add(TaskColumns.Read(ToNullable(row)));
        }

        return states;
    }

    private static async Task<List<IdentityLinkRecord>> QueryIdentityLinksAsync(
        IDbSession session,
        string sql,
        object? parameters,
        CancellationToken ct)
    {
        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var links = new List<IdentityLinkRecord>();

        foreach (IDictionary<string, object> row in rows)
        {
            var values = ToNullable(row);

            links.Add(new IdentityLinkRecord
            {
                Id = Text(values, "ID_") ?? string.Empty,
                Type = Text(values, "TYPE_") ?? string.Empty,
                UserId = Text(values, "USER_ID_"),
                GroupId = Text(values, "GROUP_ID_"),
                TaskId = Text(values, "TASK_ID_"),
                ProcessInstanceId = Text(values, "PROC_INST_ID_"),
                ProcessDefinitionId = Text(values, "PROC_DEF_ID_"),
            });
        }

        return links;
    }

    private static Dictionary<string, object?> ToNullable(IDictionary<string, object> row)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var pair in row)
        {
            values[pair.Key] = pair.Value;
        }

        return values;
    }

    private static string? Text(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value as string : null;

    private static DynamicParameters BuildTaskParameters(
        TaskEntity task,
        int? revisionNext = null,
        int? revision = null)
    {
        var parameters = new DynamicParameters();

        parameters.Add("Id", task.Id);
        parameters.Add("Name", task.Name);
        parameters.Add("BusinessKey", task.BusinessKey);
        parameters.Add("ParentTaskId", task.ParentTaskId);
        parameters.Add("Description", task.Description);
        parameters.Add("Priority", task.Priority);
        parameters.Add("CreateTime", task.CreateTime);
        parameters.Add("Owner", task.Owner);
        parameters.Add("Assignee", task.Assignee);
        parameters.Add("Delegation", task.DelegationStateText);
        parameters.Add("ExecutionId", task.ExecutionId);
        parameters.Add("ProcessInstanceId", task.ProcessInstanceId);
        parameters.Add("ProcessDefinitionId", task.ProcessDefinitionId);
        parameters.Add("TaskDefinitionKey", task.TaskDefinitionKey);
        parameters.Add("DueDate", task.DueDate);
        parameters.Add("Category", task.Category);
        parameters.Add("SuspensionState", task.SuspensionState);
        parameters.Add("TenantId", task.TenantId ?? string.Empty);
        parameters.Add("FormKey", task.FormKey);
        parameters.Add("ClaimTime", task.ClaimTime);

        if (revisionNext is not null)
        {
            parameters.Add("RevisionNext", revisionNext.Value);
        }

        if (revision is not null)
        {
            parameters.Add("Revision", revision.Value);
        }

        return parameters;
    }

    // ─────────────────────────── SQL（对应 Task.xml / IdentityLink.xml）───────────────────────────

    /// <summary>对应 Activiti <c>updateTaskSuspensionState</c> 的「按流程实例」批量形态。</summary>
    private const string UpdateSuspensionStateByProcessInstanceIdSql =
        "update ACT_RU_TASK set SUSPENSION_STATE_ = @state where PROC_INST_ID_ = @processInstanceId";

    /// <summary>挂起流程定义时批量挂起该定义下全部任务的配套语句。</summary>
    private const string UpdateSuspensionStateByProcessDefinitionIdSql =
        "update ACT_RU_TASK set SUSPENSION_STATE_ = @state where PROC_DEF_ID_ = @processDefinitionId";

    private const string InsertTaskSql = """
        insert into ACT_RU_TASK (
            ID_, REV_, NAME_, BUSINESS_KEY_, PARENT_TASK_ID_, DESCRIPTION_, PRIORITY_, CREATE_TIME_, OWNER_,
            ASSIGNEE_, DELEGATION_, EXECUTION_ID_, PROC_INST_ID_, PROC_DEF_ID_, TASK_DEF_KEY_, DUE_DATE_,
            CATEGORY_, SUSPENSION_STATE_, TENANT_ID_, FORM_KEY_, CLAIM_TIME_, APP_VERSION_)
        values (
            @Id, 1, @Name, @BusinessKey, @ParentTaskId, @Description, @Priority, @CreateTime, @Owner,
            @Assignee, @Delegation, @ExecutionId, @ProcessInstanceId, @ProcessDefinitionId, @TaskDefinitionKey, @DueDate,
            @Category, @SuspensionState, @TenantId, @FormKey, @ClaimTime, null)
        """;

    /// <summary>列清单与 Activiti <c>updateTask</c> 的 <c>&lt;set&gt;</c> 一致（不含台账列）。</summary>
    private const string UpdateTaskSql = """
        update ACT_RU_TASK set
            REV_ = @RevisionNext,
            NAME_ = @Name,
            BUSINESS_KEY_ = @BusinessKey,
            PARENT_TASK_ID_ = @ParentTaskId,
            PRIORITY_ = @Priority,
            CREATE_TIME_ = @CreateTime,
            OWNER_ = @Owner,
            ASSIGNEE_ = @Assignee,
            DELEGATION_ = @Delegation,
            EXECUTION_ID_ = @ExecutionId,
            PROC_DEF_ID_ = @ProcessDefinitionId,
            DESCRIPTION_ = @Description,
            DUE_DATE_ = @DueDate,
            CATEGORY_ = @Category,
            SUSPENSION_STATE_ = @SuspensionState,
            FORM_KEY_ = @FormKey,
            CLAIM_TIME_ = @ClaimTime
        where ID_ = @Id and REV_ = @Revision
        """;

    private const string DeleteTaskSql = "delete from ACT_RU_TASK where ID_ = @id and REV_ = @revision";

    /// <summary>只删任务级候选行；流程实例级 participant 行是审计信息，保留。</summary>
    private const string DeleteCandidateUsersSql = """
        delete from ACT_RU_IDENTITYLINK
        where TASK_ID_ = @taskId and TYPE_ = 'candidate' and USER_ID_ in @userIds
        """;

    private const string DeleteCandidateGroupsSql = """
        delete from ACT_RU_IDENTITYLINK
        where TASK_ID_ = @taskId and TYPE_ = 'candidate' and GROUP_ID_ in @groupIds
        """;

    private const string InsertIdentityLinksSqlPrefix = """
        insert into ACT_RU_IDENTITYLINK (ID_, REV_, TYPE_, USER_ID_, GROUP_ID_, TASK_ID_, PROC_INST_ID_, PROC_DEF_ID_, DETAILS_) values
        """;

    private const string SelectExistingParticipantUserIdsSql = """
        select USER_ID_ from ACT_RU_IDENTITYLINK
        where PROC_INST_ID_ = @processInstanceId and USER_ID_ in @userIds
        """;

    private const string SelectByIdSql =
        "select " + TaskColumns.All + " from ACT_RU_TASK where ID_ = @taskId";

    private const string SelectByExecutionIdSql =
        "select " + TaskColumns.All + " from ACT_RU_TASK where EXECUTION_ID_ = @executionId";

    private const string SelectByAssigneeSql =
        "select " + TaskColumns.All + " from ACT_RU_TASK where ASSIGNEE_ = @assignee";

    private const string SelectAllSql =
        "select " + TaskColumns.All + " from ACT_RU_TASK";

    private const string SelectByProcessInstanceIdSql =
        "select " + TaskColumns.All + " from ACT_RU_TASK where PROC_INST_ID_ = @processInstanceId";
}

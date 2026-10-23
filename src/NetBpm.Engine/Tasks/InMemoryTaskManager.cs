using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Identity;

namespace NetBpm.Engine.Tasks;

/// <summary>
/// 内存版任务管理器（无数据库宿主 / 单元测试）。
/// <para>生产必须使用 <c>NetBpm.Persistence.Tasks.DapperTaskManager</c>。</para>
/// <para>
/// 身份关系的语义与 Activiti 保持一致（见 <see cref="IdentityLinkRecord"/> 的说明），
/// 否则内存宿主与数据库宿主会给出不同的候选待办结果。
/// </para>
/// </summary>
public sealed class InMemoryTaskManager : ITaskManager
{
    private readonly IIdGenerator _idGenerator;
    private readonly IHistoryManager _historyManager;
    private readonly IClock _clock;
    private readonly Dictionary<string, TaskEntity> _tasks = new(StringComparer.Ordinal);
    private readonly List<IdentityLinkRecord> _identityLinks = [];
    private readonly Lock _syncRoot = new();

    public InMemoryTaskManager(
        IIdGenerator idGenerator,
        IHistoryManager historyManager,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(historyManager);

        _idGenerator = idGenerator;
        _historyManager = historyManager;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>当前任务数（诊断用）。</summary>
    public int TaskCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _tasks.Count;
            }
        }
    }

    /// <summary>当前全部任务（诊断 / 测试用的快照）。</summary>
    public IReadOnlyList<TaskEntity> Tasks
    {
        get
        {
            lock (_syncRoot)
            {
                return _tasks.Values.ToList();
            }
        }
    }

    /// <summary>当前全部身份关系（诊断 / 测试用的快照）。</summary>
    public IReadOnlyList<IdentityLinkRecord> IdentityLinks
    {
        get
        {
            lock (_syncRoot)
            {
                return _identityLinks.ToList();
            }
        }
    }

    /// <inheritdoc />
    public TaskEntity CreateTask() => new()
    {
        // 对应 TaskEntityManagerImpl.create()：创建即打上创建时间
        CreateTime = _clock.UtcNow,
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

        lock (_syncRoot)
        {
            if (string.IsNullOrEmpty(task.Id))
            {
                task.Id = _idGenerator.NextId();
            }

            task.Revision = 1;
            task.ExecutionId = execution.Id;
            task.ProcessInstanceId = execution.ProcessInstanceId ?? execution.Id;
            task.ProcessDefinitionId = execution.ProcessDefinitionId;
            task.TenantId ??= execution.TenantId;
            task.Execution = execution;
            _tasks[task.Id] = task;
        }

        // 对应 TaskEntityManagerImpl.insert(task, fireCreateEvent)：插入前补 owner / assignee 的身份关系。
        // 注意这里不能走 ChangeXxx（它们的「值未变化就不处理」判定会把它跳过）。
        if (task.Owner is not null && task.ProcessInstanceId is not null)
        {
            await InvolveUserAsync(task.ProcessInstanceId, task.Owner, task.ProcessDefinitionId, ct)
                .ConfigureAwait(false);
        }

        if (task.Assignee is not null && task.ProcessInstanceId is not null)
        {
            await InvolveUserAsync(task.ProcessInstanceId, task.Assignee, task.ProcessDefinitionId, ct)
                .ConfigureAwait(false);
        }

        await _historyManager.RecordTaskCreatedAsync(task, execution, ct).ConfigureAwait(false);
        await _historyManager.RecordTaskIdAsync(task, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<TaskEntity?> FindTaskByIdAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult(_tasks.GetValueOrDefault(taskId));
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<TaskEntity>> FindTasksByExecutionIdAsync(
        string executionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult<IList<TaskEntity>>(
                _tasks.Values
                    .Where(task => string.Equals(task.ExecutionId, executionId, StringComparison.Ordinal))
                    .ToList());
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<TaskEntity>> FindTasksByAssigneeAsync(string assignee, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assignee);

        lock (_syncRoot)
        {
            return ValueTask.FromResult<IList<TaskEntity>>(
                _tasks.Values
                    .Where(task => string.Equals(task.Assignee, assignee, StringComparison.Ordinal))
                    .ToList());
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<TaskEntity>> FindAllOpenTasksAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            return ValueTask.FromResult<IList<TaskEntity>>(_tasks.Values.ToList());
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<TaskEntity>> FindTasksByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult<IList<TaskEntity>>(
                _tasks.Values
                    .Where(task => string.Equals(task.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                    .ToList());
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<TaskEntity>> FindTasksByCandidateUserAsync(
        string userId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(groupIds);

        lock (_syncRoot)
        {
            var candidateTaskIds = _identityLinks
                .Where(link => string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                    && link.TaskId is not null
                    && (string.Equals(link.UserId, userId, StringComparison.Ordinal)
                        || (link.GroupId is not null && groupIds.Contains(link.GroupId, StringComparer.Ordinal))))
                .Select(link => link.TaskId!)
                .ToHashSet(StringComparer.Ordinal);

            return ValueTask.FromResult<IList<TaskEntity>>(
                _tasks.Values
                    .Where(task => task.Assignee is null && candidateTaskIds.Contains(task.Id))
                    .ToList());
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<TaskEntity>> FindTasksByCandidateGroupAsync(
        string groupId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        lock (_syncRoot)
        {
            var candidateTaskIds = _identityLinks
                .Where(link => string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                    && link.TaskId is not null
                    && string.Equals(link.GroupId, groupId, StringComparison.Ordinal))
                .Select(link => link.TaskId!)
                .ToHashSet(StringComparer.Ordinal);

            return ValueTask.FromResult<IList<TaskEntity>>(
                _tasks.Values
                    .Where(task => task.Assignee is null && candidateTaskIds.Contains(task.Id))
                    .ToList());
        }
    }

    /// <inheritdoc />
    public async ValueTask UpdateTaskAsync(TaskEntity task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        task.Revision++;
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

        task.Revision++;

        // addAssigneeIdentityLinks：只在流程实例上记 'participant'
        if (assignee is not null && task.ProcessInstanceId is not null)
        {
            await InvolveUserAsync(task.ProcessInstanceId, assignee, task.ProcessDefinitionId, ct)
                .ConfigureAwait(false);
        }

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

        task.Revision++;

        if (owner is not null && task.ProcessInstanceId is not null)
        {
            await InvolveUserAsync(task.ProcessInstanceId, owner, task.ProcessDefinitionId, ct)
                .ConfigureAwait(false);
        }

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

        foreach (var candidateUser in candidateUsers)
        {
            await AddIdentityLinkAsync(
                new IdentityLinkRecord
                {
                    Id = _idGenerator.NextId(),
                    Type = IdentityLinkType.Candidate,
                    UserId = candidateUser,
                    TaskId = task.Id,
                },
                ct).ConfigureAwait(false);

            if (task.ProcessInstanceId is not null)
            {
                await InvolveUserAsync(task.ProcessInstanceId, candidateUser, task.ProcessDefinitionId, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask AddCandidateGroupsAsync(
        TaskEntity task,
        IEnumerable<string> candidateGroups,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(candidateGroups);

        foreach (var candidateGroup in candidateGroups)
        {
            await AddIdentityLinkAsync(
                new IdentityLinkRecord
                {
                    Id = _idGenerator.NextId(),
                    Type = IdentityLinkType.Candidate,
                    GroupId = candidateGroup,
                    TaskId = task.Id,
                },
                ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveCandidateUsersAsync(
        TaskEntity task,
        IEnumerable<string> candidateUsers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(candidateUsers);

        var users = candidateUsers.ToHashSet(StringComparer.Ordinal);

        lock (_syncRoot)
        {
            // 只删任务级 candidate 行；流程实例级 participant 行是审计信息，保留
            _identityLinks.RemoveAll(link =>
                string.Equals(link.TaskId, task.Id, StringComparison.Ordinal)
                && string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                && link.UserId is not null
                && users.Contains(link.UserId));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveCandidateGroupsAsync(
        TaskEntity task,
        IEnumerable<string> candidateGroups,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(candidateGroups);

        var groups = candidateGroups.ToHashSet(StringComparer.Ordinal);

        lock (_syncRoot)
        {
            _identityLinks.RemoveAll(link =>
                string.Equals(link.TaskId, task.Id, StringComparison.Ordinal)
                && string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                && link.GroupId is not null
                && groups.Contains(link.GroupId));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask AddIdentityLinkAsync(IdentityLinkRecord link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        lock (_syncRoot)
        {
            _identityLinks.Add(link);
        }

        await _historyManager.RecordIdentityLinkCreatedAsync(link, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteTaskAsync(TaskEntity task, string? deleteReason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        // 历史身份关系行不删（Activiti 的 deleteIdentityLinksByTaskId 传 cascadeHistory=false）
        await _historyManager.RecordTaskEndAsync(task.Id, deleteReason, ct).ConfigureAwait(false);

        lock (_syncRoot)
        {
            _tasks.Remove(task.Id);
            _identityLinks.RemoveAll(link => string.Equals(link.TaskId, task.Id, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteTasksByProcessInstanceAsync(
        string processInstanceId,
        string? deleteReason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        List<TaskEntity> tasks;

        lock (_syncRoot)
        {
            tasks = _tasks.Values
                .Where(task => string.Equals(task.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .ToList();
        }

        foreach (var task in tasks)
        {
            // 删除原因要顺着传到任务历史：删除实例时它是「为什么被删」，正常结束时才是 null
            await DeleteTaskAsync(task, deleteReason, ct).ConfigureAwait(false);
        }

        // 任务删完后，清掉挂在流程实例上的 'participant' 等残留关系
        lock (_syncRoot)
        {
            _identityLinks.RemoveAll(link =>
                string.Equals(link.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public ValueTask<int> UpdateSuspensionStateByProcessInstanceIdAsync(
        string processInstanceId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var state = suspended ? TaskEntity.SuspendedSuspensionState : TaskEntity.ActiveSuspensionState;

        lock (_syncRoot)
        {
            var tasks = _tasks.Values
                .Where(task => string.Equals(task.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .ToList();

            foreach (var task in tasks)
            {
                task.SuspensionState = state;
            }

            return ValueTask.FromResult(tasks.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> UpdateSuspensionStateByProcessDefinitionIdAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        var state = suspended ? TaskEntity.SuspendedSuspensionState : TaskEntity.ActiveSuspensionState;

        lock (_syncRoot)
        {
            var tasks = _tasks.Values
                .Where(task => string.Equals(task.ProcessDefinitionId, processDefinitionId, StringComparison.Ordinal))
                .ToList();

            foreach (var task in tasks)
            {
                task.SuspensionState = state;
            }

            return ValueTask.FromResult(tasks.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<IdentityLinkRecord>> FindIdentityLinksByTaskIdAsync(
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult<IList<IdentityLinkRecord>>(
                _identityLinks
                    .Where(link => string.Equals(link.TaskId, taskId, StringComparison.Ordinal))
                    .ToList());
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<IdentityLinkRecord>> FindIdentityLinksByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult<IList<IdentityLinkRecord>>(
                _identityLinks
                    .Where(link => string.Equals(link.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                    .ToList());
        }
    }

    /// <inheritdoc />
    public ValueTask SetTaskVariableAsync(
        TaskEntity task,
        string name,
        object? value,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        task.LocalVariables[name] = value;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<object?> GetTaskVariableAsync(string taskId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_syncRoot)
        {
            var task = _tasks.GetValueOrDefault(taskId);
            object? value = null;
            var found = task is not null && task.LocalVariables.TryGetValue(name, out value);

            return ValueTask.FromResult(found ? value : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IDictionary<string, object?>> GetTaskVariablesAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (_syncRoot)
        {
            var task = _tasks.GetValueOrDefault(taskId);

            return ValueTask.FromResult<IDictionary<string, object?>>(
                task is null
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(task.LocalVariables, StringComparer.Ordinal));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveTaskVariableAsync(string taskId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_syncRoot)
        {
            var task = _tasks.GetValueOrDefault(taskId);
            return ValueTask.FromResult(task is not null && task.LocalVariables.Remove(name));
        }
    }

    /// <summary>对应 <c>involveUser</c>：同一用户在同一流程实例上只保留一条 'participant' 关系。</summary>
    private async ValueTask InvolveUserAsync(
        string processInstanceId,
        string userId,
        string? processDefinitionId,
        CancellationToken ct)
    {
        lock (_syncRoot)
        {
            var existing = _identityLinks.Any(link =>
                string.Equals(link.ProcessInstanceId, processInstanceId, StringComparison.Ordinal)
                && string.Equals(link.UserId, userId, StringComparison.Ordinal));

            if (existing)
            {
                return;
            }
        }

        // 走 AddIdentityLinkAsync 而不是直接塞集合：历史写入与数据库宿主必须同构
        await AddIdentityLinkAsync(
            new IdentityLinkRecord
            {
                Id = _idGenerator.NextId(),
                Type = IdentityLinkType.Participant,
                UserId = userId,
                ProcessInstanceId = processInstanceId,
                ProcessDefinitionId = processDefinitionId,
            },
            ct).ConfigureAwait(false);
    }
}

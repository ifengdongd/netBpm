using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>
/// 修改任务的可变字段（优先级 / 到期时间 / 描述 / 分类 / 所有者）。
/// <para>
/// 语义对齐 Activiti <c>taskService.setPriority/setDueDate/setDescription/setCategory/setOwner</c>：
/// 每个字段独立可空，<see langword="null"/> 表示<b>不修改</b>；
/// <paramref name="owner"/> 传空串表示清空所有者（Activiti 用 <c>setOwner(null)</c>）。
/// 到期时间 / 描述本身可空，用 <paramref name="clearDueDate"/> / <paramref name="clearDescription"/>
/// 显式清空（对齐 <c>setDueDate(null)</c> / <c>setDescription(null)</c>）。
/// </para>
/// </summary>
public sealed class UpdateTaskCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly string? _name;
    private readonly string? _description;
    private readonly int? _priority;
    private readonly DateTime? _dueDate;
    private readonly string? _category;
    private readonly string? _owner;
    private readonly bool _clearDueDate;
    private readonly bool _clearDescription;

    public UpdateTaskCommand(
        string taskId,
        string? name = null,
        string? description = null,
        int? priority = null,
        DateTime? dueDate = null,
        string? category = null,
        string? owner = null,
        bool clearDueDate = false,
        bool clearDescription = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        _taskId = taskId;
        _name = name;
        _description = description;
        _priority = priority;
        _dueDate = dueDate;
        _category = category;
        _owner = owner;
        _clearDueDate = clearDueDate;
        _clearDescription = clearDescription;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var taskManager = context.Services.TaskManager
            ?? throw new EngineNotImplementedException(
                "任务管理器（ITaskManager）",
                "P7",
                "宿主必须注册任务管理器实现");

        var task = await taskManager.FindTaskByIdAsync(_taskId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"任务 '{_taskId}' 不存在（可能已完成或已被删除）。");

        if (task.SuspensionState != TaskEntity.ActiveSuspensionState)
        {
            throw new EngineConflictException($"任务 '{_taskId}' 处于挂起状态，不能修改。");
        }

        var fieldsChanged = false;

        if (_name is not null)
        {
            task.Name = _name;
            fieldsChanged = true;
        }

        if (_clearDescription)
        {
            task.Description = null;
            fieldsChanged = true;
        }
        else if (_description is not null)
        {
            task.Description = _description;
            fieldsChanged = true;
        }

        if (_priority is not null)
        {
            task.Priority = _priority.Value;
            fieldsChanged = true;
        }

        if (_clearDueDate)
        {
            task.DueDate = null;
            fieldsChanged = true;
        }
        else if (_dueDate is not null)
        {
            task.DueDate = _dueDate;
            fieldsChanged = true;
        }

        if (_category is not null)
        {
            task.Category = _category;
            fieldsChanged = true;
        }

        if (fieldsChanged)
        {
            await taskManager.UpdateTaskAsync(task, ct).ConfigureAwait(false);
        }

        if (_owner is not null)
        {
            // 空串 = 清空所有者（Activiti 的 setOwner(null)）
            await taskManager
                .ChangeTaskOwnerAsync(task, string.IsNullOrWhiteSpace(_owner) ? null : _owner, ct)
                .ConfigureAwait(false);
        }

        return Unit.Value;
    }
}

/// <summary>增加任务候选用户 / 候选组（管理动作，不改办理人）。</summary>
public sealed class AddTaskCandidatesCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly IReadOnlyCollection<string> _users;
    private readonly IReadOnlyCollection<string> _groups;

    public AddTaskCandidatesCommand(
        string taskId,
        IReadOnlyCollection<string>? users = null,
        IReadOnlyCollection<string>? groups = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        _taskId = taskId;
        _users = users ?? [];
        _groups = groups ?? [];
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var taskManager = RequireTaskManager(context);
        var task = await FindActiveTaskAsync(taskManager, _taskId, ct).ConfigureAwait(false);

        if (_users.Count > 0)
        {
            await taskManager.AddCandidateUsersAsync(task, _users, ct).ConfigureAwait(false);
        }

        if (_groups.Count > 0)
        {
            await taskManager.AddCandidateGroupsAsync(task, _groups, ct).ConfigureAwait(false);
        }

        return Unit.Value;
    }

    internal static ITaskManager RequireTaskManager(ICommandContext context) =>
        context.Services.TaskManager
            ?? throw new EngineNotImplementedException(
                "任务管理器（ITaskManager）",
                "P7",
                "宿主必须注册任务管理器实现");

    internal static async ValueTask<TaskEntity> FindActiveTaskAsync(
        ITaskManager taskManager,
        string taskId,
        CancellationToken ct)
    {
        var task = await taskManager.FindTaskByIdAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"任务 '{taskId}' 不存在（可能已完成或已被删除）。");

        if (task.SuspensionState != TaskEntity.ActiveSuspensionState)
        {
            throw new EngineConflictException($"任务 '{taskId}' 处于挂起状态，不能修改候选关系。");
        }

        return task;
    }
}

/// <summary>移除任务候选用户 / 候选组（仅删任务级 <c>candidate</c> 行）。</summary>
public sealed class RemoveTaskCandidatesCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly IReadOnlyCollection<string> _users;
    private readonly IReadOnlyCollection<string> _groups;

    public RemoveTaskCandidatesCommand(
        string taskId,
        IReadOnlyCollection<string>? users = null,
        IReadOnlyCollection<string>? groups = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        _taskId = taskId;
        _users = users ?? [];
        _groups = groups ?? [];
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var taskManager = AddTaskCandidatesCommand.RequireTaskManager(context);
        var task = await AddTaskCandidatesCommand.FindActiveTaskAsync(taskManager, _taskId, ct)
            .ConfigureAwait(false);

        if (_users.Count > 0)
        {
            await taskManager.RemoveCandidateUsersAsync(task, _users, ct).ConfigureAwait(false);
        }

        if (_groups.Count > 0)
        {
            await taskManager.RemoveCandidateGroupsAsync(task, _groups, ct).ConfigureAwait(false);
        }

        return Unit.Value;
    }
}

/// <summary>
/// 委派任务：把任务交给他人办理，原办理人记入 <c>OWNER_</c>，委派状态置 <c>PENDING</c>。
/// <para>
/// 对应 Activiti <c>taskService.delegateTask(taskId, userId)</c>。
/// <b>PENDING</b> 的任务不能直接完成（<c>CompleteTaskCommand</c> 会拒绝），必须先 <c>resolve</c>。
/// </para>
/// </summary>
public sealed class DelegateTaskCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly string _userId;

    public DelegateTaskCommand(string taskId, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        _taskId = taskId;
        _userId = userId;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var taskManager = AddTaskCandidatesCommand.RequireTaskManager(context);
        var task = await AddTaskCandidatesCommand.FindActiveTaskAsync(taskManager, _taskId, ct)
            .ConfigureAwait(false);

        if (task.DelegationState == DelegationState.Pending)
        {
            throw new EngineConflictException($"任务 '{_taskId}' 已处于委派中，不能重复委派（请先 resolve）。");
        }

        // 原办理人 → OWNER_（可能为空：未签收的任务也能直接委派）
        var owner = task.Assignee;

        await taskManager.ChangeTaskOwnerAsync(task, owner, ct).ConfigureAwait(false);
        await taskManager.ChangeTaskAssigneeAsync(task, _userId, ct).ConfigureAwait(false);

        task.DelegationState = DelegationState.Pending;
        await taskManager.UpdateTaskAsync(task, ct).ConfigureAwait(false);

        return Unit.Value;
    }
}

/// <summary>
/// 解决委派：办理人回到 <c>OWNER_</c>，委派状态置 <c>RESOLVED</c>。
/// <para>对应 Activiti <c>taskService.resolveTask(taskId)</c>；任务不会因此完成。</para>
/// </summary>
public sealed class ResolveTaskCommand : ICommand<Unit>
{
    private readonly string _taskId;

    public ResolveTaskCommand(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        _taskId = taskId;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var taskManager = AddTaskCandidatesCommand.RequireTaskManager(context);
        var task = await AddTaskCandidatesCommand.FindActiveTaskAsync(taskManager, _taskId, ct)
            .ConfigureAwait(false);

        if (task.DelegationState != DelegationState.Pending)
        {
            throw new EngineConflictException($"任务 '{_taskId}' 不在委派中（DELEGATION_ 不是 PENDING），不能 resolve。");
        }

        var owner = task.Owner;

        await taskManager.ChangeTaskAssigneeAsync(task, owner, ct).ConfigureAwait(false);
        await taskManager.ChangeTaskOwnerAsync(task, null, ct).ConfigureAwait(false);

        task.DelegationState = DelegationState.Resolved;
        await taskManager.UpdateTaskAsync(task, ct).ConfigureAwait(false);

        return Unit.Value;
    }
}

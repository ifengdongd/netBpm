using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence.Admin;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Services;

/// <summary>
/// 任务命令服务：完成 / 认领 / 转办 / 改字段 / 候选关系 / 委派 + 批量变体 + 待办统计。
/// <para>
/// 授权拆分方式与 <see cref="IInstanceCommandService"/> 一致：单对象端点的授权在过滤器里声明
/// （<see cref="RuntimeAccess.RequireTaskAccess"/>），批量端点逐项执行，因此授权在服务内部逐项完成。
/// </para>
/// <para>
/// 注意：完成 / 认领 / 委派 / 解决委派<b>不写审计</b> —— 它们是业务办理动作，不是管理动作；
/// 只有转办、改字段、增删候选关系这类「能改变谁能办」的管理动作才留痕（见 <c>AdminAuditActions</c>）。
/// </para>
/// </summary>
public interface ITaskCommandService
{
    /// <summary>完成任务（可带变量与乐观锁版本）。</summary>
    Task CompleteAsync(
        TaskEntity task,
        IReadOnlyDictionary<string, object?>? variables,
        int? expectedRevision,
        CancellationToken ct);

    /// <summary>认领任务。</summary>
    Task ClaimAsync(string taskId, string userId, int? expectedRevision, CancellationToken ct);

    /// <summary>取消认领任务。</summary>
    Task UnclaimAsync(string taskId, int? expectedRevision, CancellationToken ct);

    /// <summary>转办（改派）。</summary>
    Task SetAssigneeAsync(TaskEntity task, string? assignee, RequestContext context, CancellationToken ct);

    /// <summary>修改任务可变字段。</summary>
    Task UpdateAsync(
        TaskEntity task,
        UpdateTaskRequest request,
        int? expectedRevision,
        RequestContext context,
        CancellationToken ct);

    /// <summary>加候选用户。</summary>
    Task AddCandidateUserAsync(TaskEntity task, string userId, RequestContext context, CancellationToken ct);

    /// <summary>移除候选用户；关系不存在时 404。</summary>
    Task RemoveCandidateUserAsync(TaskEntity task, string userId, RequestContext context, CancellationToken ct);

    /// <summary>加候选组。</summary>
    Task AddCandidateGroupAsync(TaskEntity task, string groupId, RequestContext context, CancellationToken ct);

    /// <summary>移除候选组；关系不存在时 404。</summary>
    Task RemoveCandidateGroupAsync(TaskEntity task, string groupId, RequestContext context, CancellationToken ct);

    /// <summary>委派任务。</summary>
    Task DelegateAsync(TaskEntity task, string userId, CancellationToken ct);

    /// <summary>解决委派。</summary>
    Task ResolveAsync(TaskEntity task, CancellationToken ct);

    /// <summary>待办统计（总数 / 逾期 / 今天到期 / 按定义分组）。</summary>
    Task<TaskStatsResponse> GetStatsAsync(
        string? assignee,
        string? candidateUser,
        string? candidateGroup,
        string? processDefinitionId,
        string? tenantId,
        int? timeZoneOffsetMinutes,
        RequestContext context,
        CancellationToken ct);

    /// <summary>批量完成（逐项授权 + 逐项执行，单项失败不影响其它项）。</summary>
    Task<BatchResultResponse> BatchCompleteAsync(
        IReadOnlyList<string> taskIds,
        IReadOnlyDictionary<string, object?>? variables,
        RequestContext context,
        CancellationToken ct);

    /// <summary>批量转办（逐项授权 + 逐项审计）。</summary>
    Task<BatchResultResponse> BatchSetAssigneeAsync(
        IReadOnlyList<string> taskIds,
        string? assignee,
        RequestContext context,
        CancellationToken ct);

    /// <summary>批量改到期时间（逐项授权 + 逐项审计）。</summary>
    Task<BatchResultResponse> BatchSetDueDateAsync(
        IReadOnlyList<string> taskIds,
        DateTime? dueDate,
        bool clearDueDate,
        RequestContext context,
        CancellationToken ct);
}

/// <summary><see cref="ITaskCommandService"/> 的默认实现。</summary>
internal sealed class TaskCommandService(
    IProcessEngine engine,
    IExecutionManager executionManager,
    RuntimeAudit audit) : ITaskCommandService
{
    public async Task CompleteAsync(
        TaskEntity task,
        IReadOnlyDictionary<string, object?>? variables,
        int? expectedRevision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await engine.CompleteTaskAsync(
                task.Id,
                variables is null ? null : new Dictionary<string, object?>(variables, StringComparer.Ordinal),
                expectedRevision,
                ct)
            .ConfigureAwait(false);
    }

    public async Task ClaimAsync(string taskId, string userId, int? expectedRevision, CancellationToken ct) =>
        await engine.ClaimTaskAsync(taskId, userId, expectedRevision, ct).ConfigureAwait(false);

    public async Task UnclaimAsync(string taskId, int? expectedRevision, CancellationToken ct) =>
        await engine.UnclaimTaskAsync(taskId, expectedRevision, ct).ConfigureAwait(false);

    public async Task SetAssigneeAsync(
        TaskEntity task,
        string? assignee,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await audit.RunAsync(
            context,
            AdminAuditActions.TaskReassign,
            AdminAuditTargets.Task,
            task.Id,
            task.ProcessInstanceId,
            new { from = task.Assignee, to = assignee },
            async () =>
            {
                await engine.SetAssigneeAsync(task.Id, assignee, ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        TaskEntity task,
        UpdateTaskRequest request,
        int? expectedRevision,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(request);

        await audit.RunAsync(
            context,
            AdminAuditActions.TaskUpdate,
            AdminAuditTargets.Task,
            task.Id,
            task.ProcessInstanceId,
            new
            {
                name = request.Name,
                priority = request.Priority,
                category = request.Category,
                owner = request.Owner,
                dueDate = request.DueDate,
                clearDueDate = request.ClearDueDate == true,
                // 描述可能含敏感信息：只记「是否改动」，不记内容
                descriptionChanged = request.Description is not null || request.ClearDescription == true,
            },
            async () =>
            {
                await engine.UpdateTaskAsync(
                        task.Id,
                        new TaskUpdate(
                            request.Name,
                            request.Description,
                            request.Priority,
                            request.DueDate,
                            request.Category,
                            request.Owner,
                            request.ClearDueDate == true,
                            request.ClearDescription == true,
                            expectedRevision),
                        ct)
                    .ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task AddCandidateUserAsync(
        TaskEntity task,
        string userId,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await audit.RunAsync(
            context,
            AdminAuditActions.TaskCandidateAdd,
            AdminAuditTargets.Task,
            task.Id,
            task.ProcessInstanceId,
            new { kind = "user", userId },
            async () =>
            {
                await engine.AddTaskCandidateUsersAsync(task.Id, [userId], ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task RemoveCandidateUserAsync(
        TaskEntity task,
        string userId,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await audit.RunAsync(
            context,
            AdminAuditActions.TaskCandidateRemove,
            AdminAuditTargets.Task,
            task.Id,
            task.ProcessInstanceId,
            new { kind = "user", userId },
            async () =>
            {
                var links = await engine.FindTaskIdentityLinksAsync(task.Id, ct).ConfigureAwait(false);

                if (!links.Any(link =>
                        string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                        && string.Equals(link.UserId, userId, StringComparison.Ordinal)))
                {
                    throw new ResourceNotFoundException($"任务 '{task.Id}' 的候选用户 '{userId}' 不存在。");
                }

                await engine.RemoveTaskCandidateUserAsync(task.Id, userId, ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task AddCandidateGroupAsync(
        TaskEntity task,
        string groupId,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await audit.RunAsync(
            context,
            AdminAuditActions.TaskCandidateAdd,
            AdminAuditTargets.Task,
            task.Id,
            task.ProcessInstanceId,
            new { kind = "group", groupId },
            async () =>
            {
                await engine.AddTaskCandidateGroupsAsync(task.Id, [groupId], ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task RemoveCandidateGroupAsync(
        TaskEntity task,
        string groupId,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await audit.RunAsync(
            context,
            AdminAuditActions.TaskCandidateRemove,
            AdminAuditTargets.Task,
            task.Id,
            task.ProcessInstanceId,
            new { kind = "group", groupId },
            async () =>
            {
                var links = await engine.FindTaskIdentityLinksAsync(task.Id, ct).ConfigureAwait(false);

                if (!links.Any(link =>
                        string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                        && string.Equals(link.GroupId, groupId, StringComparison.Ordinal)))
                {
                    throw new ResourceNotFoundException($"任务 '{task.Id}' 的候选组 '{groupId}' 不存在。");
                }

                await engine.RemoveTaskCandidateGroupAsync(task.Id, groupId, ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task DelegateAsync(TaskEntity task, string userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await engine.DelegateTaskAsync(task.Id, userId, ct).ConfigureAwait(false);
    }

    public async Task ResolveAsync(TaskEntity task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await engine.ResolveTaskAsync(task.Id, ct).ConfigureAwait(false);
    }

    public async Task<TaskStatsResponse> GetStatsAsync(
        string? assignee,
        string? candidateUser,
        string? candidateGroup,
        string? processDefinitionId,
        string? tenantId,
        int? timeZoneOffsetMinutes,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var unfiltered = false;

        if (string.IsNullOrWhiteSpace(assignee)
            && string.IsNullOrWhiteSpace(candidateUser)
            && string.IsNullOrWhiteSpace(candidateGroup))
        {
            assignee = context.UserId;

            // 「我的待办」兜不了底（匿名 / 服务身份没有操作人）→ 管理方放行全量，普通用户仍被引擎防呆拦下
            unfiltered = string.IsNullOrWhiteSpace(assignee) && RuntimeAccess.IsAdminPrincipal(context);
        }

        // 统计与列表同口径：强制带租户，避免跨租户数字泄露
        var query = new TaskQuery(
            Assignee: assignee,
            CandidateUser: candidateUser,
            ProcessDefinitionId: processDefinitionId,
            CandidateGroup: candidateGroup,
            TenantId: RequestIdentity.Tenant(tenantId, context),
            AllUsers: unfiltered);

        var tasks = await engine.FindTasksAsync(query, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;

        // 「今天到期」按调用方时区计算自然日（默认 UTC）；把本地 00:00~24:00 换算回 UTC 区间比较
        var offset = timeZoneOffsetMinutes ?? 0;
        var localStart = now.AddMinutes(offset).Date;
        var todayStart = localStart.AddMinutes(-offset);
        var tomorrowStart = todayStart.AddDays(1);

        var overdue = tasks.Count(task => task.DueDate is { } due && due < now);
        var dueToday = tasks.Count(task =>
            task.DueDate is { } due && due >= todayStart && due < tomorrowStart);

        // 按流程 key 分组（同 key 多版本合并，业务上更直观）
        var byDefinition = tasks
            .GroupBy(task => DefinitionIdParser.Key(task.ProcessDefinitionId) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new TaskStatsResponse(tasks.Count, overdue, dueToday, byDefinition);
    }

    public async Task<BatchResultResponse> BatchCompleteAsync(
        IReadOnlyList<string> taskIds,
        IReadOnlyDictionary<string, object?>? variables,
        RequestContext context,
        CancellationToken ct)
    {
        var items = new List<BatchItemResult>(taskIds.Count);

        foreach (var taskId in taskIds)
        {
            try
            {
                var task = await RuntimeAccess.EnsureTaskAccessAsync(taskId, context, engine, ct)
                    .ConfigureAwait(false);

                await TaskAuthorization.EnsureCanCompleteAsync(task, context, engine, ct)
                    .ConfigureAwait(false);

                await engine.CompleteTaskAsync(
                        taskId,
                        variables is null
                            ? null
                            : new Dictionary<string, object?>(variables, StringComparer.Ordinal),
                        expectedRevision: null,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, true, null));
            }
            catch (EngineException exception)
            {
                items.Add(new BatchItemResult(taskId, false, exception.Message));
            }
        }

        return BatchResults.From(items);
    }

    public async Task<BatchResultResponse> BatchSetAssigneeAsync(
        IReadOnlyList<string> taskIds,
        string? assignee,
        RequestContext context,
        CancellationToken ct)
    {
        var items = new List<BatchItemResult>(taskIds.Count);

        foreach (var taskId in taskIds)
        {
            try
            {
                // 跨租户的任务按「不存在」逐项失败，不影响其它项
                var task = await RuntimeAccess.EnsureTaskAccessAsync(taskId, context, engine, ct)
                    .ConfigureAwait(false);

                await TaskAuthorization
                    .EnsureCanModerateAsync(task, context, executionManager, "转办", ct)
                    .ConfigureAwait(false);

                await engine.SetAssigneeAsync(taskId, assignee, ct).ConfigureAwait(false);

                // 批量转办逐项审计：一条记录对应一个任务，失败项也留痕
                await audit.RecordAsync(
                        context,
                        AdminAuditActions.TaskBatchReassign,
                        AdminAuditTargets.Task,
                        taskId,
                        task.ProcessInstanceId,
                        new { from = task.Assignee, to = assignee },
                        success: true,
                        error: null,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, true, null));
            }
            catch (EngineException exception)
            {
                await audit.RecordAsync(
                        context,
                        AdminAuditActions.TaskBatchReassign,
                        AdminAuditTargets.Task,
                        taskId,
                        processInstanceId: null,
                        detail: new { to = assignee },
                        success: false,
                        error: exception.Message,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, false, exception.Message));
            }
        }

        return BatchResults.From(items);
    }

    public async Task<BatchResultResponse> BatchSetDueDateAsync(
        IReadOnlyList<string> taskIds,
        DateTime? dueDate,
        bool clearDueDate,
        RequestContext context,
        CancellationToken ct)
    {
        var items = new List<BatchItemResult>(taskIds.Count);

        foreach (var taskId in taskIds)
        {
            try
            {
                var task = await RuntimeAccess.EnsureTaskAccessAsync(taskId, context, engine, ct)
                    .ConfigureAwait(false);

                await TaskAuthorization
                    .EnsureCanModerateAsync(task, context, executionManager, "修改任务到期时间", ct)
                    .ConfigureAwait(false);

                await engine.UpdateTaskAsync(
                        taskId,
                        new TaskUpdate(DueDate: dueDate, ClearDueDate: clearDueDate),
                        ct)
                    .ConfigureAwait(false);

                await audit.RecordAsync(
                        context,
                        AdminAuditActions.TaskUpdate,
                        AdminAuditTargets.Task,
                        taskId,
                        task.ProcessInstanceId,
                        new { dueDate, clearDueDate, batch = true },
                        success: true,
                        error: null,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, true, null));
            }
            catch (EngineException exception)
            {
                await audit.RecordAsync(
                        context,
                        AdminAuditActions.TaskUpdate,
                        AdminAuditTargets.Task,
                        taskId,
                        processInstanceId: null,
                        detail: new { dueDate, clearDueDate, batch = true },
                        success: false,
                        error: exception.Message,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, false, exception.Message));
            }
        }

        return BatchResults.From(items);
    }
}

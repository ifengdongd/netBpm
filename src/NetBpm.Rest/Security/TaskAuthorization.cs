using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Runtime;

namespace NetBpm.Rest.Security;

/// <summary>
/// 任务级操作授权（JWT 用户身份下生效）。
/// <para>
/// 租户隔离只回答“能不能看见这个租户”，这里回答“能不能对这个任务动手”：
/// <list type="bullet">
///   <item><description>完成任务：办理人本人，或未签收时的候选人（含候选组）；</description></item>
///   <item><description>认领 / 取消认领：未签收任务要求是候选人；已签收任务只能由办理人取消；</description></item>
///   <item><description>委派 / 解决委派：办理人或所有者；</description></item>
///   <item><description>转办 / 改任务字段 / 增删候选人：办理人、所有者或流程实例发起人；</description></item>
///   <item><description>持有 <c>netbpm.admin</c> scope 可越过上述限制（运维 / 管理员）。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 管理动作必须走 <see cref="EnsureCanModerateAsync"/>：这些端点能改变办理人或候选关系，
/// 若不校验，任何同租户用户都能把别人的待办改给自己，从而绕过「仅办理人 / 候选人可完成」的边界。
/// </para>
/// <para>
/// API Key 模式代表可信服务，不做任务级校验（与 Activiti 一致：引擎本身不做授权）。
/// 越权统一返回 403（对象确实存在，只是当前主体无权操作）。
/// </para>
/// </summary>
internal static class TaskAuthorization
{
    /// <summary>完成前校验：办理人，或候选人 / 候选组。</summary>
    public static async Task EnsureCanCompleteAsync(
        TaskEntity task,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (Skip(context))
        {
            return;
        }

        var userId = RequireUser(context);

        if (!string.IsNullOrWhiteSpace(task.Assignee))
        {
            if (!IsSameUser(task.Assignee, userId))
            {
                throw new ForbiddenException(
                    $"任务 '{task.Id}' 已由 '{task.Assignee}' 办理，当前用户无权完成。");
            }

            return;
        }

        var links = await engine.FindTaskIdentityLinksAsync(task.Id, ct).ConfigureAwait(false);

        if (!IsCandidate(links, userId, context.Groups))
        {
            throw new ForbiddenException($"任务 '{task.Id}' 未签收，且当前用户不是候选人，无法完成。");
        }
    }

    /// <summary>认领前校验：未签收任务在有候选人时只允许候选人认领。</summary>
    public static async Task EnsureCanClaimAsync(
        TaskEntity task,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (Skip(context))
        {
            return;
        }

        var userId = RequireUser(context);

        // 已签收（自己或他人）：交给引擎按冲突处理（409），这里不重复判断
        if (!string.IsNullOrWhiteSpace(task.Assignee))
        {
            return;
        }

        var links = await engine.FindTaskIdentityLinksAsync(task.Id, ct).ConfigureAwait(false);

        // 与 Activiti 一致：没有配置候选人时允许任何租户内用户认领
        if (!HasCandidates(links))
        {
            return;
        }

        if (!IsCandidate(links, userId, context.Groups))
        {
            throw new ForbiddenException($"任务 '{task.Id}' 配置了候选人，当前用户不在其中，无法认领。");
        }
    }

    /// <summary>取消认领：只能由当前办理人（或管理员）操作。</summary>
    public static void EnsureCanUnclaim(TaskEntity task, RequestContext context)
    {
        if (Skip(context))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(task.Assignee))
        {
            return;
        }

        if (!IsSameUser(task.Assignee, RequireUser(context)))
        {
            throw new ForbiddenException(
                $"任务 '{task.Id}' 由 '{task.Assignee}' 办理，当前用户无权取消认领。");
        }
    }

    /// <summary>委派：办理人或所有者。</summary>
    public static void EnsureCanDelegate(TaskEntity task, RequestContext context)
    {
        EnsureOwnerOrAssignee(task, context, "委派");
    }

    /// <summary>解决委派：所有者或当前办理人。</summary>
    public static void EnsureCanResolve(TaskEntity task, RequestContext context)
    {
        EnsureOwnerOrAssignee(task, context, "解决委派");
    }

    /// <summary>
    /// 管理动作授权：转办、修改任务字段、增删候选关系。
    /// <para>
    /// 允许三类主体：当前办理人、所有者、流程实例发起人（发起人是业务上的流程负责人），
    /// 以及持有 <c>netbpm.admin</c>（或细粒度 <paramref name="requiredScope"/>）的管理员。
    /// 未签收且无所有者时只有发起人或管理员能操作。
    /// </para>
    /// <para>
    /// 该方法是任务级授权闭合的关键：这些动作会改变办理人 / 候选关系，若不校验，
    /// 任何同租户用户都能把待办改给自己，再以「办理人」身份完成，绕过完成 / 认领校验。
    /// </para>
    /// </summary>
    public static async Task EnsureCanModerateAsync(
        TaskEntity task,
        RequestContext context,
        IExecutionManager executionManager,
        string action,
        CancellationToken ct,
        string requiredScope = TaskScopes.AdminTaskModerate)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(executionManager);

        if (Skip(context, requiredScope))
        {
            return;
        }

        var userId = RequireUser(context);

        if (IsSameUser(task.Assignee, userId) || IsSameUser(task.Owner, userId))
        {
            return;
        }

        if (task.ProcessInstanceId is { Length: > 0 } processInstanceId)
        {
            var instance = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

            if (instance is not null && IsSameUser(instance.StartUserId, userId))
            {
                return;
            }
        }

        throw new ForbiddenException(
            $"任务 '{task.Id}' 只有办理人、所有者或流程发起人可以{action}。");
    }

    private static void EnsureOwnerOrAssignee(TaskEntity task, RequestContext context, string action)
    {
        if (Skip(context))
        {
            return;
        }

        var userId = RequireUser(context);

        if (IsSameUser(task.Assignee, userId) || IsSameUser(task.Owner, userId))
        {
            return;
        }

        throw new ForbiddenException($"任务 '{task.Id}' 只有办理人或所有者可以{action}。");
    }

    /// <summary>
    /// 是否是该任务的参与人：办理人 / 所有者 / 候选人（含候选组）。
    /// <para>供变量读写授权（<see cref="VariableAuthorization"/>）复用；跳过判断由调用方负责。</para>
    /// </summary>
    public static async Task<bool> IsParticipantAsync(
        TaskEntity task,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(context);

        var userId = RequireUser(context);

        if (IsSameUser(task.Assignee, userId) || IsSameUser(task.Owner, userId))
        {
            return true;
        }

        var links = await engine.FindTaskIdentityLinksAsync(task.Id, ct).ConfigureAwait(false);

        return IsCandidate(links, userId, context.Groups);
    }

    /// <summary>
    /// 是否跳过任务级授权：可信服务（API Key）跳过；JWT 用户持有超级范围
    /// <c>netbpm.admin</c> 或该动作对应的细粒度 scope 时跳过。
    /// </summary>
    private static bool Skip(RequestContext context, string requiredScope = TaskScopes.Admin) =>
        !context.IsUserPrincipal || context.HasAdminScope(requiredScope);

    private static string RequireUser(RequestContext context) =>
        context.UserId ?? throw new ForbiddenException("JWT 缺少 sub 声明，无法判断任务归属。");

    private static bool IsSameUser(string? left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static bool HasCandidates(IReadOnlyList<IdentityLinkRecord> links) =>
        links.Any(link => string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal));

    private static bool IsCandidate(
        IReadOnlyList<IdentityLinkRecord> links,
        string userId,
        IReadOnlySet<string> groups) =>
        links.Any(link =>
            string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
            && (IsSameUser(link.UserId, userId)
                || (link.GroupId is { Length: > 0 } groupId && groups.Contains(groupId))));
}

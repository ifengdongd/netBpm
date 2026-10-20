using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;
using NetBpm.Runtime;

namespace NetBpm.Rest.Security;

/// <summary>
/// 变量读写授权（JWT 用户身份下生效）。
/// <para>
/// 租户隔离回答“能不能看见这个租户”，任务级授权回答“能不能对这个任务动手”，
/// 这里回答“能不能读写它的变量”：
/// <list type="bullet">
///   <item><description>流程实例变量：发起人，或该实例<b>运行中任务</b>的办理人 / 所有者 / 候选人（含候选组）；</description></item>
///   <item><description>任务局部变量：该任务的办理人 / 所有者 / 候选人，或流程实例发起人；</description></item>
///   <item><description>持有 <c>netbpm.admin</c>（或写操作对应的 <c>netbpm.admin.variables.write</c>）可越过上述限制。</description></item>
/// </list>
/// </para>
/// <para>
/// API Key 模式代表可信服务，不做参与人校验（与 Activiti 一致：引擎本身不做授权）。
/// 越权统一返回 403（对象确实存在，只是当前主体无权读写）。
/// </para>
/// </summary>
internal static class VariableAuthorization
{
    /// <summary>流程实例变量：发起人或任一运行中任务的参与人。</summary>
    /// <param name="instance">已通过租户校验的实例；未受限上下文为 <see langword="null"/>。</param>
    /// <param name="processInstanceId">实例 id（用于报错信息与任务查询）。</param>
    public static async Task EnsureCanAccessInstanceVariablesAsync(
        IExecutionEntity? instance,
        string processInstanceId,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct,
        string requiredScope = TaskScopes.Admin)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Skip(context, requiredScope))
        {
            return;
        }

        var userId = RequireUser(context);

        if (instance is not null && IsSameUser(instance.StartUserId, userId))
        {
            return;
        }

        var tasks = await engine
            .FindTasksAsync(new TaskQuery(ProcessInstanceId: processInstanceId), ct)
            .ConfigureAwait(false);

        foreach (var task in tasks)
        {
            if (await TaskAuthorization.IsParticipantAsync(task, context, engine, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new ForbiddenException(
            $"当前用户不是流程实例 '{processInstanceId}' 的参与人（发起人 / 办理人 / 候选人），无权读写变量。");
    }

    /// <summary>
    /// 历史变量：与运行态变量同口径（实例发起人或运行中任务的参与人）。
    /// <para>
    /// 实例已结束时没有运行中任务，此时只有发起人（或管理员）可读；
    /// 这是 B2.16 的补漏 —— 历史表只增不删，若只校验租户，任何同租户用户都能
    /// 通过 <c>/history/.../variables</c> 读到运行态已授权保护的变量值。
    /// </para>
    /// </summary>
    /// <param name="runningInstance">运行态实例（已通过租户校验）；实例已结束或调用方不受限时为 <see langword="null"/>。</param>
    public static async Task EnsureCanAccessHistoricVariablesAsync(
        IExecutionEntity? runningInstance,
        string processInstanceId,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Skip(context, TaskScopes.Admin))
        {
            return;
        }

        if (runningInstance is not null)
        {
            await EnsureCanAccessInstanceVariablesAsync(runningInstance, processInstanceId, context, engine, ct)
                .ConfigureAwait(false);

            return;
        }

        var historic = await engine.FindHistoricProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);
        var userId = RequireUser(context);

        if (historic is not null && IsSameUser(historic.StartUserId, userId))
        {
            return;
        }

        throw new ForbiddenException(
            $"当前用户不是流程实例 '{processInstanceId}' 的发起人，无权读取历史变量。");
    }

    /// <summary>任务局部变量：任务参与人，或流程实例发起人。</summary>
    public static async Task EnsureCanAccessTaskVariablesAsync(
        TaskEntity task,
        RequestContext context,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct,
        string requiredScope = TaskScopes.Admin)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(context);

        if (Skip(context, requiredScope))
        {
            return;
        }

        if (await TaskAuthorization.IsParticipantAsync(task, context, engine, ct).ConfigureAwait(false))
        {
            return;
        }

        var userId = RequireUser(context);

        if (task.ProcessInstanceId is { Length: > 0 } processInstanceId)
        {
            var instance = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

            if (instance is not null && IsSameUser(instance.StartUserId, userId))
            {
                return;
            }
        }

        throw new ForbiddenException(
            $"当前用户不是任务 '{task.Id}' 的办理人 / 所有者 / 候选人，无权读写变量。");
    }

    /// <summary>
    /// 是否跳过参与人校验：可信服务（API Key）跳过；JWT 用户持有超级范围
    /// <c>netbpm.admin</c> 或该动作对应的细粒度 scope 时跳过。
    /// </summary>
    private static bool Skip(RequestContext context, string requiredScope) =>
        !context.IsUserPrincipal || context.HasAdminScope(requiredScope);

    private static string RequireUser(RequestContext context) =>
        context.UserId ?? throw new ForbiddenException("JWT 缺少 sub 声明，无法判断变量归属。");

    private static bool IsSameUser(string? left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}

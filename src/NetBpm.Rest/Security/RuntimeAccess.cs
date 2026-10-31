using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.Rest.Security;

/// <summary>流程实例端点的访问级别（过滤器声明式表达）。</summary>
public enum InstanceAccess
{
    /// <summary>只做租户隔离（不受限身份不查库，保持零额外往返）。</summary>
    TenantOnly = 0,

    /// <summary>实例必须存在（运行态），否则 404。</summary>
    RequireExisting,

    /// <summary>租户隔离 + 流程实例变量读授权（发起人 / 参与人 / 管理员）。</summary>
    ReadVariables,

    /// <summary>租户隔离 + 变量写授权（额外要求 <c>netbpm.admin.variables.write</c> 才能越权）。</summary>
    WriteVariables,
}

/// <summary>任务端点的访问级别（过滤器声明式表达）。</summary>
public enum TaskAccess
{
    /// <summary>任务必须存在（含已结束）+ 租户隔离，否则 404。</summary>
    TenantOnly = 0,

    /// <summary>完成任务授权：办理人，或未签收时的候选人（含候选组）。</summary>
    Complete,

    /// <summary>认领授权：未签收且配置了候选人时只允许候选人。</summary>
    Claim,

    /// <summary>取消认领授权：只能由当前办理人（或管理员）。</summary>
    Unclaim,

    /// <summary>委派授权：办理人或所有者。</summary>
    Delegate,

    /// <summary>解决委派授权：所有者或当前办理人。</summary>
    Resolve,

    /// <summary>管理动作授权（转办 / 改字段 / 增删候选关系）：办理人、所有者、发起人或管理员。</summary>
    Moderate,

    /// <summary>任务局部变量读授权：任务参与人或实例发起人。</summary>
    ReadVariables,

    /// <summary>任务局部变量写授权（额外要求 <c>netbpm.admin.variables.write</c> 才能越权）。</summary>
    WriteVariables,
}

/// <summary>历史 / 流程图端点的访问级别。</summary>
public enum HistoryAccess
{
    /// <summary>运行态优先、历史兜底，二者都按租户校验；都没有则 404。</summary>
    TenantOnly = 0,

    /// <summary>租户校验 + 历史变量读授权（与运行态变量同口径）。</summary>
    ReadVariables,
}

/// <summary>
/// 运行态端点的访问控制：<b>过滤器</b>负责「按 id 取对象 + 租户隔离 + 语义授权」，端点和 Service 只消费结果。
/// <para>
/// 🔑 为什么必须收进过滤器：重构前 <c>EnsureInstanceAccessAsync</c> 有 16 处、<c>TaskAuthorization.*</c> 12 处、
/// 变量授权 7 处，散落在 2000 行端点里手工调用。手工调用的必然产物是<b>顺序漂移</b>
/// （有的先租户后 scope，有的反过来）和<b>漏调</b>（新增端点时忘了加）。过滤器把「这个路由需要什么级别的访问」
/// 变成声明式的一行，并在同一条管道里固定顺序：<b>先对象存在性 / 租户（404）→ 再 scope（403）</b>，
/// 与既有契约（跨租户按「不存在」处理，避免存在性泄露）保持一致。
/// </para>
/// <para>
/// 过滤器取到的实体放进 <see cref="HttpContext.Items"/>，端点用
/// <see cref="InstanceOf"/> / <see cref="TaskOf"/> / <see cref="HistoricOf"/> 直接取，避免重复查库。
/// 批量端点逐项执行（单项失败不影响其它项），因此不挂过滤器，而是调用本类的异步辅助方法。
/// </para>
/// </summary>
public static class RuntimeAccess
{
    private const string InstanceItem = "netbpm.runtime.instance";
    private const string TaskItem = "netbpm.runtime.task";
    private const string HistoricItem = "netbpm.runtime.historic";

    /// <summary>要求流程实例可访问（按 <paramref name="access"/> 决定额外授权）。</summary>
    public static RouteHandlerBuilder RequireInstanceAccess(
        this RouteHandlerBuilder builder,
        InstanceAccess access = InstanceAccess.TenantOnly)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var services = http.RequestServices;
            var context = services.GetRequiredService<RequestContext>();
            var executionManager = services.GetRequiredService<IExecutionManager>();
            var id = RouteValue(http, "processInstanceId");

            var instance = access == InstanceAccess.RequireExisting
                ? await RequireInstanceAsync(id, context, executionManager, http.RequestAborted).ConfigureAwait(false)
                : await EnsureInstanceAsync(id, context, executionManager, http.RequestAborted).ConfigureAwait(false);

            if (access is InstanceAccess.ReadVariables or InstanceAccess.WriteVariables)
            {
                var engine = services.GetRequiredService<IProcessEngine>();
                var scope = access == InstanceAccess.WriteVariables ? TaskScopes.AdminVariablesWrite : TaskScopes.Admin;

                await VariableAuthorization
                    .EnsureCanAccessInstanceVariablesAsync(instance, id, context, engine, http.RequestAborted, scope)
                    .ConfigureAwait(false);
            }

            http.Items[InstanceItem] = instance;

            return await next(invocation).ConfigureAwait(false);
        });
    }

    /// <summary>要求任务可访问（按 <paramref name="access"/> 决定额外授权）。</summary>
    /// <param name="builder">端点构建器。</param>
    /// <param name="access">访问级别。</param>
    /// <param name="action">管理动作描述（仅 <see cref="TaskAccess.Moderate"/> 使用，用于 403 文案）。</param>
    public static RouteHandlerBuilder RequireTaskAccess(
        this RouteHandlerBuilder builder,
        TaskAccess access = TaskAccess.TenantOnly,
        string? action = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var services = http.RequestServices;
            var context = services.GetRequiredService<RequestContext>();
            var engine = services.GetRequiredService<IProcessEngine>();
            var id = RouteValue(http, "taskId");
            var ct = http.RequestAborted;

            var task = await EnsureTaskAccessAsync(id, context, engine, ct).ConfigureAwait(false);

            switch (access)
            {
                case TaskAccess.Complete:
                    await TaskAuthorization.EnsureCanCompleteAsync(task, context, engine, ct).ConfigureAwait(false);
                    break;

                case TaskAccess.Claim:
                    await TaskAuthorization.EnsureCanClaimAsync(task, context, engine, ct).ConfigureAwait(false);
                    break;

                case TaskAccess.Unclaim:
                    TaskAuthorization.EnsureCanUnclaim(task, context);
                    break;

                case TaskAccess.Delegate:
                    TaskAuthorization.EnsureCanDelegate(task, context);
                    break;

                case TaskAccess.Resolve:
                    TaskAuthorization.EnsureCanResolve(task, context);
                    break;

                case TaskAccess.Moderate:
                    await TaskAuthorization
                        .EnsureCanModerateAsync(
                            task,
                            context,
                            services.GetRequiredService<IExecutionManager>(),
                            action ?? "操作任务",
                            ct)
                        .ConfigureAwait(false);
                    break;

                case TaskAccess.ReadVariables:
                case TaskAccess.WriteVariables:
                    var scope = access == TaskAccess.WriteVariables ? TaskScopes.AdminVariablesWrite : TaskScopes.Admin;

                    await VariableAuthorization
                        .EnsureCanAccessTaskVariablesAsync(
                            task,
                            context,
                            services.GetRequiredService<IExecutionManager>(),
                            engine,
                            ct,
                            scope)
                        .ConfigureAwait(false);
                    break;

                default:
                    break;
            }

            http.Items[TaskItem] = task;

            return await next(invocation).ConfigureAwait(false);
        });
    }

    /// <summary>要求历史 / 流程图可访问（运行态优先，历史兜底）。</summary>
    public static RouteHandlerBuilder RequireHistoryAccess(
        this RouteHandlerBuilder builder,
        HistoryAccess access = HistoryAccess.TenantOnly)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var services = http.RequestServices;
            var context = services.GetRequiredService<RequestContext>();
            var engine = services.GetRequiredService<IProcessEngine>();
            var id = RouteValue(http, "processInstanceId");
            var ct = http.RequestAborted;

            var (running, historic) = await ResolveHistoryAsync(
                    id,
                    context,
                    services.GetRequiredService<IExecutionManager>(),
                    engine,
                    ct)
                .ConfigureAwait(false);

            if (access == HistoryAccess.ReadVariables)
            {
                await VariableAuthorization
                    .EnsureCanAccessHistoricVariablesAsync(running, id, context, engine, ct)
                    .ConfigureAwait(false);
            }

            http.Items[InstanceItem] = running;

            if (historic is not null)
            {
                http.Items[HistoricItem] = historic;
            }

            return await next(invocation).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 当前请求是否具备管理能力（判定口径与 <see cref="AdminAuthorization"/> 一致）：
    /// 未启用凭据（开发 / 单机演示）或 API Key 可信服务视为管理方；JWT 用户必须持 <c>netbpm.admin</c>；
    /// 注册客户端必须显式持有<b>跨人读</b> scope（<c>workflow:admin:instance</c> / <c>workflow:history:read:all</c>）。
    /// <para>用于「全量待办视图」的显式放行 —— 引擎对无条件任务查询的防呆仍然默认生效。</para>
    /// <para>
    /// ⚠️ 客户端不能沿用「非用户身份即管理方」的老口径：引擎面没有人员收窄，
    /// 「全量待办」等于「读该租户所有人的待办」，一个只被授予 <c>workflow:task:read</c> 的接入系统
    /// 不该顺带拿到它。
    /// </para>
    /// </summary>
    public static bool IsAdminPrincipal(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.AuthMode is RequestAuthMode.None or RequestAuthMode.ApiKey)
        {
            return true;
        }

        if (context.IsUserPrincipal)
        {
            return context.HasAdminScope(TaskScopes.Admin);
        }

        return context.HasAdminScope(TaskScopes.Admin)
            || PlatformScopes.IsSatisfiedBy([.. context.Scopes], PlatformScopes.AdminInstance)
            || PlatformScopes.IsSatisfiedBy([.. context.Scopes], PlatformScopes.HistoryReadAll);
    }

    /// <summary>过滤器存入的运行态实例（未受限身份为 <see langword="null"/>）。</summary>
    public static IExecutionEntity? InstanceOf(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return http.Items.TryGetValue(InstanceItem, out var value) ? value as IExecutionEntity : null;
    }

    /// <summary>过滤器存入的任务实体。</summary>
    public static TaskEntity? TaskOf(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return http.Items.TryGetValue(TaskItem, out var value) ? value as TaskEntity : null;
    }

    /// <summary>过滤器存入的历史实例（仅在受限身份且走历史兜底时存在）。</summary>
    public static HistoricProcessInstance? HistoricOf(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return http.Items.TryGetValue(HistoricItem, out var value) ? value as HistoricProcessInstance : null;
    }

    /// <summary>
    /// 按 id 校验运行态实例的租户归属（仅受限身份生效），返回实例供变量授权复用。
    /// <para>不受限（API Key 未配置服务作用域 / 未启用鉴权）时返回 <see langword="null"/>。</para>
    /// </summary>
    internal static async Task<IExecutionEntity?> EnsureInstanceAsync(
        string processInstanceId,
        RequestContext context,
        IExecutionManager executionManager,
        CancellationToken ct)
    {
        if (!context.IsTenantRestricted)
        {
            return null;
        }

        var execution = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException($"流程实例 '{processInstanceId}' 不存在。");

        TenantGuard.Ensure(context, execution.TenantId, "流程实例", processInstanceId);

        return execution;
    }

    /// <summary>实例必须存在：无论是否受限都查库，不存在即 404。</summary>
    internal static async Task<IExecutionEntity> RequireInstanceAsync(
        string processInstanceId,
        RequestContext context,
        IExecutionManager executionManager,
        CancellationToken ct)
    {
        var execution = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException($"流程实例 '{processInstanceId}' 不存在。");

        TenantGuard.Ensure(context, execution.TenantId, "流程实例", processInstanceId);

        return execution;
    }

    /// <summary>按 id 校验任务（含已结束）的租户归属，并返回任务实体供进一步的授权判断。</summary>
    internal static async Task<TaskEntity> EnsureTaskAccessAsync(
        string taskId,
        RequestContext context,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await engine.GetTaskAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException($"任务 '{taskId}' 不存在（可能已完成或流程已推进）。");

        TenantGuard.Ensure(context, task.TenantId, "任务", taskId);

        return task;
    }

    /// <summary>
    /// 历史 / 流程图访问：先看运行态，再看历史，二者都按租户校验。
    /// </summary>
    /// <returns>
    /// 运行态实例（供变量等更细粒度授权复用；已结束或调用方不受租户限制时为 <see langword="null"/>）
    /// 与命中的历史行（走历史兜底时非空）。
    /// </returns>
    internal static async Task<(IExecutionEntity? Running, HistoricProcessInstance? Historic)> ResolveHistoryAsync(
        string processInstanceId,
        RequestContext context,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (!context.IsTenantRestricted)
        {
            return (null, null);
        }

        var execution = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

        if (execution is not null)
        {
            TenantGuard.Ensure(context, execution.TenantId, "流程实例", processInstanceId);
            return (execution, null);
        }

        var historic = await engine.FindHistoricProcessInstanceAsync(processInstanceId, ct)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException($"流程实例 '{processInstanceId}' 没有历史记录。");

        TenantGuard.Ensure(context, historic.TenantId, "流程实例", processInstanceId);

        return (null, historic);
    }

    private static string RouteValue(HttpContext http, string name)
    {
        if (http.Request.RouteValues.TryGetValue(name, out var value)
            && value is string { Length: > 0 } text)
        {
            return text;
        }

        throw new InvalidOperationException($"路由缺少 '{name}' 参数。");
    }
}

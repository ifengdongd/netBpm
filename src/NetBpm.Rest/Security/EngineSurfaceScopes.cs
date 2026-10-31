using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Diagnostics;

namespace NetBpm.Rest.Security;

/// <summary>
/// 引擎原语面（<c>/api/runtime</c> / <c>/api/identity</c> / <c>/api/designer</c>）的 scope 收口。
/// </summary>
/// <remarks>
/// <para>
/// 平台面（<c>/workflow/v1</c>）与引擎面是<b>两层不同的话术</b>：前者按 <c>workflow:*</c> 记账，
/// 每条路径都带着业务语义（参数 schema、审批结论、时间线、表单快照）；后者直接暴露引擎原语
/// （直接完成任务、直写变量、跳转节点），业务语义一条都不做。
/// </para>
/// <para>
/// 重构前引擎面只做租户隔离：一个只被授予 <c>workflow:instance:read</c> 的接入方，拿着自己的凭据
/// 就能 <c>POST /api/runtime/tasks/{id}/complete</c> 把审批办掉 —— 没有时间线、没有快照、
/// 没有 <c>optionId</c> 校验，事后审计里查不到「批了什么」。这正是「对外提供服务」前必须堵的口子。
/// </para>
/// <para>
/// 🔑 <b>只对 <see cref="RequestAuthMode.Client"/> 生效</b>，其余三种凭据保持既有行为：
/// <list type="bullet">
///   <item><description><see cref="RequestAuthMode.None"/>：未启用鉴权（开发 / 单机演示），没有可记账的主体；</description></item>
///   <item><description><see cref="RequestAuthMode.ApiKey"/>：旧版单值密钥代表「整个可信内网」，
///     收紧会一次性打断已上线调用方；</description></item>
///   <item><description><see cref="RequestAuthMode.Jwt"/>：真人令牌，引擎面已按「发起人 / 参与人 / 管理员」
///     做了数据级授权，再叠一层 <c>workflow:*</c> 会把既有控制台全员挡在门外；</description></item>
///   <item><description><see cref="RequestAuthMode.Client"/>：<b>本类存在的理由</b> ——
///     注册客户端的定义就是「一个具体系统，能力上限由 scope 逐条写死」。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 与 <see cref="AdminAuthorization"/> 的分工：那里判「引擎管理动作」（<c>netbpm.admin.*</c>），
/// 这里判「平台能力上限」（<c>workflow:*</c>）。客户端必须<b>两种话术都能对上</b>，故
/// <see cref="IsAdminSatisfied"/> 负责把 <c>netbpm.admin.*</c> 翻译成对应的 <c>workflow:admin:*</c>：
/// 客户端的 scope 只能取自 <see cref="PlatformScopes.All"/>（见 <c>ClientRegistry</c> 的校验），
/// 里面根本没有 <c>netbpm.admin.*</c>，不翻译的话客户端一个管理动作都做不了。
/// </para>
/// <para>
/// 🔑 <b>客户端只拿得到「读」与「管理干预」，拿不到「业务写入」</b>：发起实例 / 办理任务 /
/// 批量办理一律 403（<see cref="EnsureBusinessSurface"/>），与有没有 <c>workflow:task:submit</c> 无关。
/// 原因是原语面的写入<b>不产生审批结论</b>（无 <c>optionId</c>、不写时间线与表单快照），
/// 放行等于允许「无结论的办结」。业务写入统一走 <c>/workflow/v1</c>。
/// </para>
/// <para>
/// ⚠️ scope 仍只是<b>能力上限</b>：租户边界、流程白名单、任务归属、变量参与人校验照旧在过滤器里执行，
/// 且顺序固定为「先 404（不存在 / 跨租户）再 403（缺 scope）」，避免存在性泄露。
/// </para>
/// </remarks>
internal static class EngineSurfaceScopes
{
    /// <summary>
    /// 引擎管理 scope → 平台管理 scope 的对应表。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>覆盖指标 / 审计 / Redis / 作业 / 归档这几类：它们的平台面还没有对应端点
    /// （`/admin/v1/*` 尚未铺开），也就没有可授予客户端的 scope。宁可让客户端被拒，
    /// 也不要为「先通再说」发明一个 <c>workflow:admin:*</c>。
    /// </remarks>
    private static readonly Dictionary<string, string> AdminBridge = new(StringComparer.Ordinal)
    {
        [TaskScopes.AdminModelRead] = PlatformScopes.ProcessRead,
        [TaskScopes.AdminModelWrite] = PlatformScopes.AdminProcess,
        [TaskScopes.AdminTaskModerate] = PlatformScopes.TaskAssign,
        [TaskScopes.AdminInstanceJump] = PlatformScopes.AdminInstance,
        [TaskScopes.AdminInstanceDelete] = PlatformScopes.AdminInstance,
        [TaskScopes.AdminInstanceSuspend] = PlatformScopes.AdminInstance,
        [TaskScopes.AdminVariablesWrite] = PlatformScopes.AdminVariables,
    };

    /// <summary>引擎管理 scope 在平台话术里的等价 scope（无对应时返回 <see langword="null"/>）。</summary>
    /// <param name="engineScope">引擎管理 scope（见 <see cref="TaskScopes"/>）。</param>
    internal static string? PlatformEquivalent(string engineScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineScope);

        return AdminBridge.TryGetValue(engineScope, out var platform) ? platform : null;
    }

    /// <summary>
    /// 当前请求是否具备某个「引擎管理动作」的权限（<see cref="AdminAuthorization"/> 的判定内核）。
    /// </summary>
    /// <param name="context">请求身份上下文。</param>
    /// <param name="engineScope">引擎管理 scope（<c>netbpm.admin.*</c>）。</param>
    internal static bool IsAdminSatisfied(RequestContext context, string engineScope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineScope);

        // 用户令牌与旧 API Key 走原有口径：只认 netbpm.admin.*（ApiKey 在两处调用点之前已放行）
        if (context.HasAdminScope(engineScope))
        {
            return true;
        }

        // 客户端：翻译成平台 scope 再判一次（PlatformScopes.IsSatisfiedBy 对 workflow:admin:* 不做通配）
        if (context.AuthMode != RequestAuthMode.Client)
        {
            return false;
        }

        return PlatformEquivalent(engineScope) is { } platform
            && PlatformScopes.IsSatisfiedBy([.. context.Scopes], platform);
    }

    /// <summary>
    /// 校验当前请求是否具备指定的平台 scope；不具备时抛 <see cref="ForbiddenException"/>（映射 403）。
    /// <para>非 <see cref="RequestAuthMode.Client"/> 一律放行（见类型注释里的四种凭据口径）。</para>
    /// </summary>
    /// <param name="context">请求身份上下文。</param>
    /// <param name="scope">所需平台 scope（见 <see cref="PlatformScopes"/>）。</param>
    /// <param name="action">动作描述（用于 403 文案）。</param>
    internal static void Ensure(RequestContext context, string scope, string action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        if (context.AuthMode != RequestAuthMode.Client)
        {
            return;
        }

        if (PlatformScopes.IsSatisfiedBy([.. context.Scopes], scope))
        {
            return;
        }

        // 与 AdminAuthorization 共用同一个「谁在撞门」计数器：收紧上线后第一件事就是看
        // 哪个客户端缺哪个 scope，两种话术的拒绝必须能一起被订阅到
        AdminDiagnostics.AuthorizationDenied.Add(
            1,
            new(AdminDiagnostics.TagScope, scope),
            new(AdminDiagnostics.TagAuthMode, AdminDiagnostics.AuthModeTag(context.AuthMode)));

        throw new ForbiddenException(
            $"当前客户端缺少 scope '{scope}'，无法{action}。"
            + "引擎原语面（/api/runtime、/api/identity、/api/designer）按平台 scope 记账，"
            + "请为该客户端补充授权；业务面的 /workflow/v1 是推荐的对外入口。");
    }

    /// <summary>
    /// 给引擎面端点挂上平台 scope 校验（在 handler 之前执行，缺 scope 返回 403）。
    /// <para>⚠️ 必须排在 <see cref="RuntimeAccess"/> 的访问过滤器<b>之后</b>：先 404 再 403。</para>
    /// </summary>
    /// <param name="builder">端点构建器。</param>
    /// <param name="scope">所需平台 scope。</param>
    /// <param name="action">动作描述（用于 403 文案）。</param>
    internal static RouteHandlerBuilder RequireEngineScope(
        this RouteHandlerBuilder builder,
        string scope,
        string action)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        return builder.AddEndpointFilter(async (invocationContext, next) =>
        {
            var requestContext = invocationContext.HttpContext.RequestServices
                .GetRequiredService<RequestContext>();

            Ensure(requestContext, scope, action);

            return await next(invocationContext).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 拒绝注册客户端在引擎原语面执行<b>业务写入</b>（发起实例 / 办理任务 / 批量办理），
    /// 无论它持有什么 scope。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 引擎原语的 <c>complete</c> 只做「把任务推进一步」：没有 <c>optionId</c>、没有审批语义、
    /// 不写 <c>nb_instance_timeline</c>、不落 <c>nb_approval_snapshot</c>。于是有
    /// <c>workflow:task:submit</c> 的客户端能拿到一次<b>「无结论的办结」</b>——
    /// 实例确实办完了，但事后谁也答不出「批了什么、谁批的」。
    /// </para>
    /// <para>
    /// 两条路里选了「关掉」，而不是「让原语面也写时间线」：
    /// <list type="number">
    ///   <item><description>原语面<b>拿不到</b>审批选项，硬写时间线只能靠猜语义；
    ///     而时间线的设计前提是「写时定稿、不猜」——填一条猜出来的记录比缺一条更坏，
    ///     因为它冒充了唯一权威记录；</description></item>
    ///   <item><description>同样缺时间线的还有跳转 / 终止 / 变量直写（见清单 §3 ② 的前置规矩），
    ///     单补 <c>complete</c> 只是把一类漏洞缩小成一个点。</description></item>
    /// </list>
    /// 结论：业务写入是<b>平台面的职责</b>，原语面只保留「读」与「管理干预」
    /// （挂起 / 激活 / 删除 / 跳转 / 迁移 / 转办 / 变量直写）——
    /// 后者都是显式管理授权、可审计的运维动作，不冒充审批结论。
    /// </para>
    /// </remarks>
    /// <param name="context">请求身份上下文。</param>
    /// <param name="action">动作描述（用于 403 文案）。</param>
    /// <param name="platformEndpoint">替代入口（写进 403 文案，让调用方知道该改哪里）。</param>
    internal static void EnsureBusinessSurface(
        RequestContext context,
        string action,
        string platformEndpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformEndpoint);

        // 与 scope 闸门同一口径：只对注册客户端生效，其余凭据是既定集成方式，不能一刀切
        if (context.AuthMode != RequestAuthMode.Client)
        {
            return;
        }

        AdminDiagnostics.BusinessSurfaceDenied.Add(
            1,
            new(AdminDiagnostics.TagAction, action),
            new(AdminDiagnostics.TagAuthMode, AdminDiagnostics.AuthModeTag(context.AuthMode)));

        throw new ForbiddenException(
            $"注册客户端不得在引擎原语面{action}：该路径绕开业务面的全部语义"
            + "（参数 schema / 审批选项校验、审批时间线、表单快照），"
            + "事后无法回答「这张单到底发生了什么」。请改走平台业务面："
            + platformEndpoint
            + "；引擎面只向客户端开放「读」与「管理干预」。");
    }

    /// <summary>
    /// 给引擎面的业务写入端点挂上「客户端不得调用」的限制。
    /// <para>⚠️ 与 <see cref="RequireEngineScope"/> 一样必须排在访问过滤器<b>之后</b>：先 404 再 403。</para>
    /// </summary>
    /// <param name="builder">端点构建器。</param>
    /// <param name="action">动作描述（用于 403 文案）。</param>
    /// <param name="platformEndpoint">替代入口（写进 403 文案）。</param>
    internal static RouteHandlerBuilder RequireBusinessSurface(
        this RouteHandlerBuilder builder,
        string action,
        string platformEndpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(async (invocationContext, next) =>
        {
            var requestContext = invocationContext.HttpContext.RequestServices
                .GetRequiredService<RequestContext>();

            EnsureBusinessSurface(requestContext, action, platformEndpoint);

            return await next(invocationContext).ConfigureAwait(false);
        });
    }
}

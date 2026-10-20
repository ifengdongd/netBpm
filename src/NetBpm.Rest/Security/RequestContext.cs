namespace NetBpm.Rest.Security;

/// <summary>本请求使用的鉴权方式。</summary>
public enum RequestAuthMode
{
    /// <summary>未配置任何凭据（开发 / 单机演示）。</summary>
    None = 0,

    /// <summary>服务间 API Key：调用方是可信服务，身份可由请求头声明。</summary>
    ApiKey = 1,

    /// <summary>用户级 JWT：身份只来自已签名令牌，请求体 / 查询参数不得覆盖。</summary>
    Jwt = 2,
}

/// <summary>
/// 当前请求的身份上下文（由鉴权中间件填充）。
/// <para>
/// 本子集不内置用户中心：API Key 证明「调用方是谁」，<c>X-User-Id</c> / <c>X-Tenant-Id</c>
/// 声明「以谁的身份、在哪个租户下操作」。未启用凭据时这两个头仍会被读取，方便开发环境模拟。
/// </para>
/// <para>
/// <see cref="RequestAuthMode.Jwt"/> 是安全边界：身份只能来自令牌，
/// 业务参数里的 <c>startUserId</c> / <c>tenantId</c> 与令牌不一致时必须拒绝（403）。
/// </para>
/// </summary>
public sealed class RequestContext
{
    /// <summary>操作人（JWT 的 <c>sub</c>，或 API Key 模式下的 <c>X-User-Id</c>）。</summary>
    public string? UserId { get; set; }

    /// <summary>租户（JWT 的 <c>tenant</c> / <c>tenant_id</c>，或 <c>X-Tenant-Id</c>）。</summary>
    public string? TenantId { get; set; }

    /// <summary>本请求采用的鉴权方式。</summary>
    public RequestAuthMode AuthMode { get; set; }

    /// <summary>令牌里的组（<c>groups</c> / <c>group</c> 声明），用于候选组匹配。</summary>
    public IReadOnlySet<string> Groups { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>令牌里的授权范围（<c>scope</c> / <c>scp</c> 声明）。</summary>
    public IReadOnlySet<string> Scopes { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// API Key 允许访问的租户（服务作用域）。为空表示不限制（兼容旧行为：可信服务可跨租户）。
    /// <para>配置项：<c>NetBpm:Security:ApiKeyTenants</c>。</para>
    /// </summary>
    public IReadOnlySet<string> AllowedTenants { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 是否是以<b>用户身份</b>认证（JWT）。用户身份需要做身份不可覆盖校验；
    /// API Key 代表可信服务，允许显式传参。
    /// </summary>
    public bool IsUserPrincipal => AuthMode == RequestAuthMode.Jwt;

    /// <summary>
    /// 本请求是否受租户边界约束：JWT 一律受限；API Key 在配置了 <see cref="AllowedTenants"/> 时受限。
    /// </summary>
    public bool IsTenantRestricted => IsUserPrincipal || AllowedTenants.Count > 0;

    /// <summary>
    /// 生效租户：显式声明优先；服务密钥只配置了一个租户时可自动落到该租户。
    /// </summary>
    public string? EffectiveTenantId =>
        TenantId ?? (AllowedTenants.Count == 1 ? AllowedTenants.First() : null);

    /// <summary>是否拥有管理范围（<c>netbpm.admin</c>），可越过“仅办理人可操作”的限制。</summary>
    public bool IsAdmin => Scopes.Contains(TaskScopes.Admin);

    /// <summary>是否具备指定 scope。</summary>
    public bool HasScope(string scope) => Scopes.Contains(scope);

    /// <summary>
    /// 是否具备细粒度管理 scope：持有 <c>netbpm.admin</c>（超级权限）或该 scope 本身。
    /// <para>
    /// 这是管理能力判定的唯一入口：<c>netbpm.admin</c> 隐含全部 <c>netbpm.admin.*</c> 子权限，
    /// 保证已发放的旧令牌在引入细粒度授权后仍然可用。
    /// </para>
    /// </summary>
    public bool HasAdminScope(string scope) => Scopes.Contains(TaskScopes.Admin) || Scopes.Contains(scope);
}

/// <summary>
/// REST 层使用的授权范围常量。
/// <para>
/// 命名约定：<c>netbpm.admin</c> 是超级管理范围，<c>netbpm.admin.&lt;域&gt;.&lt;动作&gt;</c> 是可单独授予的
/// 细粒度权限。持有 <c>netbpm.admin</c> 隐含全部子 scope（见 <see cref="RequestContext.HasAdminScope"/>）。
/// </para>
/// <para>
/// 注意：细粒度管理 scope <b>只约束「管理员越权」这条路径</b>。业务参与人（办理人 / 候选人 / 发起人）
/// 依业务规则本就可以执行的动作，不需要管理 scope —— 否则会把正常办理也挡在门外。
/// </para>
/// </summary>
public static class TaskScopes
{
    /// <summary>超级管理范围：隐含全部 <c>netbpm.admin.*</c> 子权限，可越过“仅办理人可操作”的限制。</summary>
    public const string Admin = "netbpm.admin";

    /// <summary>管理端读：流程模型 / 版本历史 / 流程类型。</summary>
    public const string AdminModelRead = "netbpm.admin.model.read";

    /// <summary>管理端写：保存 / 删除 / 回滚 / 发布模型，增删改流程类型。</summary>
    public const string AdminModelWrite = "netbpm.admin.model.write";

    /// <summary>任务管理动作：转办、改任务字段、增删候选关系（越权时所需）。</summary>
    public const string AdminTaskModerate = "netbpm.admin.task.moderate";

    /// <summary>实例管理动作：跳转节点、迁移到其它定义（越权时所需）。</summary>
    public const string AdminInstanceJump = "netbpm.admin.instance.jump";

    /// <summary>实例管理动作：删除（撤销）运行中的流程实例，含批量删除。</summary>
    public const string AdminInstanceDelete = "netbpm.admin.instance.delete";

    /// <summary>实例管理动作：挂起 / 激活流程实例（影响所有人能否继续办理）。</summary>
    public const string AdminInstanceSuspend = "netbpm.admin.instance.suspend";

    /// <summary>变量直写：越过参与人限制直接写 / 删流程实例变量与任务局部变量。</summary>
    public const string AdminVariablesWrite = "netbpm.admin.variables.write";

    /// <summary>指标查询。</summary>
    public const string AdminMetricsRead = "netbpm.admin.metrics.read";

    /// <summary>管理动作审计查询。</summary>
    public const string AdminAuditRead = "netbpm.admin.audit.read";

    /// <summary>清理管理动作审计（按保留期删除，可抹除历史痕迹，需单独授权）。</summary>
    public const string AdminAuditPurge = "netbpm.admin.audit.purge";

    /// <summary>历史归档（把已结束实例的历史行搬运到归档表）。</summary>
    public const string AdminHistoryArchive = "netbpm.admin.history.archive";
}

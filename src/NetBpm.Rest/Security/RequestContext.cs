namespace NetBpm.Rest.Security;

/// <summary>本请求使用的鉴权方式。</summary>
public enum RequestAuthMode
{
    /// <summary>未配置任何凭据（开发 / 单机演示）。</summary>
    None = 0,

    /// <summary>
    /// 旧版服务间 API Key（<c>NetBpm:Security:ApiKey</c> 单值配置）：
    /// 调用方是可信服务，身份可由请求头声明。
    /// </summary>
    /// <remarks>
    /// 保留是因为已上线的调用方还在用。新接入一律走 <see cref="Client"/>：
    /// 单值密钥泄漏一次全员停摆、无法按系统限流、审计里分不清是谁在调。
    /// </remarks>
    ApiKey = 1,

    /// <summary>用户级 JWT：身份只来自已签名令牌，请求体 / 查询参数不得覆盖。</summary>
    Jwt = 2,

    /// <summary>
    /// 注册客户端密钥（<c>nbc.{clientId}.{token}</c>，见 <c>IClientRegistry</c>）：
    /// 调用方是<b>一个具体系统</b>，租户由客户端记录钉死，scope 按客户端记账。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ApiKey"/> 的关键区别：客户端凭据<b>不代表管理授权</b>。
    /// 旧 API Key 走「可信服务即管理员」的老口径，客户端凭据必须逐条命中 scope，
    /// 否则一个只被授予 <c>workflow:instance:read</c> 的接入方就能调用管理端点。
    /// </remarks>
    Client = 3,
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
    /// <summary>
    /// 实际操作人（JWT 的 <c>sub</c>；客户端凭据模式下由 <c>X-User-Id</c> / <c>X-Subject-Id</c> 声明；
    /// 旧 API Key 模式下由请求头或请求体声明）。
    /// </summary>
    /// <remarks>
    /// 这是中台三元组里的 <c>subject</c>，也是审计记录里「谁批的」的唯一来源。
    /// 纯服务令牌（未声明操作人）时为 <see langword="null"/>——此时<b>不得提交审批</b>。
    /// </remarks>
    public string? SubjectId { get; set; }

    /// <summary>
    /// 操作人（兼容别名，等价于 <see cref="SubjectId"/>）。
    /// </summary>
    /// <remarks>
    /// 保留旧名字是为了让既有端点与授权逻辑零改动；新代码一律写 <see cref="SubjectId"/>，
    /// 因为「UserId」在客户端凭据模式下会产生歧义（是调用系统的用户，还是系统自己？）。
    /// </remarks>
    public string? UserId
    {
        get => SubjectId;
        set => SubjectId = value;
    }

    /// <summary>
    /// 调用方系统（中台三元组里的 <c>caller</c>）：客户端凭据模式下的 <c>clientId</c>。
    /// 旧 API Key / JWT 模式下为 <see langword="null"/>（那两种形态里没有「系统」这一层）。
    /// </summary>
    public string? CallerId { get; set; }

    /// <summary>调用方系统的显示名（进审计与日志，避免只有一串 <c>cl_xxxx</c>）。</summary>
    public string? CallerName { get; set; }

    /// <summary>租户（JWT 的 <c>tenant</c> / <c>tenant_id</c>，或 <c>X-Tenant-Id</c>）。</summary>
    public string? TenantId { get; set; }

    /// <summary>本请求采用的鉴权方式。</summary>
    public RequestAuthMode AuthMode { get; set; }

    /// <summary>
    /// 请求 id（响应头 <c>X-Request-Id</c> 回显）：调用方传什么就回什么，没传则用框架的 trace id。
    /// 排障时让「调用方日志里的一个值」直接对上「服务端日志里的一个值」。
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>令牌里的组（<c>groups</c> / <c>group</c> 声明），用于候选组匹配。</summary>
    public IReadOnlySet<string> Groups { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>令牌里的授权范围（<c>scope</c> / <c>scp</c> 声明）。</summary>
    public IReadOnlySet<string> Scopes { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// API Key 允许访问的租户（服务作用域）。为空表示不限制（兼容旧行为：可信服务可跨租户）。
    /// <para>配置项：<c>NetBpm:Security:ApiKeyTenants</c>；客户端凭据模式下恒为「该客户端所属租户」。</para>
    /// </summary>
    public IReadOnlySet<string> AllowedTenants { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 客户端可用的流程白名单（空集表示不限）。未授权流程按<b>不存在</b>处理（404），不返回 403。
    /// </summary>
    public IReadOnlySet<string> AllowedProcessKeys { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 本调用方的每分钟请求配额（已含租户默认值的回落）；为 <see langword="null"/> 表示未声明，
    /// 由限流中间件按凭据种类取缺省值。
    /// </summary>
    /// <remarks>
    /// 由鉴权中间件从客户端记录里取出：限流中间件必须<b>在鉴权之后</b>才能按客户端配额分桶，
    /// 而它自己不应该去查库（每个请求一次多余查询，且把 IO 放进限流热路径）。
    /// </remarks>
    public int? QuotaPerMinute { get; set; }

    /// <summary>
    /// 是否是以<b>用户身份</b>认证（JWT）。用户身份需要做身份不可覆盖校验；
    /// API Key / 客户端凭据代表可信服务，允许显式传参。
    /// </summary>
    public bool IsUserPrincipal => AuthMode == RequestAuthMode.Jwt;

    /// <summary>是否由注册客户端凭据认证（中台形态的「系统调用」）。</summary>
    public bool IsClientPrincipal => AuthMode == RequestAuthMode.Client;

    /// <summary>是否为「系统代用户」：既有调用方（系统），也有真人主体。</summary>
    public bool IsOnBehalfOf =>
        IsClientPrincipal
        && SubjectId is not null
        && !string.Equals(CallerId, SubjectId, StringComparison.Ordinal);

    /// <summary>
    /// 本请求是否允许提交审批。
    /// </summary>
    /// <remarks>
    /// 判据是「有没有真人主体」，不是「有没有 scope」：纯服务令牌即使被授予
    /// <c>workflow:task:submit</c> 也必须拒绝——审批结论要能追到一个人。
    /// 旧 API Key 与未启用鉴权的开发模式沿用既有行为（它们本来就没有主体概念）。
    /// </remarks>
    public bool CanSubmitApproval => AuthMode switch
    {
        RequestAuthMode.None => true,
        RequestAuthMode.Jwt => true,
        RequestAuthMode.ApiKey => true,
        RequestAuthMode.Client => SubjectId is not null,
        _ => false,
    };

    /// <summary>
    /// 本请求是否受租户边界约束：JWT 一律受限；客户端凭据一律受限；API Key 在配置了
    /// <see cref="AllowedTenants"/> 时受限。
    /// </summary>
    public bool IsTenantRestricted =>
        IsUserPrincipal || IsClientPrincipal || AllowedTenants.Count > 0;

    /// <summary>
    /// 生效租户：显式声明优先；服务密钥只配置了一个租户时可自动落到该租户。
    /// </summary>
    public string? EffectiveTenantId =>
        TenantId ?? (AllowedTenants.Count == 1 ? AllowedTenants.First() : null);

    /// <summary>是否被授权使用该流程（白名单为空视为全部授权）。</summary>
    /// <param name="processKey">流程 key。</param>
    public bool CanUseProcess(string processKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(processKey);

        return AllowedProcessKeys.Count == 0
            || AllowedProcessKeys.Contains(processKey);
    }

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

    /// <summary>Redis 作业队列诊断（连接状态 / 队列键条目数 / 服务端 INFO）。</summary>
    public const string AdminRedisRead = "netbpm.admin.redis.read";

    /// <summary>管理动作审计查询。</summary>
    public const string AdminAuditRead = "netbpm.admin.audit.read";

    /// <summary>清理管理动作审计（按保留期删除，可抹除历史痕迹，需单独授权）。</summary>
    public const string AdminAuditPurge = "netbpm.admin.audit.purge";

    /// <summary>历史归档（把已结束实例的历史行搬运到归档表）。</summary>
    public const string AdminHistoryArchive = "netbpm.admin.history.archive";

    /// <summary>定时作业查询（边界定时器的运维诊断）。</summary>
    public const string AdminJobRead = "netbpm.admin.job.read";

    /// <summary>定时作业改期（修改到期时间）。</summary>
    public const string AdminJobWrite = "netbpm.admin.job.write";
}

using NetBpm.Persistence;
using NetBpm.Persistence.Admin;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Endpoints;

/// <summary>账号密码登录请求。</summary>
/// <param name="Username">登录名。</param>
/// <param name="Password">口令（明文，仅在本请求内用于校验，不落库不记审计）。</param>
/// <param name="TenantId">期望登录到的租户（账号绑定了租户时必须一致）。</param>
public sealed record LoginRequest(string Username, string Password, string? TenantId = null);

/// <summary>登录成功响应。</summary>
/// <param name="Token">访问令牌（<c>Authorization: Bearer &lt;token&gt;</c>）。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
/// <param name="User">操作人（令牌 <c>sub</c>）。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Tenant">租户（可为空）。</param>
/// <param name="Scopes">令牌携带的授权范围。</param>
public sealed record LoginResponse(
    string Token,
    DateTime ExpiresAt,
    string User,
    string? DisplayName,
    string? Tenant,
    IReadOnlyList<string> Scopes);

/// <summary>当前身份（<c>GET /api/auth/me</c>）。</summary>
/// <param name="User">操作人（兼容字段，等价于 <paramref name="Subject"/>）。</param>
/// <param name="Tenant">租户。</param>
/// <param name="AuthMode">鉴权方式（<c>none</c> / <c>apikey</c> / <c>jwt</c> / <c>client</c>）。</param>
/// <param name="Scopes">凭据携带的 scope。</param>
/// <param name="IsAdmin">是否具备引擎管理范围（<c>netbpm.admin</c>）。</param>
/// <param name="Subject">实际操作人；纯服务令牌为 <see langword="null"/>。</param>
/// <param name="Caller">调用方系统（客户端凭据模式下的 <c>clientId</c>）。</param>
/// <param name="CallerName">调用方系统名称。</param>
/// <param name="CanSubmitApproval">
/// 是否允许提交审批。纯服务令牌恒为 <see langword="false"/>——审批结论必须落到一个真人。
/// </param>
/// <param name="RequestId">本次请求 id（与响应头 <c>X-Request-Id</c> 一致）。</param>
public sealed record CurrentIdentity(
    string? User,
    string? Tenant,
    string AuthMode,
    IReadOnlyList<string> Scopes,
    bool IsAdmin,
    string? Subject = null,
    string? Caller = null,
    string? CallerName = null,
    bool CanSubmitApproval = true,
    string? RequestId = null);

/// <summary>
/// 账号密码登录端点：<c>POST /api/auth/login</c> 校验 <c>admin_user</c> 里的账号，
/// 成功后用与鉴权中间件同一套密钥签发 JWT（<c>GET /api/auth/me</c> 回显当前身份）。
/// <para>
/// 与既有鉴权方式的关系：本端点<b>只新增「用户令牌」这一种来源</b>，
/// <c>NetBpm:Security:ApiKey</c> 与外部签发的 JWT 仍然照旧可用；
/// 未配置 <c>NetBpm:Security:Jwt:SigningKey</c> 时签不出令牌，端点返回 503 说明原因。
/// </para>
/// </summary>
public static class AuthEndpoints
{
    // 用于「账号不存在」时也走一次 PBKDF2，抹平与「口令错误」的耗时差，避免用响应时间探测账号是否存在
    private static readonly string DummyPasswordHash = NetBpm.Persistence.Admin.PasswordHasher.Hash(
        Guid.NewGuid().ToString("N"));

    /// <summary>注册鉴权相关端点。</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/auth").WithTags("auth");

        group.MapPost("/login", LoginAsync)
            .Accepts<LoginRequest>("application/json")
            .Produces<LoginResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/me", MeAsync).Produces<CurrentIdentity>()
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest? request,
        AuthTokenIssuer issuer,
        IAdminUserStore users,
        AuthTokenOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return EndpointResults.InvalidRequest("请提供登录名与口令。");
        }

        if (!issuer.CanIssue)
        {
            // 签不出令牌就不要让登录「看起来成功」
            return Results.Json(
                new ErrorResponse(
                    "未启用令牌签发：请先配置 NetBpm:Security:Jwt:RsaPrivateKey（RS256，推荐）"
                    + "或 SigningKey（HS256，至少 32 字节）。",
                    Code: "jwt_not_configured"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var username = request.Username.Trim();
        var user = await users.FindByUsernameAsync(username, ct).ConfigureAwait(false);

        // 锁定先于口令校验：口令再对也不放行，否则「试到正确口令」就能绕过锁定
        if (user is not null
            && user.LockedUntil is { } lockedUntil
            && lockedUntil > DateTime.UtcNow)
        {
            return Results.Json(
                new ErrorResponse(
                    $"账号已锁定（连续失败 {options.MaxFailedAttempts} 次），请在 {lockedUntil:u} 之后重试。",
                    Code: "account_locked"),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        // 账号不存在也做一次同强度校验：既防计时探测，也让失败路径的耗时一致
        var passwordOk = NetBpm.Persistence.Admin.PasswordHasher.Verify(
            request.Password,
            user?.PasswordHash ?? DummyPasswordHash);

        if (user is null || !passwordOk)
        {
            if (user is not null)
            {
                await users.MarkLoginFailureAsync(
                    user.Id,
                    options.MaxFailedAttempts,
                    options.MaxFailedAttempts > 0
                        ? DateTime.UtcNow.AddMinutes(Math.Max(1, options.LockoutMinutes))
                        : null,
                    ct).ConfigureAwait(false);
            }

            loggerFactory.CreateLogger("NetBpm.Auth")
                .LogWarning("登录失败：{Username}（口令错误或账号不存在）", username);

            // 统一文案：不透露账号是否存在
            return Results.Json(
                new ErrorResponse("登录名或口令错误。", Code: "invalid_credentials"),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!user.Enabled)
        {
            return Results.Json(
                new ErrorResponse("账号已停用。", Code: "account_disabled"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var tenant = string.IsNullOrWhiteSpace(user.TenantId) ? null : user.TenantId;

        if (tenant is not null
            && !string.IsNullOrWhiteSpace(request.TenantId)
            && !string.Equals(request.TenantId.Trim(), tenant, StringComparison.Ordinal))
        {
            return Results.Json(
                new ErrorResponse($"账号 '{username}' 不属于租户 '{request.TenantId}'。", Code: "tenant_mismatch"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var token = issuer.Issue(user.Username, user.Scopes, user.DisplayName, tenant);

        await users.MarkLoginSuccessAsync(user.Id, DateTime.UtcNow, ct).ConfigureAwait(false);

        return Results.Ok(new LoginResponse(
            token.Token,
            token.ExpiresAt,
            token.UserId,
            user.DisplayName,
            token.TenantId,
            token.Scopes));
    }

    /// <summary>
    /// 当前身份（<c>GET /api/auth/me</c>）：凭据探针，前端登录页用它判断手里的凭据是否可用。
    /// <para>
    /// 能走到这里说明中间件已经放行，因此 <see cref="RequestAuthMode.None"/> 只有一种含义：
    /// <b>后端未启用鉴权（开放模式）</b> —— 此时「无凭据」不是错误，回显 <c>authMode=none</c>。
    /// 启用鉴权后，无凭据 / 无效凭据在中间件就返回了 401，到不了这里。
    /// </para>
    /// </summary>
    private static IResult MeAsync(RequestContext requestContext)
    {
        return Results.Ok(new CurrentIdentity(
            requestContext.SubjectId,
            requestContext.TenantId,
            requestContext.AuthMode.ToString().ToLowerInvariant(),
            [.. requestContext.Scopes],
            requestContext.IsAdmin,
            requestContext.SubjectId,
            requestContext.CallerId,
            requestContext.CallerName,
            requestContext.CanSubmitApproval,
            requestContext.RequestId));
    }
}

/// <summary>鉴权相关服务的注册。</summary>
public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// 注册令牌签发器与账号存储：有库走 <c>admin_user</c> 表，无库（in-memory / 开发）
    /// 走内存实现（由 <c>NetBpm:Security:DevUsers</c> 播种，不配置就是空表）。
    /// </summary>
    public static IServiceCollection AddNetBpmAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AuthTokenOptions
        {
            SigningKey = configuration["NetBpm:Security:Jwt:SigningKey"],
            Issuer = configuration["NetBpm:Security:Jwt:Issuer"],
            Audience = configuration["NetBpm:Security:Jwt:Audience"],
        };

        var section = configuration.GetSection("NetBpm:Security:Login");

        if (int.TryParse(section["AccessTokenMinutes"], out var minutes) && minutes > 0)
        {
            options.AccessTokenMinutes = minutes;
        }

        if (int.TryParse(section["MaxFailedAttempts"], out var maxFailures) && maxFailures >= 0)
        {
            options.MaxFailedAttempts = maxFailures;
        }

        if (int.TryParse(section["LockoutMinutes"], out var lockout) && lockout > 0)
        {
            options.LockoutMinutes = lockout;
        }

        services.AddSingleton(options);

        // JWT 密钥材料：HS256（内网）与 RS256（对外 / 外部 IdP）统一的唯一来源。
        // 中间件与签发器都从这里取，避免「签的时候用 A、验的时候用 B」这种登录成功却处处 401 的故障。
        services.AddSingleton(provider => JwtKeyMaterial.Load(
            configuration,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("NetBpm.Security.Jwt")));

        // 外部 IdP 公钥集：必须是单例（缓存就在实例里），因此用工厂从 IHttpClientFactory 取连接池
        services.AddHttpClient();

        services.AddSingleton(provider => new RemoteJwksProvider(
            configuration,
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(RemoteJwksProvider)),
            provider.GetRequiredService<ILogger<RemoteJwksProvider>>()));

        services.AddSingleton<AuthTokenIssuer>();
        services.AddSingleton<IAdminUserStore>(provider =>
        {
            var netBpmOptions = provider.GetRequiredService<NetBpmOptions>();

            if (!netBpmOptions.UsesDatabase)
            {
                return new InMemoryAdminUserStore(ReadDevUsers(configuration, provider));
            }

            return new DapperAdminUserStore(provider.GetRequiredService<IDbConnectionFactory>());
        });

        return services;
    }

    /// <summary>
    /// 开发账号（<c>NetBpm:Security:DevUsers</c>）：只在无库模式下生效，
    /// 明文口令只存在于配置里，启动时转成散列。
    /// </summary>
    private static List<AdminUser> ReadDevUsers(IConfiguration configuration, IServiceProvider provider)
    {
        var section = configuration.GetSection("NetBpm:Security:DevUsers");

        if (!section.Exists())
        {
            return [];
        }

        var configured = section.Get<DevUserOption[]>() ?? [];
        var users = new List<AdminUser>();

        foreach (var item in configured)
        {
            if (string.IsNullOrWhiteSpace(item.Username) || string.IsNullOrWhiteSpace(item.Password))
            {
                continue;
            }

            var scopes = string.IsNullOrWhiteSpace(item.Scopes)
                ? Array.Empty<string>()
                : item.Scopes.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var now = DateTime.UtcNow;

            users.Add(new AdminUser(
                Guid.NewGuid().ToString("N"),
                item.Username.Trim(),
                NetBpm.Persistence.Admin.PasswordHasher.Hash(item.Password),
                scopes,
                item.DisplayName,
                string.IsNullOrWhiteSpace(item.TenantId) ? null : item.TenantId.Trim(),
                true,
                0,
                null,
                null,
                now,
                now));
        }

        if (users.Count > 0)
        {
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("NetBpm.Auth")
                .LogWarning(
                    "启用了 {Count} 个开发账号（NetBpm:Security:DevUsers，口令写在配置里）：仅限无库 / 本地开发，生产请改用 admin_user 表。",
                    users.Count);
        }

        return users;
    }

    private sealed class DevUserOption
    {
        public string? Username { get; set; }

        public string? Password { get; set; }

        public string? DisplayName { get; set; }

        public string? Scopes { get; set; }

        public string? TenantId { get; set; }
    }
}

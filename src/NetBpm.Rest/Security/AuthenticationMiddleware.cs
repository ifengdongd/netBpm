using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;

namespace NetBpm.Rest.Security;

/// <summary>
/// 可选的鉴权：支持用户级 JWT（HS256）、注册客户端密钥（<c>nbc.*</c>）与服务间 API Key，可同时启用。
/// <para>
/// 配置任一凭据后，所有 <c>/api/**</c> 请求必须通过其中一种校验，否则 401。
/// 都未配置时保持开放（开发 / 单机演示），但仍解析 <c>X-User-Id</c> / <c>X-Tenant-Id</c>。
/// </para>
/// <para>
/// 例外（见 <see cref="AnonymousPaths"/>，与就绪探针同口径）：
/// <c>POST /api/auth/login</c> 用来换取令牌，<c>GET /api/admin/info</c> 只含版本 / 方言 / 结构版本
/// 等运维信息；两者匿名可达，其余 <c>/api/**</c> 一律校验。
/// </para>
/// <para>
/// 校验顺序：
/// <list type="number">
///   <item><description>带 <c>Authorization: Bearer</c> → 按 JWT 校验（签名 / iss / aud / 过期）；</description></item>
///   <item><description>凭据形如 <c>nbc.{clientId}.{token}</c> → 走客户端注册表（租户钉死、scope 按客户端记账）；</description></item>
///   <item><description>带 <c>X-Api-Key</c> → 按旧版服务密钥校验；</description></item>
///   <item><description>都未配置 → 放行；配置了但没带凭据 → 401。</description></item>
/// </list>
/// </para>
/// <para>
/// JWT 的 <c>sub</c> 映射为操作人、<c>tenant</c> / <c>tenant_id</c> 映射为租户。
/// JWT 模式下<b>不读取</b> <c>X-User-Id</c> / <c>X-Tenant-Id</c>：
/// 身份只能来自签名令牌，避免持有效令牌的调用方用请求头提权到其他租户。
/// </para>
/// <para>
/// 服务作用域：配置 <c>NetBpm:Security:ApiKeyTenants</c> 后，该密钥只能在白名单租户内读写；
/// <c>X-Tenant-Id</c> 不在白名单内直接 403，白名单之外的资源按 404 处理（见 <see cref="TenantGuard"/>）。
/// 未配置时保持「可信服务可跨租户」的旧行为。
/// </para>
/// </summary>
public sealed class AuthenticationMiddleware
{
    /// <summary>API Key 请求头（同时接受客户端密钥 <c>nbc.*</c>）。</summary>
    public const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>操作人请求头（JWT 未提供 sub 时使用）。</summary>
    public const string UserHeaderName = "X-User-Id";

    /// <summary>操作人请求头（新名字，语义明确；与 <see cref="UserHeaderName"/> 等价）。</summary>
    public const string SubjectHeaderName = "X-Subject-Id";

    /// <summary>租户请求头（JWT 未提供 tenant 时使用）。</summary>
    public const string TenantHeaderName = "X-Tenant-Id";

    /// <summary>请求 id 请求头（原样回显，缺省用框架 trace id）。</summary>
    public const string RequestIdHeaderName = "X-Request-Id";

    /// <summary>
    /// 即便启用了鉴权也匿名可达的 <c>/api/**</c> 路径（按路径段精确匹配，不匹配前缀相同的其它路径）。
    /// <list type="bullet">
    ///   <item><description><c>/api/auth/login</c>：换凭据的入口，要求先带令牌会造成鸡生蛋；</description></item>
    ///   <item><description><c>/api/admin/info</c>：只含版本 / 方言 / 结构版本等运维信息，与就绪探针同口径
    ///     （登录页要在登录前读它）。鉴权是否启用由 <c>/api/auth/me</c> 回答，不在这里泄漏。</description></item>
    /// </list>
    /// </summary>
    private static readonly string[] AnonymousPaths =
    [
        "/api/auth/login",
        "/api/admin/info",
    ];

    /// <summary>
    /// 受凭据保护的路由前缀。除引擎面 <c>/api</c> 外，还包括平台对外面
    /// <c>/workflow</c> / <c>/engine</c> / <c>/admin</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须按前缀集合判断，而不是只认 <c>/api</c>：<c>/workflow/v1/**</c> 是给外部系统用的业务面，
    /// 若它落在鉴权之外，一个匿名请求就能读到全部流程目录——scope 与白名单全部形同虚设。
    /// </para>
    /// <para>
    /// 未列出的前缀（<c>/health</c>、静态资源、OpenAPI 文档）保持匿名可达。
    /// </para>
    /// </remarks>
    private static readonly string[] ProtectedPrefixes = ["/api", "/workflow", "/engine", "/admin"];

    private readonly RequestDelegate _next;
    private readonly byte[]? _expectedKeyHash;
    private readonly string[] _apiKeyTenants;
    private readonly JwtSecurityTokenHandler? _tokenHandler;
    private readonly TokenValidationParameters? _tokenValidation;
    private readonly bool _clientsEnabled;
    private readonly bool _requireCredential;

    public AuthenticationMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(configuration);

        _next = next;

        // 客户端接入（nbc.* 密钥）默认开启：它的开关与「是否必须带凭据」无关，
        // 只决定要不要接受这种凭据形态。
        _clientsEnabled = configuration.GetValue("NetBpm:Security:Clients:Enabled", true);

        // 「必须带凭据」显式开：只靠客户端注册表保护 API 的部署必须打开它，
        // 否则没配旧 API Key / JWT 时匿名请求依旧放行（见 ApplyNetBpmPlatformStartup 的启动告警）。
        _requireCredential = configuration.GetValue("NetBpm:Security:RequireCredential", false);

        var apiKey = configuration["NetBpm:Security:ApiKey"];

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _expectedKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        }

        // 服务作用域：限定该密钥可访问的租户；为空表示不限制（可信服务可跨租户）
        _apiKeyTenants = configuration
            .GetSection("NetBpm:Security:ApiKeyTenants")
            .Get<string[]>()?
            .Where(tenant => !string.IsNullOrWhiteSpace(tenant))
            .Select(tenant => tenant.Trim())
            .ToArray() ?? [];

        var signingKey = configuration["NetBpm:Security:Jwt:SigningKey"];

        if (!string.IsNullOrWhiteSpace(signingKey))
        {
            var keyBytes = Encoding.UTF8.GetBytes(signingKey);

            if (keyBytes.Length < 32)
            {
                throw new InvalidOperationException(
                    "NetBpm:Security:Jwt:SigningKey 至少需要 32 字节（HS256 密钥强度）。");
            }

            var issuer = configuration["NetBpm:Security:Jwt:Issuer"];
            var audience = configuration["NetBpm:Security:Jwt:Audience"];

            _tokenHandler = new JwtSecurityTokenHandler
            {
                // 关闭入站声明映射：否则 sub 会被改写为 ClaimTypes.NameIdentifier，
                // 后续按 "sub" 取操作人永远取不到（表现为 JWT 通过但身份为空）。
                MapInboundClaims = false,
            };

            _tokenValidation = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                ValidateIssuer = !string.IsNullOrWhiteSpace(issuer),
                ValidIssuer = issuer,
                ValidateAudience = !string.IsNullOrWhiteSpace(audience),
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Sub,
            };
        }
    }

    /// <summary>请求是否配置了任一鉴权方式（或显式要求凭据）。</summary>
    private bool IsConfigured => _expectedKeyHash is not null || _tokenHandler is not null || _requireCredential;

    /// <summary>鉴权中间件入口。</summary>
    public async Task InvokeAsync(HttpContext context, RequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestContext);

        // 请求 id 先落地：401 / 403 的响应体里也要能带回它，否则「失败请求」永远无法对齐日志
        requestContext.RequestId = ResolveRequestId(context);
        context.Response.Headers[RequestIdHeaderName] = requestContext.RequestId;

        if (!IsProtectedPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (IsAnonymousPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var bearer = ResolveBearerToken(context.Request);

        if (!string.IsNullOrEmpty(bearer) && _tokenHandler is not null)
        {
            if (!TryAuthenticateJwt(bearer, requestContext))
            {
                await WriteUnauthorizedAsync(context, "JWT 无效或已过期。", bearer: true).ConfigureAwait(false);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        var apiKey = context.Request.Headers[ApiKeyHeaderName].ToString().Trim();

        // 兼容旧调用方：未启用 JWT 时，Authorization: Bearer <api-key> 等同 X-Api-Key。
        // 客户端密钥（nbc.*）自带可识别前缀，与 JWT 不会混淆，所以无论是否启用 JWT 都按它处理。
        if (apiKey.Length == 0
            && !string.IsNullOrEmpty(bearer)
            && (_tokenHandler is null || LooksLikeClientSecret(bearer)))
        {
            apiKey = bearer;
        }

        // 客户端凭据：形如 nbc.{clientId}.{token}。有前缀就当凭据对待，绝不「验不过就降级成匿名」——
        // 否则一把格式正确但内容错误的密钥会被静默当成未登录的开放模式。
        if (apiKey.Length > 0 && LooksLikeClientSecret(apiKey))
        {
            if (!ClientSecretFormat.TryParse(apiKey, out _, out _))
            {
                await WriteUnauthorizedAsync(
                    context,
                    "客户端密钥格式不正确：应为 nbc.{clientId}.{token}。").ConfigureAwait(false);

                return;
            }

            var outcome = await AuthenticateClientAsync(context, requestContext, apiKey).ConfigureAwait(false);

            if (outcome == ClientCredentialOutcome.Authenticated)
            {
                await _next(context).ConfigureAwait(false);
            }

            return;
        }

        if (!string.IsNullOrEmpty(apiKey) && _expectedKeyHash is not null)
        {
            if (!MatchesApiKey(apiKey))
            {
                await WriteUnauthorizedAsync(context, "API Key 无效。").ConfigureAwait(false);
                return;
            }

            requestContext.AuthMode = RequestAuthMode.ApiKey;

            if (_apiKeyTenants.Length > 0)
            {
                requestContext.AllowedTenants = new HashSet<string>(_apiKeyTenants, StringComparer.Ordinal);
            }

            ApplyHeaderIdentity(context.Request, requestContext);
            EnsureDeclaredTenantAllowed(requestContext);

            await _next(context).ConfigureAwait(false);
            return;
        }

        if (IsConfigured)
        {
            await WriteUnauthorizedAsync(
                context,
                "缺少凭据：请提供 Bearer JWT、客户端密钥（nbc.*）或 X-Api-Key。",
                bearer: _tokenHandler is not null).ConfigureAwait(false);

            return;
        }

        // 未启用鉴权：允许用请求头模拟身份（仅开发环境）
        ApplyHeaderIdentity(context.Request, requestContext);

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>客户端凭据的校验结果。</summary>
    /// <remarks>
    /// 只有两态：通过（继续管道）与已拒绝（响应已写出，调用方直接返回）。
    /// 刻意不给「无法处理」第三态——那会让「服务端没装注册表」变成一种可降级的模糊情况。
    /// </remarks>
    private enum ClientCredentialOutcome
    {
        /// <summary>通过，继续管道。</summary>
        Authenticated = 0,

        /// <summary>未通过，响应已写出。</summary>
        Rejected = 1,
    }

    /// <summary>
    /// 校验客户端密钥并填充身份三元组。
    /// </summary>
    /// <remarks>
    /// 与旧 API Key 的三处关键差异：
    /// ① 租户由客户端记录钉死（<c>X-Tenant-Id</c> 只能等于它，否则 403）；
    /// ② scope 来自客户端记录，而不是「服务即管理员」；
    /// ③ 操作人只能由 <c>X-Subject-Id</c> / <c>X-User-Id</c> 声明，且必须持有
    ///   <see cref="PlatformScopes.TaskSubmitOnBehalf"/>——代客身份是显式授权，不是默认能力。
    /// </remarks>
    private async Task<ClientCredentialOutcome> AuthenticateClientAsync(
        HttpContext context,
        RequestContext requestContext,
        string presentedSecret)
    {
        if (!_clientsEnabled)
        {
            await WriteUnauthorizedAsync(
                context,
                "客户端接入未启用（NetBpm:Security:Clients:Enabled = false）。").ConfigureAwait(false);

            return ClientCredentialOutcome.Rejected;
        }

        var registry = context.RequestServices.GetService<IClientRegistry>();

        if (registry is null)
        {
            await WriteUnauthorizedAsync(
                context,
                "客户端接入未启用：服务端未注册 IClientRegistry。").ConfigureAwait(false);

            return ClientCredentialOutcome.Rejected;
        }

        var result = await registry
            .AuthenticateAsync(presentedSecret, context.RequestAborted)
            .ConfigureAwait(false);

        if (!result.Succeeded || result.Client is not { } client)
        {
            await WriteUnauthorizedAsync(context, DescribeFailure(result.Status)).ConfigureAwait(false);

            return ClientCredentialOutcome.Rejected;
        }

        requestContext.AuthMode = RequestAuthMode.Client;
        requestContext.CallerId = client.ClientId;
        requestContext.CallerName = client.Name;
        requestContext.Scopes = new HashSet<string>(client.Scopes, StringComparer.Ordinal);
        requestContext.AllowedTenants = new HashSet<string>([client.TenantId], StringComparer.Ordinal);
        requestContext.AllowedProcessKeys = new HashSet<string>(client.AllowedProcessKeys, StringComparer.Ordinal);
        requestContext.TenantId = client.TenantId;

        // 配额随身份一起带出：限流中间件在下一个环节，它不该再回表查一次
        requestContext.QuotaPerMinute = client.QuotaPerMinute > 0 ? client.QuotaPerMinute : null;

        // 客户端声明的租户必须与记录一致。这里刻意「拒绝」而不是「忽略」：
        // 忽略会让「写 tenant-b」的请求静默落到 tenant-a，比报错危险得多。
        var declaredTenant = ReadHeader(context.Request, TenantHeaderName);

        if (declaredTenant is not null
            && !string.Equals(declaredTenant, client.TenantId, StringComparison.Ordinal))
        {
            throw new ForbiddenException(
                $"客户端 '{client.ClientId}' 属于租户 '{client.TenantId}'，不能访问租户 '{declaredTenant}'。");
        }

        var declaredSubject = ReadHeader(context.Request, SubjectHeaderName)
            ?? ReadHeader(context.Request, UserHeaderName);

        if (declaredSubject is not null)
        {
            if (!PlatformScopes.IsSatisfiedBy([.. client.Scopes], PlatformScopes.TaskSubmitOnBehalf))
            {
                // 允许任何客户端声明操作人，等于允许「纯服务令牌冒充真人提交审批」，
                // 审批记录里的办理人将不再可信。
                throw new ForbiddenException(
                    $"客户端 '{client.ClientId}' 未获授权声明操作人：代客调用需要 scope "
                    + $"'{PlatformScopes.TaskSubmitOnBehalf}'。");
            }

            requestContext.SubjectId = declaredSubject;
        }

        EnsureDeclaredTenantAllowed(requestContext);

        return ClientCredentialOutcome.Authenticated;
    }

    /// <summary>
    /// 失败原因转文案。<b>不区分</b>「客户端不存在」与「密钥错误」：区分它们等于赠送客户端枚举手段。
    /// </summary>
    private static string DescribeFailure(ClientAuthenticationStatus status) => status switch
    {
        ClientAuthenticationStatus.SecretExpired => "客户端密钥已过期，请轮换后重试。",
        ClientAuthenticationStatus.ClientNotActive => "客户端已停用或尚未启用。",
        ClientAuthenticationStatus.TenantSuspended => "所属租户已停用。",
        _ => "客户端凭据无效。",
    };

    /// <summary>请求 id：调用方传了就用它（便于跨系统串联），否则用框架生成的 trace id。</summary>
    private static string ResolveRequestId(HttpContext context)
    {
        var provided = context.Request.Headers[RequestIdHeaderName].ToString().Trim();

        return provided.Length is > 0 and <= 128 ? provided : context.TraceIdentifier;
    }

    /// <summary>
    /// 是否「看起来是客户端密钥」：只看前缀，不看是否解析得通。
    /// </summary>
    /// <remarks>
    /// 前缀就是承诺。带 <c>nbc.</c> 却解析不出来只能说明凭据有损（复制时截断、多了空格），
    /// 此时必须 401 而不是当作「没带凭据」——后者会让配置错误以「匿名调用成功」的形式潜伏。
    /// </remarks>
    private static bool LooksLikeClientSecret(string value) =>
        value.StartsWith(ClientSecretFormat.Prefix + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>路径是否在 <see cref="AnonymousPaths"/> 内（按段匹配，<c>/api/admin/info</c> 不会放行 <c>/api/admin/information</c>）。</summary>
    private static bool IsAnonymousPath(PathString path)
    {
        foreach (var candidate in AnonymousPaths)
        {
            if (path.StartsWithSegments(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>路径是否需要凭据（按段匹配，<c>/api</c> 不会匹配 <c>/apix</c>）。</summary>
    private static bool IsProtectedPath(PathString path)
    {
        foreach (var prefix in ProtectedPrefixes)
        {
            if (path.StartsWithSegments(prefix))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAuthenticateJwt(string token, RequestContext requestContext)
    {
        try
        {
            var principal = _tokenHandler!.ValidateToken(
                token,
                _tokenValidation!,
                out _);

            var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst("nameid")?.Value
                ?? principal.Identity?.Name;

            var tenantId = principal.FindFirst("tenant")?.Value
                ?? principal.FindFirst("tenant_id")?.Value;

            requestContext.UserId = string.IsNullOrWhiteSpace(userId) ? null : userId;
            requestContext.TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
            requestContext.AuthMode = RequestAuthMode.Jwt;
            requestContext.Groups = ReadClaimSet(principal, "groups", "group");
            requestContext.Scopes = ReadClaimSet(principal, "scope", "scp");

            return true;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取声明集合，兼容三种常见写法：空格分隔（OAuth2 scope）、逗号分隔、JSON 数组字符串。
    /// </summary>
    private static HashSet<string> ReadClaimSet(ClaimsPrincipal principal, params string[] claimTypes)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claimType in claimTypes)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                var raw = claim.Value.Trim().Trim('[', ']');

                foreach (var part in raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
                {
                    var value = part.Trim().Trim('"');

                    if (value.Length > 0)
                    {
                        values.Add(value);
                    }
                }
            }
        }

        return values;
    }

    private bool MatchesApiKey(string provided)
    {
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));

        return CryptographicOperations.FixedTimeEquals(providedHash, _expectedKeyHash);
    }

    private static string? ResolveBearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();

        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>
    /// 服务作用域：<c>X-Tenant-Id</c> 必须落在 <c>ApiKeyTenants</c> 白名单内。
    /// <para>
    /// 必须在这里拒绝：按 id 访问资源时的租户校验（<see cref="TenantGuard"/>）把
    /// <see cref="RequestContext.EffectiveTenantId"/> 当作可信值，若允许请求头声明任意租户，
    /// 就能用它“证明”自己属于目标租户，从而绕过隔离（跨租户读写变成同租户）。
    /// </para>
    /// </summary>
    private static void EnsureDeclaredTenantAllowed(RequestContext requestContext)
    {
        if (requestContext.AllowedTenants.Count == 0
            || string.IsNullOrEmpty(requestContext.TenantId))
        {
            return;
        }

        if (!requestContext.AllowedTenants.Contains(requestContext.TenantId))
        {
            throw new ForbiddenException($"服务密钥无权访问租户 '{requestContext.TenantId}'。");
        }
    }

    private static void ApplyHeaderIdentity(HttpRequest request, RequestContext requestContext)
    {
        // JWT 里已有身份时不覆盖（令牌优先于可伪造的请求头）
        requestContext.UserId ??= ReadHeader(request, UserHeaderName);
        requestContext.TenantId ??= ReadHeader(request, TenantHeaderName);
    }

    private static string? ReadHeader(HttpRequest request, string name)
    {
        var value = request.Headers[name].ToString().Trim();

        return value.Length == 0 ? null : value;
    }

    private static async Task WriteUnauthorizedAsync(
        HttpContext context,
        string message,
        bool bearer = false)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        if (bearer)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
        }

        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response
            .WriteAsJsonAsync(new ErrorResponse(message, Code: "unauthorized"))
            .ConfigureAwait(false);
    }
}

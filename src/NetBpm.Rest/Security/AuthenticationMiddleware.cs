using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace NetBpm.Rest.Security;

/// <summary>
/// 可选的鉴权：支持用户级 JWT（HS256）与服务间 API Key，二者可同时启用。
/// <para>
/// 配置任一凭据后，所有 <c>/api/**</c> 请求必须通过其中一种校验，否则 401。
/// 都未配置时保持开放（开发 / 单机演示），但仍解析 <c>X-User-Id</c> / <c>X-Tenant-Id</c>。
/// </para>
/// <para>
/// <c>POST /api/auth/login</c> 不受本中间件约束（它用来换取令牌）；其余 <c>/api/**</c> 一律校验。
/// </para>
/// <para>
/// 校验顺序：
/// <list type="number">
///   <item><description>带 <c>Authorization: Bearer</c> → 按 JWT 校验（签名 / iss / aud / 过期）；</description></item>
///   <item><description>带 <c>X-Api-Key</c> → 按服务密钥校验；</description></item>
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
    /// <summary>API Key 请求头。</summary>
    public const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>操作人请求头（JWT 未提供 sub 时使用）。</summary>
    public const string UserHeaderName = "X-User-Id";

    /// <summary>租户请求头（JWT 未提供 tenant 时使用）。</summary>
    public const string TenantHeaderName = "X-Tenant-Id";

    private readonly RequestDelegate _next;
    private readonly byte[]? _expectedKeyHash;
    private readonly string[] _apiKeyTenants;
    private readonly JwtSecurityTokenHandler? _tokenHandler;
    private readonly TokenValidationParameters? _tokenValidation;

    public AuthenticationMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(configuration);

        _next = next;

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

    /// <summary>请求是否配置了任一鉴权方式。</summary>
    private bool IsConfigured => _expectedKeyHash is not null || _tokenHandler is not null;

    /// <summary>鉴权中间件入口。</summary>
    public async Task InvokeAsync(HttpContext context, RequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 登录端点必须匿名可达：它本身就是「换凭据」的地方，
        // 若要求先带令牌，登录就变成鸡生蛋问题。
        if (context.Request.Path.StartsWithSegments("/api/auth/login"))
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

        // 兼容旧调用方：未启用 JWT 时，Authorization: Bearer <api-key> 等同 X-Api-Key
        if (apiKey.Length == 0 && _tokenHandler is null && !string.IsNullOrEmpty(bearer))
        {
            apiKey = bearer;
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
                "缺少凭据：请提供 Bearer JWT 或 X-Api-Key。",
                bearer: _tokenHandler is not null).ConfigureAwait(false);

            return;
        }

        // 未启用鉴权：允许用请求头模拟身份（仅开发环境）
        ApplyHeaderIdentity(context.Request, requestContext);

        await _next(context).ConfigureAwait(false);
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

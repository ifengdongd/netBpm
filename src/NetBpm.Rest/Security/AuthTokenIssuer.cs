using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace NetBpm.Rest.Security;

/// <summary>签发出的访问令牌。</summary>
/// <param name="Token">JWT 串（<c>Authorization: Bearer &lt;token&gt;</c>）。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
/// <param name="UserId">操作人（<c>sub</c>）。</param>
/// <param name="TenantId">租户（<c>tenant</c>，可为空）。</param>
/// <param name="Scopes">令牌携带的授权范围（<c>scope</c>）。</param>
public sealed record AuthToken(
    string Token,
    DateTime ExpiresAt,
    string UserId,
    string? TenantId,
    IReadOnlyList<string> Scopes);

/// <summary>令牌签发参数（<c>NetBpm:Security:Jwt</c> + <c>NetBpm:Security:Login</c>）。</summary>
public sealed class AuthTokenOptions
{
    /// <summary>HS256 签名密钥（<c>NetBpm:Security:Jwt:SigningKey</c>）。为空则不能签发。</summary>
    public string? SigningKey { get; set; }

    /// <summary>签发者（<c>NetBpm:Security:Jwt:Issuer</c>，可为空）。</summary>
    public string? Issuer { get; set; }

    /// <summary>受众（<c>NetBpm:Security:Jwt:Audience</c>，可为空）。</summary>
    public string? Audience { get; set; }

    /// <summary>访问令牌有效期（分钟），默认 120。</summary>
    public int AccessTokenMinutes { get; set; } = 120;

    /// <summary>连续失败多少次锁定账号，默认 5（0 = 不锁定）。</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>锁定时长（分钟），默认 10。</summary>
    public int LockoutMinutes { get; set; } = 10;
}

/// <summary>
/// 访问令牌签发器：账号密码登录成功后签发 JWT，供 <see cref="AuthenticationMiddleware"/> 校验。
/// <para>
/// 复用鉴权中间件的同一套密钥与 iss / aud 配置 —— 签出来的令牌必须能被自己验过，
/// 否则登录成功却处处 401。
/// </para>
/// </summary>
public sealed class AuthTokenIssuer
{
    private readonly AuthTokenOptions _options;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly SigningCredentials? _credentials;

    public AuthTokenIssuer(AuthTokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            return;
        }

        var keyBytes = Encoding.UTF8.GetBytes(options.SigningKey);

        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "NetBpm:Security:Jwt:SigningKey 至少需要 32 字节（HS256 密钥强度）。");
        }

        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes),
            SecurityAlgorithms.HmacSha256);
    }

    /// <summary>是否已具备签发能力（配置了签名密钥）。</summary>
    public bool CanIssue => _credentials is not null;

    /// <summary>签发一个访问令牌。</summary>
    /// <exception cref="InvalidOperationException">未配置签名密钥（先查 <see cref="CanIssue"/>）。</exception>
    public AuthToken Issue(
        string userId,
        IReadOnlyList<string> scopes,
        string? displayName = null,
        string? tenantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (_credentials is null)
        {
            throw new InvalidOperationException(
                "未配置 NetBpm:Security:Jwt:SigningKey，无法签发令牌。");
        }

        var issuedAt = DateTime.UtcNow;
        var expires = issuedAt.AddMinutes(Math.Max(1, _options.AccessTokenMinutes));

        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId.Trim()) };

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            claims.Add(new Claim("name", displayName.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tenant", tenantId.Trim()));
        }

        if (scopes.Count > 0)
        {
            // 与中间件 ReadClaimSet 同口径：空格分隔（OAuth2 scope 写法）
            claims.Add(new Claim("scope", string.Join(' ', scopes)));
        }

        var token = new JwtSecurityToken(
            issuer: string.IsNullOrWhiteSpace(_options.Issuer) ? null : _options.Issuer,
            audience: string.IsNullOrWhiteSpace(_options.Audience) ? null : _options.Audience,
            claims: claims,
            notBefore: issuedAt,
            expires: expires,
            signingCredentials: _credentials);

        return new AuthToken(
            _handler.WriteToken(token),
            expires,
            userId.Trim(),
            string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim(),
            scopes);
    }
}

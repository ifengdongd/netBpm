using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace NetBpm.Rest.Security;

/// <summary>
/// JWT 的本地密钥材料（HS256 对称密钥 / RS256 非对称密钥对），从 <c>NetBpm:Security:Jwt</c> 读出。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：原先「签名密钥就是一个字符串」的模型无法表达非对称签名，
/// 而 RS256 是「对外提供服务」与「内部统一接入」的分界线——只有公钥可分发，
/// 外部系统才能自己验签，平台也不必把私钥复制给每一个调用方。
/// </para>
/// <para>
/// 配置项（都在 <c>NetBpm:Security:Jwt</c> 下）：
/// </para>
/// <list type="bullet">
///   <item><description><c>SigningKey</c>：HS256 对称密钥（≥32 字节）。内网模式继续可用。</description></item>
///   <item><description><c>RsaPrivateKey</c> / <c>RsaPrivateKeyPath</c>：PEM（PKCS#1 或 PKCS#8）。用于签发。</description></item>
///   <item><description><c>RsaPublicKey</c> / <c>RsaPublicKeyPath</c>：PEM 公钥。只验签不签发（对外部 IdP 签发的令牌）。</description></item>
///   <item><description><c>KeyId</c>：<c>kid</c> 声明；缺省用公钥 SPKI 的 SHA-256 指纹（换密钥自动换 kid）。</description></item>
///   <item><description><c>Algorithm</c>：<c>HS256</c> / <c>RS256</c>。缺省按「配了哪个用哪个」推断。</description></item>
///   <item><description><c>JwksUri</c>：外部 IdP 的公钥集地址（见 <see cref="RemoteJwksProvider"/>）。</description></item>
/// </list>
/// <para>
/// <b>算法白名单是安全边界</b>：<see cref="ValidAlgorithms"/> 只包含「确实配了对应密钥」的算法。
/// 少了这一步就会有经典的算法混淆攻击——把 RSA 公钥当 HMAC 密钥签一个 HS256 令牌，
/// 校验端若还接受 HS256，就等于用公开信息伪造了签名。
/// </para>
/// </remarks>
public sealed class JwtKeyMaterial
{
    /// <summary>配置节。</summary>
    public const string SectionName = "NetBpm:Security:Jwt";

    private JwtKeyMaterial(
        SymmetricSecurityKey? symmetric,
        RSA? rsa,
        RsaSecurityKey? rsaPrivate,
        RsaSecurityKey? rsaPublic,
        string? rsaKeyId,
        IReadOnlyList<string> validAlgorithms)
    {
        SymmetricKey = symmetric;
        Rsa = rsa;
        RsaPrivateKey = rsaPrivate;
        RsaPublicKey = rsaPublic;
        RsaKeyId = rsaKeyId;
        ValidAlgorithms = validAlgorithms;
    }

    /// <summary>HS256 对称密钥；未配置时为 <see langword="null"/>。</summary>
    public SymmetricSecurityKey? SymmetricKey { get; }

    /// <summary>验签用的 RSA 实例（导出 JWKS 公钥参数时用；拿不到私钥）。</summary>
    private RSA? Rsa { get; }

    /// <summary>RS256 私钥（仅签发用）；未配置时为 <see langword="null"/>。</summary>
    public RsaSecurityKey? RsaPrivateKey { get; }

    /// <summary>RS256 公钥（校验用）。</summary>
    public RsaSecurityKey? RsaPublicKey { get; }

    /// <summary>RS256 的 <c>kid</c>。</summary>
    public string? RsaKeyId { get; }

    /// <summary>已配置的全部签名算法（白名单）。</summary>
    public IReadOnlyList<string> ValidAlgorithms { get; }

    /// <summary>是否具备 JWT 校验能力（配了至少一把校验密钥；外部 JWKS 由中间件另行判断）。</summary>
    public bool HasValidationKeys => SymmetricKey is not null || RsaPublicKey is not null;

    /// <summary>
    /// 签发用的凭证：优先 RS256（配了私钥），否则 HS256，都没有则为 <see langword="null"/>。
    /// </summary>
    public SigningCredentials? SigningCredentials
    {
        get
        {
            if (RsaPrivateKey is not null)
            {
                return new SigningCredentials(RsaPrivateKey, SecurityAlgorithms.RsaSha256);
            }

            return SymmetricKey is null
                ? null
                : new SigningCredentials(SymmetricKey, SecurityAlgorithms.HmacSha256);
        }
    }

    /// <summary>签发时写入令牌头的 <c>kid</c>（HS256 没有，返回 <see langword="null"/>）。</summary>
    public string? SigningKeyId => RsaPrivateKey is null ? null : RsaKeyId;

    /// <summary>校验用的密钥集合（不含外部 JWKS）。</summary>
    public IReadOnlyList<SecurityKey> ValidationKeys
    {
        get
        {
            var keys = new List<SecurityKey>(2);

            if (RsaPublicKey is not null)
            {
                keys.Add(RsaPublicKey);
            }

            if (SymmetricKey is not null)
            {
                keys.Add(SymmetricKey);
            }

            return keys;
        }
    }

    /// <summary>RS256 的公钥 JWKS 文档（<c>/.well-known/jwks.json</c> 的响应体）。</summary>
    /// <remarks>
    /// HS256 不产出公钥（对称密钥一旦公开就等于作废），因此内网模式下这里返回空集——
    /// 调用方据此可以看出「本服务不提供非对称验签」。
    /// </remarks>
    public JsonWebKeySetDocument ToJwks()
    {
        if (Rsa is null || RsaPublicKey is null)
        {
            return new JsonWebKeySetDocument([]);
        }

        var parameters = Rsa.ExportParameters(includePrivateParameters: false);

        var key = new JsonWebKeyDocument(
            Kty: "RSA",
            Use: "sig",
            Alg: SecurityAlgorithms.RsaSha256,
            Kid: RsaKeyId ?? string.Empty,
            N: Base64UrlEncoder.Encode(TrimLeadingZeros(parameters.Modulus!)),
            E: Base64UrlEncoder.Encode(TrimLeadingZeros(parameters.Exponent!)));

        return new JsonWebKeySetDocument([key]);
    }

    /// <summary>从配置装载。</summary>
    /// <param name="configuration">配置。</param>
    /// <param name="logger">日志（密钥来源不明时给出可操作的告警）。</param>
    /// <exception cref="InvalidOperationException">配置自相矛盾（指了算法却没有对应密钥、PEM 无法解析）。</exception>
    public static JwtKeyMaterial Load(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var section = configuration.GetSection(SectionName);

        var signingKey = section["SigningKey"];
        var requestedAlgorithm = section["Algorithm"]?.Trim();

        var symmetric = string.IsNullOrWhiteSpace(signingKey) ? null : CreateSymmetric(signingKey!);

        var privatePem = ReadPem(configuration, section, "RsaPrivateKey", "RsaPrivateKeyPath", logger);
        var publicPem = ReadPem(configuration, section, "RsaPublicKey", "RsaPublicKeyPath", logger);

        RSA? rsa = null;
        RsaSecurityKey? rsaPrivate = null;
        RsaSecurityKey? rsaPublic = null;
        string? keyId = null;

        if (privatePem is not null || publicPem is not null)
        {
            // 签名与验签各用一把 RSA 实例：.NET 不保证同一个实例能被并发 Sign / Verify，
            // 而这两件事在同一个进程里必然并发发生（签发令牌与校验令牌）。
            var signingRsa = privatePem is null ? null : CreateRsa(privatePem, "RsaPrivateKey");
            var verifyRsa = signingRsa is null
                ? CreateRsa(publicPem!, "RsaPublicKey")
                : CreateRsaFromPublicParameters(signingRsa);

            rsa = verifyRsa;

            var parameters = verifyRsa.ExportParameters(includePrivateParameters: false);

            keyId = section["KeyId"]?.Trim();

            if (string.IsNullOrWhiteSpace(keyId))
            {
                keyId = ComputeKeyId(parameters);
            }

            if (signingRsa is not null)
            {
                rsaPrivate = new RsaSecurityKey(signingRsa) { KeyId = keyId };
            }

            rsaPublic = new RsaSecurityKey(verifyRsa) { KeyId = keyId };
        }

        var wantsHs = string.Equals(requestedAlgorithm, SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase);
        var wantsRs = string.Equals(requestedAlgorithm, SecurityAlgorithms.RsaSha256, StringComparison.OrdinalIgnoreCase);

        if (wantsHs && symmetric is null)
        {
            throw new InvalidOperationException(
                "NetBpm:Security:Jwt:Algorithm=HS256，但没有配置 SigningKey（至少 32 字节）。");
        }

        if (wantsRs && rsaPublic is null)
        {
            throw new InvalidOperationException(
                "NetBpm:Security:Jwt:Algorithm=RS256，但没有配置 RsaPrivateKey / RsaPublicKey（PEM）。");
        }

        // 算法白名单：显式指定就只认它；没指定就把「有密钥的」全部放进来（迁移期两种令牌并存）
        var algorithms = new List<string>(2);

        if (wantsHs || (!wantsRs && symmetric is not null))
        {
            algorithms.Add(SecurityAlgorithms.HmacSha256);
        }

        if (wantsRs || (!wantsHs && rsaPublic is not null))
        {
            algorithms.Add(SecurityAlgorithms.RsaSha256);
        }

        if (algorithms.Count == 0 && (symmetric is not null || rsaPublic is not null))
        {
            throw new InvalidOperationException(
                $"NetBpm:Security:Jwt:Algorithm 取值 '{requestedAlgorithm}' 不受支持；可用值：HS256 / RS256。");
        }

        if (rsaPrivate is not null && symmetric is not null && !wantsHs && !wantsRs)
        {
            logger.LogWarning(
                "同时配置了 HS256 对称密钥与 RS256 私钥：签发使用 RS256，HS256 仅用于校验存量令牌。"
                + "迁移完成后请移除 SigningKey。");
        }

        return new JwtKeyMaterial(symmetric, rsa, rsaPrivate, rsaPublic, keyId, algorithms);
    }

    private static SymmetricSecurityKey CreateSymmetric(string signingKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(signingKey);

        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "NetBpm:Security:Jwt:SigningKey 至少需要 32 字节（HS256 密钥强度）。");
        }

        return new SymmetricSecurityKey(keyBytes);
    }

    /// <summary>
    /// 读 PEM：优先内联值，其次文件。内联值里的 <c>\n</c> 转义会被还原
    /// （环境变量与部分配置源无法携带真实换行）。
    /// </summary>
    private static string? ReadPem(
        IConfiguration configuration,
        IConfigurationSection section,
        string inlineKey,
        string pathKey,
        ILogger logger)
    {
        var inline = section[inlineKey];

        if (!string.IsNullOrWhiteSpace(inline))
        {
            return inline!.Replace("\\n", "\n", StringComparison.Ordinal);
        }

        var path = section[pathKey];

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var resolved = path!.Trim();

        if (!File.Exists(resolved))
        {
            throw new InvalidOperationException(
                $"NetBpm:Security:Jwt:{pathKey} 指向的文件不存在：{resolved}");
        }

        logger.LogInformation("已从文件读取 JWT 密钥：{Path}（{Key}）", resolved, pathKey);

        return File.ReadAllText(resolved);
    }

    private static void ImportPem(RSA rsa, string pem, string keyName)
    {
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException(
                $"NetBpm:Security:Jwt:{keyName} 不是可用的 PEM（支持 PKCS#1 / PKCS#8，块头形如 "
                + "-----BEGIN PRIVATE KEY-----）。",
                exception);
        }
    }

    /// <summary>按 PEM 造一把 RSA 实例（私钥或公钥，ImportFromPem 自己识别块头）。</summary>
    private static RSA CreateRsa(string pem, string keyName)
    {
        var rsa = RSA.Create();

        try
        {
            ImportPem(rsa, pem, keyName);

            return rsa;
        }
        catch
        {
            rsa.Dispose();

            throw;
        }
    }

    /// <summary>从私钥导出只含公钥部分的 RSA 实例（验签用，拿不到私钥）。</summary>
    private static RSA CreateRsaFromPublicParameters(RSA privateRsa)
    {
        var rsa = RSA.Create();

        rsa.ImportParameters(privateRsa.ExportParameters(includePrivateParameters: false));

        return rsa;
    }

    private static string ComputeKeyId(RSAParameters parameters)
    {
        // SPKI 指纹：与密钥内容一一对应，换密钥自动换 kid，不需要人工维护版本号
        var hash = SHA256.HashData(parameters.Modulus!.Concat(parameters.Exponent!).ToArray());

        return Base64UrlEncoder.Encode(hash)[..16];
    }

    private static byte[] TrimLeadingZeros(byte[] value)
    {
        var index = 0;

        while (index < value.Length - 1 && value[index] == 0)
        {
            index++;
        }

        return index == 0 ? value : value[index..];
    }
}

/// <summary>JWKS 里的一把公钥（字段名即 JOSE 规范里的小写名）。</summary>
/// <param name="Kty">密钥类型（<c>RSA</c>）。</param>
/// <param name="Use">用途（<c>sig</c>）。</param>
/// <param name="Alg">算法（<c>RS256</c>）。</param>
/// <param name="Kid">密钥标识。</param>
/// <param name="N">模数（Base64Url）。</param>
/// <param name="E">指数（Base64Url）。</param>
public sealed record JsonWebKeyDocument(string Kty, string Use, string Alg, string Kid, string N, string E);

/// <summary>JWKS 文档。</summary>
/// <param name="Keys">公钥集合。</param>
public sealed record JsonWebKeySetDocument(IReadOnlyList<JsonWebKeyDocument> Keys);

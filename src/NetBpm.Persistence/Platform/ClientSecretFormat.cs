using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 接入客户端密钥的格式与摘要。
/// </summary>
/// <remarks>
/// <para>
/// 格式：<c>nbc.{clientId}.{token}</c>。密钥<b>自带客户端 id</b>：鉴权时先解析出 id、再按主键查一行，
/// 而不是「取出全部客户端逐个比对摘要」——后者既是 O(n) 次哈希，也会因逐行比较放大计时侧信道。
/// </para>
/// <para>
/// 摘要用 SHA-256 而不是 PBKDF2：token 是 256 位随机数，没有「人类可猜」的口令空间，
/// 慢哈希在这里只会让每个请求都付一次十万轮的开销（用户口令仍走 <see cref="Admin.PasswordHasher"/>）。
/// 这与主流做法一致（GitHub PAT / Stripe sk 都是「高熵随机串 + 快速摘要」）。
/// </para>
/// <para>
/// 明文只在签发响应里出现一次，之后任何读取路径都拿不到——包括运维。
/// </para>
/// </remarks>
public static class ClientSecretFormat
{
    /// <summary>密钥前缀（与 JWT、控制台口令一眼可区分，便于日志脱敏规则识别）。</summary>
    public const string Prefix = "nbc";

    /// <summary>客户端 id 前缀。</summary>
    public const string ClientIdPrefix = "cl_";

    /// <summary>客户端 id 的随机字节数（8 字节 = 64 位，配合主键唯一约束足够）。</summary>
    private const int ClientIdBytes = 8;

    /// <summary>token 的随机字节数（32 字节 = 256 位）。</summary>
    private const int TokenBytes = 32;

    /// <summary>生成新的客户端 id（<c>cl_</c> + 16 位小写十六进制）。</summary>
    public static string NewClientId() =>
        string.Concat(ClientIdPrefix, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(ClientIdBytes)));

    /// <summary>生成新的 token（32 字节随机数的 base64url，无填充、无 <c>.</c>）。</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>拼出完整密钥。</summary>
    /// <param name="clientId">客户端 id。</param>
    /// <param name="token">随机 token。</param>
    public static string Compose(string clientId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return string.Concat(Prefix, ".", clientId, ".", token);
    }

    /// <summary>
    /// 从密钥里解析出客户端 id 与 token。
    /// </summary>
    /// <remarks>
    /// 解析失败一律返回 <see langword="false"/> 且不回传原因：调用方对外的响应必须统一成
    /// <c>auth.invalid_token</c>，否则「格式对但 id 不存在」与「格式不对」的差异会变成客户端枚举手段。
    /// </remarks>
    /// <param name="presentedSecret">请求携带的密钥原文。</param>
    /// <param name="clientId">解析出的客户端 id。</param>
    /// <param name="token">解析出的 token。</param>
    public static bool TryParse(string? presentedSecret, out string clientId, out string token)
    {
        clientId = string.Empty;
        token = string.Empty;

        if (string.IsNullOrWhiteSpace(presentedSecret))
        {
            return false;
        }

        var parts = presentedSecret.Split('.');

        if (parts.Length != 3
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || parts[1].Length == 0
            || parts[2].Length == 0)
        {
            return false;
        }

        clientId = parts[1];
        token = parts[2];

        return true;
    }

    /// <summary>计算密钥摘要（SHA-256 → base64），落库只存这个值。</summary>
    /// <param name="secret">密钥原文。</param>
    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>
    /// 校验密钥是否匹配已存摘要（定长比较，不因前缀相同提前返回）。
    /// </summary>
    /// <param name="presentedSecret">请求携带的密钥原文。</param>
    /// <param name="storedHash">库里的摘要（为空或格式不认识时返回 <see langword="false"/>）。</param>
    public static bool Verify(string? presentedSecret, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(presentedSecret) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        byte[] expected;

        try
        {
            expected = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presentedSecret)),
            expected);
    }

    /// <summary>
    /// 生成可安全写进日志的密钥描述：只保留客户端 id 与 token 首尾各 2 位。
    /// </summary>
    /// <param name="secret">密钥原文。</param>
    public static string Describe(string? secret)
    {
        if (!TryParse(secret, out var clientId, out var token))
        {
            return "(malformed)";
        }

        return token.Length <= 4
            ? $"{Prefix}.{clientId}.***"
            : $"{Prefix}.{clientId}.{token[..2]}***{token[^2..]}";
    }
}

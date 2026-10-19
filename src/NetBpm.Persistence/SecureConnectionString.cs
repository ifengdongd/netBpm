using MySqlConnector;

namespace NetBpm.Persistence;

/// <summary>
/// 连接串安全归一：把「传输层协议版本」钉死在安全范围，避免客户端与服务器协商到 TLS 1.0 / 1.1。
/// <para>
/// .NET 的 <c>SslStream</c> 在协商出 TLS 1.0 时会打印
/// 「安全警告:协商的 TLS 1.0 是非安全协议，只有在为了实现向后兼容性才受支持」——
/// 该告警无法在客户端静默，唯一正确的处理是<b>不允许降级</b>：
/// 服务器只支持老协议时连接直接失败，而不是带着弱加密继续跑。
/// </para>
/// <para>
/// 只对显式开启 TLS 的连接生效：<c>SslMode=None/Disabled</c> 原样返回（本地容器 / 纯内网场景由调用方负责）。
/// </para>
/// </summary>
public static class SecureConnectionString
{
    /// <summary>MySQL 允许协商的 TLS 版本（服务器不支持时连接失败，而不是回退到 1.0/1.1）。</summary>
    public const string MySqlTlsVersions = "TLS 1.2, TLS 1.3";

    /// <summary>按方言补齐安全默认值；已是显式配置的项不覆盖。</summary>
    /// <exception cref="NotSupportedException">方言未支持。</exception>
    public static string Normalize(DatabaseType databaseType, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return databaseType switch
        {
            // SqlClient 4.0 起已移除 TLS 1.0/1.1 支持，且不提供协议版本开关：
            // 加密由 Encrypt 控制（7.x 默认 true），老服务器会直接连不上。
            DatabaseType.SqlServer => connectionString,
            DatabaseType.MySql => NormalizeMySql(connectionString),
            _ => throw new NotSupportedException($"不支持的数据库方言：{databaseType}"),
        };
    }

    private static string NormalizeMySql(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);

        if (builder.SslMode is MySqlSslMode.None or MySqlSslMode.Disabled)
        {
            return connectionString;
        }

        if (string.IsNullOrWhiteSpace(builder.TlsVersion))
        {
            builder.TlsVersion = MySqlTlsVersions;
        }
        else if (ContainsInsecureProtocol(builder.TlsVersion))
        {
            // 显式把 1.0/1.1 写进连接串属于危险配置：启动即失败，而不是悄悄降级
            throw new InvalidOperationException(
                $"连接串的 TlsVersion（{builder.TlsVersion}）包含 TLS 1.0/1.1；"
                + $"请改为 TLS 1.2 及以上（推荐 {MySqlTlsVersions}）。");
        }

        return builder.ConnectionString;
    }

    /// <summary>判断版本列表里是否包含 TLS 1.0 / 1.1（容忍 <c>TLSv1.0</c> / <c>TLS 1.1</c> / <c>TLS1</c> 等写法）。</summary>
    private static bool ContainsInsecureProtocol(string tlsVersions)
    {
        foreach (var version in tlsVersions.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = version
                .Trim()
                .ToLowerInvariant()
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("v", string.Empty, StringComparison.Ordinal)
                .Replace(".", string.Empty, StringComparison.Ordinal);

            // tls1 / tls10 → TLS 1.0；tls11 → TLS 1.1
            if (normalized is "tls1" or "tls10" or "tls11")
            {
                return true;
            }
        }

        return false;
    }
}

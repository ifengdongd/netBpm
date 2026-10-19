using MySqlConnector;
using NetBpm.Persistence;

namespace NetBpm.UnitTests;

/// <summary>
/// 连接串 TLS 策略：不允许协商到 TLS 1.0 / 1.1（.NET 会打印不可静默的非安全协议告警）。
/// </summary>
public sealed class SecureConnectionStringTests
{
    private const string MySqlBase =
        "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=;Allow User Variables=true;";

    [Fact]
    public void MySql_WithoutTlsVersion_ShouldPinTls12AndAbove()
    {
        var normalized = SecureConnectionString.Normalize(DatabaseType.MySql, MySqlBase);
        var builder = new MySqlConnectionStringBuilder(normalized);

        Assert.Equal(SecureConnectionString.MySqlTlsVersions, builder.TlsVersion);
        Assert.Equal(MySqlSslMode.Preferred, builder.SslMode);
    }

    [Theory]
    [InlineData("SslMode=None")]
    [InlineData("SslMode=Disabled")]
    public void MySql_WhenTlsExplicitlyDisabled_ShouldStayUnchanged(string sslMode)
    {
        var original = MySqlBase + sslMode + ";";

        var normalized = SecureConnectionString.Normalize(DatabaseType.MySql, original);

        Assert.Equal(original, normalized);
        Assert.Equal(string.Empty, new MySqlConnectionStringBuilder(normalized).TlsVersion);
    }

    [Theory]
    [InlineData("TLS 1.0")]
    [InlineData("TLSv1")]
    [InlineData("TLS1.1")]
    [InlineData("TLSv1.2,TLSv1.0")]
    public void MySql_WhenTlsVersionIncludesInsecureProtocol_ShouldThrow(string tlsVersion)
    {
        var connectionString = MySqlBase + "TlsVersion=" + tlsVersion + ";";

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecureConnectionString.Normalize(DatabaseType.MySql, connectionString));

        Assert.Contains("TLS 1.0/1.1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MySql_WhenTlsVersionIsAlreadySecure_ShouldKeepCallerChoice()
    {
        var normalized = SecureConnectionString.Normalize(
            DatabaseType.MySql,
            MySqlBase + "TlsVersion=TLS 1.3;");

        Assert.Equal("TLS 1.3", new MySqlConnectionStringBuilder(normalized).TlsVersion);
    }

    [Fact]
    public void SqlServer_ConnectionString_ShouldBeUnchanged()
    {
        const string connectionString =
            "Server=127.0.0.1;Database=netbpm;User ID=sa;Password=NetBpm!Test2024;Encrypt=True;TrustServerCertificate=True;";

        Assert.Equal(
            connectionString,
            SecureConnectionString.Normalize(DatabaseType.SqlServer, connectionString));
    }
}

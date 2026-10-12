using System.Data.Common;

namespace NetBpm.Persistence;

/// <summary>
/// 数据库连接工厂。SQL 全部通过 Dapper 手写执行（开发计划 §3：贴近 MyBatis 的心智模型，
/// 不用 EF Core）。
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>方言。</summary>
    DatabaseType DatabaseType { get; }

    /// <summary>创建并打开一个连接。调用方负责释放。</summary>
    DbConnection CreateConnection();
}

/// <summary>基于 ADO.NET 提供程序的连接工厂。</summary>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(DatabaseType databaseType, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        DatabaseType = databaseType;
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public DatabaseType DatabaseType { get; }

    /// <summary>连接字符串（不含口令时应使用集成认证；此处仅暴露给诊断用途）。</summary>
    public string ConnectionString => _connectionString;

    /// <inheritdoc />
    public DbConnection CreateConnection()
    {
        DbConnection connection = DatabaseType switch
        {
            DatabaseType.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(_connectionString),
            DatabaseType.MySql => new MySqlConnector.MySqlConnection(_connectionString),
            _ => throw new NotSupportedException($"不支持的数据库方言：{DatabaseType}"),
        };

        connection.Open();
        return connection;
    }
}

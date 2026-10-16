using Dapper;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

namespace NetBpm.Demo.Storage;

/// <summary>
/// Demo 启动期的建库 + 迁移：库缺就建（SqlServer 连 master 建库；MySQL 无库名直连建库），
/// 然后按 <c>NetBpm:DatabaseType</c> 幂等执行 <c>db/&lt;dialect&gt;</c> 全部脚本
/// （引擎 ACT_* 表 + 业务表，双方言同库同 schema，见 <see cref="DatabaseMigrator"/>）。
/// </summary>
/// <remarks>
/// <see cref="DatabaseMigrator"/> 以「ACT_GE_PROPERTY 是否存在 + schema.version 是否匹配」判断幂等：
/// 已初始化的库直接跳过，重复启动不重复建表。
/// </remarks>
public static class DemoSchemaInitializer
{
    /// <summary>确保引擎与业务数据共用的库存在且表结构就绪。返回迁移结果（日志用）。</summary>
    public static MigrationResult EnsureSchema(DatabaseType databaseType, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        switch (databaseType)
        {
            case DatabaseType.SqlServer:
                EnsureSqlServerDatabase(connectionString);
                break;

            case DatabaseType.MySql:
                EnsureMySqlDatabase(connectionString);
                break;

            default:
                throw new NotSupportedException($"不支持的数据库方言：{databaseType}");
        }

        var factory = new DbConnectionFactory(databaseType, connectionString);
        var migrator = new DatabaseMigrator(
            new DbSessionFactory(factory),
            DatabaseScripts.ResolveDirectory(databaseType));

        return migrator.Migrate();
    }

    /// <summary>SQL Server：连 master 建库（若缺）。</summary>
    private static void EnsureSqlServerDatabase(string connectionString)
    {
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        if (string.IsNullOrWhiteSpace(database) || string.Equals(database, "master", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "NetBpm:ConnectionString 必须带 Database（Initial Catalog），Demo 引擎与业务数据共用这个库。");
        }

        var bootstrapBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
        };

        using var bootstrap = new SqlConnection(bootstrapBuilder.ConnectionString);
        bootstrap.Open();
        bootstrap.Execute($"if db_id('{database}') is null create database [{database}]");
    }

    /// <summary>MySQL：摘掉库名直连服务器建库（若缺）。</summary>
    private static void EnsureMySqlDatabase(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);

        var database = builder.Database;
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException(
                "NetBpm:ConnectionString 必须带 Database，Demo 引擎与业务数据共用这个库。");
        }

        builder.Database = string.Empty;

        using var bootstrap = new MySqlConnection(builder.ConnectionString);
        bootstrap.Open();
        bootstrap.Execute(
            $"create database if not exists `{database}` character set utf8 collate utf8_bin");
    }
}

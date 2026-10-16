using Dapper;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary>一个可用的测试数据库（方言 + 连接串 + 展示名）。</summary>
internal sealed record TestDatabaseSpec(DatabaseType Type, string ConnectionString, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// 双库测试供给（任务清单 B1.4 的延伸：B1/B2 功能矩阵要在 <b>MySQL 与 SqlServer</b> 上各跑一遍）。
/// <para>
/// 供给策略（<b>惰性</b>探测，MemberData 发现阶段不付连接成本）：
/// <list type="number">
///   <item><description><b>MySQL</b>：环境变量 <c>NETBPM_MYSQL_CONNECTION</c> → 本机默认库（<c>localhost:3306/netbpm</c>）；</description></item>
///   <item><description><b>SqlServer</b>：环境变量 <c>NETBPM_SQLSERVER_CONNECTION</c> →
///     默认测试实例 <c>10.1.245.36</c>（<b>Windows 身份认证</b>，库与表自动创建迁移）。</description></item>
/// </list>
/// 不可用的方言让测试<b>动态跳过</b>（与既有 MySQL 测试同一策略），不误红也不假绿。
/// </para>
/// </summary>
internal static class TestDatabases
{
    internal const string DatabaseName = "netbpm";
    private const string SqlServerEnvironmentVariable = "NETBPM_SQLSERVER_CONNECTION";
    private const string MySqlEnvironmentVariable = "NETBPM_MYSQL_CONNECTION";

    /// <summary>默认 SqlServer 测试实例（Windows 身份认证；TrustServerCertificate 因服务器常无受信证书）。</summary>
    internal const string DefaultSqlServerConnectionString =
        "Server=10.1.245.36;Integrated Security=True;TrustServerCertificate=True;Database=" + DatabaseName + ";";

    private static readonly Lazy<TestDatabaseSpec?> MySqlSpec = new(() => ProbeMySql());
    private static readonly Lazy<TestDatabaseSpec?> SqlServerSpec = new(() => ProbeSqlServer());

    /// <summary>MySQL（既有测试的默认库，直连语义不变）。</summary>
    internal static TestDatabaseSpec? MySql => MySqlSpec.Value;

    /// <summary>SqlServer（env → 10.1.245.36 Windows 认证）。</summary>
    internal static TestDatabaseSpec? SqlServer => SqlServerSpec.Value;

    /// <summary>建库（若缺）+ 执行对应方言脚本（幂等）。</summary>
    internal static void EnsureSchema(TestDatabaseSpec spec)
    {
        switch (spec.Type)
        {
            case DatabaseType.MySql:
                EnsureMySqlSchema(spec);
                break;

            case DatabaseType.SqlServer:
                EnsureSqlServerSchema(spec);
                break;

            default:
                throw new NotSupportedException($"未支持的测试方言：{spec.Type}");
        }
    }

    /// <summary>按方言规格创建会话工厂（测试 harness 用）。</summary>
    internal static IDbSessionFactory CreateSessionFactory(TestDatabaseSpec spec) =>
        new DbSessionFactory(new DbConnectionFactory(spec.Type, spec.ConnectionString));

    // ─────────────────────────── MySQL ───────────────────────────

    private static TestDatabaseSpec? ProbeMySql()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(MySqlEnvironmentVariable) is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : "Server=localhost;Port=3306;Database=" + DatabaseName
                    + ";User ID=root;Password=;Allow User Variables=true;";

        var spec = new TestDatabaseSpec(DatabaseType.MySql, connectionString, "MySQL(localhost)");

        try
        {
            EnsureMySqlSchema(spec);

            using var session = CreateSessionFactory(spec).Create();

            return HasEngineTables(session) ? spec : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void EnsureMySqlSchema(TestDatabaseSpec spec)
    {
        // 建库（若缺）—— 用 MySqlConnectionStringBuilder 摘掉库名再连
        var builder = new MySqlConnector.MySqlConnectionStringBuilder(spec.ConnectionString)
        {
            Database = string.Empty,
        };

        using (var bootstrap = new MySqlConnector.MySqlConnection(builder.ConnectionString))
        {
            bootstrap.Open();
            bootstrap.Execute(
                $"create database if not exists `{DatabaseName}` character set utf8 collate utf8_bin");
        }

        new DatabaseMigrator(
            CreateSessionFactory(spec),
            DatabaseScripts.ResolveDirectory(DatabaseType.MySql)).Migrate();
    }

    // ─────────────────────────── SqlServer ───────────────────────────

    private static TestDatabaseSpec? ProbeSqlServer()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(SqlServerEnvironmentVariable) is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : DefaultSqlServerConnectionString;

        var spec = new TestDatabaseSpec(DatabaseType.SqlServer, connectionString, "SqlServer(10.1.245.36)");

        try
        {
            EnsureSqlServerSchema(spec);

            using var session = CreateSessionFactory(spec).Create();

            return HasEngineTables(session) ? spec : null;
        }
        catch (Exception)
        {
            // 连不上 / 无 Windows 认证权限 / 无建库权限都视为「不可用」，测试动态跳过
            return null;
        }
    }

    private static void EnsureSqlServerSchema(TestDatabaseSpec spec)
    {
        // 建库（若缺）—— 连 master 执行
        var bootstrapBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(spec.ConnectionString)
        {
            InitialCatalog = "master",
        };

        using (var bootstrap = new Microsoft.Data.SqlClient.SqlConnection(bootstrapBuilder.ConnectionString))
        {
            bootstrap.Open();
            bootstrap.Execute($"if db_id('{DatabaseName}') is null create database [{DatabaseName}]");
        }

        var targetBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(spec.ConnectionString)
        {
            InitialCatalog = DatabaseName,
        };

        new DatabaseMigrator(
            new DbSessionFactory(new DbConnectionFactory(DatabaseType.SqlServer, targetBuilder.ConnectionString)),
            DatabaseScripts.ResolveDirectory(DatabaseType.SqlServer)).Migrate();
    }

    private static bool HasEngineTables(IDbSession session) =>
        session.DatabaseType switch
        {
            DatabaseType.MySql => session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.tables "
                + "where table_schema = database() and table_name = 'ACT_RU_EXECUTION'") > 0,

            DatabaseType.SqlServer => session.Connection.ExecuteScalar<long>(
                "select count(*) from sys.tables where name = 'ACT_RU_EXECUTION'") > 0,

            _ => throw new NotSupportedException($"未支持的测试方言：{session.DatabaseType}"),
        };
}

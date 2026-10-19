using Dapper;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;
using Testcontainers.MsSql;

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
///   <item><description><b>SqlServer</b>：环境变量 <c>NETBPM_SQLSERVER_CONNECTION</c> 优先（指向已有实例）→
///     否则由 <b>Testcontainers</b> 拉起 <c>mcr.microsoft.com/mssql/server:2022-latest</c>。</description></item>
/// </list>
/// 不可用的方言让测试<b>动态跳过</b>（与既有 MySQL 测试同一策略），不误红也不假绿：
/// Docker 未运行 / 镜像拉不下来 / 环境变量指向的实例连不上，都只是跳过 SqlServer 那一行。
/// </para>
/// <para>
/// 为什么不再默认连 <c>10.1.245.36</c>：那台老实例只协商到 <b>TLS 1.0</b>，连接时会打印
/// 「安全警告:协商的 TLS 1.0 是非安全协议」；改成 2022 容器后既支持 TLS 1.2，也不依赖内网机器与
/// Windows 身份认证。MySQL 侧另有客户端兜底：<see cref="SecureConnectionString"/> 锁定 TLS 1.2+
/// （见 <c>DbConnectionFactory</c>），环境变量指向只支持老协议的实例时会直接连不上而不是降级。
/// </para>
/// </summary>
internal static class TestDatabases
{
    internal const string DatabaseName = "netbpm";
    private const string SqlServerEnvironmentVariable = "NETBPM_SQLSERVER_CONNECTION";
    private const string MySqlEnvironmentVariable = "NETBPM_MYSQL_CONNECTION";

    /// <summary>SqlServer 容器镜像（2022 默认支持 TLS 1.2；Linux 容器，需要 Linux 模式或 WSL2 后端）。</summary>
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>容器 SA 密码（Testcontainers 要求满足 SQL Server 复杂度策略）。</summary>
    private const string SqlServerSaPassword = "NetBpm!Test2024";

    private static readonly Lazy<TestDatabaseSpec?> MySqlSpec = new(() => ProbeMySql());
    private static readonly Lazy<TestDatabaseSpec?> SqlServerSpec = new(() => ProbeSqlServer());

    /// <summary>进程内复用的 SqlServer 容器；使用环境变量覆盖时为 <see langword="null"/>。</summary>
    private static MsSqlContainer? _sqlServerContainer;

    static TestDatabases()
    {
        // 容器由 Ryuk 兜底清理；进程正常退出时先主动释放，避免本机残留
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => StopSqlServerContainer();
    }

    /// <summary>MySQL（既有测试的默认库，直连语义不变）。</summary>
    internal static TestDatabaseSpec? MySql => MySqlSpec.Value;

    /// <summary>SqlServer（环境变量优先，否则 Testcontainers；不可用返回 <see langword="null"/>）。</summary>
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
        var connectionString = SecureConnectionString.Normalize(
            DatabaseType.MySql,
            Environment.GetEnvironmentVariable(MySqlEnvironmentVariable) is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : "Server=localhost;Port=3306;Database=" + DatabaseName
                    + ";User ID=root;Password=;Allow User Variables=true;");

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

    /// <summary>
    /// 环境变量优先（指向已有实例），否则拉起 Testcontainers 容器。
    /// <para>
    /// 容器在<b>进程内复用</b>：多个测试类并行探测时由 <see cref="Lazy{T}"/> 保证只启动一次，
    /// 其余线程阻塞等待（首次要拉镜像，可能几十秒到几分钟）。
    /// </para>
    /// </summary>
    private static TestDatabaseSpec? ProbeSqlServer()
    {
        if (Environment.GetEnvironmentVariable(SqlServerEnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return TryPrepareSqlServer(
                new TestDatabaseSpec(DatabaseType.SqlServer, fromEnvironment, "SqlServer(env)"));
        }

        MsSqlContainer? container = null;

        try
        {
            // 镜像走构造函数（4.16 起无参构造已过时）
            container = new MsSqlBuilder(SqlServerImage)
                .WithPassword(SqlServerSaPassword)
                .Build();

            // 探针是同步的（Lazy / Assert.SkipUnless 在同步上下文里调用），而 Testcontainers 只提供异步 API
            container.StartAsync().GetAwaiter().GetResult();

            // 容器连接串默认指向 master；改成引擎库（库本身由 EnsureSqlServerSchema 创建并迁移）
            var connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(container.GetConnectionString())
            {
                InitialCatalog = DatabaseName,
            }.ConnectionString;

            var spec = new TestDatabaseSpec(DatabaseType.SqlServer, connectionString, "SqlServer(testcontainers)");
            var prepared = TryPrepareSqlServer(spec);

            if (prepared is null)
            {
                container.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return null;
            }

            _sqlServerContainer = container;

            return prepared;
        }
        catch (Exception)
        {
            // Docker 未运行 / 镜像拉不下来 / 容器起不来 → 方言不可用，测试动态跳过（与 MySQL 同一策略）
            try
            {
                container?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 释放失败不影响判定：Ryuk 会在会话结束时清理
            }

            return null;
        }
    }

    /// <summary>建库 + 迁移 + 确认引擎表存在；任何一步失败都视为该方言不可用。</summary>
    private static TestDatabaseSpec? TryPrepareSqlServer(TestDatabaseSpec spec)
    {
        try
        {
            EnsureSqlServerSchema(spec);

            using var session = CreateSessionFactory(spec).Create();

            return HasEngineTables(session) ? spec : null;
        }
        catch (Exception)
        {
            // 连不上 / 无建库权限 / 迁移失败都视为「不可用」
            return null;
        }
    }

    /// <summary>进程退出时主动释放容器（Ryuk 仍会兜底）。</summary>
    private static void StopSqlServerContainer()
    {
        var container = Interlocked.Exchange(ref _sqlServerContainer, null);
        if (container is null)
        {
            return;
        }

        try
        {
            container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 退出路径上的失败没有补救价值
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

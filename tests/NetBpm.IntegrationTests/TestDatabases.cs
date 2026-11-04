using System.Collections.Concurrent;
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
///   <item><description><b>MySQL</b>：环境变量 <c>NETBPM_MYSQL_CONNECTION</c> → 本机测试库（<c>localhost:3306/netbpm_ittest</c>）；</description></item>
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
    internal const string DatabaseName = "netbpm_ittest";
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

    /// <summary>探测失败的真实原因（连不上 / 无建库权限 / 迁移失败 / 引擎表没建齐）。</summary>
    private static readonly ConcurrentDictionary<string, string> FailureReasons = new();

    /// <summary>MySQL（既有测试的默认库，直连语义不变）。</summary>
    internal static TestDatabaseSpec? MySql => MySqlSpec.Value;

    /// <summary>
    /// 方言不可用时的跳过理由。
    /// <para>
    /// 带上真实失败原因，而不是一句「方言不可用」：CI 上 SqlServer 那一行被跳过时，
    /// 要看得出是 Docker 没跑、镜像拉不下来，还是连上了但建表失败 ——
    /// 这三种情况的处置完全不同，光看「跳过」只会让人以为跑过了。
    /// </para>
    /// </summary>
    internal static string UnavailableReason(string provider) =>
        $"方言 {provider} 不可用"
        + (FailureReasons.TryGetValue(provider, out var reason)
            ? $"：{reason}"
            : "（未探测到可用实例；MySQL=localhost:3306，SqlServer=Testcontainers mssql/server:2022 或环境变量指向的实例）");

    /// <summary>SqlServer（环境变量优先，否则 Testcontainers；不可用返回 <see langword="null"/>）。</summary>
    internal static TestDatabaseSpec? SqlServer => SqlServerSpec.Value;

    /// <summary>
    /// 建库（若缺）+ 执行对应方言脚本（幂等）——<b>同一连接串在本进程内只真正执行一次</b>，
    /// 后续调用直接返回（见下方「为什么不能并发迁移」）。
    /// </summary>
    internal static void EnsureSchema(TestDatabaseSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // 单飞：Lazy(ExecutionAndPublication) 保证只有一个线程执行迁移，其余线程阻塞等结果。
        // 迁移失败时异常被 Lazy 记住并对所有等待者原样重抛 —— 与「每个调用者各自失败」等效，
        // 但不会出现「A 已经迁移成功、B 却拿到一个半路失败」的错位。
        var migrated = SchemaMigrations.GetOrAdd(
            spec.ConnectionString,
            _ => new Lazy<bool>(
                () =>
                {
                    MigrateSchema(spec);
                    return true;
                },
                LazyThreadSafetyMode.ExecutionAndPublication));

        _ = migrated.Value;
    }

    /// <summary>每个连接串「已经迁移过 / 正在迁移」的哨兵。</summary>
    private static readonly ConcurrentDictionary<string, Lazy<bool>> SchemaMigrations =
        new(StringComparer.Ordinal);

    /// <summary>真正执行迁移（按方言分派）。</summary>
    /// <remarks>
    /// 🔑 <b>为什么不能并发迁移</b>：迁移脚本是「建库 + 全量 DDL」的<b>单个事务</b>
    /// （<see cref="DatabaseMigrator"/> 一次 <c>Begin/Commit</c> 跑完整份脚本）。
    /// 每个测试类各自调用 <see cref="EnsureSchema"/>，而 xUnit 默认<b>类间并行</b>；
    /// 于是一个类在跑 DDL 事务、另一个类的用例正对着同一批表做 DML，
    /// SQL Server 直接判定循环等待并回滚牺牲品 ——
    /// CI 2026-11-03 就是这么红的：<c>error 1205 deadlock victim</c> 落在
    /// <c>ClientRegistryDatabaseTests</c> / <c>AdminUserStoreDatabaseTests</c> 的写事务上，
    /// 而本地永远看不出来（本机没有 SqlServer，那一半恒被跳过）。
    /// 串行化之后「迁移期间的 DML」不再可能发生：三个类的用例都在这道闸门后面才开始跑。
    /// </remarks>
    private static void MigrateSchema(TestDatabaseSpec spec)
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

        // 环境变量可能只指向实例（没有 Database=）：补上库名，否则探测会话落在默认库上，
        // 会把「库和表都建好了」误判成不可用（information_schema 查的是 database()）。
        var mySqlBuilder = new MySqlConnector.MySqlConnectionStringBuilder(connectionString);

        if (string.IsNullOrWhiteSpace(mySqlBuilder.Database))
        {
            mySqlBuilder.Database = DatabaseName;
        }

        var spec = new TestDatabaseSpec(DatabaseType.MySql, mySqlBuilder.ConnectionString, "MySQL(localhost)");

        try
        {
            // 走单飞入口：探测时的这次迁移就是本进程唯一一次，
            // 之后各测试类的 EnsureSchema 直接命中缓存（否则每个类都要重跑一遍全量 DDL）
            EnsureSchema(spec);

            using var session = CreateSessionFactory(spec).Create();

            if (HasEngineTables(session))
            {
                return spec;
            }

            FailureReasons["mysql"] = "库连上了但引擎表没建齐（迁移可能只跑了一半）";

            return null;
        }
        catch (Exception exception)
        {
            FailureReasons["mysql"] = exception.Message;

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

        // 建的是「实际要连的库」而不是默认常量：NETBPM_MYSQL_CONNECTION 指向别的库时（CI 就是这么用的）
        // 不该再额外造一个空的默认库出来。
        var targetDatabase = new MySqlConnector.MySqlConnectionStringBuilder(spec.ConnectionString).Database;

        using (var bootstrap = new MySqlConnector.MySqlConnection(builder.ConnectionString))
        {
            bootstrap.Open();
            bootstrap.Execute(
                $"create database if not exists `{targetDatabase}` character set utf8 collate utf8_bin");
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
        catch (Exception exception)
        {
            // Docker 未运行 / 镜像拉不下来 / 容器起不来 → 方言不可用，测试动态跳过（与 MySQL 同一策略）
            FailureReasons["sqlserver"] = exception.Message;

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
        // 与 MySQL 同一条：连接串常常只指向实例。补上库名（缺省 netbpm）后再建表与探测，
        // 否则 sys.tables 查的是默认库（master），结果同样是「建好了却判成不可用」。
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(spec.ConnectionString)
        {
            InitialCatalog = ResolveSqlServerDatabaseName(spec.ConnectionString),
        };

        var prepared = new TestDatabaseSpec(DatabaseType.SqlServer, builder.ConnectionString, spec.DisplayName);

        try
        {
            EnsureSchema(prepared);

            using var session = CreateSessionFactory(prepared).Create();

            if (HasEngineTables(session))
            {
                return prepared;
            }

            FailureReasons["sqlserver"] = "库连上了但引擎表没建齐（迁移可能只跑了一半）";

            return null;
        }
        catch (Exception exception)
        {
            // 连不上 / 无建库权限 / 迁移失败都视为「不可用」
            FailureReasons["sqlserver"] = exception.Message;

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
        // 库名从连接串读取（支持 netbpm_demo 等自定义库），否则用默认 netbpm
        var databaseName = ResolveSqlServerDatabaseName(spec.ConnectionString);

        // 建库（若缺）—— 连 master 执行
        var bootstrapBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(spec.ConnectionString)
        {
            InitialCatalog = "master",
        };

        using (var bootstrap = new Microsoft.Data.SqlClient.SqlConnection(bootstrapBuilder.ConnectionString))
        {
            bootstrap.Open();
            bootstrap.Execute($"if db_id('{databaseName}') is null create database [{databaseName}]");
        }

        var targetBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(spec.ConnectionString)
        {
            InitialCatalog = databaseName,
        };

        new DatabaseMigrator(
            new DbSessionFactory(new DbConnectionFactory(DatabaseType.SqlServer, targetBuilder.ConnectionString)),
            DatabaseScripts.ResolveDirectory(DatabaseType.SqlServer)).Migrate();
    }

    /// <summary>SqlServer 测试库名：优先取连接串的 <c>InitialCatalog</c>（如 <c>netbpm_demo</c>），否则用默认库。</summary>
    private static string ResolveSqlServerDatabaseName(string connectionString)
    {
        var initialCatalog = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;

        return string.IsNullOrWhiteSpace(initialCatalog) ? DatabaseName : initialCatalog;
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

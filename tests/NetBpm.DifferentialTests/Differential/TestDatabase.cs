using Dapper;
using MySqlConnector;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

namespace NetBpm.DifferentialTests.Differential;

/// <summary>
/// 对拍用的数据库（**独立库**：<c>netbpm_diff</c>）。
///
/// <para>
/// 🔑 <b>为什么要独立库</b>：对拍每跑一个语料都会 <c>purge</c> 全部引擎表。若与集成测试共用
/// <c>netbpm</c>，两个项目在同一个 <c>dotnet test</c> 里并行执行时会互相清表（表现为随机失败），
/// 而且跑批也会污染开发库。独立库把这两件事一起解决。
/// </para>
/// <para>
/// 库与表都在首次需要时自动创建/迁移（复用 <see cref="DatabaseMigrator"/> 与 <c>db/mysql</c> 脚本），
/// 因此 CI 不需要为它额外准备数据库；MySQL 不可用时测试会<b>动态跳过</b>。
/// </para>
/// </summary>
internal static class TestDatabase
{
    /// <summary>对拍专用库名（与环境变量 <c>NETBPM_DIFF_CONNECTION</c> 二选一）。</summary>
    internal const string DatabaseName = "netbpm_diff";

    internal const string DefaultConnectionString =
        "Server=127.0.0.1;Port=3306;Database=" + DatabaseName + ";User ID=root;Password=;Allow User Variables=true;";

    private static readonly Lazy<bool> Available = new(Probe);
    private static readonly Lock SyncRoot = new();
    private static bool _schemaReady;

    /// <summary>连接串：优先环境变量，便于在其它机器/容器上跑。</summary>
    internal static string ConnectionString =>
        Environment.GetEnvironmentVariable("NETBPM_DIFF_CONNECTION") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : DefaultConnectionString;

    internal static bool IsAvailable => Available.Value;

    internal static string SkipReason =>
        "本机 MySQL 不可用（对拍需要真库：维度②要 dump ACT_* 行）。"
        + "请确认 127.0.0.1:3306 可达，或用 NETBPM_DIFF_CONNECTION 指定连接串。"
        + "库与表会自动创建，无需手工准备。";

    /// <summary>仓库根目录（用于定位 tests/corpus、db 脚本与 artifacts）。</summary>
    internal static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetBpm.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? Directory.GetCurrentDirectory();
        }
    }

    internal static string CorpusRoot => Path.Combine(RepositoryRoot, "tests", "corpus");

    internal static string ArtifactRoot => Path.Combine(RepositoryRoot, "artifacts");

    internal static string KnownIssuesFile => Path.Combine(CorpusRoot, "known-issues.json");

    internal static IDbSessionFactory CreateSessionFactory() =>
        new DbSessionFactory(new DbConnectionFactory(DatabaseType.MySql, ConnectionString));

    private static bool Probe()
    {
        try
        {
            EnsureSchema();

            using var session = CreateSessionFactory().Create();

            return session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.tables "
                + "where table_schema = database() and table_name = 'ACT_RU_EXECUTION'") > 0;
        }
        catch (Exception)
        {
            // 连不上 / 无权限建库都视为"不可用"，测试动态跳过
            return false;
        }
    }

    /// <summary>建库（若不存在）+ 执行 <c>db/mysql</c> 脚本（幂等）。</summary>
    internal static void EnsureSchema()
    {
        if (_schemaReady)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_schemaReady)
            {
                return;
            }

            CreateDatabaseIfMissing();

            new DatabaseMigrator(
                CreateSessionFactory(),
                Path.Combine(RepositoryRoot, "db", "mysql")).Migrate();

            _schemaReady = true;
        }
    }

    private static void CreateDatabaseIfMissing()
    {
        var builder = new MySqlConnectionStringBuilder(ConnectionString) { Database = string.Empty };

        using var connection = new MySqlConnection(builder.ConnectionString);
        connection.Open();

        connection.Execute(
            $"create database if not exists `{DatabaseName}` character set utf8 collate utf8_bin");
    }
}

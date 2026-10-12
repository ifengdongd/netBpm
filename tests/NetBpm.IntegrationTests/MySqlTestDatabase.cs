using Dapper;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 真实 MySQL 的连接与清理工具。
/// <para>
/// 连接串优先取环境变量 <c>NETBPM_MYSQL_CONNECTION</c>（CI / 其它开发机），
/// 否则退化到本机开发库。<b>数据库不可用时测试会动态跳过</b>而不是失败 ——
/// 这样「没有 MySQL 的机器」也能跑通整个测试套件。
/// </para>
/// </summary>
internal static class MySqlTestDatabase
{
    internal const string DefaultConnectionString =
        "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=;Allow User Variables=true;";

    internal const string SkipReason =
        "本机 MySQL（数据库 netbpm）不可用。请先建库并执行 db/mysql 脚本：" +
        "dotnet run --project tools/NetBpm.DbMigrate -- migrate --create-database " +
        "--provider mysql --connection \"Server=127.0.0.1;Database=netbpm;User ID=root;Password=;\"";

    private static readonly Lazy<bool> Available = new(Probe);

    internal static string ConnectionString =>
        Environment.GetEnvironmentVariable("NETBPM_MYSQL_CONNECTION") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : DefaultConnectionString;

    internal static bool IsAvailable => Available.Value;

    internal static IDbSessionFactory CreateSessionFactory() =>
        new DbSessionFactory(new DbConnectionFactory(DatabaseType.MySql, ConnectionString));

    /// <summary>清空引擎表，保证每次运行从确定状态开始。</summary>
    internal static void Reset(IDbSession session) => EngineTableCleaner.Purge(session);

    private static bool Probe()
    {
        try
        {
            using var session = CreateSessionFactory().Create();

            var tables = session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.tables " +
                "where table_schema = database() and table_name = 'ACT_RU_EXECUTION'");

            return tables > 0;
        }
        catch (Exception)
        {
            // 连不上或库不存在都视为「不可用」，测试动态跳过
            return false;
        }
    }
}

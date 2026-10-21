using Dapper;
using NetBpm.Persistence;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 增量迁移：老库（已初始化但没有 <c>ACT_GE_SCHEMA_LOG</c> / 缺新脚本）必须能补跑未执行的脚本。
/// <para>
/// 这是对「已初始化 ⇒ 一条脚本都不执行」缺陷的回归测试：先跑一次完整迁移，
/// 再删掉执行日志 + 手工撤掉 0004 的索引与 0011 的列，第二次迁移必须把它们补回来。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class MigrationIncrementalTests
{
    private static string ScriptDirectory =>
        Path.GetDirectoryName(RepositoryLayout.DatabaseScript("mysql", "0001_create_engine_schema.sql"))!;

    [Fact]
    public void Migrate_ShouldApplyMissingScripts_OnExistingDatabase()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var factory = MySqlTestDatabase.CreateSessionFactory();
        var migrator = new DatabaseMigrator(factory, ScriptDirectory);

        // 基线：确保库结构与日志完整
        migrator.Migrate();

        // 模拟老库：日志里没有 0004 / 0011，且结构确实缺这两处
        using (var session = factory.Create())
        {
            session.Connection.Execute(
                "delete from ACT_GE_SCHEMA_LOG where SCRIPT_ like '0004%' or SCRIPT_ like '0011%'");

            session.Connection.Execute(
                "drop index ACT_IDX_TASK_ASSIGNEE_CREATE on ACT_RU_TASK");

            var hasRetries = session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.columns " +
                "where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and column_name = 'RETRIES_'");

            if (hasRetries > 0)
            {
                session.Connection.Execute("alter table ACT_RU_TIMER_JOB drop column RETRIES_");
            }
        }

        var result = migrator.Migrate();

        Assert.Contains(result.AppliedScripts, name => name.StartsWith("0004", StringComparison.Ordinal));
        Assert.Contains(result.AppliedScripts, name => name.StartsWith("0011", StringComparison.Ordinal));

        using (var session = factory.Create())
        {
            // 复合索引在 information_schema.statistics 里每个列一行，按索引名去重
            var indexCount = session.Connection.ExecuteScalar<long>(
                "select count(distinct index_name) from information_schema.statistics " +
                "where table_schema = database() and table_name = 'ACT_RU_TASK' " +
                "and index_name = 'ACT_IDX_TASK_ASSIGNEE_CREATE'");

            Assert.Equal(1, indexCount);

            var columnCount = session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.columns " +
                "where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and column_name = 'RETRIES_'");

            Assert.Equal(1, columnCount);

            var logCount = session.Connection.ExecuteScalar<long>(
                "select count(*) from ACT_GE_SCHEMA_LOG where SCRIPT_ like '0011%'");

            Assert.Equal(1, logCount);
        }
    }
}

using Dapper;
using NetBpm.Persistence;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 单文件初始化脚本的幂等性：同一份脚本可以像「生产环境重复发布」那样反复执行。
/// <para>
/// 断言三件事：① 重复执行不报错且不丢结构；② <c>next.dbid</c> / <c>schema.version</c> 这类初值不被覆盖；
/// ③ 手工被人误删的索引 / 列，再执行一次脚本就能补回来。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class InitScriptIdempotencyTests
{
    private static string ScriptDirectory =>
        Path.GetDirectoryName(RepositoryLayout.DatabaseScript("mysql"))!;

    [Fact]
    public void Migrate_Twice_ShouldPreservePropertySeeds()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var factory = MySqlTestDatabase.CreateSessionFactory();
        var migrator = new DatabaseMigrator(factory, ScriptDirectory);

        migrator.Migrate();

        string originalNextDbId;
        using (var session = factory.Create())
        {
            originalNextDbId = session.Connection.ExecuteScalar<string>(
                "select VALUE_ from ACT_GE_PROPERTY where NAME_ = 'next.dbid'")!;

            // 模拟「运行过一段时间」的库：游标已推进
            session.Connection.Execute(
                "update ACT_GE_PROPERTY set VALUE_ = '424242' where NAME_ = 'next.dbid'");
        }

        try
        {
            var result = migrator.Migrate();

            Assert.True(result.AlreadyInitialized);
            Assert.Contains(RepositoryLayout.DatabaseScriptFileName, result.AppliedScripts);

            using var session2 = factory.Create();

            // next.dbid 不能被脚本重置（否则 id 会重用）
            var nextDbId = session2.Connection.ExecuteScalar<string>(
                "select VALUE_ from ACT_GE_PROPERTY where NAME_ = 'next.dbid'");
            Assert.Equal("424242", nextDbId);

            var version = session2.Connection.ExecuteScalar<string>(
                "select VALUE_ from ACT_GE_PROPERTY where NAME_ = 'schema.version'");
            Assert.Equal(DatabaseSchemaChecker.ExpectedSchemaVersion, version);
        }
        finally
        {
            // 复原游标，避免影响其它用例
            using var session = factory.Create();
            session.Connection.Execute(
                "update ACT_GE_PROPERTY set VALUE_ = @value where NAME_ = 'next.dbid'",
                new { value = originalNextDbId });
        }
    }

    [Fact]
    public void Migrate_ShouldRepairDroppedIndexAndColumn()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var factory = MySqlTestDatabase.CreateSessionFactory();
        var migrator = new DatabaseMigrator(factory, ScriptDirectory);

        migrator.Migrate();

        // 模拟「结构被人手工改坏 / 用旧脚本建过的库」
        using (var session = factory.Create())
        {
            session.Connection.Execute("drop index ACT_IDX_TASK_ASSIGNEE_CREATE on ACT_RU_TASK");
            session.Connection.Execute("alter table ACT_RU_TIMER_JOB drop column RETRIES_");
        }

        migrator.Migrate();

        using (var session = factory.Create())
        {
            var indexCount = session.Connection.ExecuteScalar<long>(
                "select count(distinct index_name) from information_schema.statistics " +
                "where table_schema = database() and table_name = 'ACT_RU_TASK' " +
                "and index_name = 'ACT_IDX_TASK_ASSIGNEE_CREATE'");

            Assert.Equal(1, indexCount);

            var columnCount = session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.columns " +
                "where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and column_name = 'RETRIES_'");

            Assert.Equal(1, columnCount);
        }
    }
}

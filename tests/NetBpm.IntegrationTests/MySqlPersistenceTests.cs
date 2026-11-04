using Dapper;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P5 的库级验收：在真实 MySQL 上跑通「建表 → 部署 → 启动实例 → 执行树收口」，
/// 并直接比对 <c>ACT_RU_EXECUTION</c> 的行内容。
/// <para>
/// 这是开发计划 §9.2「维度②」的日常版：用自建语料把并行网关的执行树形状、
/// 三个标志位与 <c>REV_</c> 乐观锁行为钉死（风险 R1 / R3）。
/// </para>
/// <para>全部用例集中在一个类内 → xunit 保证类内串行，避免共享库上的相互干扰。</para>
/// </summary>
[Collection("MySql")]
public sealed class MySqlPersistenceTests
{
    private static readonly string[] ExpectedSnapshotActivities = ["taskA", "taskB", "taskAfterJoin"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Migrator_ShouldBeIdempotentOnInitializedDatabase()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var migrator = new DatabaseMigrator(
            MySqlTestDatabase.CreateSessionFactory(),
            DatabaseScripts.ResolveDirectory(DatabaseType.MySql));

        var result = migrator.Migrate();

        // 幂等脚本每次都完整执行（靠脚本内的守卫收敛），因此 AppliedScripts 恒为初始化脚本本身
        Assert.True(result.AlreadyInitialized);
        Assert.Contains(RepositoryLayout.DatabaseScriptFileName, result.AppliedScripts);
    }

    /// <summary>
    /// 流程类型是「配置型」种子：控制台的「流程类型」页可以改 URL / 排序 / 启用，
    /// 而老库没有迁移记录表、每次 <c>Migrate()</c> 都整篇重跑脚本。
    /// 这条用例守住「迁移不得覆盖用户配置」——种子若写成 <c>on duplicate key update</c>，
    /// 每次 AutoMigrate 都会把配置刷回脚本默认值（现象就是「改了又还原」）。
    /// </summary>
    [Fact]
    public void Migrator_ShouldNotOverwriteEditableProcessTypeConfiguration()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var sessionFactory = MySqlTestDatabase.CreateSessionFactory();
        var migrator = new DatabaseMigrator(
            sessionFactory,
            DatabaseScripts.ResolveDirectory(DatabaseType.MySql));

        // 种子行必须先存在：脚本只在「缺行」时插入
        migrator.Migrate();

        const string processKey = "leave_process";
        const string customViewUrl = "/Leave/PageView?businessId={businessId}&processInstanceId={processInstanceId}";
        const string customApproveUrl = "/Leave/Approve?taskId={taskId}&processInstanceId={processInstanceId}";

        string originalViewUrl;
        string originalApproveUrl;

        using (var session = sessionFactory.Create())
        {
            originalViewUrl = session.Connection.ExecuteScalar<string>(
                "select view_url from workflow_type where name = @Name", new { Name = processKey })!;
            originalApproveUrl = session.Connection.ExecuteScalar<string>(
                "select approve_url from workflow_type where name = @Name", new { Name = processKey })!;

            session.Connection.Execute(
                "update workflow_type set view_url = @ViewUrl, approve_url = @ApproveUrl where name = @Name",
                new { ViewUrl = customViewUrl, ApproveUrl = customApproveUrl, Name = processKey });
        }

        try
        {
            migrator.Migrate();

            using var session = sessionFactory.Create();

            Assert.Equal(
                customViewUrl,
                session.Connection.ExecuteScalar<string>(
                    "select view_url from workflow_type where name = @Name", new { Name = processKey }));
            Assert.Equal(
                customApproveUrl,
                session.Connection.ExecuteScalar<string>(
                    "select approve_url from workflow_type where name = @Name", new { Name = processKey }));
        }
        finally
        {
            // 共享库：无论如何都把行还原成迁移前的值
            using var session = sessionFactory.Create();

            session.Connection.Execute(
                "update workflow_type set view_url = @ViewUrl, approve_url = @ApproveUrl where name = @Name",
                new { ViewUrl = originalViewUrl, ApproveUrl = originalApproveUrl, Name = processKey });
        }
    }

    /// <summary>
    /// 种子是「表为空才初始化」，所以用户删掉的流程类型不会被下次 <c>Migrate()</c> 补回来。
    /// 这条用例守住「删得掉」这个半边：只要种子还是「缺行就补」或 upsert，这里就会红。
    /// </summary>
    [Fact]
    public void Migrator_ShouldNotReinsertDeletedSeedRow()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var sessionFactory = MySqlTestDatabase.CreateSessionFactory();
        var migrator = new DatabaseMigrator(
            sessionFactory,
            DatabaseScripts.ResolveDirectory(DatabaseType.MySql));

        migrator.Migrate();

        // 挑最不常被引用的种子行：leave_process 有别的用例在用
        const string processKey = "dept_approval_process";

        SeedWorkflowTypeRow seed;
        using (var session = sessionFactory.Create())
        {
            seed = session.Connection.QuerySingle<SeedWorkflowTypeRow>(
                "select tenant_id as TenantId, id, name, `describe`, view_url as ViewUrl," +
                " approve_url as ApproveUrl, cast(sort_order as signed) as SortOrder, cast(enabled as signed) as Enabled" +
                " from workflow_type where name = @Name",
                new { Name = processKey });

            session.Connection.Execute(
                "delete from workflow_type where name = @Name",
                new { Name = processKey });
        }

        try
        {
            migrator.Migrate();

            using var session = sessionFactory.Create();

            Assert.True(
                session.Connection.ExecuteScalar<int>(
                    "select count(*) from workflow_type where name = @Name", new { Name = processKey }) == 0,
                "种子只在表为空时插入：被删掉的流程类型不应被迁移补回来。");
        }
        finally
        {
            // 共享库：把行还原成删除前的样子
            using var session = sessionFactory.Create();

            session.Connection.Execute(
                "insert into workflow_type (tenant_id, id, name, `describe`, view_url, approve_url, sort_order, enabled, create_time)" +
                " values (@TenantId, @Id, @Name, @Describe, @ViewUrl, @ApproveUrl, @SortOrder, @Enabled, utc_timestamp())",
                seed);
        }
    }

    /// <summary>
    /// 空表时种子必须真的插进去，而且**多行一次插入**。这条用例能抓到生成侧的两类事故：
    /// ① 判空写成逐行（第一行插入后表就不空了，后面几行被自己的守卫挡掉）；
    /// ② MySQL 派生表列名重复（<c>workflow_type</c> 首行 tenant_id / sort_order / enabled 都是 1）→ 1060。
    /// 共享库上必须自己收尾：用例开始时记下全部行，结束时清空并原样写回。
    /// </summary>
    [Fact]
    public void Migrator_ShouldSeedAllRowsIntoEmptyTable()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var sessionFactory = MySqlTestDatabase.CreateSessionFactory();
        var migrator = new DatabaseMigrator(
            sessionFactory,
            DatabaseScripts.ResolveDirectory(DatabaseType.MySql));

        migrator.Migrate();

        List<SeedWorkflowTypeRow> original;
        using (var session = sessionFactory.Create())
        {
            original = session.Connection.Query<SeedWorkflowTypeRow>(
                "select tenant_id as TenantId, id, name, `describe`, view_url as ViewUrl," +
                " approve_url as ApproveUrl, cast(sort_order as signed) as SortOrder, cast(enabled as signed) as Enabled from workflow_type").ToList();

            session.Connection.Execute("delete from workflow_type");
        }

        try
        {
            migrator.Migrate();

            using var session = sessionFactory.Create();

            var names = session.Connection.Query<string>(
                "select name from workflow_type order by sort_order").ToList();

            Assert.Equal(
                new[] { "order_process", "leave_process", "dept_approval_process" },
                names);
        }
        finally
        {
            using var session = sessionFactory.Create();

            session.Connection.Execute("delete from workflow_type");
            session.Connection.Execute(
                "insert into workflow_type (tenant_id, id, name, `describe`, view_url, approve_url, sort_order, enabled, create_time)" +
                " values (@TenantId, @Id, @Name, @Describe, @ViewUrl, @ApproveUrl, @SortOrder, @Enabled, utc_timestamp())",
                original);
        }
    }

    /// <summary>workflow_type 行（用例里用来原样搬回来，列名与库内一致）。</summary>
    private sealed record SeedWorkflowTypeRow(
        long TenantId,
        string Id,
        string Name,
        string Describe,
        string ViewUrl,
        string ApproveUrl,
        long SortOrder,
        long Enabled);

    [Fact]
    public void SchemaVersion_ShouldMatchEngineExpectation()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var sessionFactory = MySqlTestDatabase.CreateSessionFactory();
        var checker = new DatabaseSchemaChecker(
            new DapperPropertyStore(new CommandScopedDbSessionProvider(sessionFactory)));

        checker.Check();

        using var session = sessionFactory.Create();
        var nextDbId = session.Connection.ExecuteScalar<string>(
            "select VALUE_ from ACT_GE_PROPERTY where NAME_ = 'next.dbid'");

        Assert.Equal("next.dbid", DbIdGenerator.NextDbIdPropertyName);
        Assert.False(string.IsNullOrWhiteSpace(nextDbId));
    }

    [Fact]
    public async Task Deployment_ShouldRoundTripBpmnXmlThroughDatabase()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var definition = (await harness.DeployAndStartAsync(
            DatabaseEngineHarness.ParallelGatewayProcessXml, Ct)).Definition;

        // 流程定义 id 形如 {key}:{version}:{idGeneratorId}（§7.4）
        Assert.StartsWith("parallel-db:1:", definition.Id, StringComparison.Ordinal);
        Assert.Equal(1, definition.Version);

        Assert.Equal(1, harness.CountRows("ACT_RE_DEPLOYMENT"));
        Assert.Equal(1, harness.CountRows("ACT_RE_PROCDEF"));
        Assert.Equal(1, harness.CountRows("ACT_GE_BYTEARRAY"));

        var found = await harness.Definitions.FindByIdAsync(definition.Id, Ct);
        Assert.NotNull(found);
        Assert.Equal("parallel-db", found.Key);
        Assert.Equal(definition.DeploymentId, found.DeploymentId);

        // 设计器回显契约（§6.3）：XML 必须能从库里取回并重新解析
        var xml = await harness.Definitions.GetResourceXmlAsync(definition.Id, Ct);
        Assert.NotNull(xml);
        Assert.Contains("parallelGateway", xml, StringComparison.Ordinal);

        var model = await harness.Definitions.GetBpmnModelAsync(definition.Id, Ct);
        Assert.NotNull(model);
        Assert.Equal(2, model.GetMainProcess()!.FindFlowElementsOfType<Bpmn.Model.ParallelGateway>().Count());
    }

    [Fact]
    public async Task ParallelGateway_ShouldPersistExactExecutionTreeShapeAndCleanUp()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            DatabaseEngineHarness.ParallelGatewayProcessXml, Ct);

        // 三次服务任务 ⇒ 三个时刻的执行树快照（议程是 FIFO，顺序确定）
        Assert.Equal(
            ExpectedSnapshotActivities,
            harness.Recorder.Snapshots.Select(snapshot => snapshot.ActivityId));

        var atTaskA = harness.Recorder.Snapshots[0].Rows;
        var atAfterJoin = harness.Recorder.Snapshots[2].Rows;

        // ── Fork 之后：根执行 + 两条并发分支 = 3 行 ──────────────────
        Assert.Equal(3, atTaskA.Count);

        var root = Assert.Single(atTaskA, row => row.ParentId is null);
        Assert.Equal(processInstanceId, root.Id);
        Assert.True(root.IsScope);
        Assert.True(root.IsActive);
        Assert.Null(root.ActivityId);       // 根执行不参与流程推进（§5.1 的关键结构）
        Assert.False(root.IsConcurrent);
        Assert.Equal(2, root.Revision);     // Fork 的 lockFirstParentScope 强制更新过一次

        var branches = atTaskA.Where(row => row.ParentId is not null).ToList();
        Assert.Equal(2, branches.Count);
        Assert.All(branches, branch =>
        {
            Assert.Equal(root.Id, branch.ParentId);
            Assert.Equal(processInstanceId, branch.ProcessInstanceId);
            Assert.False(branch.IsScope);
            Assert.True(branch.IsActive);
        });

        // 根执行与全部分支的 PROC_INST_ID_ / ROOT_PROC_INST_ID_ 必须指向流程实例
        Assert.All(atTaskA, row =>
        {
            Assert.Equal(processInstanceId, row.ProcessInstanceId);
            Assert.Equal(processInstanceId, row.RootProcessInstanceId);
        });

        // 已核实的 Activiti 事实：引擎从不把 IS_CONCURRENT_ 置 1（见 DapperExecutionManager 的类注释）
        Assert.All(atTaskA, row => Assert.False(row.IsConcurrent));

        Assert.Contains(atTaskA, row => row.ActivityId == "taskA");

        // ── Join 之后：另一条分支的执行已被删除，只剩 2 行 ───────────
        Assert.Equal(2, atAfterJoin.Count);
        Assert.Single(atAfterJoin, row => row.ParentId is null);
        Assert.Contains(atAfterJoin, row => row.ActivityId == "taskAfterJoin");

        // ── 收口：流程实例结束后执行树与关联数据全部清空 ─────────────
        Assert.Empty(harness.ReadExecutions());
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));
        Assert.Equal(0, harness.CountRows("ACT_RU_VARIABLE"));
        Assert.Equal(0, harness.CountRows("ACT_RU_IDENTITYLINK"));
    }

    [Fact]
    public async Task IdGenerator_ShouldAdvanceNextDbIdByBlockSize()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var before = ReadNextDbId(harness);

        await harness.DeployAndStartAsync(DatabaseEngineHarness.ParallelGatewayProcessXml, Ct);

        var after = ReadNextDbId(harness);

        // §7.3：切块分配 —— 一次命令消耗的 id 远少于区块大小，因此 next.dbid 只会前进一块
        Assert.Equal(
            before + DbIdGenerator.DefaultIdBlockSize,
            after);
    }

    [Fact]
    public void IdBlockAllocator_ShouldAdvanceCursorInAutonomousTransaction()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var before = ReadNextDbId(harness);

        // 🔑 生产装配路径：分配器自建连接 + 自建短事务（不参与任何命令事务）
        var allocator = new DapperIdBlockAllocator(
            new DbConnectionFactory(DatabaseType.MySql, MySqlTestDatabase.ConnectionString));

        var first = allocator.Allocate(DbIdGenerator.NextDbIdPropertyName, 100);
        var second = allocator.Allocate(DbIdGenerator.NextDbIdPropertyName, 100);

        Assert.Equal(before + 1, first.FirstId);
        Assert.Equal(before + 100, first.LastId);
        Assert.Equal(first.LastId + 1, second.FirstId);
        Assert.Equal(before + 200, second.LastId);

        // 每次分配都已提交：换一条连接读到的游标必须同步前进
        Assert.Equal(before + 200, ReadNextDbId(harness));
    }

    private static long ReadNextDbId(DatabaseEngineHarness harness)
    {
        using var session = harness.SessionFactory.Create();

        var value = session.Connection.ExecuteScalar<string>(
            "select VALUE_ from ACT_GE_PROPERTY where NAME_ = 'next.dbid'");

        return long.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
    }
}

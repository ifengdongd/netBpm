using System.Data.Common;
using Dapper;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 真库并发冲突路径（MySQL + SqlServer 各跑一遍）：
/// 并行分支同时提交、会签实例同时提交、同一任务重复提交。
/// <para>
/// 这些用例必须真的制造行级冲突（同一 <c>ACT_RU_EXECUTION</c> / <c>ACT_RU_VARIABLE</c> 行），
/// 才能验证「乐观锁 + 重试 + 命令级议程」在真实数据库锁语义下成立：
/// 单个命令成功的断言不算数，要求<b>并发全部成功且收口恰好一次</b>。
/// </para>
/// <para>
/// 与其它库级用例同属 <c>MySql</c> collection → 类间串行，避免共享库互扰。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class ConcurrentSubmitIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IEnumerable<TheoryDataRow<string>> Providers()
    {
        yield return new TheoryDataRow<string>("mysql");
        yield return new TheoryDataRow<string>("sqlserver");
    }

    // ─────────────────────────── 并行分支同时提交 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ParallelBranches_CompletedConcurrently_ShouldJoinExactlyOnce(string provider)
    {
        var harness = Setup(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(ParallelUserTaskProcess(), Ct);

        var tasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct);
        Assert.Equal(2, tasks.Count);

        // 两条分支同时到达 Join：都要抢根执行行（LockFirstParentScope）+ 版本号
        await Task.WhenAll(tasks.Select(task => harness.CompleteTaskAsync(task.Id, ct: Ct)));

        TestContext.Current.TestOutputHelper?.WriteLine($"[{provider}] 并行 Join 冲突重试次数：{harness.RetryCount}");

        AssertNoRuntimeRows(harness);

        using var session = harness.SessionFactory.Create();

        // 两个任务都收口
        var historicTasks = RowValues.Query(session, "select END_TIME_ from ACT_HI_TASKINST");
        Assert.Equal(2, historicTasks.Count);
        Assert.All(historicTasks, row => Assert.NotNull(RowValues.Raw(row, "END_TIME_")));

        // 流程实例只结束一次，结束节点落在 end
        var historicInstance = Assert.Single(
            RowValues.Query(session, "select END_TIME_, END_ACT_ID_ from ACT_HI_PROCINST"));

        Assert.NotNull(RowValues.Raw(historicInstance, "END_TIME_"));
        Assert.Equal("end", RowValues.Text(historicInstance, "END_ACT_ID_"));
    }

    // ─────────────────────────── 会签同时提交 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Countersign_InstancesCompletedConcurrently_ShouldAdvanceExactlyOnce(string provider)
    {
        // 3 个并发命令会争抢同一行 nrOfCompletedInstances，把重试上限抬高到 5，
        // 避免调度抖动导致「最后一次尝试仍撞锁」的假红（生产默认 3 次）。
        var harness = Setup(provider, TransientDatabaseErrors.CreateRetryPolicy(
            maxAttempts: 5,
            baseDelay: TimeSpan.FromMilliseconds(20)));

        var (_, processInstanceId) = await harness.DeployAndStartAsync(CountersignProcess(), Ct);

        var tasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct);
        Assert.Equal(3, tasks.Count);

        await Task.WhenAll(tasks.Select(task => harness.CompleteTaskAsync(task.Id, ct: Ct)));

        TestContext.Current.TestOutputHelper?.WriteLine($"[{provider}] 会签计数冲突重试次数：{harness.RetryCount}");

        AssertNoRuntimeRows(harness);

        using var session = harness.SessionFactory.Create();

        var historicTasks = RowValues.Query(session, "select END_TIME_ from ACT_HI_TASKINST");
        Assert.Equal(3, historicTasks.Count);
        Assert.All(historicTasks, row => Assert.NotNull(RowValues.Raw(row, "END_TIME_")));

        // 会签节点收口一次、流程结束一次（父执行只被放行一次）
        var historicInstance = Assert.Single(
            RowValues.Query(session, "select END_TIME_, END_ACT_ID_ from ACT_HI_PROCINST"));

        Assert.NotNull(RowValues.Raw(historicInstance, "END_TIME_"));
        Assert.Equal("end", RowValues.Text(historicInstance, "END_ACT_ID_"));
    }

    // ─────────────────────────── 同一任务重复提交 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SameTask_CompletedTwiceConcurrently_ShouldSucceedExactlyOnce(string provider)
    {
        var harness = Setup(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(SingleUserTaskProcess(), Ct);
        var task = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        // 双击 / 网关重放：恰好一个命令提交成功，另一个拿到明确的业务错误（任务已不存在）
        var outcomes = await Task.WhenAll(
            TryCompleteAsync(harness, task.Id),
            TryCompleteAsync(harness, task.Id));

        TestContext.Current.TestOutputHelper?.WriteLine($"[{provider}] 双击冲突重试次数：{harness.RetryCount}");

        Assert.Equal(1, outcomes.Count(succeeded => succeeded));

        AssertNoRuntimeRows(harness);

        using var session = harness.SessionFactory.Create();

        var historicTask = Assert.Single(RowValues.Query(session, "select END_TIME_ from ACT_HI_TASKINST"));
        Assert.NotNull(RowValues.Raw(historicTask, "END_TIME_"));

        // 结束节点只走一次 —— 重复提交没有造成二次推进
        var endActivities = RowValues.Query(
            session,
            "select ID_ from ACT_HI_ACTINST where ACT_ID_ = 'end'");

        Assert.Single(endActivities);
    }

    // ─────────────────────────── 确定性行冲突（重试机制本身） ───────────────────────────

    /// <summary>
    /// 用一张两列测试表精确制造「两个事务读到同一版本、先后写同一行」的冲突，
    /// 证明：真实数据库行冲突 → <see cref="OptimisticLockingException"/> → 换新会话重试 → 收敛。
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RealRowConflict_ShouldRetryWithFreshSessionAndConverge(string provider)
    {
        var harness = Setup(provider);

        try
        {
            await PrepareLockRowAsync(harness);

            var meeting = new AsyncMeeting(participants: 2);
            var command = new ConflictingUpdateCommand(LockRowId, meeting);

            await Task.WhenAll(
                harness.Executor.ExecuteAsync(command, Ct).AsTask(),
                harness.Executor.ExecuteAsync(command, Ct).AsTask());

            // 两个事务都先读到 REV_=1 再各自更新 ⇒ 必然有一个牺牲（不是概率事件）
            Assert.True(harness.RetryCount >= 1, "本用例必须至少发生一次真实行冲突");
            Assert.Equal(3L, await ReadRevisionAsync(harness));
        }
        finally
        {
            await DropLockTableAsync(harness);
        }
    }

    private const string LockRowId = "concurrent-retry-probe";

    private static async Task PrepareLockRowAsync(DatabaseEngineHarness harness)
    {
        using var session = harness.SessionFactory.Create();

        try
        {
            await session.Connection.ExecuteAsync(
                "create table NETBPM_TEST_LOCKS (ID_ varchar(64) not null primary key, REV_ bigint not null)");
        }
        catch (DbException)
        {
            // 测试库跨轮次复用，表已存在
        }

        await session.Connection.ExecuteAsync("delete from NETBPM_TEST_LOCKS");
        await session.Connection.ExecuteAsync(
            "insert into NETBPM_TEST_LOCKS (ID_, REV_) values (@id, 1)",
            new { id = LockRowId });
    }

    private static async Task<long> ReadRevisionAsync(DatabaseEngineHarness harness)
    {
        using var session = harness.SessionFactory.Create();

        return await session.Connection.ExecuteScalarAsync<long>(
            "select REV_ from NETBPM_TEST_LOCKS where ID_ = @id",
            new { id = LockRowId });
    }

    private static async Task DropLockTableAsync(DatabaseEngineHarness harness)
    {
        using var session = harness.SessionFactory.Create();

        try
        {
            await session.Connection.ExecuteAsync("drop table NETBPM_TEST_LOCKS");
        }
        catch (DbException)
        {
            // 表不存在（用例在准备阶段就失败）
        }
    }

    private sealed class ConflictingUpdateCommand(string rowId, AsyncMeeting meeting) : ICommand<Unit>
    {
        public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            var session = (IDbSession)context.Transaction!;

            var revision = await session.Connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "select REV_ from NETBPM_TEST_LOCKS where ID_ = @id",
                    new { id = rowId },
                    session.Transaction,
                    cancellationToken: ct));

            // 两个命令都读到同一版本后才允许写
            await meeting.SignalAndWaitAsync(ct);

            var affected = await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    "update NETBPM_TEST_LOCKS set REV_ = REV_ + 1 where ID_ = @id and REV_ = @revision",
                    new { id = rowId, revision },
                    session.Transaction,
                    cancellationToken: ct));

            if (affected != 1)
            {
                throw new OptimisticLockingException(
                    $"测试行 {rowId} 版本冲突：期望 REV_={revision} 的更新影响了 {affected} 行。");
            }

            return Unit.Value;
        }
    }

    /// <summary>让两个并发命令在各自事务里「碰头」后再写（不靠 sleep 制造并发）。</summary>
    private sealed class AsyncMeeting(int participants)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task SignalAndWaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) == participants)
            {
                _released.TrySetResult();
            }

            await _released.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static async Task<bool> TryCompleteAsync(DatabaseEngineHarness harness, string taskId)
    {
        try
        {
            await harness.CompleteTaskAsync(taskId, ct: Ct);
            return true;
        }
        catch (EngineException)
        {
            // 任务已被另一个命令完成 / 乐观锁重试耗尽：都是可预期的冲突结果
            return false;
        }
    }

    private static void AssertNoRuntimeRows(DatabaseEngineHarness harness)
    {
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_VARIABLE"));
        Assert.Equal(0, harness.CountRows("ACT_RU_IDENTITYLINK"));
    }

    private static DatabaseEngineHarness Setup(
        string provider,
        CommandRetryPolicy? retryPolicy = null)
    {
        var spec = provider switch
        {
            "mysql" => TestDatabases.MySql,
            "sqlserver" => TestDatabases.SqlServer,
            _ => throw new InvalidOperationException($"未知方言 '{provider}'。"),
        };

        Assert.SkipUnless(
            spec is not null,
            $"方言 {provider} 不可用（NETBPM_MYSQL_CONNECTION / NETBPM_SQLSERVER_CONNECTION 或默认目标："
            + "MySQL=127.0.0.1，SqlServer=Testcontainers mssql/server:2022；需要 Docker 正在运行）。"
            + "库与表会自动创建。");

        var harness = new DatabaseEngineHarness(spec!, retryPolicy);
        harness.Reset();

        return harness;
    }

    private static string SingleUserTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="concurrent-single" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="审批" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>并行网关分叉出两个用户任务，Join 再收口（真实争抢根执行行）。</summary>
    private static string ParallelUserTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="concurrent-parallel" isExecutable="true">
            <startEvent id="start" />
            <parallelGateway id="fork" />
            <userTask id="taskA" name="会办 A" activiti:assignee="kermit" />
            <userTask id="taskB" name="会办 B" activiti:assignee="kermit" />
            <parallelGateway id="join" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
            <sequenceFlow id="f2" sourceRef="fork" targetRef="taskA" />
            <sequenceFlow id="f3" sourceRef="fork" targetRef="taskB" />
            <sequenceFlow id="f4" sourceRef="taskA" targetRef="join" />
            <sequenceFlow id="f5" sourceRef="taskB" targetRef="join" />
            <sequenceFlow id="f6" sourceRef="join" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>并行会签：3 个实例共享 nrOfCompletedInstances 变量行。</summary>
    private static string CountersignProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="concurrent-mi" isExecutable="true">
            <startEvent id="start" />
            <userTask id="sign" name="会签" activiti:assignee="kermit">
              <multiInstanceLoopCharacteristics isSequential="false">
                <loopCardinality>3</loopCardinality>
              </multiInstanceLoopCharacteristics>
            </userTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="sign" />
            <sequenceFlow id="f2" sourceRef="sign" targetRef="end" />
          </process>
        </definitions>
        """;
}

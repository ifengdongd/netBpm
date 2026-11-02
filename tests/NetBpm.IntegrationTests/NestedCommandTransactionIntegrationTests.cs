using System.Data.Common;
using Dapper;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P0-2 回归：嵌套命令必须<b>加入外层命令的同一个事务</b>（同一 DbSession、同一连接）。
/// <para>
/// 判定标准不是「上下文对象相同」，而是数据库事实：
/// <list type="bullet">
///   <item><description>内层能读到外层未提交的写入（同一事务）；</description></item>
///   <item><description>外层回滚时，内层已执行的写入一起消失（没有被内层提前提交）；</description></item>
///   <item><description>内层与外层拿到同一个 <see cref="IDbSession"/> 实例。</description></item>
/// </list>
/// 旧实现每次 <c>ExecuteAsync</c> 都新建上下文与连接，内层会独立提交 —— 第二条断言必然失败。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class NestedCommandTransactionIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IEnumerable<TheoryDataRow<string>> Providers()
    {
        yield return new TheoryDataRow<string>("mysql");
        yield return new TheoryDataRow<string>("sqlserver");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NestedCommand_ShouldShareOuterSessionAndCommitTogether(string provider)
    {
        var harness = await SetupAsync(provider);

        try
        {
            var sessions = new List<IDbSession>();
            var command = new OuterWriteThenNestedCommand(harness.Executor, "outer", "inner", failAtEnd: false, sessions);

            await harness.Executor.ExecuteAsync(command, Ct);

            Assert.Equal(2, sessions.Count);
            Assert.Same(sessions[0], sessions[1]);

            // 外层提交后，两行都在
            Assert.Equal(1, await CountAsync(harness, "outer"));
            Assert.Equal(1, await CountAsync(harness, "inner"));
        }
        finally
        {
            await DropProbeTableAsync(harness);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NestedCommandWrites_ShouldRollBackWithOuterFailure(string provider)
    {
        var harness = await SetupAsync(provider);

        try
        {
            var sessions = new List<IDbSession>();
            var command = new OuterWriteThenNestedCommand(harness.Executor, "outer", "inner", failAtEnd: true, sessions);

            await Assert.ThrowsAnyAsync<EngineException>(async () => await harness.Executor.ExecuteAsync(command, Ct));

            Assert.Equal(2, sessions.Count);
            Assert.Same(sessions[0], sessions[1]);

            // 外层失败 ⇒ 内层写入不得残留（证明内层没有独立提交）
            Assert.Equal(0, await CountAsync(harness, "outer"));
            Assert.Equal(0, await CountAsync(harness, "inner"));
        }
        finally
        {
            await DropProbeTableAsync(harness);
        }
    }

    // ─────────────────────────── 命令与探针表 ───────────────────────────

    private sealed class OuterWriteThenNestedCommand(
        ICommandExecutor executor,
        string outerId,
        string innerId,
        bool failAtEnd,
        List<IDbSession> sessions) : ICommand<Unit>
    {
        public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            var session = (IDbSession)context.Transaction!;
            sessions.Add(session);

            await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    "insert into NETBPM_TX_PROBE (ID_) values (@id)",
                    new { id = outerId },
                    session.Transaction,
                    cancellationToken: ct));

            // 内层命令：必须看到外层的未提交写入，并与外层同会话
            await executor.ExecuteAsync(new InnerWriteCommand(innerId, sessions), ct);

            if (failAtEnd)
            {
                throw new EngineException("外层业务失败，要求整体回滚");
            }

            return Unit.Value;
        }
    }

    private sealed class InnerWriteCommand(string id, List<IDbSession> sessions) : ICommand<Unit>
    {
        public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            var session = (IDbSession)context.Transaction!;
            sessions.Add(session);

            // 同一事务内应能看到外层的行
            var visible = await session.Connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "select count(*) from NETBPM_TX_PROBE",
                    transaction: session.Transaction,
                    cancellationToken: ct));

            if (visible != 1)
            {
                throw new EngineException($"内层命令未看到外层的未提交写入（可见行数 {visible}）。");
            }

            await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    "insert into NETBPM_TX_PROBE (ID_) values (@id)",
                    new { id },
                    session.Transaction,
                    cancellationToken: ct));

            return Unit.Value;
        }
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static async Task<DatabaseEngineHarness> SetupAsync(string provider)
    {
        var spec = provider switch
        {
            "mysql" => TestDatabases.MySql,
            "sqlserver" => TestDatabases.SqlServer,
            _ => throw new InvalidOperationException($"未知方言 '{provider}'。"),
        };

        Assert.SkipUnless(spec is not null, TestDatabases.UnavailableReason(provider));

        var harness = new DatabaseEngineHarness(spec!);
        harness.Reset();
        await CreateProbeTableAsync(harness);

        return harness;
    }

    private static async Task CreateProbeTableAsync(DatabaseEngineHarness harness)
    {
        using var session = harness.SessionFactory.Create();

        try
        {
            await session.Connection.ExecuteAsync(
                "create table NETBPM_TX_PROBE (ID_ varchar(64) not null primary key)");
        }
        catch (DbException)
        {
            await session.Connection.ExecuteAsync("delete from NETBPM_TX_PROBE");
        }
    }

    private static async Task DropProbeTableAsync(DatabaseEngineHarness harness)
    {
        using var session = harness.SessionFactory.Create();

        try
        {
            await session.Connection.ExecuteAsync("drop table NETBPM_TX_PROBE");
        }
        catch (DbException)
        {
            // 表不存在（用例在准备阶段就失败）
        }
    }

    private static async Task<long> CountAsync(DatabaseEngineHarness harness, string id)
    {
        using var session = harness.SessionFactory.Create();

        return await session.Connection.ExecuteScalarAsync<long>(
            "select count(*) from NETBPM_TX_PROBE where ID_ = @id",
            new { id });
    }
}

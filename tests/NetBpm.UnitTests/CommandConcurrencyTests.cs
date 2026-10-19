using System.Collections.Concurrent;
using NetBpm.Engine;
using NetBpm.Engine.Agenda;
using NetBpm.Engine.Command;
using NetBpm.Engine.Command.Interceptors;

namespace NetBpm.UnitTests;

/// <summary>
/// 第 1 批并发回归：命令级议程 / 嵌套上下文复用 / 乐观锁重试。
/// <para>
/// 这些用例在修复前会失败或偶发失败（单例议程被并发命令共享、
/// 嵌套命令各开一个事务、乐观锁冲突直接冒泡），修复后必须稳定通过。
/// </para>
/// </summary>
public sealed class CommandConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ─────────────────────────── 命令级议程 ───────────────────────────

    [Fact]
    public async Task ConcurrentCommands_ShouldEachOwnAnAgenda()
    {
        var engine = TestEngine.Create();
        var meeting = new AsyncMeeting(participants: 2);
        var captured = new ConcurrentBag<(CommandContext Context, IEngineAgenda Facade)>();

        // 两个命令必须同时在各自上下文里，才能证明队列不共享
        await Task.WhenAll(
            engine.CommandExecutor.ExecuteAsync(new AgendaProbeCommand(meeting, captured), Ct).AsTask(),
            engine.CommandExecutor.ExecuteAsync(new AgendaProbeCommand(meeting, captured), Ct).AsTask());

        Assert.Equal(2, captured.Count);

        // ① 上下文各自持有独立议程
        Assert.Equal(
            2,
            captured.Select(item => item.Context.Agenda)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());

        // ② 生产路径（操作 / 行为 / 命令都走 Services.Agenda）必须解析到各自的议程
        Assert.Equal(
            2,
            captured.Select(item => item.Facade)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());

        // ③ 门面与上下文必须指向同一个实例（否则排空的是空队列，操作永远不会执行）
        Assert.All(captured, item => Assert.Same(item.Context.Agenda, item.Facade));

        // 各命令的议程都被自己的 CommandContextInterceptor 排空
        Assert.All(captured, item => Assert.True(item.Context.Agenda.IsEmpty));

        // 命令外的兜底议程不得被命令内的规划污染
        Assert.True(engine.Services.Agenda.IsEmpty);
    }

    [Fact]
    public async Task ConcurrentCompleteTask_OnIndependentInstances_ShouldAllFinish()
    {
        var engine = TestEngine.Create();
        const int instanceCount = 8;

        for (var i = 0; i < instanceCount; i++)
        {
            await engine.StartProcessAsync(TestEngine.UserTaskThenServiceProcess("after"));
        }

        var taskIds = engine.TaskManager.Tasks.Select(task => task.Id).ToArray();
        Assert.Equal(instanceCount, taskIds.Length);

        // 并发提交：修复前共享议程会让部分操作在别人命令里执行 / 干脆不执行
        await Task.WhenAll(taskIds.Select(taskId => engine.CompleteTaskAsync(taskId, ct: Ct).AsTask()));

        Assert.Equal(0, engine.TaskManager.TaskCount);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
        Assert.Equal(
            instanceCount,
            engine.ExecutedDelegates.Count(name => string.Equals(name, "after", StringComparison.Ordinal)));
    }

    // ─────────────────────────── 嵌套上下文 ───────────────────────────

    [Fact]
    public async Task NestedCommand_ShouldReuseOuterContextAndAgenda()
    {
        var engine = TestEngine.Create();
        var captured = new List<CommandContext>();

        await engine.CommandExecutor.ExecuteAsync(
            new NestedProbeCommand(engine.CommandExecutor, captured),
            Ct);

        Assert.Equal(2, captured.Count);
        Assert.Same(captured[0], captured[1]);
        Assert.Same(captured[0].Agenda, captured[1].Agenda);
        Assert.True(captured[0].Agenda.IsEmpty);
    }

    // ─────────────────────────── 乐观锁重试 ───────────────────────────

    [Fact]
    public async Task RetryInterceptor_ShouldRetryOptimisticLockingConflict()
    {
        var engine = TestEngine.Create(
            retryPolicy: new CommandRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.Zero });

        var attempts = 0;

        await engine.CommandExecutor.ExecuteAsync(
            new FlakyCommand(() =>
            {
                attempts++;

                if (attempts < 3)
                {
                    throw new OptimisticLockingException($"conflict #{attempts}");
                }
            }),
            Ct);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryInterceptor_ShouldRethrowAfterAttemptsExhausted()
    {
        var engine = TestEngine.Create(
            retryPolicy: new CommandRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.Zero });

        var attempts = 0;

        var exception = await Assert.ThrowsAsync<OptimisticLockingException>(async () =>
        {
            await engine.CommandExecutor.ExecuteAsync(
                new FlakyCommand(() =>
                {
                    attempts++;
                    throw new OptimisticLockingException("always conflicts");
                }),
                Ct);
        });

        Assert.Equal(3, attempts);
        Assert.Contains("always conflicts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryInterceptor_ShouldNotRetryOtherEngineExceptions()
    {
        var engine = TestEngine.Create(
            retryPolicy: new CommandRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.Zero });

        var attempts = 0;

        var exception = await Assert.ThrowsAnyAsync<EngineException>(async () =>
        {
            await engine.CommandExecutor.ExecuteAsync(
                new FlakyCommand(() =>
                {
                    attempts++;
                    throw new EngineException("business failure");
                }),
                Ct);
        });

        Assert.Equal(1, attempts);
        Assert.Equal("business failure", exception.Message);
    }

    [Fact]
    public async Task RetryInterceptor_ShouldNotRetryNestedCommands()
    {
        var engine = TestEngine.Create(
            retryPolicy: new CommandRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.Zero });

        var innerAttempts = 0;

        await engine.CommandExecutor.ExecuteAsync(
            new CatchingOuterCommand(engine.CommandExecutor, () =>
            {
                innerAttempts++;
                throw new OptimisticLockingException("nested conflict");
            }),
            Ct);

        // 内层命令加入外层事务，重试它会重放外层已完成的写入 —— 必须只跑一次
        Assert.Equal(1, innerAttempts);
    }

    [Fact]
    public async Task RetryInterceptor_ShouldReplaceTransactionBetweenAttempts()
    {
        var engine = TestEngine.Create();
        var transactions = new List<FakeCommandTransaction>();

        var executor = new CommandExecutor(
            [
                new RetryInterceptor(new CommandRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.Zero }),
                new TransactionInterceptor(),
                new CommandContextInterceptor(),
            ],
            () => new CommandContext(
                engine.Services,
                transactionFactory: () =>
                {
                    var transaction = new FakeCommandTransaction();
                    transactions.Add(transaction);
                    return transaction;
                }));

        var attempts = 0;

        await executor.ExecuteAsync(
            new FlakyCommand(() =>
            {
                attempts++;

                if (attempts < 3)
                {
                    throw new OptimisticLockingException($"conflict #{attempts}");
                }
            }),
            Ct);

        Assert.Equal(3, attempts);

        // 每次尝试一个新事务：旧事务里的脏状态（含一级缓存）绝不带进下一次
        Assert.Equal(3, transactions.Count);
        Assert.All(transactions, transaction => Assert.True(transaction.Disposed));

        // 前两次被回滚，只有最后一次提交
        Assert.Equal(1, transactions.Sum(transaction => transaction.Commits));
    }

    [Fact]
    public void WithOnRetry_ShouldPreserveRetryDecision()
    {
        var policy = new CommandRetryPolicy
        {
            MaxAttempts = 4,
            ShouldRetry = exception => exception is InvalidOperationException,
        };

        var copy = policy.WithOnRetry((_, _) => { });

        Assert.Equal(4, copy.MaxAttempts);
        Assert.True(copy.ShouldRetry(new InvalidOperationException("transient")));
        Assert.False(copy.ShouldRetry(new OptimisticLockingException("conflict")));
        Assert.NotNull(copy.OnRetry);
    }

    // ─────────────────────────── 测试辅助 ───────────────────────────

    /// <summary>让两个并发命令在各自上下文里「碰头」后再继续（避免靠 sleep 制造并发）。</summary>
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

            await _released.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
    }

    private sealed class AgendaProbeCommand(
        AsyncMeeting meeting,
        ConcurrentBag<(CommandContext Context, IEngineAgenda Facade)> captured) : ICommand<Unit>
    {
        public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            // Services.Agenda 是生产代码实际使用的入口，必须一并取证
            captured.Add((CommandContext.Current!, context.Services.Agenda));
            await meeting.SignalAndWaitAsync(ct).ConfigureAwait(false);
            return Unit.Value;
        }
    }

    private sealed class NestedProbeCommand(ICommandExecutor executor, List<CommandContext> captured)
        : ICommand<Unit>
    {
        public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            captured.Add(CommandContext.Current!);
            await executor.ExecuteAsync(new CaptureContextCommand(captured), ct).ConfigureAwait(false);
            return Unit.Value;
        }
    }

    private sealed class CaptureContextCommand(List<CommandContext> captured) : ICommand<Unit>
    {
        public ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            captured.Add(CommandContext.Current!);
            return ValueTask.FromResult(Unit.Value);
        }
    }

    private sealed class FlakyCommand(Action attempt) : ICommand<Unit>
    {
        public ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            attempt();
            return ValueTask.FromResult(Unit.Value);
        }
    }

    private sealed class CatchingOuterCommand(ICommandExecutor executor, Action innerAttempt) : ICommand<Unit>
    {
        public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            try
            {
                await executor.ExecuteAsync(new FlakyCommand(innerAttempt), ct).ConfigureAwait(false);
            }
            catch (OptimisticLockingException)
            {
                // 内层不重试：第一次冲突就冒到这里，外层吞掉以便断言次数
            }

            return Unit.Value;
        }
    }

    /// <summary>可观察的事务替身：记录提交次数与释放状态。</summary>
    private sealed class FakeCommandTransaction : ICommandTransaction
    {
        public bool IsActive { get; private set; }

        public bool Disposed { get; private set; }

        public int Commits { get; private set; }

        public void Begin() => IsActive = true;

        public ValueTask CommitAsync(CancellationToken ct = default)
        {
            IsActive = false;
            Commits++;
            return ValueTask.CompletedTask;
        }

        public void Rollback() => IsActive = false;

        public void Dispose()
        {
            Disposed = true;
            Rollback();
        }
    }
}

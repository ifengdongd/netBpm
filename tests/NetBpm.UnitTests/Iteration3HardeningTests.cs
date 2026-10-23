using NetBpm.El;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;
using Xunit;

namespace NetBpm.UnitTests;

/// <summary>
/// 迭代 3 的加固点：表达式缓存治理、可替换时钟、内存宿主的命令级回滚。
/// </summary>
public sealed class Iteration3HardeningTests
{
    /// <summary>
    /// 缓存 key 必须与解析口径一致（trim 后），否则 <c>"${x}"</c> 与 <c>" ${x} "</c>
    /// 会各存一份 AST，且带空白的写法永远命中不了。
    /// </summary>
    [Fact]
    public void ExpressionCache_ShouldUseTrimmedKey()
    {
        var manager = new ExpressionManager();

        var plain = manager.CreateExpression("${x}");
        var padded = manager.CreateExpression("   ${x}   ");

        Assert.Same(plain, padded);
        Assert.Equal(1, manager.CacheCount);
    }

    /// <summary>缓存必须有上限，避免生成式模型把 AST 缓存撑成内存泄漏。</summary>
    [Fact]
    public void ExpressionCache_ShouldEvictWhenOverCapacity()
    {
        var manager = new ExpressionManager(maxCacheSize: 4);

        for (var i = 0; i < 20; i++)
        {
            manager.CreateExpression($"${{v{i}}}");
        }

        Assert.True(manager.CacheCount <= 4, $"缓存未被淘汰，实际 {manager.CacheCount} 条。");
    }

    /// <summary>
    /// 内存宿主没有事务：命令失败后执行树必须回到命令开始前的样子。
    /// </summary>
    [Fact]
    public async Task InMemoryCommand_ShouldRollbackExecutionTreeOnFailure()
    {
        var engine = TestEngine.Create();
        var processInstanceId = await engine.StartProcessAsync(TestEngine.UserTaskThenServiceProcess("noop"));

        var execution = (await engine.ExecutionManager
                .FindAllByProcessInstanceIdAsync(processInstanceId))
            .First(candidate => candidate.IsActive);

        Assert.True(execution.IsActive, "前置条件：选中的执行应当是活动状态。");

        var throwing = new MutateThenThrowCommand(execution.Id);

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.CommandExecutor.ExecuteAsync(throwing, CancellationToken.None));

        var after = await engine.ExecutionManager.FindByIdAsync(execution.Id);

        Assert.NotNull(after);
        Assert.True(after!.IsActive, "命令失败后，执行树状态必须回滚到命令开始前。");
    }

    /// <summary>把执行挪到别的活动上再抛异常（模拟「改了一半」的命令）。</summary>
    private sealed class MutateThenThrowCommand : ICommand<Unit>
    {
        private readonly string _executionId;

        public MutateThenThrowCommand(string executionId)
        {
            _executionId = executionId;
        }

        public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
        {
            var execution = await context.Services.ExecutionManager
                .FindByIdAsync(_executionId, ct)
                .ConfigureAwait(false);

            Assert.NotNull(execution);

            execution!.IsActive = false;

            throw new InvalidOperationException("模拟业务失败：命令已改内存、随后抛异常。");
        }
    }
}

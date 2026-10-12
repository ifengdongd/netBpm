namespace NetBpm.Engine.Command;

/// <summary>
/// 空命令。<b>P0 验收标准</b>：工程底座就绪后，「空引擎能通过一个 <see cref="NoOpCommand"/>」
/// —— 即命令/拦截器/上下文这条链路可用（见开发计划 §8 阶段总览 P0）。
/// </summary>
public sealed class NoOpCommand : ICommand<Unit>
{
    /// <inheritdoc />
    public ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(Unit.Value);
    }
}

using NetBpm.Engine.Services;

namespace NetBpm.Engine.Command;

/// <summary>
/// <see cref="ICommandContext"/> 的默认实现。
/// <para>
/// 开发计划 §5.7：Activiti 用 <c>ThreadLocal</c> + 静态 <c>Context</c> 类传递上下文；
/// .NET 下必须换成 <see cref="AsyncLocal{T}"/> ——
/// <c>ThreadLocal</c>/<c>[ThreadStatic]</c> 在 async 续体之间会丢失或串号（风险 R4）。
/// </para>
/// </summary>
public sealed class CommandContext : ICommandContext
{
    private static readonly AsyncLocal<CommandContext?> CurrentHolder = new();

    public CommandContext(IEngineServices services, ICommandTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        Services = services;
        Transaction = transaction;
    }

    /// <summary>当前命令上下文；不在命令中时为 <see langword="null"/>。</summary>
    public static CommandContext? Current => CurrentHolder.Value;

    /// <inheritdoc />
    public IEngineServices Services { get; }

    /// <inheritdoc />
    public ICommandTransaction? Transaction { get; }

    /// <summary>
    /// 切换当前上下文并返回旧值。<b>调用方必须在 finally 中恢复</b>
    /// （见 <see cref="CommandExecutor"/>）。对同一个 <see cref="ExecutionContext"/> 而言，
    /// <see cref="AsyncLocal{T}"/> 的赋值会随 async 续体流动，这正是我们想要的语义。
    /// </summary>
    internal static CommandContext? Swap(CommandContext? next)
    {
        var previous = CurrentHolder.Value;
        CurrentHolder.Value = next;
        return previous;
    }
}

using NetBpm.Engine.Agenda;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Command;

/// <summary>
/// <see cref="ICommandContext"/> 的默认实现。
/// <para>
/// 开发计划 §5.7：Activiti 用 <c>ThreadLocal</c> + 静态 <c>Context</c> 类传递上下文；
/// .NET 下必须换成 <see cref="AsyncLocal{T}"/> ——
/// <c>ThreadLocal</c>/<c>[ThreadStatic]</c> 在 async 续体之间会丢失或串号（风险 R4）。
/// </para>
/// <para>
/// 🔑 <b>议程（Agenda）归属于命令上下文，不归属于引擎服务</b>：同一宿主上的并发命令各自持有
/// 独立的队列与排空状态，避免「A 命令规划的操作被 B 命令排空」这类跨请求串扰。
/// 嵌套命令复用外层上下文，因此也天然共享外层议程与事务。
/// </para>
/// </summary>
public sealed class CommandContext : ICommandContext
{
    private static readonly AsyncLocal<CommandContext?> CurrentHolder = new();

    private readonly Func<ICommandTransaction?>? _transactionFactory;
    private int _nestedDepth;

    /// <param name="services">引擎服务（无状态，可跨命令共享）。</param>
    /// <param name="transaction">已创建的事务；内存宿主为 <see langword="null"/>。</param>
    /// <param name="transactionFactory">
    /// 事务工厂。提供时用于<b>重试</b>：每次重新尝试都换一个全新的会话，
    /// 从而换掉一级缓存 —— 旧会话里被回滚的脏实体（以及自增过的 <c>REV_</c>）不得带入下一次尝试。
    /// </param>
    public CommandContext(
        IEngineServices services,
        ICommandTransaction? transaction = null,
        Func<ICommandTransaction?>? transactionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        Services = services;
        Transaction = transactionFactory is not null ? transactionFactory() : transaction;
        Agenda = new EngineAgenda(() => services);
        _transactionFactory = transactionFactory;
    }

    /// <summary>当前命令上下文；不在命令中时为 <see langword="null"/>。</summary>
    public static CommandContext? Current => CurrentHolder.Value;

    /// <inheritdoc />
    public IEngineServices Services { get; }

    /// <inheritdoc />
    public ICommandTransaction? Transaction { get; private set; }

    /// <inheritdoc />
    public IEngineAgenda Agenda { get; }

    /// <summary>当前是否处于「命令嵌套」调用中（重试拦截器据此跳过重试）。</summary>
    public bool IsNested => Volatile.Read(ref _nestedDepth) > 0;

    /// <summary>进入嵌套命令（由 <see cref="CommandExecutor"/> 配对调用）。</summary>
    internal void EnterNested() => Interlocked.Increment(ref _nestedDepth);

    /// <summary>退出嵌套命令。</summary>
    internal void ExitNested() => Interlocked.Decrement(ref _nestedDepth);

    /// <summary>
    /// 重新尝试前重置上下文：换一个新事务（= 新 <c>DbSession</c> = 新一级缓存）并清空议程。
    /// <para>
    /// 仅供 <see cref="Interceptors.RetryInterceptor"/> 在乐观锁冲突时调用。
    /// 内存宿主没有事务，只清空议程。
    /// </para>
    /// </summary>
    internal void ResetForRetry()
    {
        if (_transactionFactory is not null)
        {
            Transaction?.Dispose();
            Transaction = _transactionFactory();
        }

        Agenda.Clear();
    }

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

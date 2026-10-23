using Microsoft.Extensions.Logging;
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
    /// <param name="commandId">
    /// 幂等键。同一个键重复执行同一条命令时，仓储应当返回「首次执行」的结果快照而非再次生效。
    /// 典型的键来源：HTTP <c>Idempotency-Key</c>、消息 id、outbox 事件 id。
    /// </param>
    /// <param name="maxOperationsPerCommand">
    /// 本命令的议程操作预算（死循环护栏），默认 <see cref="EngineAgenda.DefaultMaxOperationsPerCommand"/>。
    /// </param>
    public CommandContext(
        IEngineServices services,
        ICommandTransaction? transaction = null,
        Func<ICommandTransaction?>? transactionFactory = null,
        string? commandId = null,
        int maxOperationsPerCommand = EngineAgenda.DefaultMaxOperationsPerCommand)
    {
        ArgumentNullException.ThrowIfNull(services);

        Services = services;
        Transaction = transactionFactory is not null ? transactionFactory() : transaction;
        Agenda = new EngineAgenda(() => services, maxOperations: maxOperationsPerCommand);
        CommandId = commandId;
        _transactionFactory = transactionFactory;
    }

    /// <inheritdoc />
    public string? CommandId { get; private set; }

    /// <summary>
    /// 由 <see cref="CommandExecutor"/> 在拿到上下文后立即写入幂等键。
    /// <para>
    /// 之所以不走构造函数：上下文由宿主的 <c>Func&lt;CommandContext&gt;</c> 工厂创建
    /// （DI 组合根 / 测试 harness 各自装配），把「命令自带的幂等键」也塞进工厂签名
    /// 会让所有调用方跟着改；这里是一次幂等的后置赋值，且必须在命令执行前完成。
    /// </para>
    /// <para>空 / 空白视为未启用幂等，保持 <see langword="null"/>。</para>
    /// </summary>
    internal void ApplyCommandId(string? commandId)
    {
        if (!string.IsNullOrWhiteSpace(commandId))
        {
            CommandId = commandId;
        }
    }

    /// <summary>当前命令上下文；不在命令中时为 <see langword="null"/>。</summary>
    public static CommandContext? Current => CurrentHolder.Value;

    /// <summary>命令级状态（按类型一份）。没有事务的宿主用它挂「命令快照」等回滚材料。</summary>
    private readonly Dictionary<Type, object> _state = [];

    /// <summary>命令失败（事务回滚）时执行的动作。</summary>
    private readonly List<Action> _abortHooks = [];

    /// <summary>
    /// 取命令级状态，不存在则创建。
    /// </summary>
    /// <remarks>
    /// 与持久化层的 <c>SessionState</c> 同构：一个命令一份，命令结束即丢弃。
    /// 内存宿主（无事务）靠它持有执行树快照，命令失败时恢复。
    /// </remarks>
    public T GetOrCreateState<T>(Func<T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_state)
        {
            if (_state.TryGetValue(typeof(T), out var existing))
            {
                return (T)existing;
            }

            var created = factory();
            _state[typeof(T)] = created;

            return created;
        }
    }

    /// <summary>
    /// 注册「命令失败时」执行的回滚动作（与事务回滚对齐）。
    /// </summary>
    /// <remarks>
    /// 只在命令抛异常时执行；成功的命令什么都不做 —— 回滚材料随上下文一起丢弃。
    /// </remarks>
    public void OnAborted(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_abortHooks)
        {
            _abortHooks.Add(action);
        }
    }

    /// <summary>执行并清空回滚钩子（由 <see cref="CommandExecutor"/> 在命令失败时调用）。</summary>
    /// <remarks>单个钩子抛异常不影响其它钩子：回滚尽力而为，原始异常更重要。</remarks>
    internal void RunAbortedHooks()
    {
        Action[] hooks;

        lock (_abortHooks)
        {
            if (_abortHooks.Count == 0)
            {
                return;
            }

            hooks = _abortHooks.ToArray();
            _abortHooks.Clear();
        }

        foreach (var hook in hooks)
        {
            try
            {
                hook();
            }
            catch
            {
                // 回滚过程中的异常不得掩盖命令自身的失败原因
            }
        }
    }

    /// <summary>事务提交后的回调（事务上下文里按注册顺序执行）。</summary>
    private readonly List<Func<ValueTask>> _committedHooks = [];

    /// <summary>
    /// 注册一个「事务成功提交后」才执行的动作。
    /// </summary>
    /// <remarks>
    /// 典型用途：向作业队列投递提示。若放在命令体里直接投递，一旦事务回滚，
    /// 作业行不存在而提示却留下了（虽然是无效提示，但会污染队列并造成无谓查询）。
    /// </remarks>
    public void OnCommitted(Func<ValueTask> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_committedHooks)
        {
            _committedHooks.Add(action);
        }
    }

    /// <summary>
    /// 触发并清空提交后钩子（由 <see cref="Interceptors.TransactionInterceptor"/> 在提交后调用）。
    /// </summary>
    /// <remarks>
    /// 单个钩子抛异常<b>不</b>影响其它钩子，也不影响命令结果 —— 它们只是副作用，
    /// 失败最多退化成定期扫库。
    /// </remarks>
    internal async ValueTask RunCommittedHooksAsync(ILogger? logger, CancellationToken ct)
    {
        Func<ValueTask>[] hooks;

        lock (_committedHooks)
        {
            if (_committedHooks.Count == 0)
            {
                return;
            }

            hooks = _committedHooks.ToArray();
            _committedHooks.Clear();
        }

        foreach (var hook in hooks)
        {
            try
            {
                await hook().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "命令提交后的回调执行失败（已忽略）。");
            }
        }
    }

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

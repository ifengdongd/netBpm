using NetBpm.Engine.Agenda;

namespace NetBpm.Engine.Command;

/// <summary>
/// 一条命令。<b>一个命令 = 一个事务</b>（开发计划 §5.3 / §5.6）。
/// 对应 Activiti 的 <c>Command</c>（143 个 → 本方案约 35 个）。
/// </summary>
/// <typeparam name="TResult">命令返回值类型；无返回值时用 <see cref="Unit"/>。</typeparam>
public interface ICommand<TResult>
{
    /// <summary>
    /// 本条命令的幂等键。默认 <see langword="null"/>（不启用引擎层幂等）；
    /// 需要「重复投递也只生效一次」的命令显式提供，由 <see cref="CommandExecutor"/>
    /// 写入 <see cref="ICommandContext.CommandId"/> 供仓储判定。
    /// </summary>
    string? CommandId => null;

    ValueTask<TResult> ExecuteAsync(ICommandContext context, CancellationToken ct = default);
}

/// <summary>
/// 命令执行上下文。对应 Activiti 的 <c>CommandContext</c> ——
/// 它是 Agenda 与各 Manager 的载体。
/// </summary>
public interface ICommandContext
{
    Services.IEngineServices Services { get; }

    /// <summary>
    /// 本命令的议程（规划 → 排空）。
    /// <para>
    /// 每个命令一个实例；嵌套命令复用外层命令的上下文，因此共享外层议程。
    /// 引擎门面（<see cref="Services.IEngineServices.Agenda"/>）在命令内一律返回它。
    /// </para>
    /// </summary>
    IEngineAgenda Agenda { get; }

    /// <summary>
    /// 命令级事务边界。内存宿主为 <see langword="null"/>；
    /// 持久化宿主为 <c>DbSession</c>（见 <see cref="ICommandTransaction"/>）。
    /// </summary>
    ICommandTransaction? Transaction { get; }

    /// <summary>
    /// 本命令的<b>幂等键</b>；未显式指定时为 <see langword="null"/>（即不启用引擎层幂等）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么要把幂等从 HTTP 层下沉到这里：HTTP 幂等只能挡住「同一个网关实例上的重复请求」，
    /// 而引擎侧的重复执行还可能来自 <c>ACT_EVT_OUTBOX</c> 重投、异步作业重试、消息重复消费。
    /// 只要命令自己带幂等键，仓储就能在唯一键冲突时判定「这条命令已经执行过了」，
    /// 从而返回上一次的快照而不是抛错 —— 投递语义从「至少一次」变成「至多生效一次」。
    /// </para>
    /// <para>
    /// 幂等键的<b>作用域是命令</b>：嵌套命令复用外层上下文，因此也复用外层幂等键，
    /// 避免内层子命令各自留下一条记录、把一次业务动作拆成多份。
    /// </para>
    /// </remarks>
    string? CommandId { get; }
}

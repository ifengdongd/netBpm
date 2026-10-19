using NetBpm.Engine.Agenda;

namespace NetBpm.Engine.Command;

/// <summary>
/// 一条命令。<b>一个命令 = 一个事务</b>（开发计划 §5.3 / §5.6）。
/// 对应 Activiti 的 <c>Command</c>（143 个 → 本方案约 35 个）。
/// </summary>
/// <typeparam name="TResult">命令返回值类型；无返回值时用 <see cref="Unit"/>。</typeparam>
public interface ICommand<TResult>
{
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
}

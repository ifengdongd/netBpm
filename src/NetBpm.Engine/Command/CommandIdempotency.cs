namespace NetBpm.Engine.Command;

/// <summary>
/// 一条命令的执行结果快照（引擎层幂等的落库形态）。
/// </summary>
/// <param name="Key">去重键（由幂等键 + 命令目标推导，见 <see cref="CommandIdempotencyKeys"/>）。</param>
/// <param name="CommandName">命令类型名（审计用：同一个键只能被同一类命令占位）。</param>
/// <param name="ResultPayload">
/// 结果快照。对 <see cref="Unit"/> 型命令而言固定为空字符串，
/// 真正的作用是让「这条命令执行过了」这件事可查证。
/// </param>
/// <param name="CreatedAt">首次占位时间（UTC）。</param>
/// <param name="ExpiresAt">过期时间（UTC）；过期后同一键可以再次生效。</param>
public sealed record CommandExecutionRecord(
    string Key,
    string CommandName,
    string ResultPayload,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>
/// 命令级幂等存储。与 HTTP 层的 <c>Idempotency-Key</c> 是两套键空间
/// （本处统一加 <c>cmd:</c> 前缀），互不覆盖。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：HTTP 幂等中间件只能挡住打到同一实例的同 key 请求；
/// 而 <c>ACT_EVT_OUTBOX</c> 重投、异步作业重试、消息重复消费都会绕过 HTTP 直接驱动引擎。
/// 把幂等键随命令一起传进来，才能在引擎层做到「至多生效一次」。
/// </para>
/// <para>
/// 关键约束——<b>占位必须与业务写入同事务</b>：
/// 若命令最终回滚，占位行也要一起消失，否则重试会被永久判成「已执行」。
/// 仓储实现通过 <see cref="Persistence.Database.IDbSessionProvider"/> 取当前命令会话来保证这一点。
/// </para>
/// </remarks>
public interface ICommandIdempotencyStore
{
    /// <summary>按去重键查快照；不存在返回 <see langword="null"/>。</summary>
    ValueTask<CommandExecutionRecord?> FindAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// 原子占位。键不存在则写入并返回 <see langword="true"/>；
    /// 已存在返回 <see langword="false"/>（表示已有一次执行在进行或已完成）。
    /// </summary>
    ValueTask<bool> TryRecordAsync(CommandExecutionRecord record, CancellationToken ct = default);

    /// <summary>删除占位（业务失败回滚用）。</summary>
    ValueTask DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// 去重键的构造规则。
/// </summary>
/// <remarks>
/// 键里必须带上命令目标（此处是任务 id），否则「同一幂等键去完成两个不同任务」
/// 会被误判成重复。
/// </remarks>
public static class CommandIdempotencyKeys
{
    /// <summary>构造「完成某任务」的去重键。</summary>
    /// <param name="commandId">幂等键（HTTP 头 / 消息 id / outbox 事件 id）。</param>
    /// <param name="taskId">被完成的任务 id。</param>
    public static string ForTask(string commandId, string taskId) =>
        $"cmd:complete-task:{commandId}:{taskId}";
}

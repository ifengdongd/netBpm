namespace NetBpm.Engine.Command;

/// <summary>
/// 命令级事务边界。对应 Activiti 的 <c>TransactionContext</c>。
/// <para>
/// 🔑 开发计划 §5.3 / §5.6 的硬约束：<b>一个命令 = 一个事务</b>，
/// 且议程排空（<see cref="Interceptors.CommandContextInterceptor"/>）必须发生在事务内。
/// </para>
/// <para>
/// 接口刻意留在引擎侧：内存宿主不注入实现（<see langword="null"/>），
/// 持久化宿主注入 <c>NetBpm.Persistence.DbSession</c>，引擎无须知道数据库的存在。
/// </para>
/// </summary>
public interface ICommandTransaction : IDisposable
{
    /// <summary>是否已有活动事务（嵌套命令会加入外层事务，不再新开）。</summary>
    bool IsActive { get; }

    /// <summary>开启事务。</summary>
    void Begin();

    /// <summary>
    /// 提交事务。实现方必须在提交前完成一级缓存 flush
    /// （否则命令期间对执行树的内存改动会丢失）。
    /// <para>刻意设计为异步：flush 需要写库，同步阻塞等待 I/O 会引入
    /// sync-over-async 的死锁风险。</para>
    /// </summary>
    ValueTask CommitAsync(CancellationToken ct = default);

    /// <summary>回滚事务。重复调用应安全。</summary>
    void Rollback();
}

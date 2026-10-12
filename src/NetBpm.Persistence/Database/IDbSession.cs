using System.Data.Common;
using NetBpm.Engine.Command;

namespace NetBpm.Persistence.Database;

/// <summary>
/// 数据库会话：<b>一个会话 = 一个命令 = 一个事务 = 一份一级缓存</b>。
/// 对应 Activiti 的 <c>DbSqlSession</c>。
/// </summary>
public interface IDbSession : ICommandTransaction
{
    /// <summary>方言。</summary>
    DatabaseType DatabaseType { get; }

    /// <summary>
    /// 会话内的连接（首次访问时惰性打开）。所有仓储必须使用它，
    /// 否则写操作会跑到事务之外。
    /// </summary>
    DbConnection Connection { get; }

    /// <summary>当前事务；未开启时为 <see langword="null"/>。</summary>
    DbTransaction? Transaction { get; }

    /// <summary>
    /// 注册 flush 参与方。<see cref="ICommandTransaction.CommitAsync"/> 会先按注册顺序 flush 再提交 ——
    /// 这样命令期间对执行树的内存改动才能落库（对应 Activiti 的 <c>DbSqlSession.flush()</c>）。
    /// </summary>
    void RegisterFlusher(IDbFlushParticipant flusher);

    /// <summary>
    /// 立即把一级缓存写库（<b>不提交事务</b>）。对应 Activiti 的 <c>DbSqlSession.flush()</c>。
    /// <para>
    /// 用途：需要在命令中途读取「内存改动的当前结果」时（诊断、快照、对拍）必须先 flush，
    /// 否则读到的是库里的陈旧行。仓储的查询方法内部已自动调用它。
    /// </para>
    /// </summary>
    ValueTask FlushAsync(CancellationToken ct = default);
}

/// <summary>可被会话在提交前 flush 的组件（一级缓存持有者）。</summary>
public interface IDbFlushParticipant
{
    /// <summary>把内存改动写入数据库。必须可重复调用（幂等）。</summary>
    ValueTask FlushAsync(IDbSession session, CancellationToken ct = default);
}

/// <summary>
/// 会话租约。命令内取到的是命令自己的会话（不拥有，不能释放）；
/// 命令外（迁移 / 结构校验）取得的是临时会话，必须释放。
/// </summary>
public readonly struct DbSessionLease : IDisposable
{
    private readonly IDbSession _session;
    private readonly bool _isOwned;

    internal DbSessionLease(IDbSession session, bool isOwned)
    {
        _session = session;
        _isOwned = isOwned;
    }

    /// <summary>会话实例。</summary>
    public IDbSession Session => _session;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isOwned)
        {
            _session.Dispose();
        }
    }
}

/// <summary>创建会话。</summary>
public interface IDbSessionFactory
{
    IDbSession Create();
}

/// <summary>
/// 取「当前会话」：
/// <list type="bullet">
///   <item><description>命令内 → 命令上下文里的会话（与命令同生共死，参与其事务）；</description></item>
///   <item><description>命令外 → 新建临时会话（调用方负责释放租约）。</description></item>
/// </list>
/// </summary>
public interface IDbSessionProvider
{
    DbSessionLease Acquire();
}

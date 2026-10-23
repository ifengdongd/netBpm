namespace NetBpm.Persistence.Database;

/// <summary>
/// 命令外的显式工作单元：多条 DML 要么全提交、要么全回滚。
/// </summary>
/// <remarks>
/// <para>
/// 命令内的事务由 <c>TransactionInterceptor</c> 统一管理（一个命令 = 一个事务），
/// 所以这里的 <see cref="IsTransactional"/> 为 <see langword="false"/>，
/// <see cref="CommitAsync"/> 是 no-op（提交权归命令，事务边界不能被仓储提前关掉）。
/// </para>
/// <para>
/// 命令外（管理端批量操作、迁移、清理、归档）才是它的用武之地：过去这些路径的多条语句
/// 各自自动提交，中途失败就留下「有头无尾」的数据（例如删了变量行、字节行没删）。
/// </para>
/// <para>
/// 🔑 必须显式调用 <see cref="CommitAsync"/>：没提交就 <c>Dispose</c> ⇒ 回滚。
/// 这是刻意的 —— 「忘了提交」的错误应该响亮地表现为「什么都没写」，
/// 而不是「看起来成功了」。
/// </para>
/// </remarks>
public sealed class SessionUnitOfWork : IAsyncDisposable
{
    private readonly IDbSession _session;
    private readonly bool _ownsTransaction;
    private bool _completed;

    internal SessionUnitOfWork(IDbSession session, bool ownsTransaction)
    {
        _session = session;
        _ownsTransaction = ownsTransaction;
    }

    /// <summary>本工作单元是否真的持有事务（命令内调用为 <see langword="false"/>）。</summary>
    public bool IsTransactional => _ownsTransaction;

    /// <summary>提交。命令内为 no-op（提交归命令）。</summary>
    /// <exception cref="InvalidOperationException">重复提交。</exception>
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("工作单元已经结束，不能重复提交。");
        }

        _completed = true;

        if (_ownsTransaction)
        {
            await _session.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>未提交即释放 ⇒ 回滚（命令内不做任何事）。</summary>
    public ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            _completed = true;

            if (_ownsTransaction)
            {
                _session.Rollback();
            }
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>工作单元入口。</summary>
public static class SessionUnitOfWorkExtensions
{
    /// <summary>
    /// 开启工作单元：命令外真的 <c>Begin()</c>，命令内退化为 no-op（复用命令事务）。
    /// </summary>
    /// <remarks>
    /// 用法：<c>await using var uow = lease.BeginUnitOfWork(); … await uow.CommitAsync(ct);</c>
    /// </remarks>
    public static SessionUnitOfWork BeginUnitOfWork(this DbSessionLease lease)
    {
        var session = lease.Session;

        if (session.IsActive)
        {
            return new SessionUnitOfWork(session, ownsTransaction: false);
        }

        session.Begin();

        return new SessionUnitOfWork(session, ownsTransaction: true);
    }
}

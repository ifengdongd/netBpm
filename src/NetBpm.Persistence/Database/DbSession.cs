using System.Data;
using System.Data.Common;
using NetBpm.Engine;
using NetBpm.Engine.Command;

namespace NetBpm.Persistence.Database;

/// <summary>
/// <see cref="IDbSession"/> 的默认实现。
/// <para>连接<b>惰性打开</b>：只跑内存操作的命令（如 <c>NoOpCommand</c>）不会碰到数据库。</para>
/// <para>
/// ⚠️ <b>所有仓储在会话内执行语句时必须传 <see cref="Transaction"/></b>：
/// MySqlConnector 在连接已有活动事务时，若命令未显式指定该事务，会抛
/// <c>"The transaction associated with this command is not the connection's active transaction"</c>。
/// 这是驱动层面的强制约束（与 Connector/NET 不同），漏传会在运行期立刻暴露。
/// </para>
/// </summary>
public sealed class DbSession : IDbSession
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly List<IDbFlushParticipant> _flushers = [];

    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private bool _disposed;

    public DbSession(IDbConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public DatabaseType DatabaseType => _connectionFactory.DatabaseType;

    /// <inheritdoc />
    public DbConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection ??= _connectionFactory.CreateConnection();
        }
    }

    /// <summary>连接是否已打开（诊断 / 测试用）。</summary>
    public bool HasConnection => _connection is not null;

    /// <inheritdoc />
    public DbTransaction? Transaction => _transaction;

    /// <inheritdoc />
    public SessionState State { get; } = new();

    /// <inheritdoc />
    public bool IsActive => _transaction is not null;

    /// <inheritdoc />
    public void RegisterFlusher(IDbFlushParticipant flusher)
    {
        ArgumentNullException.ThrowIfNull(flusher);

        if (!_flushers.Contains(flusher))
        {
            _flushers.Add(flusher);
        }
    }

    /// <inheritdoc />
    public void Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is not null)
        {
            throw new EngineException(
                "同一个 DbSession 上已有活动事务。一个命令 = 一个事务（§5.3），" +
                "嵌套命令应复用外层命令的上下文。");
        }

        _transaction = Connection.BeginTransaction(IsolationLevel.ReadCommitted);
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        // 顺序 flush：注册顺序即写入顺序，保证父子执行的 INSERT 顺序满足外键约束
        foreach (var flusher in _flushers)
        {
            await flusher.FlushAsync(this, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        await FlushAsync(ct).ConfigureAwait(false);

        if (_transaction is null)
        {
            // 命令外调用（迁移 / 校验）：语句已是自动提交，只需 flush
            return;
        }

        var transaction = _transaction;
        _transaction = null;
        try
        {
            // 必须走异步提交：同步 Commit 会阻塞调用线程等网络往返，
            // 与「全链路 ConfigureAwait(false) + CancellationToken」的纪律不一致。
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Rollback()
    {
        var transaction = _transaction;
        _transaction = null;

        if (transaction is null)
        {
            return;
        }

        try
        {
            transaction.Rollback();
        }
        catch (Exception)
        {
            // 回滚失败通常意味着连接已断（网络/超时）。此时已无法挽回，
            // 但不能让回滚异常掩盖业务异常，故仅忽略、保持原始异常向上传播。
        }
        finally
        {
            transaction.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 未提交就释放 ⇒ 必须回滚，否则连接归还连接池时会隐式回滚（并可能报错）
        Rollback();

        _flushers.Clear();
        _connection?.Dispose();
        _connection = null;
    }


}

/// <summary>默认会话工厂。</summary>
public sealed class DbSessionFactory : IDbSessionFactory
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DbSessionFactory(IDbConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public IDbSession Create() => new DbSession(_connectionFactory);
}

/// <summary>
/// 命令内复用当前命令的会话，命令外新建临时会话。
/// </summary>
public sealed class CommandScopedDbSessionProvider : IDbSessionProvider
{
    private readonly IDbSessionFactory _sessionFactory;

    public CommandScopedDbSessionProvider(IDbSessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        _sessionFactory = sessionFactory;
    }

    /// <inheritdoc />
    public DbSessionLease Acquire()
    {
        if (CommandContext.Current?.Transaction is IDbSession session)
        {
            return new DbSessionLease(session, isOwned: false);
        }

        return new DbSessionLease(_sessionFactory.Create(), isOwned: true);
    }
}

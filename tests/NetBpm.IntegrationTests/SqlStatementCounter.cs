using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using NetBpm.Persistence;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 统计一段时间内实际发到数据库的语句条数。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：N+1 回归最典型的形态不是「变慢一点点」，而是「往返次数随数据量线性增长」。
/// 断言耗时会被机器负载淹没（CI 上经常抖 10 倍），断言<b>语句条数</b>则是确定性的 ——
/// 同样的流程、同样的数据量，语句数应当稳定。
/// </para>
/// <para>
/// 这里刻意在 <c>ADO.NET</c> 层计数而不是在仓储里埋点：
/// 埋点会漏掉「新增的查询」这种恰恰最需要防的回归，而在连接层计数则一网打尽。
/// </para>
/// </remarks>
internal sealed class SqlStatementCounter
{
    private int _value;

    /// <summary>当前累计语句条数。</summary>
    public int Value => Volatile.Read(ref _value);

    /// <summary>归零并返回归零前的读数（便于「先清零、再测量」的写法）。</summary>
    public int Reset() => Interlocked.Exchange(ref _value, 0);

    /// <summary>累加。</summary>
    internal void Increment() => Interlocked.Increment(ref _value);
}

/// <summary>
/// 包一层连接工厂，让本 harness 建出的会话都走可计数的连接。
/// <para>仅测试用：生产组合根不感知它的存在。</para>
/// </summary>
internal sealed class CountingDbConnectionFactory : IDbConnectionFactory
{
    private readonly IDbConnectionFactory _inner;
    private readonly SqlStatementCounter _counter;

    public CountingDbConnectionFactory(IDbConnectionFactory inner, SqlStatementCounter counter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(counter);

        _inner = inner;
        _counter = counter;
    }

    /// <inheritdoc />
    public DatabaseType DatabaseType => _inner.DatabaseType;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new CountingDbConnection(_inner.CreateConnection(), _counter);
}

/// <summary>
/// 转发式 <see cref="DbConnection"/> 代理：只重写 <c>CreateDbCommand</c>，
/// 让本连接上创建出来的命令在执行时被计数。
/// </summary>
internal sealed class CountingDbConnection : DbConnection
{
    private readonly DbConnection _inner;
    private readonly SqlStatementCounter _counter;

    public CountingDbConnection(DbConnection inner, SqlStatementCounter counter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(counter);

        _inner = inner;
        _counter = counter;
    }

    /// <summary>被代理的真连接（命令代理认这个对象为 <c>Connection</c>，避免破坏驱动的类型检查）。</summary>
    internal DbConnection Inner => _inner;

    /// <inheritdoc />
    // 基类 DbConnection.ConnectionString 的 setter 允许 null（[AllowNull]），
    // 只给 getter 标非空会让重写签名与基类不一致（CS8765）。
    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    /// <inheritdoc />
    public override string Database => _inner.Database;

    /// <inheritdoc />
    public override string DataSource => _inner.DataSource;

    /// <inheritdoc />
    public override string ServerVersion => _inner.ServerVersion;

    /// <inheritdoc />
    public override ConnectionState State => _inner.State;

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    /// <inheritdoc />
    public override void Close() => _inner.Close();

    /// <inheritdoc />
    public override void Open() => _inner.Open();

    /// <inheritdoc />
    public override Task OpenAsync(CancellationToken cancellationToken) =>
        _inner.OpenAsync(cancellationToken);

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        _inner.BeginTransaction(isolationLevel);

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() =>
        new CountingDbCommand(_inner.CreateCommand(), _inner, _counter);
}

/// <summary>
/// 转发式 <see cref="DbCommand"/> 代理：三个执行入口各自 +1。
/// </summary>
/// <remarks>
/// 只统计「真正发到库的语句」：<c>Prepare</c> / <c>Cancel</c> / <c>Dispose</c> 不计。
/// 参数集合交给真命令，Dapper 的行为完全不变。
/// </remarks>
internal sealed class CountingDbCommand : DbCommand
{
    private readonly DbCommand _inner;
    private readonly DbConnection _connection;
    private readonly SqlStatementCounter _counter;

    public CountingDbCommand(DbCommand inner, DbConnection connection, SqlStatementCounter counter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(counter);

        _inner = inner;
        _connection = connection;
        _counter = counter;
    }

    /// <inheritdoc />
    // 同 ConnectionString：DbCommand.CommandText 的 setter 也允许 null。
    [AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value;
    }

    /// <inheritdoc />
    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    /// <inheritdoc />
    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    /// <inheritdoc />
    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => throw new NotSupportedException("计数命令不支持跨连接复用。");
    }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => _inner.Transaction;
        set => _inner.Transaction = value;
    }

    /// <inheritdoc />
    public override void Cancel() => _inner.Cancel();

    /// <inheritdoc />
    public override int ExecuteNonQuery()
    {
        _counter.Increment();
        return _inner.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override object? ExecuteScalar()
    {
        _counter.Increment();
        return _inner.ExecuteScalar();
    }

    /// <inheritdoc />
    public override void Prepare() => _inner.Prepare();

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        _counter.Increment();
        return _inner.ExecuteReader(behavior);
    }
}

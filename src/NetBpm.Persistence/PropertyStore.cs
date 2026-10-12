using Dapper;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence;

/// <summary><c>ACT_GE_PROPERTY</c> 的一行。</summary>
public sealed class PropertyEntity
{
    /// <summary><c>NAME_</c>。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>VALUE_</c>。</summary>
    public string? Value { get; set; }

    /// <summary><c>REV_</c>（乐观锁）。</summary>
    public int Revision { get; set; }
}

/// <summary>
/// 引擎属性表读写。<c>ACT_GE_PROPERTY</c> 是 <c>schema.version</c> 与
/// ID 生成器区块状态（<c>next.dbid</c>）的唯一载体（见 §7.2、§7.3）。
/// <para>刻意保持<b>同步</b>接口：Activiti 的 <c>DbIdGenerator</c> 是同步的，
/// 同步实现可避免 .NET 中「sync-over-async」的死锁风险。</para>
/// </summary>
public interface IPropertyStore
{
    /// <param name="forUpdate">
    /// 是否加行锁读（<c>FOR UPDATE</c> / <c>WITH (UPDLOCK, ROWLOCK)</c>）。
    /// <b>ID 区块分配必须用 <see langword="true"/></b>，原因见 <see cref="DapperPropertyStore"/> 的类注释。
    /// </param>
    PropertyEntity? FindProperty(string name, bool forUpdate = false);

    /// <summary>以乐观锁更新；返回 <see langword="false"/> 表示 <c>REV_</c> 不匹配（需重试）。</summary>
    bool TryUpdateProperty(string name, int expectedRevision, string value);

    void InsertProperty(string name, string value, int revision);
}

/// <summary>
/// 基于 Dapper 的 <see cref="IPropertyStore"/> 实现，读写走当前命令的会话（因此参与命令事务）。
/// <para>
/// 🔑 <b>关于 ID 区块分配的一个必要强化</b>：Activiti 的 <c>DbIdGenerator</c> 用
/// 「普通 SELECT + <c>REV_</c> 乐观锁 UPDATE + 冲突重试」。这套做法在 MySQL 默认的
/// <c>REPEATABLE READ</c> 下会<b>死循环</b> —— 冲突后重读仍读到事务开始时的一致性快照，
/// <c>REV_</c> 永远对不上。因此本实现在占区块时使用<b>加锁读</b>
/// （MySQL <c>FOR UPDATE</c> / SQL Server <c>WITH (UPDLOCK)</c>），
/// 它总是读取最新已提交版本并串行化区块分配。
/// </para>
/// <para>
/// 这是<b>实现层面的加固，不是行为偏离</b>：对拍只比对行内容，锁策略不产生可见差异
/// （§9.4 的差异清单里无需登记）。
/// </para>
/// </summary>
public sealed class DapperPropertyStore : IPropertyStore
{
    private readonly IDbSessionProvider _sessions;

    public DapperPropertyStore(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    /// <inheritdoc />
    public PropertyEntity? FindProperty(string name, bool forUpdate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var sql = forUpdate ? LockingSelectSql(session.DatabaseType) : PlainSelectSql;
        return session.Connection.QuerySingleOrDefault<PropertyEntity>(
            sql,
            new { name },
            session.Transaction);
    }

    /// <inheritdoc />
    public bool TryUpdateProperty(string name, int expectedRevision, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var affected = session.Connection.Execute(
            UpdateSql,
            new { name, revision = expectedRevision, value },
            session.Transaction);

        return affected == 1;
    }

    /// <inheritdoc />
    public void InsertProperty(string name, string value, int revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        session.Connection.Execute(
            InsertSql,
            new { name, value, revision },
            session.Transaction);
    }

    private const string PlainSelectSql =
        "select NAME_ as Name, VALUE_ as Value, REV_ as Revision from ACT_GE_PROPERTY where NAME_ = @name";

    private static string LockingSelectSql(DatabaseType databaseType) => databaseType switch
    {
        // MySQL：FOR UPDATE 在 REPEATABLE READ 下也读最新已提交行，并持有排他行锁
        DatabaseType.MySql =>
            "select NAME_ as Name, VALUE_ as Value, REV_ as Revision from ACT_GE_PROPERTY where NAME_ = @name for update",

        // SQL Server：UPDLOCK + ROWLOCK 等价语义
        DatabaseType.SqlServer =>
            "select NAME_ as Name, VALUE_ as Value, REV_ as Revision from ACT_GE_PROPERTY with (updlock, rowlock) where NAME_ = @name",

        _ => throw new NotSupportedException($"不支持的数据库方言：{databaseType}"),
    };

    private const string UpdateSql =
        "update ACT_GE_PROPERTY set VALUE_ = @value, REV_ = @revision + 1 where NAME_ = @name and REV_ = @revision";

    private const string InsertSql =
        "insert into ACT_GE_PROPERTY (NAME_, VALUE_, REV_) values (@name, @value, @revision)";
}

using NetBpm.Persistence;

namespace NetBpm.Demo.Storage.DataAccess;

/// <summary>
/// <see cref="IDataAccessor{TEntity}"/> 的 <b>MySQL</b> 实现：反引号引用、行数上限用 <c>limit n</c>。
/// </summary>
public sealed class MySqlDataAccessor<TEntity> : DataAccessorBase<TEntity> where TEntity : class
{
    /// <summary>构造访问器（连接工厂与引擎同一个，方言随 <c>NetBpm:DatabaseType</c>）。</summary>
    public MySqlDataAccessor(IDbConnectionFactory connections, IEntityMapper<TEntity> mapper)
        : base(connections, mapper)
    {
    }

    /// <inheritdoc />
    protected override string SelectHead(int? limit) => "select";

    /// <inheritdoc />
    protected override string TailLimit(int? limit) => limit is { } count ? $" limit {count}" : string.Empty;

    /// <inheritdoc />
    /// <remarks>纯逐段引用（列名等）；本 Demo MySQL 表无 schema 前缀。</remarks>
    protected override string Quote(string identifier) =>
        string.Join(".", identifier
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => $"`{part}`"));

    /// <inheritdoc />
    /// <remarks>MySQL 业务表无 schema 前缀，表名与列同一引用方式。</remarks>
    protected override string QuoteTable(string tableName) => Quote(tableName);
}

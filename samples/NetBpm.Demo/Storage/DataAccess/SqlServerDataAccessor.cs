using NetBpm.Persistence;

namespace NetBpm.Demo.Storage.DataAccess;

/// <summary>
/// <see cref="IDataAccessor{TEntity}"/> 的 <b>SQL Server</b> 实现：方括号引用、行数上限用 <c>top (n)</c>；
/// 表名自动带 schema 前缀（默认 <c>wf</c>，Demo 业务表都在 wf schema，见 db/sqlserver/0003）。
/// </summary>
public sealed class SqlServerDataAccessor<TEntity> : DataAccessorBase<TEntity> where TEntity : class
{
    private readonly string _schema;

    /// <summary>构造访问器（连接工厂与引擎同一个；schema 默认 <c>wf</c>，传空串表示 dbo）。</summary>
    public SqlServerDataAccessor(IDbConnectionFactory connections, IEntityMapper<TEntity> mapper, string schema = "wf")
        : base(connections, mapper)
    {
        _schema = schema;
    }

    /// <inheritdoc />
    protected override string SelectHead(int? limit) =>
        limit is { } count ? $"select top ({count})" : "select";

    /// <inheritdoc />
    protected override string TailLimit(int? limit) => string.Empty;

    /// <inheritdoc />
    /// <remarks>纯逐段引用（列名等），不做 schema 前缀。</remarks>
    protected override string Quote(string identifier) =>
        string.Join(".", identifier
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => $"[{part}]"));

    /// <inheritdoc />
    /// <remarks>表名补 schema 前缀：<c>order_bill</c> → <c>[wf].[order_bill]</c>。</remarks>
    protected override string QuoteTable(string tableName) =>
        string.IsNullOrEmpty(_schema)
            ? Quote(tableName)
            : $"[{_schema}].{Quote(tableName)}";
}

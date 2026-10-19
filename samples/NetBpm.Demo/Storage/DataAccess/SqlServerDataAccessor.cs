using NetBpm.Persistence;

namespace NetBpm.Demo.Storage.DataAccess;

/// <summary>
/// <see cref="IDataAccessor{TEntity}"/> 的 <b>SQL Server</b> 实现：方括号引用、行数上限用 <c>top (n)</c>；
/// 业务表统一落 dbo（与引擎 ACT_* 同 schema，与 MySQL 表名逐字相同），无需 schema 前缀
/// （构造参数保留扩展位，需要隔离 schema 的宿主可传入）。
/// </summary>
public sealed class SqlServerDataAccessor<TEntity> : DataAccessorBase<TEntity> where TEntity : class
{
    private readonly string _schema;

    /// <summary>构造访问器（连接工厂与引擎同一个；schema 默认空串 = dbo）。</summary>
    public SqlServerDataAccessor(IDbConnectionFactory connections, IEntityMapper<TEntity> mapper, string schema = "")
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
    /// <remarks>schema 参数非空时补前缀（如 <c>wf</c> → <c>[wf].[order_bill]</c>）；默认空串直接 <c>[order_bill]</c>。</remarks>
    protected override string QuoteTable(string tableName) =>
        string.IsNullOrEmpty(_schema)
            ? Quote(tableName)
            : $"[{_schema}].{Quote(tableName)}";
}

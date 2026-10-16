namespace NetBpm.Demo.Storage.DataAccess;

/// <summary>
/// 实体表的映射规格：表名、主键、列白名单与伪删除列。
/// </summary>
/// <param name="TableName">表名（不含 schema 前缀；前缀由方言实现负责引用与拼接）。</param>
/// <param name="KeyColumn">主键列（业务侧生成）。</param>
/// <param name="Columns">
/// 全部列的白名单 —— insert 的列集，同时是查询条件 / 排序字段的合法名单（防注入）。
/// </param>
/// <param name="UpdateColumns">「修改」允许更新的列（不含主键与创建类列）。</param>
/// <param name="SoftDeleteColumn">伪删除状态列（如 <c>deleted_flag</c>）；null 表示不支持伪删除。</param>
/// <param name="SoftDeleteValue">伪删除写入的值（如 <c>(byte)1</c>）。</param>
public sealed record EntityTableSpec(
    string TableName,
    string KeyColumn,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> UpdateColumns,
    string? SoftDeleteColumn = null,
    object? SoftDeleteValue = null)
{
    /// <summary>列名是否在白名单内（大小写不敏感，与库列命名一致）。</summary>
    public bool IsKnownColumn(string column) =>
        Columns.Contains(column, StringComparer.OrdinalIgnoreCase)
        || string.Equals(column, KeyColumn, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 实体 ↔ 表列的双向映射。方言无关 —— 列名原样交给方言实现做标识符引用。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
public interface IEntityMapper<TEntity> where TEntity : class
{
    /// <summary>表规格（表名 / 主键 / 列白名单 / 伪删除列）。</summary>
    EntityTableSpec Table { get; }

    /// <summary>取实体主键值（新增后由 <see cref="IDataAccessor{TEntity}.InsertAsync"/> 返回）。</summary>
    long GetKey(TEntity entity);

    /// <summary>实体 → 全列字典（insert 用；含主键列）。</summary>
    IReadOnlyDictionary<string, object?> ToColumns(TEntity entity);

    /// <summary>实体 → 可更新列字典（update 用；不含主键与创建类列）。</summary>
    IReadOnlyDictionary<string, object?> ToUpdateColumns(TEntity entity);

    /// <summary>一行（Dapper dynamic row 的字典形态）→ 实体；行不存在返回 <see langword="null"/>。</summary>
    TEntity? FromRow(IReadOnlyDictionary<string, object?> row);
}

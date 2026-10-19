namespace NetBpm.DbInit.Schema;

/// <summary>方言无关的列类型（由 <see cref="SqlGenerator"/> 映射到具体方言）。</summary>
internal enum ColType
{
    /// <summary>变长字符串（varchar/nvarchar，长度见 <see cref="Column.Length"/>）。</summary>
    Varchar,

    /// <summary>64 位整数（bigint）。</summary>
    BigInt,

    /// <summary>32 位整数（int / integer）。</summary>
    Int,

    /// <summary>16 位整数（smallint）。</summary>
    SmallInt,

    /// <summary>8 位整数（tinyint）。</summary>
    TinyInt,

    /// <summary>双精度浮点（double / double precision）。</summary>
    Double,

    /// <summary>长整数（MySQL bigint / SQL Server numeric(19,0)，对应 Activiti LONG_/DURATION_）。</summary>
    Long,

    /// <summary>金额（decimal(18,2)）。</summary>
    Money,

    /// <summary>日期时间（MySQL datetime / SQL Server datetime）。</summary>
    DateTime,

    /// <summary>毫秒日期时间（MySQL datetime(3) / SQL Server datetime）。</summary>
    DateTime3,

    /// <summary>毫秒时间戳（MySQL timestamp(3) / SQL Server datetime）。</summary>
    Timestamp,

    /// <summary>毫秒日期时间（MySQL datetime(3) / SQL Server datetime2(3)）。</summary>
    DateTime2,

    /// <summary>二进制大对象（LONGBLOB / varbinary(max)）。</summary>
    Blob,

    /// <summary>长文本（text / nvarchar(max)）。</summary>
    LongText,

    /// <summary>中等文本（mediumtext / nvarchar(max)）。</summary>
    MediumText,
}

/// <summary>一列。</summary>
/// <param name="Name">列名。</param>
/// <param name="Type">方言无关类型。</param>
/// <param name="Length"><see cref="ColType.Varchar"/> 的长度。</param>
/// <param name="Nullable">是否可空。</param>
/// <param name="Default">默认值 SQL 字面量（如 <c>"''"</c>、<c>"1"</c>、<c>"0"</c>）。</param>
/// <param name="Comment">列注释（仅业务表有；引擎表列无注释）。</param>
internal sealed record Column(
    string Name,
    ColType Type,
    int? Length = null,
    bool Nullable = true,
    string? Default = null,
    string? Comment = null);

/// <summary>一个（唯一）索引定义（所属表由外层 <see cref="TableDef"/> 决定）。</summary>
internal sealed record IndexDef(string Name, IReadOnlyList<string> Columns, bool Unique = false);

/// <summary>一个显式指定所属表的独立索引（用于 0001 / 0009 的引擎表索引）。</summary>
internal sealed record TableIndexDef(string Table, string Name, IReadOnlyList<string> Columns);

/// <summary>一个外键定义。</summary>
internal sealed record ForeignKeyDef(
    string Table,
    string Name,
    string Column,
    string ReferencedTable,
    string ReferencedColumn,
    bool CascadeDelete = false,
    bool CascadeUpdate = false);

/// <summary>一张表。</summary>
/// <param name="Name">表名。</param>
/// <param name="Comment">表注释。</param>
/// <param name="Columns">列。</param>
/// <param name="PrimaryKey">主键列。</param>
/// <param name="Indexes">普通索引。</param>
/// <param name="UniqueKeys">唯一键（MySQL 内联 unique key / SQL Server create unique index）。</param>
/// <param name="ForeignKeys">外键（仅引擎表）。</param>
/// <param name="EngineCharset">是否使用引擎表的 utf8 字符集（否则 utf8mb4）。</param>
internal sealed record TableDef(
    string Name,
    string Comment,
    IReadOnlyList<Column> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<IndexDef> Indexes,
    IReadOnlyList<IndexDef> UniqueKeys,
    IReadOnlyList<ForeignKeyDef> ForeignKeys,
    bool EngineCharset);

/// <summary>一张种子的定义（幂等/一次性插入数据）。</summary>
/// <param name="TableName">表名。</param>
/// <param name="Columns">插入列。</param>
/// <param name="Rows">每行取值（字符串 / 数值 / null）。</param>
/// <param name="GuardColumns">SQL Server <c>if not exists</c> 用的判重列。</param>
/// <param name="UpsertUpdateColumns">MySQL <c>on duplicate key update</c> 的更新列。</param>
internal sealed record SeedTable(
    string TableName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    IReadOnlyList<string> GuardColumns,
    IReadOnlyList<string> UpsertUpdateColumns);

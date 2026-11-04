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

/// <summary>一张种子的定义：**只在目标表为空时**初始化，一旦表里有行就整段跳过。</summary>
/// <param name="TableName">表名。</param>
/// <param name="Columns">插入列。</param>
/// <param name="Rows">每行取值（字符串 / 数值 / null）。</param>
/// <remarks>
/// 语义刻意定为「表为空才初始化」，不是「缺行就补」，也不是 upsert。原因是本平台没有迁移记录表，
/// <c>AutoMigrate</c> 每次启动都把整篇幂等脚本重跑一遍：
/// <list type="bullet">
/// <item>upsert（<c>on duplicate key update</c>）→ 每次重跑把用户在控制台改的列刷回脚本默认值；</item>
/// <item>按主键补行（<c>insert ignore</c> / <c>if not exists (… where id = …)</c>）→ 用户删掉的种子行会被插回来；</item>
/// <item>表为空才初始化 → 两种情况都不发生，用户对自己数据的增删改都留得住。</item>
/// </list>
/// 因此这里不再提供「更新列 / 判重列」参数：给出这类开关就等于给「重置用户数据」留了入口。
/// </remarks>
internal sealed record SeedTable(
    string TableName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows);

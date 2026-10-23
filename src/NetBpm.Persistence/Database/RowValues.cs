using System.Globalization;

namespace NetBpm.Persistence;

/// <summary>
/// Dapper 动态行 → CLR 值的读取助手（一个文件一份，避免每个仓储各抄一遍）。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 这里的每个方法都藏着一个<b>契约</b>，复制粘贴是最容易让契约分叉的方式：
/// </para>
/// <list type="bullet">
///   <item><description><c>DBNull</c> 与「列不存在」必须得到相同结果（<c>null</c> / 默认值）；</description></item>
///   <item><description>时间列一律 <see cref="DateTime.SpecifyKind"/> 标成 UTC —— 数据库读回的是
///   <c>Unspecified</c>，不标就会在序列化时被当成本地时间；</description></item>
///   <item><description>数值转换强制 <see cref="CultureInfo.InvariantCulture"/>，否则在某些区域设置下
///   小数分隔符会让 <c>Convert</c> 抛异常或悄悄取整。</description></item>
/// </list>
/// <para>调用方通常 <c>using static NetBpm.Persistence.RowValues;</c>，保持 <c>Text(row, "ID_")</c> 的可读性。</para>
/// </remarks>
internal static class RowValues
{
    /// <summary>可空字符串列。</summary>
    public static string? Text(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value as string : null;

    /// <summary>可空整数列（<c>tinyint</c> / <c>int</c> 均适用）。</summary>
    public static int? Number(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull and not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>非空时间列：缺列时回退 <see cref="DateTime.MinValue"/>（区别于「有时间但为 NULL」）。</summary>
    public static DateTime Date(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull and not null
            ? DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Utc)
            : DateTime.MinValue;

    /// <summary>可空时间列。</summary>
    public static DateTime? NullableDate(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull and not null
            ? DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Utc)
            : null;

    /// <summary>可空长整数列（对应 <c>bigint</c>）。</summary>
    public static long? Long(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull and not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>可空浮点列（对应 <c>double</c>）。</summary>
    public static double? Double(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull and not null
            ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>可空二进制列。</summary>
    public static byte[]? Bytes(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value as byte[] : null;

    /// <summary>原始值（<c>DBNull</c> 归一为 <see langword="null"/>）。</summary>
    public static object? Raw(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value : null;

    /// <summary><c>TINYINT</c> / <c>bit</c> 标志位：非零即真（列缺失按 false）。</summary>
    /// <remarks>
    /// 显式走数值转换而不是让 Dapper 直接映射 <c>bool</c>：SQL Server 的 <c>bit</c>、
    /// MySQL 的 <c>TINYINT</c> 被驱动返回的类型在两种方言下不同（<c>bool</c> / <c>sbyte</c> / <c>byte</c>），
    /// 统一转成数值再判非零最稳。
    /// </remarks>
    public static bool Flag(IDictionary<string, object?> row, string column) => (Number(row, column) ?? 0) != 0;
}

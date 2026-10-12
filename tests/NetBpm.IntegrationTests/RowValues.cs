using System.Globalization;
using Dapper;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 真库断言的取值工具。
/// <para>
/// 刻意走 Dapper 的<b>非泛型</b>查询 + 手工按列名取值：各驱动对 <c>TINYINT</c> / <c>bit</c> /
/// <c>datetime(3)</c> 的返回类型不一致，手工转换最稳，断言的也是「库里真实的值」。
/// </para>
/// </summary>
internal static class RowValues
{
    /// <summary>查询并返回按列名索引的行集合。</summary>
    public static List<Dictionary<string, object?>> Query(
        IDbSession session,
        string sql,
        object? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var rows = session.Connection.Query(sql, parameters, session.Transaction);
        var result = new List<Dictionary<string, object?>>();

        foreach (IDictionary<string, object> row in rows)
        {
            result.Add(Of(row));
        }

        return result;
    }

    public static Dictionary<string, object?> Of(object row) =>
        ((IDictionary<string, object>)row).ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value,
            StringComparer.Ordinal);

    public static object? Raw(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value : null;

    public static string? Text(Dictionary<string, object?> row, string column) =>
        Raw(row, column) as string ?? Raw(row, column)?.ToString();

    public static bool IsNull(Dictionary<string, object?> row, string column) => Raw(row, column) is null;

    public static int Int(Dictionary<string, object?> row, string column) =>
        Raw(row, column) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    public static long? Long(Dictionary<string, object?> row, string column) =>
        Raw(row, column) is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;

    public static double? Double(Dictionary<string, object?> row, string column) =>
        Raw(row, column) is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : null;

    public static DateTime? Time(Dictionary<string, object?> row, string column) =>
        Raw(row, column) as DateTime? ?? (Raw(row, column) is { } value
            ? Convert.ToDateTime(value, CultureInfo.InvariantCulture)
            : null);

    /// <summary>取指定列的值（断言集合成员用）。</summary>
    public static List<string?> Column(IEnumerable<Dictionary<string, object?>> rows, string column) =>
        [.. rows.Select(row => Text(row, column))];
}

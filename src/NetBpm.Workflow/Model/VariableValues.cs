using System.Globalization;

namespace NetBpm.Workflow;

/// <summary>
/// 流程变量的宽容读取。
/// </summary>
/// <remarks>
/// 变量在引擎里可能落成 <c>long</c> / <c>string</c> / <c>double</c> 等（取决于提交时是数字还是字符串，
/// 以及走的是内存宿主还是数据库宿主 —— 数据库宿主经 <c>ACT_RU_VARIABLE</c> 往返后类型会归一）。
/// 业务侧不应该假设某一种具体类型，否则换个宿主就崩。
/// </remarks>
public static class VariableValues
{
    /// <summary>按整数读取，读不出返回 0。</summary>
    public static int AsInt(object? value) => value switch
    {
        null => 0,
        int number => number,
        long number => (int)number,
        short number => number,
        byte number => number,
        double number => (int)number,
        float number => (int)number,
        decimal number => (int)number,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    /// <summary>按字符串读取，读不出返回 <see langword="null"/>。</summary>
    public static string? AsString(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// 按时间读取，读不出返回 <see langword="null"/>。
    /// <para>
    /// 兼容三种落形：内存宿主里的 <see cref="DateTime"/>、经 JSON / 数据库往返后的字符串、
    /// 以及带偏移量的 <see cref="DateTimeOffset"/>。统一归一成 <b>UTC</b>。
    /// </para>
    /// </summary>
    public static DateTime? AsDateTime(object? value) => value switch
    {
        null => null,
        DateTime moment => moment.Kind == DateTimeKind.Utc ? moment : moment.ToUniversalTime(),
        DateTimeOffset offset => offset.UtcDateTime,
        string text when DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// 把变量字典整体转成字符串字典（键序与来源一致）。
    /// <para>
    /// 用于「把这次提交的表单值原样记进审批记录」：记录是给人看的，
    /// 统一成文本比保留 <c>long</c> / <c>DateTime</c> 混排更好读，也更好落库。
    /// </para>
    /// </summary>
    /// <param name="variables">变量快照。</param>
    /// <returns>键 = 变量名，值 = 字符串形式（原值为 <see langword="null"/> 时保留 <see langword="null"/>）。</returns>
    public static Dictionary<string, string?> ToText(IReadOnlyDictionary<string, object?>? variables)
    {
        var text = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (variables is null)
        {
            return text;
        }

        foreach (var (name, value) in variables)
        {
            text[name] = AsString(value);
        }

        return text;
    }
}

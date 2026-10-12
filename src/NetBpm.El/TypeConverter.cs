using System.Globalization;
using System.Numerics;

namespace NetBpm.El;

/// <summary>
/// 类型转换。**逐条照搬** JUEL 的 <c>misc.TypeConverterImpl</c>（EL 2.1 规范 1.17 节）。
/// <para>
/// 语义要点（都是踩过坑的地方）：
/// <list type="bullet">
///   <item><description><c>toBoolean</c>：<c>null</c> 与空串 → <b>false</b>；<c>Boolean</c> → 自身；
///   <c>String</c> → 仅忽略大小写等于 <c>"true"</c> 为真；<b>其它类型一律抛异常</b>（不做"非空即真"）；</description></item>
///   <item><description><c>toString</c>：<c>null</c> → <b>空串</b>；<c>Double(1.0)</c> → <c>"1.0"</c>（不是 <c>"1"</c>）；</description></item>
///   <item><description><c>toLong</c>：<c>null</c>/空串 → <b>0</b>；<c>Boolean</c> → 抛异常；字符串解析失败 → 抛异常；</description></item>
///   <item><description>Java 类型对齐：<c>Long</c>=<c>long</c>、<c>Integer</c>=<c>int</c>、<c>Double</c>=<c>double</c>、
///   <c>Float</c>=<c>float</c>、<c>BigDecimal</c>=<c>decimal</c>、<c>BigInteger</c>=<see cref="BigInteger"/>、
///   <c>Character</c>=<c>char</c>。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class TypeConverter
{
    /// <summary><c>coerceToBoolean</c>：null / "" → false；bool → 自身；string → 仅 "true"（忽略大小写）为真。</summary>
    public static bool CoerceToBoolean(object? value)
    {
        switch (value)
        {
            case null:
                return false;
            case string text when text.Length == 0:
                return false;
            case bool boolean:
                return boolean;
            case string text:
                return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
            default:
                throw new ElException(
                    $"无法把 '{JavaFormat.Describe(value)}'（{value.GetType().Name}）转换为 Boolean（类型不兼容）。");
        }
    }

    /// <summary><c>coerceToString</c>：null → ""；string → 自身；枚举 → 名称；其它 → <c>toString()</c>。</summary>
    public static string CoerceToString(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text,
            Enum enumeration => enumeration.ToString(),
            _ => JavaFormat.ToJavaString(value),
        };
    }

    /// <summary><c>coerceToLong</c>。</summary>
    public static long CoerceToLong(object? value) => value switch
    {
        null => 0L,
        string text when text.Length == 0 => 0L,
        long number => number,
        bool => throw CoerceTypeError(value, "Long"),
        string text => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw CoerceValueError(value, "Long"),
        char character => (short)character,
        sbyte or byte or short or ushort or int or uint or ulong => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        float or double or decimal or BigInteger => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        _ => throw CoerceTypeError(value, "Long"),
    };

    /// <summary><c>coerceToDouble</c>。</summary>
    public static double CoerceToDouble(object? value) => value switch
    {
        null => 0d,
        string text when text.Length == 0 => 0d,
        double number => number,
        float number => number,
        bool => throw CoerceTypeError(value, "Double"),
        string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw CoerceValueError(value, "Double"),
        char character => (short)character,
        _ when value is sbyte or byte or short or ushort or int or uint or long or ulong or decimal or BigInteger =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => throw CoerceTypeError(value, "Double"),
    };

    /// <summary><c>coerceToInteger</c>。</summary>
    public static int CoerceToInteger(object? value) => value switch
    {
        null => 0,
        string text when text.Length == 0 => 0,
        int number => number,
        bool => throw CoerceTypeError(value, "Integer"),
        string text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw CoerceValueError(value, "Integer"),
        char character => (short)character,
        _ when value is sbyte or byte or short or ushort or uint or long or ulong or float or double or decimal or BigInteger =>
            Convert.ToInt32(value, CultureInfo.InvariantCulture),
        _ => throw CoerceTypeError(value, "Integer"),
    };

    /// <summary><c>coerceToBigDecimal</c>（C# 侧用 <see cref="decimal"/> 表示）。</summary>
    public static decimal CoerceToDecimal(object? value) => value switch
    {
        null => 0m,
        string text when text.Length == 0 => 0m,
        decimal number => number,
        BigInteger bigInteger => (decimal)bigInteger,
        bool => throw CoerceTypeError(value, "BigDecimal"),
        string text => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw CoerceValueError(value, "BigDecimal"),
        char character => (short)character,
        _ when value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double =>
            Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        _ => throw CoerceTypeError(value, "BigDecimal"),
    };

    /// <summary><c>coerceToBigInteger</c>。</summary>
    public static BigInteger CoerceToBigInteger(object? value) => value switch
    {
        null => BigInteger.Zero,
        string text when text.Length == 0 => BigInteger.Zero,
        BigInteger bigInteger => bigInteger,
        decimal number => new BigInteger(decimal.Truncate(number)),
        bool => throw CoerceTypeError(value, "BigInteger"),
        string text => BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw CoerceValueError(value, "BigInteger"),
        char character => (short)character,
        _ when value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double =>
            new BigInteger(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
        _ => throw CoerceTypeError(value, "BigInteger"),
    };

    /// <summary>Java 的 <c>Number</c> 家族（含 <c>BigInteger</c>/<c>BigDecimal</c>）。</summary>
    public static bool IsNumber(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or BigInteger;

    /// <summary>Java 的 <c>Byte/Short/Integer/Long</c>（简单整数类型）。</summary>
    public static bool IsSimpleInteger(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong;

    /// <summary>Java 的 <c>Float/Double</c>。</summary>
    public static bool IsSimpleFloat(object? value) => value is float or double;

    private static ElException CoerceTypeError(object? value, string target) =>
        new($"无法把 '{JavaFormat.Describe(value)}'（{value?.GetType().Name ?? "null"}）转换为 {target}（类型不兼容）。");

    private static ElException CoerceValueError(object? value, string target) =>
        new($"无法把 '{JavaFormat.Describe(value)}'（{value?.GetType().Name ?? "null"}）转换为 {target}（值不兼容）。");
}

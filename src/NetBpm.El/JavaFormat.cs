using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace NetBpm.El;

/// <summary>
/// Java 风格的 <c>toString</c>。EL 的复合表达式拼接与 <c>+</c> 的报错消息都依赖它，
/// 而 C# 的默认格式与 Java 有几处**会改变结果**的差异，必须显式对齐：
/// <list type="bullet">
///   <item><description><c>Boolean</c>：Java 是 <c>"true"/"false"</c>，C# 默认是 <c>"True"/"False"</c>；</description></item>
///   <item><description><c>Double</c>：Java 的 <c>Double.toString(1.0)</c> 是 <c>"1.0"</c>，C# 默认是 <c>"1"</c>；
///   大/小值 Java 用 <c>1.0E20</c> 形式，C# 用 <c>1E+20</c>；</description></item>
///   <item><description><c>Float</c>：同 Double，但要按 <b>float</b> 的最短往返表示。</description></item>
/// </list>
/// <para>
/// ⚠️ 已登记的偏差：<c>DateTime</c> 无法完全复刻 Java <c>Date.toString()</c>（Java 打印时区缩写如 <c>CST</c>，
/// 而 .NET 不暴露该缩写），本实现打印 <c>GMT+08:00</c> 形式。它只影响"把日期拼进字符串"的表达式，
/// 不影响任何条件判定。
/// </para>
/// </summary>
internal static class JavaFormat
{
    /// <summary>Java 的 <c>Object.toString()</c>（用于拼接与报错消息）。</summary>
    public static string ToJavaString(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            bool boolean => boolean ? "true" : "false",
            string text => text,
            char character => character.ToString(),
            double number => DoubleToString(number),
            float number => FloatToString(number),
            decimal number => DecimalToString(number),
            BigInteger bigInteger => bigInteger.ToString(CultureInfo.InvariantCulture),
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
            DateTime dateTime => DateTimeToString(dateTime),
            IDictionary dictionary => DictionaryToString(dictionary),
            IEnumerable sequence => SequenceToString(sequence),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Java <c>Collection.toString()</c> 的形态：<c>[a, b]</c>。
    /// <para>
    /// ⚠️ 用 <see cref="Describe"/>（即 <c>String.valueOf</c>）而不是 EL 的 <c>coerceToString</c>：
    /// Java 的集合拼接里 <c>null</c> 元素打印成 <c>null</c>，而 EL 的強转会把 <c>null</c> 变成空串。
    /// </para>
    /// </summary>
    private static string SequenceToString(IEnumerable sequence)
    {
        var builder = new StringBuilder("[");

        var first = true;

        foreach (var item in sequence)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(item is null ? "null" : ToJavaString(item));
            first = false;
        }

        return builder.Append(']').ToString();
    }

    /// <summary>Java <c>Map.toString()</c> 的形态：<c>{k=v, k2=v2}</c>。</summary>
    private static string DictionaryToString(IDictionary dictionary)
    {
        var builder = new StringBuilder("{");

        var first = true;

        foreach (DictionaryEntry entry in dictionary)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(entry.Key is null ? "null" : ToJavaString(entry.Key))
                .Append('=')
                .Append(entry.Value is null ? "null" : ToJavaString(entry.Value));

            first = false;
        }

        return builder.Append('}').ToString();
    }

    /// <summary>报错消息里的值描述（Java 里就是 <c>value.toString()</c>，<c>null</c> 打印 <c>null</c>）。</summary>
    public static string Describe(object? value) => value is null ? "null" : ToJavaString(value);

    /// <summary>Java 的 <c>Double.toString(double)</c>。</summary>
    public static string DoubleToString(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0d)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        return FormatFloating(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Java 的 <c>Float.toString(float)</c>（按 float 的最短往返表示）。</summary>
    public static string FloatToString(float value)
    {
        if (float.IsNaN(value))
        {
            return "NaN";
        }

        if (float.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (float.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0f)
        {
            return float.IsNegative(value) ? "-0.0" : "0.0";
        }

        return FormatFloating(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Java 的 <c>BigDecimal.toString()</c>（<c>decimal</c> 侧近似）。</summary>
    public static string DecimalToString(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 把 Java <c>Date.toString()</c> 的形态近似出来：<c>EEE MMM dd HH:mm:ss GMT±hh:mm yyyy</c>
    /// （Java 用本地时区，我们保留本地时区，只有"缩写 vs 偏移"这一点不同，见类注释）。
    /// </summary>
    private static string DateTimeToString(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var zone = $"GMT{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";

        return string.Concat(
            local.ToString("ddd MMM dd HH:mm:ss ", CultureInfo.InvariantCulture),
            zone,
            " ",
            local.ToString("yyyy", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 把 .NET 的往返格式（<c>"1"</c> / <c>"1.5"</c> / <c>"1E+20"</c> / <c>"1E-05"</c>）整理成 Java 的形态：
    /// 常规区间用普通小数（<c>1.0</c>、<c>1.5</c>），其余用科学计数（<c>1.0E20</c>）。
    /// </summary>
    private static string FormatFloating(string text)
    {
        var negative = text.StartsWith('-');
        var body = negative ? text[1..] : text;

        var exponent = 0;
        var exponentIndex = body.IndexOfAny(['E', 'e']);

        if (exponentIndex >= 0)
        {
            exponent = int.Parse(body[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
            body = body[..exponentIndex];
        }

        var pointIndex = body.IndexOf('.');
        var integerPart = pointIndex < 0 ? body : body[..pointIndex];
        var fractionPart = pointIndex < 0 ? string.Empty : body[(pointIndex + 1)..];

        var digits = (integerPart + fractionPart).TrimStart('0');
        var leadingZeros = (integerPart + fractionPart).Length - digits.Length;
        digits = digits.TrimEnd('0');

        if (digits.Length == 0)
        {
            return negative ? "-0.0" : "0.0";
        }

        // value = 0.digits × 10^decExp
        var decExp = integerPart.Length - leadingZeros + exponent;

        var builder = new StringBuilder();

        if (negative)
        {
            builder.Append('-');
        }

        // Java：10^-3 <= |v| < 10^7 用普通小数，其余科学计数
        if (decExp is > -3 and <= 7)
        {
            if (decExp <= 0)
            {
                builder.Append("0.").Append('0', -decExp).Append(digits);
            }
            else if (decExp >= digits.Length)
            {
                builder.Append(digits).Append('0', decExp - digits.Length).Append(".0");
            }
            else
            {
                builder.Append(digits, 0, decExp).Append('.').Append(digits[decExp..]);
            }

            return builder.ToString();
        }

        builder.Append(digits[0]).Append('.').Append(digits.Length > 1 ? digits[1..] : "0")
            .Append('E').Append((decExp - 1).ToString(CultureInfo.InvariantCulture));

        return builder.ToString();
    }
}

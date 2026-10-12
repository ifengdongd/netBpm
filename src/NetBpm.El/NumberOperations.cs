using System.Globalization;
using System.Numerics;

namespace NetBpm.El;

/// <summary>
/// 算术运算。**逐条照搬** JUEL 的 <c>misc.NumberOperations</c>（EL 规范 1.7 节）。
/// <para>
/// 🔑 四个与直觉相反、且会直接改变流程走向的语义：
/// <list type="number">
///   <item><description><b><c>+</c> 从不拼接字符串</b>：<c>"1" + "2"</c> 是 <b>3</b>，<c>"a" + 1</c> <b>抛异常</b>；</description></item>
///   <item><description><b><c>/</c> 恒为浮点除法</b>：<c>5 / 2</c> 是 <b>2.5</b>（没有整数除法）；除以 0 得
///   <c>Infinity</c>/<c>NaN</c>，<b>不抛异常</b>（只有 BigDecimal/BigInteger 参与时才抛）；</description></item>
///   <item><description><c>%</c> 的符号<b>跟随被除数</b>（Java 语义）；</description></item>
///   <item><description>单边 <c>null</c> 参与 <c>+ - * </c> 时按 <b>0</b> 处理（<c>null + 5</c> 是 5），
///   但 <c>true + 1</c> 抛异常。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class NumberOperations
{
    /// <summary><c>+</c>。</summary>
    public static object Add(object? o1, object? o2)
    {
        if (o1 is null && o2 is null)
        {
            return 0L;
        }

        if (o1 is decimal || o2 is decimal)
        {
            return TypeConverter.CoerceToDecimal(o1) + TypeConverter.CoerceToDecimal(o2);
        }

        if (IsFloatOrDoubleOrDotEe(o1) || IsFloatOrDoubleOrDotEe(o2))
        {
            return o1 is BigInteger || o2 is BigInteger
                ? TypeConverter.CoerceToDecimal(o1) + TypeConverter.CoerceToDecimal(o2)
                : TypeConverter.CoerceToDouble(o1) + TypeConverter.CoerceToDouble(o2);
        }

        if (o1 is BigInteger || o2 is BigInteger)
        {
            return TypeConverter.CoerceToBigInteger(o1) + TypeConverter.CoerceToBigInteger(o2);
        }

        return TypeConverter.CoerceToLong(o1) + TypeConverter.CoerceToLong(o2);
    }

    /// <summary><c>-</c>。</summary>
    public static object Sub(object? o1, object? o2)
    {
        if (o1 is null && o2 is null)
        {
            return 0L;
        }

        if (o1 is decimal || o2 is decimal)
        {
            return TypeConverter.CoerceToDecimal(o1) - TypeConverter.CoerceToDecimal(o2);
        }

        if (IsFloatOrDoubleOrDotEe(o1) || IsFloatOrDoubleOrDotEe(o2))
        {
            return o1 is BigInteger || o2 is BigInteger
                ? TypeConverter.CoerceToDecimal(o1) - TypeConverter.CoerceToDecimal(o2)
                : TypeConverter.CoerceToDouble(o1) - TypeConverter.CoerceToDouble(o2);
        }

        if (o1 is BigInteger || o2 is BigInteger)
        {
            return TypeConverter.CoerceToBigInteger(o1) - TypeConverter.CoerceToBigInteger(o2);
        }

        return TypeConverter.CoerceToLong(o1) - TypeConverter.CoerceToLong(o2);
    }

    /// <summary><c>*</c>。</summary>
    public static object Mul(object? o1, object? o2)
    {
        if (o1 is null && o2 is null)
        {
            return 0L;
        }

        if (o1 is decimal || o2 is decimal)
        {
            return TypeConverter.CoerceToDecimal(o1) * TypeConverter.CoerceToDecimal(o2);
        }

        if (IsFloatOrDoubleOrDotEe(o1) || IsFloatOrDoubleOrDotEe(o2))
        {
            return o1 is BigInteger || o2 is BigInteger
                ? TypeConverter.CoerceToDecimal(o1) * TypeConverter.CoerceToDecimal(o2)
                : TypeConverter.CoerceToDouble(o1) * TypeConverter.CoerceToDouble(o2);
        }

        if (o1 is BigInteger || o2 is BigInteger)
        {
            return TypeConverter.CoerceToBigInteger(o1) * TypeConverter.CoerceToBigInteger(o2);
        }

        return TypeConverter.CoerceToLong(o1) * TypeConverter.CoerceToLong(o2);
    }

    /// <summary><c>/</c>（<c>div</c>）。⚠️ 恒为浮点除法；只有 BigDecimal/BigInteger 参与才走"定点 + HALF_UP"。</summary>
    public static object Div(object? o1, object? o2)
    {
        if (o1 is null && o2 is null)
        {
            return 0L;
        }

        if (o1 is decimal or BigInteger || o2 is decimal or BigInteger)
        {
            var dividend = TypeConverter.CoerceToDecimal(o1);
            var divisor = TypeConverter.CoerceToDecimal(o2);

            if (divisor == 0m)
            {
                throw new ElException("BigDecimal 除法遇到除数为 0。");
            }

            // Java：divide(divisor, ROUND_HALF_UP) ⇒ 结果 scale = 被除数的 scale
            return decimal.Round(dividend / divisor, GetScale(dividend), MidpointRounding.AwayFromZero);
        }

        return TypeConverter.CoerceToDouble(o1) / TypeConverter.CoerceToDouble(o2);
    }

    /// <summary><c>%</c>（<c>mod</c>）。⚠️ BigDecimal 与 Float/Double 同路（走 double），符号跟随被除数。</summary>
    public static object Mod(object? o1, object? o2)
    {
        if (o1 is null && o2 is null)
        {
            return 0L;
        }

        if (o1 is decimal or float or double || o2 is decimal or float or double
            || IsDotEe(o1) || IsDotEe(o2))
        {
            return TypeConverter.CoerceToDouble(o1) % TypeConverter.CoerceToDouble(o2);
        }

        if (o1 is BigInteger || o2 is BigInteger)
        {
            return TypeConverter.CoerceToBigInteger(o1) % TypeConverter.CoerceToBigInteger(o2);
        }

        return TypeConverter.CoerceToLong(o1) % TypeConverter.CoerceToLong(o2);
    }

    /// <summary>一元 <c>-</c>。整数类型取负后<b>保持原类型</b>（Java 语义）。</summary>
    public static object Neg(object? value) => value switch
    {
        null => 0L,
        decimal number => -number,
        BigInteger bigInteger => -bigInteger,
        double number => -number,
        float number => -number,
        string text when IsDotEe(text) => -TypeConverter.CoerceToDouble(text),
        string text => -TypeConverter.CoerceToLong(text),
        long number => -number,
        int number => -number,
        short number => (short)-number,
        sbyte number => (sbyte)-number,
        byte number => (byte)-number,
        _ => throw new ElException($"无法对 '{JavaFormat.Describe(value)}'（{value.GetType().Name}）取负。"),
    };

    /// <summary>Java 的 <c>Float/Double</c>，或"含 <c>.</c>/<c>E</c>/<c>e</c> 的字符串"（JUEL 的 <c>isDotEe</c>）。</summary>
    private static bool IsFloatOrDoubleOrDotEe(object? value) =>
        TypeConverter.IsSimpleFloat(value) || IsDotEe(value);

    private static bool IsDotEe(object? value) => value is string text && IsDotEe(text);

    private static bool IsDotEe(string value)
    {
        foreach (var character in value)
        {
            if (character is '.' or 'E' or 'e')
            {
                return true;
            }
        }

        return false;
    }

    private static int GetScale(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;
}

using System.Collections;
using System.Numerics;

namespace NetBpm.El;

/// <summary>
/// 相等、比较与 <c>empty</c>。**逐条照搬** JUEL 的 <c>misc.BooleanOperations</c>。
/// <para>
/// 🔑 三个最容易写错、且会直接导致走错分支的语义：
/// <list type="number">
///   <item><description>比较运算符遇到 <c>null</c> <b>不抛异常</b>，一律返回 <b>false</b>
///   （<c>null &lt; 1</c>、<c>null &gt; 1</c>、<c>null &lt;= 1</c> 全是 false）；只有两个都非 null 且不可比，才抛异常；</description></item>
///   <item><description><c>"abc" == 1</c>、<c>"1.0" == 1</c> <b>不是 false，而是抛异常</b> ——
///   命中整数分支后按 <c>Long.valueOf</c> 解析字符串，失败即报错；</description></item>
///   <item><description>同一对象（含 <c>null == null</c>）直接为 <b>true</b>；<c>null</c> 与任何非 null 比是 false。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class BooleanOperations
{
    /// <summary><c>==</c> / <c>eq</c>。</summary>
    public static bool Eq(object? o1, object? o2)
    {
        if (ReferenceEquals(o1, o2))
        {
            return true;
        }

        if (o1 is null || o2 is null)
        {
            return false;
        }

        if (o1 is decimal || o2 is decimal)
        {
            return TypeConverter.CoerceToDecimal(o1) == TypeConverter.CoerceToDecimal(o2);
        }

        if (TypeConverter.IsSimpleFloat(o1) || TypeConverter.IsSimpleFloat(o2))
        {
            return TypeConverter.CoerceToDouble(o1).Equals(TypeConverter.CoerceToDouble(o2));
        }

        if (o1 is BigInteger || o2 is BigInteger)
        {
            return TypeConverter.CoerceToBigInteger(o1) == TypeConverter.CoerceToBigInteger(o2);
        }

        // ⚠️ 这一支在 String 分支之前：所以 "1" == 1 会走 Long 解析
        if (TypeConverter.IsSimpleInteger(o1) || TypeConverter.IsSimpleInteger(o2))
        {
            return TypeConverter.CoerceToLong(o1) == TypeConverter.CoerceToLong(o2);
        }

        if (o1 is bool || o2 is bool)
        {
            return TypeConverter.CoerceToBoolean(o1) == TypeConverter.CoerceToBoolean(o2);
        }

        if (o1 is Enum || o2 is Enum)
        {
            return string.Equals(TypeConverter.CoerceToString(o1), TypeConverter.CoerceToString(o2), StringComparison.Ordinal);
        }

        if (o1 is string || o2 is string)
        {
            return string.Equals(TypeConverter.CoerceToString(o1), TypeConverter.CoerceToString(o2), StringComparison.Ordinal);
        }

        return o1.Equals(o2);
    }

    /// <summary><c>!=</c> / <c>ne</c>。</summary>
    public static bool Ne(object? o1, object? o2) => !Eq(o1, o2);

    /// <summary><c>&lt;</c> / <c>lt</c>。</summary>
    public static bool Lt(object? o1, object? o2) =>
        !ReferenceEquals(o1, o2) && o1 is not null && o2 is not null && Compare(o1, o2) < 0;

    /// <summary><c>&gt;</c> / <c>gt</c>。</summary>
    public static bool Gt(object? o1, object? o2) =>
        !ReferenceEquals(o1, o2) && o1 is not null && o2 is not null && Compare(o1, o2) > 0;

    /// <summary><c>&lt;=</c> / <c>le</c>。</summary>
    public static bool Le(object? o1, object? o2) =>
        ReferenceEquals(o1, o2) || (o1 is not null && o2 is not null && Compare(o1, o2) <= 0);

    /// <summary><c>&gt;=</c> / <c>ge</c>。</summary>
    public static bool Ge(object? o1, object? o2) =>
        ReferenceEquals(o1, o2) || (o1 is not null && o2 is not null && Compare(o1, o2) >= 0);

    /// <summary><c>empty</c>：null、空串、空数组、空 Map、空集合为真。</summary>
    public static bool Empty(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        object?[] array => array.Length == 0,
        IDictionary dictionary => dictionary.Count == 0,
        ICollection collection => collection.Count == 0,
        _ => false,
    };

    /// <summary>
    /// 比较（JUEL 的 <c>lt0</c>/<c>gt0</c>）。数值提升顺序<b>严格</b>为：
    /// BigDecimal → Float/Double → BigInteger → Byte/Short/Integer/Long → String → Comparable → 抛异常。
    /// </summary>
    private static int Compare(object o1, object o2)
    {
        if (o1 is decimal || o2 is decimal)
        {
            return TypeConverter.CoerceToDecimal(o1).CompareTo(TypeConverter.CoerceToDecimal(o2));
        }

        if (TypeConverter.IsSimpleFloat(o1) || TypeConverter.IsSimpleFloat(o2))
        {
            return TypeConverter.CoerceToDouble(o1).CompareTo(TypeConverter.CoerceToDouble(o2));
        }

        if (o1 is BigInteger || o2 is BigInteger)
        {
            return TypeConverter.CoerceToBigInteger(o1).CompareTo(TypeConverter.CoerceToBigInteger(o2));
        }

        if (TypeConverter.IsSimpleInteger(o1) || TypeConverter.IsSimpleInteger(o2))
        {
            return TypeConverter.CoerceToLong(o1).CompareTo(TypeConverter.CoerceToLong(o2));
        }

        if (o1 is string || o2 is string)
        {
            // Java 的 String.compareTo 是 UTF-16 码元序，等价于 Ordinal
            return string.CompareOrdinal(TypeConverter.CoerceToString(o1), TypeConverter.CoerceToString(o2));
        }

        if (o1 is IComparable comparable)
        {
            try
            {
                return comparable.CompareTo(o2);
            }
            catch (Exception exception)
            {
                throw new ElException($"无法比较 '{JavaFormat.Describe(o1)}' 与 '{JavaFormat.Describe(o2)}'。", exception);
            }
        }

        if (o2 is IComparable reverse)
        {
            try
            {
                return -reverse.CompareTo(o1);
            }
            catch (Exception exception)
            {
                throw new ElException($"无法比较 '{JavaFormat.Describe(o1)}' 与 '{JavaFormat.Describe(o2)}'。", exception);
            }
        }

        throw new ElException($"无法比较 '{JavaFormat.Describe(o1)}' 与 '{JavaFormat.Describe(o2)}'。");
    }
}

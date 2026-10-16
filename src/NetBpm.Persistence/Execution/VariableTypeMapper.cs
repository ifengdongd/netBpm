using System.Globalization;
using System.Text;
using NetBpm.Engine;

namespace NetBpm.Persistence.Execution;

/// <summary>变量值在 <c>ACT_RU_VARIABLE</c> / <c>ACT_HI_VARINST</c> 中的列布局。</summary>
/// <param name="TypeName"><c>TYPE_</c> / <c>VAR_TYPE_</c>。</param>
/// <param name="LongValue"><c>LONG_</c>。</param>
/// <param name="DoubleValue"><c>DOUBLE_</c>。</param>
/// <param name="TextValue"><c>TEXT_</c>。</param>
/// <param name="TextValue2"><c>TEXT2_</c>（普通类型恒为 null，见类型说明）。</param>
/// <param name="Bytes">超长值（longString）写入 <c>ACT_GE_BYTEARRAY.BYTES_</c> 的字节。</param>
internal readonly record struct VariableColumns(
    string TypeName,
    long? LongValue,
    double? DoubleValue,
    string? TextValue,
    string? TextValue2,
    byte[]? Bytes = null);

/// <summary>
/// 变量类型 ↔ CLR 类型 的映射。逐条对应 Activiti 的 <c>VariableType</c> 实现
/// （<c>org.activiti.engine.impl.variable.*</c>）。
/// <para>
/// 🔑 <b>已核实的类型名与列布局</b>：
/// <list type="bullet">
///   <item><description><c>String</c> → <c>string</c>，写 <c>TEXT_</c>；</description></item>
///   <item><description><c>Integer</c> → <c>integer</c>，写 <c>LONG_</c> <b>和</b> <c>TEXT_</c>（数字的字符串副本）；</description></item>
///   <item><description><c>Long</c> → <c>long</c>，写 <c>LONG_</c> <b>和</b> <c>TEXT_</c>；</description></item>
///   <item><description><c>Double</c> → <c>double</c>，只写 <c>DOUBLE_</c>（不写 <c>TEXT_</c>）；</description></item>
///   <item><description><c>Boolean</c> → <c>boolean</c>，只写 <c>LONG_</c>（true=1 / false=0）；</description></item>
///   <item><description><c>Date</c> → <c>date</c>，只写 <c>LONG_</c>（<b>UTC 毫秒</b>，即 Java <c>Date.getTime()</c>）；</description></item>
///   <item><description><c>null</c> → <c>null</c>，所有值列均为 null。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <c>TEXT2_</c> <b>不是</b> 4000 字符截断列，而是「Java 类全名」辅助列（<c>json</c>/<c>longJson</c> 用），
/// 本子集的类型全部为 null。
/// </para>
/// <para>
/// ⚠️ <b>已登记的限制</b>：<c>bytes</c> / <c>serializable</c> / <c>json</c> 等类型未支持
/// （需要 Java 序列化布局，跨语言不兼容）。
/// 超长字符串（&gt; <see cref="MaxStringLength"/>）已支持：降级为 <c>longString</c>，
/// UTF-8 字节写入 <c>ACT_GE_BYTEARRAY</c>（与 Activiti 的 <c>LongStringType</c> 同布局）。
/// </para>
/// </summary>
internal static class VariableTypeMapper
{
    private const int MaxStringLength = 4000;

    /// <summary>Activiti 各变量类型名。</summary>
    internal static class TypeNames
    {
        public const string Null = "null";
        public const string String = "string";
        public const string LongString = "longString";
        public const string Integer = "integer";
        public const string Long = "long";
        public const string Double = "double";
        public const string Boolean = "boolean";
        public const string Date = "date";
    }

    /// <summary>按注册顺序（null → string → …）解析第一个可存储的类型，并算出各值列。</summary>
    public static VariableColumns ToColumns(object? value)
    {
        switch (value)
        {
            case null:
                return new VariableColumns(TypeNames.Null, null, null, null, null);

            case string text when text.Length > MaxStringLength:
                // 对应 Activiti 的 LongStringType：超长字符串降级为 longString，
                // UTF-8 字节写入 ACT_GE_BYTEARRAY（BYTEARRAY_ID_ 由仓储层补齐）
                return new VariableColumns(
                    TypeNames.LongString,
                    null,
                    null,
                    null,
                    null,
                    Encoding.UTF8.GetBytes(text));

            case string text:
                return new VariableColumns(TypeNames.String, null, null, text, null);

            case int intValue:
                return new VariableColumns(
                    TypeNames.Integer,
                    intValue,
                    null,
                    intValue.ToString(CultureInfo.InvariantCulture),
                    null);

            case long longValue:
                return new VariableColumns(
                    TypeNames.Long,
                    longValue,
                    null,
                    longValue.ToString(CultureInfo.InvariantCulture),
                    null);

            case double doubleValue:
                return new VariableColumns(TypeNames.Double, null, doubleValue, null, null);

            case bool boolValue:
                return new VariableColumns(TypeNames.Boolean, boolValue ? 1 : 0, null, null, null);

            case DateTime dateTime:
                return new VariableColumns(TypeNames.Date, ToUnixMilliseconds(dateTime), null, null, null);

            default:
                throw new EngineNotImplementedException(
                    $"变量类型 '{value.GetType().FullName}'",
                    "P5 后续",
                    "本子集只支持 string / longString / int / long / double / bool / DateTime / null。"
                    + "其余类型在 Activiti 走 bytes / serializable / json 等类型，需要 Java 序列化布局参与。");
        }
    }

    /// <summary>把 <c>TYPE_</c> 与各值列解码回 CLR 值。</summary>
    public static object? FromColumns(
        string? typeName,
        long? longValue,
        double? doubleValue,
        string? textValue,
        byte[]? bytes = null) =>
        typeName switch
        {
            null or TypeNames.Null => null,
            TypeNames.String => textValue,
            TypeNames.LongString => bytes is { } raw ? Encoding.UTF8.GetString(raw) : null,
            TypeNames.Integer => longValue is { } integer ? (int)integer : null,
            TypeNames.Long => longValue,
            TypeNames.Double => doubleValue,
            TypeNames.Boolean => longValue is { } flag && flag != 0,
            TypeNames.Date => longValue is { } ticks
                ? DateTimeOffset.FromUnixTimeMilliseconds(ticks).UtcDateTime
                : null,

            _ => throw new EngineException(
                $"未知的变量类型 '{typeName}'。本子集只支持 "
                + $"{TypeNames.Null} / {TypeNames.String} / {TypeNames.LongString} / {TypeNames.Integer} / {TypeNames.Long} / "
                + $"{TypeNames.Double} / {TypeNames.Boolean} / {TypeNames.Date}。"),
        };

    /// <summary>
    /// <see cref="DateTime"/> → Java <c>Date.getTime()</c> 的毫秒数。
    /// <para><c>Kind</c> 为 <see cref="DateTimeKind.Utc"/> 时直接取；否则按本地时区解释（与 JVM 的默认行为一致）。</para>
    /// </summary>
    private static long ToUnixMilliseconds(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(dateTime).ToUnixTimeMilliseconds()
            : new DateTimeOffset(dateTime.ToUniversalTime()).ToUnixTimeMilliseconds();
}

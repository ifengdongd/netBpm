using System.Globalization;
using System.Text.Json;
using NetBpm.Engine;

namespace NetBpm.Rest;

/// <summary>
/// JSON 变量 → CLR 基本类型 的转换（REST 边界的必需适配）。
/// <para>
/// 🔑 <b>为什么必须有它</b>：System.Text.Json 把 <c>object?</c> 反序列化成 <see cref="JsonElement"/>，
/// 直接交给引擎会被变量类型映射拒绝（<c>JsonElement</c> 不是受支持的变量类型），
/// 表现为「带变量的启动/完成任务一律 400」。这里在边界把 JSON 值还原成
/// <c>string / long / double / bool / DateTime / null</c>，与 <c>ACT_RU_VARIABLE</c> 的列布局对齐。
/// </para>
/// <para>
/// 数值一律先尝试 <see cref="long"/>（对应 Activiti 的 <c>integer</c>/<c>long</c>），
/// 失败才用 <see cref="double"/>（<c>double</c>）—— 这样 <c>{"days": 3}</c> 落成 <c>LONG_ = 3</c>，
/// 而不是 <c>DOUBLE_ = 3.0</c>。
/// </para>
/// <para>
/// <b>类型保真写法</b>：需要写入 <c>date</c> / <c>integer</c> 等显式类型时，把值写成
/// <c>{"type": "date", "value": "2026-10-19T00:00:00Z"}</c>。支持的类型：
/// <c>string / integer / long / double / boolean / date</c>（大小写不敏感）。
/// 裸标量继续按上面的推断规则映射。
/// </para>
/// <para>
/// <b>null 语义（统一）</b>：<see langword="null"/> 不是可写入的变量值，所有入口
/// （启动 / 完成任务 / 批量写 / 单写）一律忽略 null 值，单写端点返回 400。
/// 这与「删除变量用 DELETE」一致，避免用 null 值制造空变量行。
/// </para>
/// </summary>
internal static class JsonVariableReader
{
    /// <summary>把变量字典里的 JSON 值还原成 CLR 值；全部为 <c>null</c> 时返回 <see langword="null"/>。</summary>
    public static Dictionary<string, object?>? Read(IDictionary<string, JsonElement>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(variables.Count, StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            result[name] = ReadValue(name, value);
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// 还原<b>单个</b>变量值（指令式 modify 的 setVariable 用）；<c>null</c> / 未定义返回 <see langword="null"/>。
    /// </summary>
    public static object? ReadScalar(string name, JsonElement? element) =>
        element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : ReadValue(name, value);

    private static object? ReadValue(string name, JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),

        // ⚠️ 不能写成 `TryGetInt64(out var i) ? i : element.GetDouble()`：
        // 条件表达式的两个分支是 long / double，编译器会把公共类型推成 double，
        // 装箱后整数变量会退化成 double（落库 VAR_TYPE_ = 'double'）。
        // 必须用语句分支，保证 long 分支的装箱类型仍是 long。
        JsonValueKind.Number => ReadNumber(element),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => ReadTyped(name, element),

        _ => throw new EngineException(
            $"变量 '{name}' 的 JSON 类型（{element.ValueKind}）不受支持：流程变量只支持标量"
            + "（string / number / boolean）或 {\"type\":...,\"value\":...} 的显式类型写法。"),
    };

    /// <summary>
    /// 解析 <c>{"type": "...", "value": ...}</c> 的显式类型写法。
    /// <para>用于裸标量无法表达的类型（尤其是 <c>date</c> —— 裸字符串会落成 <c>string</c>）。</para>
    /// </summary>
    private static object ReadTyped(string name, JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("value", out var valueElement))
        {
            throw new EngineException(
                $"变量 '{name}' 的对象值必须写成 {{\"type\":\"date\",\"value\":\"...\"}}（type / value 均必填）；"
                + "流程变量不支持数组与普通对象。");
        }

        var type = typeElement.GetString()?.Trim().ToLowerInvariant();

        if (valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new EngineException($"变量 '{name}' 的显式类型值不能为 null；删除变量请用 DELETE。");
        }

        return type switch
        {
            "string" => ReadString(name, valueElement),
            "integer" or "int" => ReadInt(name, valueElement),
            "long" => ReadLong(name, valueElement),
            "double" or "number" => ReadDouble(name, valueElement),
            "boolean" or "bool" => ReadBoolean(name, valueElement),
            "date" or "datetime" => ReadDate(name, valueElement),

            _ => throw new EngineException(
                $"变量 '{name}' 的类型 '{type}' 不受支持：可选 string / integer / long / double / boolean / date。"),
        };
    }

    private static string ReadString(string name, JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new EngineException($"变量 '{name}' 声明为 string，value 必须是字符串。");

    private static int ReadInt(string name, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        throw new EngineException($"变量 '{name}' 声明为 integer，value 必须是 32 位整数。");
    }

    private static long ReadLong(string name, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
        {
            return value;
        }

        throw new EngineException($"变量 '{name}' 声明为 long，value 必须是 64 位整数。");
    }

    private static double ReadDouble(string name, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetDouble();
        }

        throw new EngineException($"变量 '{name}' 声明为 double，value 必须是数字。");
    }

    private static bool ReadBoolean(string name, JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new EngineException($"变量 '{name}' 声明为 boolean，value 必须是 true / false。"),
    };

    /// <summary>
    /// 日期：ISO-8601 字符串（建议带 <c>Z</c> 或偏移）。
    /// <para>无偏移时按 <b>UTC</b> 解释 —— 引擎的唯一时间口径是 UTC（见 README「时间契约」）。</para>
    /// </summary>
    private static DateTime ReadDate(string name, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new EngineException($"变量 '{name}' 声明为 date，value 必须是 ISO-8601 字符串。");
        }

        var text = element.GetString();

        if (string.IsNullOrWhiteSpace(text)
            || !DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var value))
        {
            throw new EngineException(
                $"变量 '{name}' 的日期 '{text}' 无法解析：请用 ISO-8601（如 2026-10-19T00:00:00Z）。");
        }

        return value;
    }

    /// <summary>
    /// 整数优先 <see cref="long"/>（Activiti 的 <c>long</c>），超出范围才退化成 <see cref="double"/>。
    /// </summary>
    private static object ReadNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            return integer;
        }

        return element.GetDouble();
    }
}

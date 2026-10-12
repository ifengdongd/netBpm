using System.Text.Json;
using NetBpm.Engine;

namespace NetBpm.Rest;

/// <summary>
/// JSON 变量 → CLR 基本类型 的转换（REST 边界的必需适配）。
/// <para>
/// 🔑 <b>为什么必须有它</b>：System.Text.Json 把 <c>object?</c> 反序列化成 <see cref="JsonElement"/>，
/// 直接交给引擎会被变量类型映射拒绝（<c>JsonElement</c> 不是受支持的变量类型），
/// 表现为「带变量的启动/完成任务一律 400」。这里在边界把 JSON 值还原成
/// <c>string / long / double / bool / null</c>，与 <c>ACT_RU_VARIABLE</c> 的列布局对齐。
/// </para>
/// <para>
/// 数值一律先尝试 <see cref="long"/>（对应 Activiti 的 <c>integer</c>/<c>long</c>），
/// 失败才用 <see cref="double"/>（<c>double</c>）—— 这样 <c>{"days": 3}</c> 落成 <c>LONG_ = 3</c>，
/// 而不是 <c>DOUBLE_ = 3.0</c>。
/// </para>
/// </summary>
internal static class JsonVariableReader
{
    public static Dictionary<string, object?>? Read(IDictionary<string, JsonElement>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(variables.Count, StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            result[name] = ReadValue(name, value);
        }

        return result;
    }

    private static object? ReadValue(string name, JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,

        _ => throw new EngineException(
            $"变量 '{name}' 的 JSON 类型（{element.ValueKind}）不受支持：流程变量只支持标量"
            + "（string / number / boolean / null）。数组与对象在 Activiti 走 bytes / serializable / json，"
            + "需要 ACT_GE_BYTEARRAY 参与（见开发计划 §15.4 V8）。"),
    };
}

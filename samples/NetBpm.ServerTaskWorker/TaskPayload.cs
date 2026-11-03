using System.Globalization;
using System.Text.Json;

namespace NetBpm.ServerTaskWorker;

/// <summary>
/// 读 <c>payload</c>（流程变量快照）的小工具。
/// </summary>
/// <remarks>
/// 变量过了一遍 JSON：数字可能是 <see cref="JsonValueKind.Number"/>，也可能是字符串 <c>"3"</c>
/// （控制台表单提交、HTTP 表单参数都会带成字符串）。worker 侧宁容错，
/// 也不要因为类型不同就让整条流程卡住。
/// </remarks>
public static class TaskPayload
{
    /// <summary>按键取值；不存在或为 null 时返回 <c>null</c>。</summary>
    /// <param name="payload">变量快照。</param>
    /// <param name="key">变量名（大小写敏感）。</param>
    /// <returns>文本值。</returns>
    public static string? GetString(IReadOnlyDictionary<string, JsonElement> payload, string key)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    /// <summary>按键取整数；不存在或无法解析时返回 <c>false</c>。</summary>
    /// <param name="payload">变量快照。</param>
    /// <param name="key">变量名。</param>
    /// <param name="value">解析结果。</param>
    /// <returns>是否取到。</returns>
    public static bool TryGetInt(IReadOnlyDictionary<string, JsonElement> payload, string key, out int value)
    {
        ArgumentNullException.ThrowIfNull(payload);

        value = 0;

        if (!payload.TryGetValue(key, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
            _ => false,
        };
    }
}

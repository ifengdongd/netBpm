using System.Globalization;

namespace NetBpm.Rest;

/// <summary>HTTP 前置条件解析（<c>If-Match</c>）。</summary>
internal static class Preconditions
{
    /// <summary>
    /// 解析 <c>If-Match: "12"</c>（也接受 <c>12</c>、弱校验 <c>W/"12"</c>）。
    /// 未提供或格式非法时返回 <see langword="null"/>（不启用并发校验，兼容旧客户端）。
    /// </summary>
    internal static int? ReadExpectedRevision(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Headers.IfMatch.ToString().Trim();

        if (value.Length == 0)
        {
            return null;
        }

        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        value = value.Trim('"');

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
               && revision > 0
            ? revision
            : null;
    }
}

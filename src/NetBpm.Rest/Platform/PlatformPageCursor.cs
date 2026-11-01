using System.Text;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 管理面列表的游标编解码（keyset 分页）。
/// </summary>
/// <remarks>
/// <para>
/// 平台面的业务列表统一用「(排序键, 唯一键) 游标」而不是 offset：审批与管理数据是持续写入的，
/// offset 在并发写入下会漏行或重复行（翻页时有人插了一条，第二页就从中间开始）。
/// </para>
/// <para>
/// 这里只做「把若干段字符串打包成一个不透明串」，段之间的分隔符用 <c>\u001f</c>
/// （不可能出现在 id 里），再走 Base64Url，避免调用方手工拼 <c>tenantId:clientId</c>
/// 时被 id 里的冒号搞乱。
/// </para>
/// </remarks>
internal static class PlatformPageCursor
{
    private const char Separator = '\u001f';

    /// <summary>把若干段打包成游标（空段表示「该维度没有排序键」）。</summary>
    /// <param name="segments">按顺序排列的段。</param>
    public static string Encode(params string[] segments) =>
        Base64UrlEncode(string.Join(Separator, segments));

    /// <summary>
    /// 解析游标。
    /// </summary>
    /// <param name="cursor">游标原文（<see langword="null"/> / 空白视为「从头开始」）。</param>
    /// <param name="expectedSegments">期望的段数。</param>
    /// <exception cref="FormatException">游标不是本端点签发的形状。</exception>
    public static string[]? Decode(string? cursor, int expectedSegments)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        string raw;

        try
        {
            raw = Base64UrlDecode(cursor.Trim());
        }
        catch (FormatException)
        {
            throw new FormatException("分页游标格式不正确：请原样回传响应里的 nextCursor。");
        }

        var segments = raw.Split(Separator);

        if (segments.Length != expectedSegments)
        {
            throw new FormatException("分页游标格式不正确：段数与当前端点不符。");
        }

        return segments;
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("分页游标格式不正确。"),
        };

        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}

namespace NetBpm.Rest;

/// <summary>
/// 时间契约的请求侧补充。
/// <para>
/// 引擎唯一时间口径是 UTC（响应统一带 <c>Z</c>）；查询参数走 URL 绑定，不经过
/// <see cref="UtcDateTimeJsonConverter"/>，无偏移的时间串会得到 <c>Kind=Unspecified</c>。
/// 这里把查询时间统一归一为 UTC：无偏移按 UTC 解释，本地按本地时区转换。
/// </para>
/// </summary>
internal static class UtcTime
{
    public static DateTime? Normalize(DateTime? value) => value is { } v ? Normalize(v) : null;

    public static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

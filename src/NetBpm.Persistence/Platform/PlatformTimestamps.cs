namespace NetBpm.Persistence.Platform;

/// <summary>
/// 平台表的时间列与 <see cref="DateTimeOffset"/> 的互转。
/// </summary>
/// <remarks>
/// 库里一律存 UTC（<c>datetime(3)</c> / <c>datetime2(3)</c>，无时区信息），
/// 而 MySQL 驱动读回来是 <see cref="DateTimeKind.Unspecified"/>、
/// SQL Server 读回来是 <see cref="DateTimeKind.Unspecified"/> 或 <see cref="DateTimeKind.Local"/>。
/// 这里统一「按 UTC 解释」，避免同一行数据在两方言下相差 8 小时。
/// </remarks>
internal static class PlatformTimestamps
{
    internal static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    internal static DateTimeOffset? ToOffset(DateTime? value) =>
        value is { } actual ? ToOffset(actual) : null;

    /// <summary>写入数据库用的 UTC 时间（去掉 <see cref="DateTimeOffset"/> 的偏移）。</summary>
    internal static DateTime UtcNow(TimeProvider clock) => clock.GetUtcNow().UtcDateTime;
}

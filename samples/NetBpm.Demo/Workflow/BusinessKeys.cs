using System.Globalization;

namespace NetBpm.Demo.Workflow;

/// <summary>
/// 业务单号发号器（订单 / 请假两条流程共用）—— 校验与发号。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 业务表里 <c>business_id</c> 都是 <c>bigint</c>，单号必须是正整数；
/// 带前缀的字符串（<c>LV-2026-0001</c>）落到真表就写不进去。
/// </para>
/// <para>
/// ⚠️ 「时间戳 &lt;&lt; 20 | 进程内序号」只保证单进程唯一；多实例部署要换成统一发号服务。
/// 订单用<b>秒级</b>精度（把单号压进 JS 安全整数范围），请假用<b>毫秒级</b>。
/// </para>
/// </remarks>
public static class BusinessKeys
{
    private static long _sequence;

    /// <summary>单号是否合法（正整数，且能放进 bigint）。</summary>
    public static bool IsValid(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0;

    /// <summary>发一个毫秒级精度的单号（请假）。</summary>
    public static string NextMilliseconds()
    {
        var ticks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return ((ticks << 20) | (Interlocked.Increment(ref _sequence) & 0xFFFFF))
            .ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>发一个秒级精度的单号（订单：压进 JS 安全整数范围）。</summary>
    public static string NextSeconds()
    {
        var ticks = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return ((ticks << 20) | (Interlocked.Increment(ref _sequence) & 0xFFFFF))
            .ToString(CultureInfo.InvariantCulture);
    }
}

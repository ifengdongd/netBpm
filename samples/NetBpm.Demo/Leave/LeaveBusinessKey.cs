using System.Globalization;

namespace NetBpm.Demo.Leave;

/// <summary>
/// 请假单号（业务主键）—— 校验与发号。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么必须是正整数</b>：业务表里 <c>instances.business_id</c> 是 <c>bigint</c>。
/// 早先 Demo 用的 <c>LV-2026-0001</c> 那种带前缀的字符串在 JSON 里没问题，
/// 一落到真表就写不进去 —— 这是「换真库」时最先暴露的一类不匹配。
/// </para>
/// <para>
/// ⚠️ 「毫秒时间戳 &lt;&lt; 20 | 进程内序号」只保证单进程唯一；多实例部署要换成统一发号服务。
/// </para>
/// </remarks>
public static class LeaveBusinessKey
{
    private static long _sequence;

    /// <summary>单号是否合法（正整数，且能放进 <c>bigint</c>）。</summary>
    /// <param name="value">待校验的单号。</param>
    public static bool IsValid(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0;

    /// <summary>发一个新单号（单进程内唯一，单调递增）。</summary>
    public static string Next()
    {
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return ((milliseconds << 20) | (Interlocked.Increment(ref _sequence) & 0xFFFFF))
            .ToString(CultureInfo.InvariantCulture);
    }
}

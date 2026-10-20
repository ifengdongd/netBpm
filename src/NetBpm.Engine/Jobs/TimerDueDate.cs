using System.Globalization;
using System.Xml;
using NetBpm.Bpmn.Model;
using NetBpm.El;
using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Jobs;

/// <summary>
/// 计算定时边界事件的到期时间（UTC）。
/// <para>
/// 与 Activiti 的 <c>TimerEventHandler</c> 口径一致：<c>timeDuration</c> 相对创建时间、<c>timeDate</c> 绝对时间。
/// 取值支持字面量与表达式（<c>${timeout}</c>），表达式在<b>创建定时器时</b>求值 ——
/// 因此流程变量可以在发起时决定「几小时不处理就转办」。
/// </para>
/// </summary>
internal static class TimerDueDate
{
    /// <summary>
    /// 计算到期时间。
    /// </summary>
    /// <returns>
    /// <c>Due</c> 为 UTC 到期时间；<c>Error</c> 非空表示配置或求值失败（调用方转成 <see cref="EngineException"/>）。
    /// </returns>
    public static async ValueTask<(DateTime Due, string? Error)> ComputeAsync(
        TimerEventDefinition timer,
        IExpressionManager expressions,
        IDelegateExecution execution,
        DateTime utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(expressions);
        ArgumentNullException.ThrowIfNull(execution);

        if (!string.IsNullOrWhiteSpace(timer.TimeDuration))
        {
            var raw = await ResolveTextAsync(timer.TimeDuration, expressions, execution, ct).ConfigureAwait(false);

            if (!TryParseDuration(raw, out var duration) || duration <= TimeSpan.Zero)
            {
                return (default, $"timeDuration 求值为 '{raw}'，不是合法的正数 ISO-8601 时长（如 PT30M）。");
            }

            return (utcNow.Add(duration), null);
        }

        if (!string.IsNullOrWhiteSpace(timer.TimeDate))
        {
            var value = await expressions
                .CreateExpression(timer.TimeDate)
                .GetValueAsync(execution, ct)
                .ConfigureAwait(false);

            DateTime? due = value switch
            {
                DateTime dateTime => EnsureUtc(dateTime),
                DateTimeOffset offset => offset.UtcDateTime,
                string text when DateTimeOffset.TryParse(
                    text.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed) => parsed.UtcDateTime,
                _ => null,
            };

            if (due is null)
            {
                return (default, $"timeDate 求值为 '{value}'，不是合法的 ISO-8601 时间点。");
            }

            // 已过期的时间点按「立即到期」处理（Activiti 也是落到下一个调度周期）
            return (due.Value <= utcNow ? utcNow : due.Value, null);
        }

        return (default, "timerEventDefinition 必须指定 timeDuration 或 timeDate 之一。");
    }

    /// <summary>把时长/日期文本求值成字符串（字面量表达式会原样返回）。</summary>
    private static async ValueTask<string> ResolveTextAsync(
        string rawValue,
        IExpressionManager expressions,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        var value = await expressions
            .CreateExpression(rawValue)
            .GetValueAsync(execution, ct)
            .ConfigureAwait(false);

        return value switch
        {
            null => string.Empty,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    internal static bool TryParseDuration(string value, out TimeSpan duration)
    {
        try
        {
            // xs:duration（PnYnMnDTnHnMnS）
            duration = XmlConvert.ToTimeSpan(value.Trim());
            return true;
        }
        catch (FormatException)
        {
            duration = default;
            return false;
        }
        catch (OverflowException)
        {
            duration = default;
            return false;
        }
    }

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

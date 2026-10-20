using System.Globalization;
using System.Xml;
using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 规则：定时边界事件的配置必须完整且在本子集能力范围内。
/// <para>
/// 本子集只支持「挂在用户任务上的<b>中断型</b>定时边界事件」：到点后取消任务、沿边界事件出流继续。
/// 不支持非中断型（催办类）、重复定时（timeCycle）与挂在其它的活动上 —— 这些都会在部署期
/// 显式拒绝，而不是等到运行时才发现语义不对。
/// </para>
/// </summary>
public sealed class BoundaryEventConfigurationRule : IProcessValidationRule
{
    /// <inheritdoc />
    public string Name => "boundary-event-configuration";

    /// <inheritdoc />
    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var boundaryEvent in process.FindFlowElementsOfType<BoundaryEvent>())
        {
            var id = boundaryEvent.Id;

            if (string.IsNullOrWhiteSpace(boundaryEvent.AttachedToRef))
            {
                yield return Error(id, "边界事件必须通过 attachedToRef 挂在用户任务上（当前没有配置）。");
                continue;
            }

            if (boundaryEvent.AttachedTo is null)
            {
                yield return Error(
                    id,
                    $"边界事件的 attachedToRef='{boundaryEvent.AttachedToRef}' 解析不到任何活动（id 拼写错误？）。");
                continue;
            }

            if (boundaryEvent.AttachedTo is not UserTask)
            {
                yield return Error(
                    id,
                    $"边界事件只支持挂在用户任务上（当前挂在 {boundaryEvent.AttachedTo.GetType().Name} " +
                    $"'{boundaryEvent.AttachedToRef}'）：服务/脚本任务同步执行，定时器没有意义。");
            }
            else if (boundaryEvent.AttachedTo is UserTask { LoopCharacteristics: not null })
            {
                yield return Error(
                    id,
                    "会签/串签（多实例）节点暂不支持边界定时器：实例级超时会与会签计数脱节，" +
                    "请把超时控制放到会签外层（例如在会签后接一个定时边界事件，或由宿主侧催办）。");
            }

            if (!boundaryEvent.CancelActivity)
            {
                yield return Error(
                    id,
                    "不支持非中断型（cancelActivity=\"false\"）边界事件：本子集只实现「到点取消任务并转入边界分支」。");
            }

            if (boundaryEvent.Timer is null)
            {
                yield return Error(
                    id,
                    "边界事件必须配置 <timerEventDefinition>（本子集只支持定时边界事件）。");
                continue;
            }

            foreach (var error in ValidateTimer(id, boundaryEvent.Timer))
            {
                yield return error;
            }
        }
    }

    private IEnumerable<ValidationError> ValidateTimer(string? elementId, TimerEventDefinition timer)
    {
        var configured = new List<string>();

        if (!string.IsNullOrWhiteSpace(timer.TimeDate))
        {
            configured.Add("timeDate");
        }

        if (!string.IsNullOrWhiteSpace(timer.TimeDuration))
        {
            configured.Add("timeDuration");
        }

        if (!string.IsNullOrWhiteSpace(timer.TimeCycle))
        {
            configured.Add("timeCycle");
        }

        if (configured.Count == 0)
        {
            yield return Error(
                elementId,
                "timerEventDefinition 必须指定 timeDuration 或 timeDate 之一。");

            yield break;
        }

        if (configured.Count > 1)
        {
            yield return Error(
                elementId,
                $"timerEventDefinition 同时配置了 {string.Join(" / ", configured)}：只能三选一。");
        }

        if (!string.IsNullOrWhiteSpace(timer.TimeCycle))
        {
            yield return Error(
                elementId,
                "不支持 timeCycle（重复/周期定时）：本子集只实现一次性定时器，请改用 timeDuration 或 timeDate。");
        }

        if (!string.IsNullOrWhiteSpace(timer.TimeDuration)
            && !IsExpression(timer.TimeDuration)
            && (!TryParseDuration(timer.TimeDuration, out var duration) || duration <= TimeSpan.Zero))
        {
            yield return Error(
                elementId,
                $"timeDuration '{timer.TimeDuration}' 不是合法的 ISO-8601 时长（示例：PT30M、PT2H30M、P1D）；" +
                "也可以写成表达式 ${...}，在创建定时器时求值。");
        }

        if (!string.IsNullOrWhiteSpace(timer.TimeDate)
            && !IsExpression(timer.TimeDate)
            && !IsValidDate(timer.TimeDate))
        {
            yield return Error(
                elementId,
                $"timeDate '{timer.TimeDate}' 不是合法的 ISO-8601 时间点（示例：2026-10-20T09:00:00Z）；" +
                "也可以写成表达式 ${...}，在创建定时器时求值。");
        }
    }

    private static bool IsExpression(string value) =>
        value.Contains("${", StringComparison.Ordinal) || value.Contains("#{", StringComparison.Ordinal);

    internal static bool TryParseDuration(string value, out TimeSpan duration)
    {
        try
        {
            // XmlConvert 支持 xs:duration（PnYnMnDTnHnMnS），与 Activiti 的 ISO-8601 口径一致
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

    private static bool IsValidDate(string value) =>
        DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);

    private ValidationError Error(string? elementId, string message) =>
        new(Name, elementId, nameof(BoundaryEvent), message);
}

/// <summary>
/// 规则：终止结束事件不得有出流。
/// <para>
/// <c>terminateEventDefinition</c> 的语义是「立即终止整个流程实例」，出流永远不会被执行；
/// 画了出流通常意味着建模者本意是普通结束事件（或想表达「终止后归档」），必须显式纠正。
/// </para>
/// </summary>
public sealed class TerminateEndEventRule : IProcessValidationRule
{
    /// <inheritdoc />
    public string Name => "terminate-end-event";

    /// <inheritdoc />
    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var endEvent in process.FindFlowElementsOfType<EndEvent>())
        {
            if (!endEvent.IsTerminate)
            {
                continue;
            }

            if (endEvent.OutgoingFlows.Count > 0)
            {
                yield return new ValidationError(
                    Name,
                    endEvent.Id,
                    nameof(EndEvent),
                    "终止结束事件不能有出流：它会立即终止整个流程实例，后续节点永远不会执行。");
            }
        }
    }
}

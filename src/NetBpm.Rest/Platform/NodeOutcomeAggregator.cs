using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.History;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 环节聚合（<c>nodeOutcomes</c>）：把「某人在某环节投了什么票」汇总成
/// 「这个环节有几票同意、几票反对、还差几票」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要单独一个服务</b>：实例详情（<see cref="InstanceView.NodeOutcomes"/>）与审批记录
/// （<see cref="HistoryView.NodeOutcomes"/>）都要这份聚合，而
/// <see cref="InstanceCatalog"/> 与 <see cref="ApprovalTimeline"/> 互为依赖方向（前者是后者的数据源），
/// 谁也不能直接调对方。抽成不依赖两者的独立服务，两边共用同一份口径——
/// 否则「同一个实例在详情页和审批记录页人数不一样」这种问题迟早出现。
/// </para>
/// <para>
/// <b>口径来自流程定义，不靠条数猜</b>：<c>aggregation</c> 由 BPMN 多实例配置
/// （<c>multiInstanceLoopCharacteristics</c> 的 <c>completionCondition</c>）换算，
/// 见 <see cref="NodeOutcomeAggregationRules"/>。
/// </para>
/// </remarks>
public interface INodeOutcomeAggregator
{
    /// <summary>
    /// 计算一条实例的环节聚合结果（按时间线顺序，同一节点重办会再次出现）。
    /// </summary>
    /// <param name="instanceId">流程实例 id（读引擎历史数每个环节的实例数）。</param>
    /// <param name="processDefinitionId">流程定义 id（取多实例配置；<see langword="null"/> 时退化为单人口径）。</param>
    /// <param name="entries">该实例的时间线条目（升序；聚合只看 <c>TASK_COMPLETED</c>）。</param>
    /// <param name="currentTasks">该实例当前的待办（补「还差几人」）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<NodeOutcome>> ComputeAsync(
        string instanceId,
        string? processDefinitionId,
        IReadOnlyList<TimelineEntry> entries,
        IReadOnlyList<TaskView> currentTasks,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class NodeOutcomeAggregator : INodeOutcomeAggregator
{
    /// <summary>聚合一次性读取的时间线条目上限（与 <see cref="ApprovalTimeline"/> 的整单视角同口径）。</summary>
    public const int MaxEntries = 1000;

    private static readonly Dictionary<string, int> NoRows = [];

    private readonly IProcessEngine _engine;
    private readonly ILogger<NodeOutcomeAggregator> _logger;

    /// <summary>
    /// 流程定义模型缓存：定义一旦部署就不可变，按 <c>processDefinitionId</c> 缓存是安全的。
    /// 缓存 <see langword="null"/> 同样安全（同一个 id 不会「稍后变得可解析」——那是另一个 id）。
    /// </summary>
    private readonly ConcurrentDictionary<string, Process?> _models = new(StringComparer.Ordinal);

    /// <summary>构造。</summary>
    /// <param name="engine">引擎门面（取 BPMN 模型 + 历史活动）。</param>
    /// <param name="logger">日志。</param>
    public NodeOutcomeAggregator(IProcessEngine engine, ILogger<NodeOutcomeAggregator> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<NodeOutcome>> ComputeAsync(
        string instanceId,
        string? processDefinitionId,
        IReadOnlyList<TimelineEntry> entries,
        IReadOnlyList<TaskView> currentTasks,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(currentTasks);

        var completed = entries
            .Where(entry => entry.Kind == TimelineEntryKind.TaskCompleted && entry.ActivityId is { Length: > 0 })
            .OrderBy(entry => entry.Sequence)
            .ToArray();

        if (completed.Length == 0)
        {
            return [];
        }

        // 同一节点的条目在时间线上是连续的；断开说明这个节点被重办过（退回 / 跳转后再走一遍），
        // 那是两轮审批，人数不能相加 —— 分成两段各自成账。
        var segments = SplitIntoSegments(completed);

        var process = await ResolveProcessAsync(processDefinitionId, ct).ConfigureAwait(false);
        var instanceRowCounts = await CountInstanceRowsAsync(instanceId, process, ct).ConfigureAwait(false);

        var pendingByActivity = currentTasks
            .Where(task => task.ActivityId is { Length: > 0 })
            .GroupBy(task => task.ActivityId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var outcomes = new List<NodeOutcome>(segments.Count);

        foreach (var segment in segments)
        {
            outcomes.Add(Aggregate(segment, completed, process, instanceRowCounts, pendingByActivity));
        }

        return outcomes;
    }

    /// <summary>把一个环节的一轮条目折算成一条 <see cref="NodeOutcome"/>。</summary>
    private static NodeOutcome Aggregate(
        List<TimelineEntry> segment,
        TimelineEntry[] allCompleted,
        Process? process,
        IReadOnlyDictionary<string, int> instanceRowCounts,
        IReadOnlyDictionary<string, int> pendingByActivity)
    {
        var activityId = segment[0].ActivityId!;
        var multiInstance = FindUserTask(process, activityId)?.LoopCharacteristics;

        var approvals = segment
            .Where(entry => entry.Semantic == ApprovalSemantic.Approve)
            .OrderBy(entry => entry.OccurredAt)
            .ToArray();

        var positive = approvals.Length;
        var negative = segment.Count(entry => entry.Semantic is ApprovalSemantic.Reject or ApprovalSemantic.Return);

        // 该节点是否只走过这一轮：重办过的话，引擎历史里的行数是多轮累加，不能当本轮人数用
        var singleRound = allCompleted.Count(entry => string.Equals(entry.ActivityId, activityId, StringComparison.Ordinal))
            == segment.Count;

        // 应办理人数三个来源取最大：
        //   ① 本轮已投的票 + 当前还挂着的待办（时间线视角，最保守，永远成立）；
        //   ② 引擎历史里该节点的实例行数（ACT_HI_ACTINST 每个多实例一条，提前结束被取消的也在）；
        //   ③ BPMN 里写得下的实例数（loopCardinality 是字面量时）。
        // 取最大而不是取最准的那个：人数算少会把「还差 2 票」误报成「已通过」，算多只会让结论更保守。
        var total = Math.Max(1, segment.Count + pendingByActivity.GetValueOrDefault(activityId));

        if (singleRound && instanceRowCounts.TryGetValue(activityId, out var rows))
        {
            total = Math.Max(total, rows);
        }

        if (multiInstance is not null && TryParseCardinality(multiInstance.LoopCardinality, out var declared))
        {
            total = Math.Max(total, declared);
        }

        // 完成条件 → 「需要几票同意」。没有 completionCondition 就是「全部实例办完才离开」。
        var resolver = NodeOutcomeAggregationRules.ResolveRequiredCompleted(multiInstance);
        var required = Math.Clamp(resolver?.Invoke(total) ?? total, 1, total);

        var satisfied = positive >= required;

        return new NodeOutcome(
            ActivityId: activityId,
            ActivityName: segment[^1].ActivityName,
            Aggregation: NodeOutcomeAggregationRules.Classify(multiInstance is not null, required, total),
            Satisfied: satisfied,
            Total: total,
            Positive: positive,
            Negative: negative,
            // 「未办理」= 应办 - 已投。或签提前结束时剩余人的任务已被引擎删除，
            // 他们确实没投过票 —— 这不能记成 0（那会让「3 人里 1 人批了就通过」看起来像全票通过）。
            Pending: Math.Max(0, total - positive - negative),
            DecidedAt: satisfied ? approvals[required - 1].OccurredAt : null);
    }

    /// <summary>按「同一节点的连续条目」切段（同节点被重办时会产生多段）。</summary>
    private static List<List<TimelineEntry>> SplitIntoSegments(IReadOnlyList<TimelineEntry> completed)
    {
        var segments = new List<List<TimelineEntry>>();

        foreach (var entry in completed)
        {
            if (segments.Count > 0
                && string.Equals(segments[^1][0].ActivityId, entry.ActivityId, StringComparison.Ordinal))
            {
                segments[^1].Add(entry);
                continue;
            }

            segments.Add([entry]);
        }

        return segments;
    }

    /// <summary>
    /// 数每个环节在 <c>ACT_HI_ACTINST</c> 里有多少条「实例行」（<c>TASK_ID_</c> 非空）。
    /// </summary>
    /// <remarks>
    /// 只有流程里真的有多实例节点时才查这一次——它是一趟额外的库往返，
    /// 绝大多数（单签）流程没必要为它付费。<c>loopCardinality</c> 是表达式时算不出静态人数，
    /// 这条路径是唯一能拿到「应到几人」的地方。
    /// </remarks>
    private async ValueTask<IReadOnlyDictionary<string, int>> CountInstanceRowsAsync(
        string instanceId,
        Process? process,
        CancellationToken ct)
    {
        if (process is null || !HasMultiInstance(process))
        {
            return NoRows;
        }

        IReadOnlyList<HistoricActivityInstance> activities;

        try
        {
            activities = await _engine.FindHistoricActivitiesAsync(instanceId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 历史没启用 / 库暂时读不到：退回「已投 + 待办」口径，聚合退化成保守数字，
            // 但不该让「看审批记录」这件事整体失败
            _logger.LogDebug(
                exception,
                "读引擎历史失败，环节人数退回「已投 + 待办」口径（实例 {InstanceId}）。",
                instanceId);

            return NoRows;
        }

        return activities
            .Where(activity => activity.TaskId is { Length: > 0 })
            .GroupBy(activity => activity.ActivityId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    /// <summary>按定义 id 取流程模型（带缓存；取不到返回 <see langword="null"/>，不抛）。</summary>
    private async ValueTask<Process?> ResolveProcessAsync(string? processDefinitionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(processDefinitionId))
        {
            return null;
        }

        if (_models.TryGetValue(processDefinitionId, out var cached))
        {
            return cached;
        }

        try
        {
            var model = await _engine.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false);
            var process = model?.GetMainProcess();

            _models[processDefinitionId] = process;

            return process;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(
                exception,
                "读流程定义 {DefinitionId} 失败，环节聚合退回单人口径。",
                processDefinitionId);

            _models[processDefinitionId] = null;

            return null;
        }
    }

    private static bool HasMultiInstance(Process process) =>
        process.FindFlowElementsOfType<UserTask>().Any(task => task.LoopCharacteristics is not null);

    private static UserTask? FindUserTask(Process? process, string activityId) =>
        process?
            .FindFlowElementsOfType<UserTask>()
            .FirstOrDefault(task => string.Equals(task.Id, activityId, StringComparison.Ordinal));

    /// <summary>实例数是字面量时直接读出来（<c>${count}</c> 这类表达式静态算不出，交给历史行数）。</summary>
    private static bool TryParseCardinality(string? cardinality, out int count)
    {
        count = 0;

        if (string.IsNullOrWhiteSpace(cardinality))
        {
            return false;
        }

        var text = cardinality.Trim();

        if (text.StartsWith("${", StringComparison.Ordinal) || text.StartsWith("#{", StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0;
    }
}

/// <summary>「达成完成条件需要几个人同意」——以实例总数换算（<c>total ⇒ required</c>）。</summary>
/// <param name="total">应办理人数。</param>
internal delegate int RequiredCompletedResolver(int total);

/// <summary>
/// 从 BPMN 多实例配置推算环节聚合口径（<see cref="NodeOutcomeAggregation"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>不靠条数猜</b>：多实例环节「几条完成记录」取决于有几个人办过，
/// 「该算会签还是或签」取决于流程定义怎么配 —— 这两件事必须分开。
/// 前者是数据，后者是契约；把后者从前者的形状里反推，
/// 结论会随「第一个人什么时候批」而变化（同一份流程，2 人先批就成会签、1 人先批就成单签）。
/// </para>
/// <para>
/// <b>换算方式</b>：把 <c>completionCondition</c> 解析成「需要几票同意」的下限函数，
/// 再与应办人数比较：
/// <list type="bullet">
///   <item><description>没有完成条件 → 全部实例办完才离开 → <see cref="NodeOutcomeAggregation.AllApproved"/></description></item>
///   <item><description>完成条件在 1 人完成时就成立（<c>nrOfCompletedInstances == 1</c>）→ <see cref="NodeOutcomeAggregation.AnyApproved"/></description></item>
///   <item><description>完成条件要求过半（<c>nrOfCompletedInstances/nrOfInstances &gt;= 0.5</c>）→ <see cref="NodeOutcomeAggregation.Majority"/></description></item>
///   <item><description>条件解析不出来（自定义表达式）→ 一律按全部完成算</description></item>
/// </list>
/// 最后一条是刻意的：宁可少报「这个环节通过了」，不能把「还有 2 人没批」说成「已通过」。
/// </para>
/// </remarks>
internal static class NodeOutcomeAggregationRules
{
    private const string Completed = "nrOfCompletedInstances";
    private const string Total = "nrOfInstances";

    /// <summary>「全部实例办完才离开」的解析器（无完成条件 / 条件不可解析时使用）。</summary>
    internal static readonly RequiredCompletedResolver AllCompleted = total => total;

    /// <summary>
    /// 把多实例配置换算成「需要几票同意」。返回 <see langword="null"/> 表示不是多实例，
    /// 由调用方按 <see cref="NodeOutcomeAggregation.Single"/> 处理。
    /// </summary>
    public static RequiredCompletedResolver? ResolveRequiredCompleted(MultiInstanceLoopCharacteristics? multiInstance)
    {
        if (multiInstance is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(multiInstance.CompletionCondition)
            ? AllCompleted
            : Parse(multiInstance.CompletionCondition!);
    }

    /// <summary>按「需要几票」与「应到几人」判定聚合口径。</summary>
    public static NodeOutcomeAggregation Classify(bool multiInstance, int required, int total)
    {
        // 一个人办的事没有「聚合」可言，哪怕定义上写了多实例（集合只有 1 个元素也常见）
        if (!multiInstance || total <= 1)
        {
            return NodeOutcomeAggregation.Single;
        }

        if (required <= 1)
        {
            return NodeOutcomeAggregation.AnyApproved;
        }

        return required >= total ? NodeOutcomeAggregation.AllApproved : NodeOutcomeAggregation.Majority;
    }

    // ─────────────────────────── 完成条件解析 ───────────────────────────

    private static RequiredCompletedResolver Parse(string condition)
    {
        var text = Normalize(condition);

        if (text.Length == 0)
        {
            return AllCompleted;
        }

        // || 任一支成立即可 → 取最宽松（最少票数）；&& 全部成立 → 取最严格
        var alternatives = Split(text, "||");

        if (alternatives.Count > 1)
        {
            var resolvers = alternatives.Select(Parse).ToArray();
            return total => resolvers.Min(resolver => resolver(total));
        }

        var conjuncts = Split(text, "&&");

        if (conjuncts.Count > 1)
        {
            var resolvers = conjuncts.Select(Parse).ToArray();
            return total => resolvers.Max(resolver => resolver(total));
        }

        return ParseComparison(text);
    }

    private static RequiredCompletedResolver ParseComparison(string text)
    {
        foreach (var op in new[] { "==", ">=", "<=", ">", "<" })
        {
            var index = text.IndexOf(op, StringComparison.Ordinal);

            if (index <= 0 || index + op.Length >= text.Length)
            {
                continue;
            }

            var left = text[..index];
            var right = text[(index + op.Length)..];

            // 剩余人数视角：nrOfInstances - nrOfCompletedInstances < 1 → 还剩不到 1 人才算完成
            if (left.Contains(Completed, StringComparison.Ordinal) && left.Contains('-', StringComparison.Ordinal))
            {
                return FromRemaining(op, right);
            }

            // 比例视角：nrOfCompletedInstances / nrOfInstances >= 0.5
            if (left.Contains(Completed, StringComparison.Ordinal) && left.Contains('/', StringComparison.Ordinal))
            {
                return FromRatio(op, right);
            }

            if (string.Equals(left, Completed, StringComparison.Ordinal))
            {
                return right.Contains(Total, StringComparison.Ordinal) ? AllCompleted : FromCount(op, right);
            }

            // 反过来写（nrOfInstances == nrOfCompletedInstances）等价于「全部完成」
            return AllCompleted;
        }

        return AllCompleted;
    }

    private static RequiredCompletedResolver FromCount(string op, string right)
    {
        if (!TryParseNumber(right, out var number))
        {
            return AllCompleted;
        }

        var votes = op switch
        {
            "==" or ">=" => (int)number,
            ">" => (int)number + 1,
            _ => int.MaxValue,
        };

        return total => Math.Clamp(votes, 1, total);
    }

    private static RequiredCompletedResolver FromRatio(string op, string right)
    {
        if (!TryParseNumber(right, out var ratio))
        {
            return AllCompleted;
        }

        return total =>
        {
            // ">" 要求严格超过，0.5 在 3 人时须 2 票而不是 1.5→2（ceil 后同为 2，这里显式区分）
            var needed = op == ">" ? Math.Floor(ratio * total) + 1 : Math.Ceiling(ratio * total);
            return Math.Clamp((int)needed, 1, total);
        };
    }

    private static RequiredCompletedResolver FromRemaining(string op, string right)
    {
        if (!TryParseNumber(right, out var remaining))
        {
            return AllCompleted;
        }

        var allowed = op switch
        {
            "<" => (int)remaining - 1,
            "<=" => (int)remaining,
            _ => 0,
        };

        return total => Math.Clamp(total - Math.Max(0, allowed), 1, total);
    }

    /// <summary>去掉 <c>${}</c> / <c>#{}</c> 外壳与全部空白，便于按操作符切分。</summary>
    private static string Normalize(string condition)
    {
        var text = condition.Trim();

        if (text.Length > 4 && (text.StartsWith("${", StringComparison.Ordinal) || text.StartsWith("#{", StringComparison.Ordinal))
            && text.EndsWith('}'))
        {
            text = text[2..^1];
        }

        return string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
    }

    private static List<string> Split(string text, string separator)
    {
        var parts = new List<string>();
        var start = 0;

        for (var index = text.IndexOf(separator, StringComparison.Ordinal); index >= 0;
            index = text.IndexOf(separator, start, StringComparison.Ordinal))
        {
            parts.Add(text[start..index]);
            start = index + separator.Length;
        }

        parts.Add(text[start..]);

        return parts;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

using System.Globalization;
using Microsoft.Extensions.Logging;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 审批记录时间线（<c>GET /workflow/v1/instances/{id}/history</c>）的编排服务。
/// </summary>
/// <remarks>
/// <para>
/// 对外只有<b>一个出口</b>：调用方拿到的是合成好的 <see cref="HistoryView"/>，
/// 不需要自己对齐引擎历史（<c>ACT_HI_ACTINST</c>）与业务审批记录两份数据。
/// </para>
/// <para>
/// 数据来源分两层：
/// </para>
/// <list type="number">
///   <item><description>
///   <b>写时定稿</b>（主路径）：发起 / 提交时把「引擎事实 + 审批意见 + 调用系统」一次性写进
///   <c>nb_instance_timeline</c>。读时不再拼——拼出来的东西会随定义升级、字典变更而漂移。
///   </description></item>
///   <item><description>
///   <b>历史回填</b>（兼容路径）：本能力上线<b>之前</b>发起的实例没有时间线行。首次读取时从
///   <c>ACT_HI_ACTINST</c> 合成一份最小时间线写进去，此后同样走主路径。
///   回填只能给出「谁在哪个环节办完了」，拿不到 optionId / semantic / 审批意见——
///   那些信息在引擎里根本不存在（引擎不认识审批意见）。
///   </description></item>
/// </list>
/// </remarks>
public interface IApprovalTimeline
{
    /// <summary>
    /// 追加一条时间线条目。写失败只记日志、返回 <see langword="null"/>，<b>不阻断主流程</b>——
    /// 时间线是审计副产品，不能因为它写不进去就让一条已经完成的审批回滚。
    /// </summary>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="entry">条目（<c>Sequence</c> 由存储层分配；<c>ActorCallerId</c> 为空时取调用方系统）。</param>
    /// <param name="context">调用方身份（取租户与 caller）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<TimelineEntry?> AppendAsync(
        string instanceId,
        TimelineEntry entry,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 读实例的审批记录。实例不存在 / 对当前调用方不可见时返回 <see langword="null"/>（端点转 404）。
    /// </summary>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="afterSequence">增量拉取游标（只取序号更大的条目）。</param>
    /// <param name="limit">本页条数（0 取默认值，上限 200）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<HistoryView?> GetHistoryAsync(
        string instanceId,
        long afterSequence,
        int limit,
        RequestContext context,
        CancellationToken ct = default);
}

/// <summary>
/// 审批记录的人员级可见性分级。
/// </summary>
/// <remarks>
/// 分级的理由：<c>workflow:history:read</c> 只证明「这个调用方会用审批记录」，
/// 不证明「这个人跟这张单据有关」。把两者混为一谈的结果是——
/// 任何一个拿到读权限的系统都能拉走全租户的审批意见。
/// </remarks>
public enum HistoryVisibilityLevel
{
    /// <summary>管理视角：全部字段（含 <c>actorCallerId</c> 这类平台内部审计字段）。</summary>
    Full = 0,

    /// <summary>相关人视角：能看本单全部条目，但看不到「是谁的系统提交的」。</summary>
    Related = 1,
}

/// <inheritdoc />
public sealed class ApprovalTimeline : IApprovalTimeline
{
    /// <summary>未声明租户时的默认租户（与鉴权中间件同口径）。</summary>
    private const string DefaultTenantId = "default";

    /// <summary>单页默认条数。</summary>
    private const int DefaultPageSize = 100;

    /// <summary>单页上限（再多就该用 afterSequence 增量拉）。</summary>
    private const int MaxPageSize = 200;

    /// <summary>环节聚合读取的条目上限：聚合是「整单视角」，不能只算当前页。</summary>
    private const int MaxAggregateEntries = NodeOutcomeAggregator.MaxEntries;

    private readonly IApprovalTimelineStore _store;
    private readonly IInstanceCatalog _instanceCatalog;
    private readonly IInstanceParticipation _participation;
    private readonly INodeOutcomeAggregator _outcomes;
    private readonly IProcessEngine _engine;
    private readonly ILogger<ApprovalTimeline> _logger;

    /// <summary>构造。</summary>
    /// <param name="store">时间线存储。</param>
    /// <param name="instanceCatalog">实例读模型（状态、起止时间、结论的唯一来源）。</param>
    /// <param name="participation">参与者索引（判定这位真人跟本单有没有关系）。</param>
    /// <param name="outcomes">环节聚合（会签 / 或签口径的唯一实现，与实例详情共用）。</param>
    /// <param name="engine">引擎（历史回填读 <c>ACT_HI_ACTINST</c>）。</param>
    /// <param name="logger">日志。</param>
    public ApprovalTimeline(
        IApprovalTimelineStore store,
        IInstanceCatalog instanceCatalog,
        IInstanceParticipation participation,
        INodeOutcomeAggregator outcomes,
        IProcessEngine engine,
        ILogger<ApprovalTimeline> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(instanceCatalog);
        ArgumentNullException.ThrowIfNull(participation);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _instanceCatalog = instanceCatalog;
        _participation = participation;
        _outcomes = outcomes;
        _engine = engine;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<TimelineEntry?> AppendAsync(
        string instanceId,
        TimelineEntry entry,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(context);

        var prepared = entry with
        {
            InstanceId = instanceId,
            ActorCallerId = entry.ActorCallerId ?? context.CallerId,
            OccurredAt = entry.OccurredAt == default ? DateTimeOffset.UtcNow : entry.OccurredAt,
        };

        try
        {
            return await _store
                .AppendAsync(context.EffectiveTenantId ?? DefaultTenantId, prepared, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 审计写失败不能让业务失败：任务已经被引擎办结了，这时候抛错只会让调用方以为没提交成功
            _logger.LogWarning(
                exception,
                "写审批时间线失败（实例 {InstanceId}，类型 {Kind}）：业务已完成，缺一条审计记录。",
                instanceId,
                prepared.Kind);

            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<HistoryView?> GetHistoryAsync(
        string instanceId,
        long afterSequence,
        int limit,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(context);

        // 存在性 / 授权 / 租户桥接全部复用实例读模型：不在这里再写一份判定，
        // 否则「GET /instances/{id} 能看、GET /instances/{id}/history 看不到」这种分裂迟早出现。
        var instance = await _instanceCatalog.FindAsync(instanceId, context, ct).ConfigureAwait(false);

        if (instance is null)
        {
            return null;
        }

        // 数据级可见性：scope 只证明「会用这个功能」，不代表「跟这张单有关」。
        // 三种情况放行：管理视角 / 纯服务令牌 / 这位真人是本单的相关人。
        var visibility = await ResolveVisibilityAsync(instance, context, ct).ConfigureAwait(false);

        if (visibility is null)
        {
            // 无关的真人：按不存在处理（404 而不是 403——403 本身就是「这张单确实存在」的证据）
            _logger.LogDebug(
                "审批记录不可见：{Subject} 与实例 {InstanceId} 无参与关系，且未持 read:all。",
                context.SubjectId,
                instanceId);

            return null;
        }

        // 首次读取老实例：从引擎历史合成一份最小时间线（之后就走写时定稿的主路径）
        if (await _store.CountAsync(instanceId, ct).ConfigureAwait(false) == 0)
        {
            await BackfillAsync(instance, context, ct).ConfigureAwait(false);
        }

        var pageSize = Math.Clamp(limit <= 0 ? DefaultPageSize : limit, 1, MaxPageSize);

        // 多取一条用来判断「还有没有下一页」，避免为了算 hasMore 再查一次 count
        var window = await _store
            .ListAsync(instanceId, afterSequence, pageSize + 1, ct)
            .ConfigureAwait(false);

        var hasMore = window.Count > pageSize;
        IReadOnlyList<TimelineEntry> entries = hasMore ? window.Take(pageSize).ToArray() : window;

        if (visibility == HistoryVisibilityLevel.Related)
        {
            // 相关人看不到「是哪个系统的令牌提交的」：那是平台自己的审计字段，
            // 参与者需要知道的是「谁批的、批了什么」，不是调用链。
            entries = entries
                .Select(entry => entry.ActorCallerId is null ? entry : entry with { ActorCallerId = null })
                .ToArray();
        }

        var nextCursor = hasMore && entries.Count > 0
            ? entries[entries.Count - 1].Sequence.ToString(CultureInfo.InvariantCulture)
            : null;

        // 环节聚合是整单视角：单独读一次（有上限），不受当前页影响
        var aggregate = await _store
            .ListAsync(instanceId, 0, MaxAggregateEntries, ct)
            .ConfigureAwait(false);

        return new HistoryView(
            InstanceId: instance.InstanceId,
            ProcessKey: instance.ProcessKey,
            BusinessKey: instance.BusinessKey,
            Status: instance.Status,
            StartedAt: instance.StartedAt,
            StartedBy: instance.StartedBy,
            EndedAt: instance.EndedAt,
            DurationInMilliseconds: instance.StartedAt is { } startedAt && instance.EndedAt is { } endedAt
                ? (long)(endedAt - startedAt).TotalMilliseconds
                : null,
            Entries: entries,
            NextCursor: nextCursor,
            NodeOutcomes: await _outcomes
                .ComputeAsync(instanceId, instance.ProcessDefinitionId, aggregate, instance.Tasks, ct)
                .ConfigureAwait(false),
            Conclusion: instance.Conclusion);
    }

    /// <summary>
    /// 判定这位调用方能不能读本单的审批记录，以及能看到多细。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返回 <see langword="null"/> 表示「不该让他知道这张单存在」。注意这与 403 的区别：
    /// 403 会告诉调用方「这张单在，只是你没权限」，而它本来连在不在都不该知道。
    /// </para>
    /// <para>
    /// 「相关人」的判定包含发起人 / 办理人 / 候选人 / 参与者两条来源：
    /// 发起人直接比 <see cref="InstanceView.StartedBy"/>（已经在手边），
    /// 其余走参与者索引——现在有了这一层，「参与人可见」才不是一句口号。
    /// </para>
    /// </remarks>
    private async ValueTask<HistoryVisibilityLevel?> ResolveVisibilityAsync(
        InstanceView instance,
        RequestContext context,
        CancellationToken ct)
    {
        // ① 管理视角：三类管理员 scope；它们同时也决定了列表能不能跨人查
        if (context.IsAdmin
            || context.HasAdminScope(TaskScopes.AdminInstanceJump)
            || context.HasScope(PlatformScopes.HistoryReadAll))
        {
            return HistoryVisibilityLevel.Full;
        }

        // ② 纯服务令牌：它代表「系统」而不是某个真人，没有「我」这层关系可比；
        //    租户与流程白名单已在实例读模型里卡过，这里不重复收窄
        if (context.SubjectId is not { Length: > 0 } subject)
        {
            return HistoryVisibilityLevel.Full;
        }

        // ③ 发起人：最常见的一种相关人，不需要查索引
        if (string.Equals(instance.StartedBy, subject, StringComparison.Ordinal))
        {
            return HistoryVisibilityLevel.Related;
        }

        // ④ 办理人 / 候选人 / 参与者
        var relationship = await _participation
            .DescribeAsync(instance.InstanceId, subject, [.. context.Groups], ct)
            .ConfigureAwait(false);

        return relationship.Involved ? HistoryVisibilityLevel.Related : null;
    }

    /// <summary>
    /// 从引擎历史回填时间线（仅对「本能力上线前发起」的实例生效，且只回填一次）。
    /// </summary>
    /// <remarks>
    /// 回填条目<b>不带审批意见与语义</b>：引擎只记「谁在什么时间办完了哪个环节」，
    /// 审批意见属于业务概念，从来就没进过 <c>ACT_HI_ACTINST</c>。
    /// 硬按 <c>result</c> 变量倒推语义等于编造一条「当时的决定」，比缺一条更糟。
    /// </remarks>
    private async ValueTask BackfillAsync(
        InstanceView instance,
        RequestContext context,
        CancellationToken ct)
    {
        if (instance.StartedAt is { } startedAt)
        {
            await AppendAsync(
                instance.InstanceId,
                new TimelineEntry(
                    Sequence: 0,
                    InstanceId: instance.InstanceId,
                    Kind: TimelineEntryKind.InstanceStarted,
                    ActivityId: null,
                    ActivityName: null,
                    TaskId: null,
                    ActorId: instance.StartedBy,
                    ActorCallerId: null,
                    OptionId: null,
                    Semantic: null,
                    Comment: null,
                    TargetNodeId: null,
                    FormSnapshotTaskId: null,
                    OccurredAt: startedAt),
                context,
                ct).ConfigureAwait(false);
        }

        var activities = await _engine
            .FindHistoricActivitiesAsync(instance.InstanceId, ct)
            .ConfigureAwait(false);

        var userTasks = activities
            .Where(activity => string.Equals(activity.ActivityType, "userTask", StringComparison.OrdinalIgnoreCase)
                && activity.EndTime is not null)
            .OrderBy(activity => activity.EndTime)
            .ThenBy(activity => activity.Id, StringComparer.Ordinal);

        foreach (var activity in userTasks)
        {
            await AppendAsync(
                instance.InstanceId,
                new TimelineEntry(
                    Sequence: 0,
                    InstanceId: instance.InstanceId,
                    Kind: TimelineEntryKind.TaskCompleted,
                    ActivityId: activity.ActivityId,
                    ActivityName: activity.ActivityName,
                    TaskId: activity.TaskId,
                    ActorId: activity.Assignee,
                    ActorCallerId: null,
                    OptionId: null,
                    Semantic: null,
                    Comment: null,
                    TargetNodeId: null,
                    FormSnapshotTaskId: null,
                    OccurredAt: new DateTimeOffset(DateTime.SpecifyKind(activity.EndTime!.Value, DateTimeKind.Utc))),
                context,
                ct).ConfigureAwait(false);
        }

        if (instance.Status is InstanceStatus.Completed or InstanceStatus.Terminated && instance.EndedAt is { } endedAt)
        {
            await AppendAsync(
                instance.InstanceId,
                new TimelineEntry(
                    Sequence: 0,
                    InstanceId: instance.InstanceId,
                    Kind: instance.Status == InstanceStatus.Completed
                        ? TimelineEntryKind.InstanceCompleted
                        : TimelineEntryKind.InstanceTerminated,
                    ActivityId: null,
                    ActivityName: null,
                    TaskId: null,
                    ActorId: null,
                    ActorCallerId: null,
                    OptionId: null,
                    Semantic: null,
                    Comment: null,
                    TargetNodeId: null,
                    FormSnapshotTaskId: null,
                    OccurredAt: endedAt),
                context,
                ct).ConfigureAwait(false);
        }
    }
}

using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 时间线条目类型。
/// </summary>
/// <remarks>
/// 时间线是 <b>append-only</b> 的：只追加，不修改、不删除。
/// 这样「谁在什么时候做了什么」可以完整回溯，也让调用方可以用
/// <see cref="TimelineEntry.Sequence"/> 做增量拉取（<c>?afterSequence=</c>）。
/// </remarks>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<TimelineEntryKind>))]
public enum TimelineEntryKind
{
    /// <summary>实例发起。</summary>
    InstanceStarted = 0,

    /// <summary>任务创建（进入某个环节）。</summary>
    TaskCreated = 1,

    /// <summary>任务办结（提交了审批决定）。</summary>
    TaskCompleted = 2,

    /// <summary>任务认领。</summary>
    TaskClaimed = 3,

    /// <summary>任务取消认领。</summary>
    TaskUnclaimed = 4,

    /// <summary>任务转办。</summary>
    TaskTransferred = 5,

    /// <summary>实例挂起。</summary>
    InstanceSuspended = 6,

    /// <summary>实例恢复。</summary>
    InstanceResumed = 7,

    /// <summary>实例终止。</summary>
    InstanceTerminated = 8,

    /// <summary>实例正常结束。</summary>
    InstanceCompleted = 9,

    /// <summary>纯评论（不对应任何推进）。</summary>
    Comment = 10,

    /// <summary>参数变更（记录变更前后值，用于合规审计）。</summary>
    ParametersUpdated = 11,

    /// <summary>
    /// 管理干预：跳转节点（<c>netbpm.admin.instance.jump</c>）。
    /// </summary>
    /// <remarks>
    /// 它<b>不是一次审批</b>——没有 optionId、没有审批语义，只是有人把这张单挪到了另一个节点。
    /// 刻意单列一个类型而不是复用 <see cref="Comment"/>：审计要能一眼区分
    /// 「这张单是被谁批过去的」和「这张单是被谁挪过去的」。
    /// </remarks>
    InstanceJumped = 12,
}

/// <summary>
/// 时间线条目——审批记录的标准表达。
/// </summary>
/// <remarks>
/// <para>
/// 现状的审批历史有<b>两个来源</b>：引擎 <c>ACT_HI_ACTINST</c>（权威但有断档）与
/// 业务侧审批记录（完整但引擎不认识）。对外必须合成<b>单一出口</b>，否则调用方要自己对齐两份数据。
/// </para>
/// <para>
/// 每条都带 <see cref="ActorId"/> 与 <see cref="ActorCallerId"/> 双身份：
/// 前者是真人（审计要求），后者是提交它的系统（故障定位与风控要求）。
/// 纯服务令牌提交的动作 <see cref="ActorId"/> 为空——这种记录在合规报表里要单独列出来。
/// </para>
/// </remarks>
/// <param name="Sequence">实例内单调递增序号（增量拉取的游标）。</param>
/// <param name="InstanceId">实例 id。</param>
/// <param name="Kind">条目类型。</param>
/// <param name="ActivityId">相关环节 id。</param>
/// <param name="ActivityName">环节名。</param>
/// <param name="TaskId">相关任务 id。</param>
/// <param name="ActorId">实际操作人（<c>subject</c>）。</param>
/// <param name="ActorCallerId">提交该动作的调用系统（<c>caller</c>）。</param>
/// <param name="OptionId">审批选项 id（仅 <see cref="TimelineEntryKind.TaskCompleted"/>）。</param>
/// <param name="Semantic">审批语义（仅 <see cref="TimelineEntryKind.TaskCompleted"/>）。</param>
/// <param name="Comment">审批意见 / 说明。</param>
/// <param name="TargetNodeId">目标节点（退回 / 转办）。</param>
/// <param name="FormSnapshotTaskId">
/// 该条记录对应的表单快照任务 id；不为空时调用方可再拉 <c>/tasks/{id}/form</c> 取当时的字段定义。
/// </param>
/// <param name="OccurredAt">发生时间。</param>
public sealed record TimelineEntry(
    long Sequence,
    string InstanceId,
    TimelineEntryKind Kind,
    string? ActivityId,
    string? ActivityName,
    string? TaskId,
    string? ActorId,
    string? ActorCallerId,
    string? OptionId,
    ApprovalSemantic? Semantic,
    string? Comment,
    string? TargetNodeId,
    string? FormSnapshotTaskId,
    DateTimeOffset OccurredAt);

/// <summary>
/// 审批历史视图（一个实例的完整时间线）。
/// </summary>
/// <param name="InstanceId">实例 id。</param>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="Status">实例状态。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="StartedBy">发起人。</param>
/// <param name="EndedAt">结束时间。</param>
/// <param name="DurationInMilliseconds">历时（毫秒）。</param>
/// <param name="Entries">时间线条目（按 <see cref="TimelineEntry.Sequence"/> 升序）。</param>
/// <param name="NextCursor">时间线分页游标（条目多时使用）。</param>
/// <param name="NodeOutcomes">环节聚合结果。</param>
/// <param name="Conclusion">实例结论。</param>
public sealed record HistoryView(
    string InstanceId,
    string ProcessKey,
    string? BusinessKey,
    InstanceStatus Status,
    DateTimeOffset? StartedAt,
    string? StartedBy,
    DateTimeOffset? EndedAt,
    long? DurationInMilliseconds,
    IReadOnlyList<TimelineEntry> Entries,
    string? NextCursor,
    IReadOnlyList<NodeOutcome> NodeOutcomes,
    InstanceConclusion? Conclusion);

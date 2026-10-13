using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 组装「审批历史记录」视图。
/// </summary>
/// <remarks>
/// <para>
/// 两个数据源、一个出口：
/// </para>
/// <list type="number">
///   <item><description>
///   <b>引擎历史</b>（<c>ACT_HI_ACTINST</c>）—— 权威，含自动节点与连线，
///   但在内存宿主 / 未开启 <c>EnableHistory</c> 时是空的；
///   </description></item>
///   <item><description>
///   <b>业务审批记录 + 当前待办</b> —— 宿主每次提交都记了，任何宿主都有。
///   </description></item>
/// </list>
/// <para>
/// 后者不只是兜底：引擎历史里<b>没有</b>「审批意见」这种东西（引擎不认识它），
/// 所以即便走引擎历史，<c>Decision</c> / <c>Comment</c> 也要从业务审批记录里按任务 id 关联回来。
/// </para>
/// </remarks>
internal sealed class WorkflowHistoryBuilder
{
    private readonly IProcessEngine _engine;
    private readonly IExecutionManager _executionManager;
    private readonly IWorkflowDataStore _store;

    internal WorkflowHistoryBuilder(
        IProcessEngine engine,
        IExecutionManager executionManager,
        IWorkflowDataStore store)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(executionManager);
        ArgumentNullException.ThrowIfNull(store);

        _engine = engine;
        _executionManager = executionManager;
        _store = store;
    }

    /// <summary>组装历史视图；引擎与业务侧都查不到该实例时返回 <see langword="null"/>。</summary>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="ct">取消标记。</param>
    internal async ValueTask<WorkflowHistoryView?> BuildAsync(string processInstanceId, CancellationToken ct)
    {
        var execution = await _executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);
        var result = await _store.FindResultAsync(processInstanceId, ct).ConfigureAwait(false);
        var record = await _store.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);
        var historic = await _engine.FindHistoricProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        if (execution is null && result is null && record is null && historic is null)
        {
            return null;
        }

        var approvals = await _store.FindApprovalsAsync(processInstanceId, ct).ConfigureAwait(false);
        var activities = await _engine.FindHistoricActivitiesAsync(processInstanceId, ct).ConfigureAwait(false);
        var startedAt = historic?.StartTime ?? record?.Base.ApplyTime ?? execution?.StartTime;

        IReadOnlyList<WorkflowStepRecord> steps = activities.Count > 0
            ? ToSteps(activities, approvals)
            : await SynthesizeStepsAsync(processInstanceId, approvals, startedAt, ct).ConfigureAwait(false);

        var endedAt = historic?.EndTime ?? result?.CompletedAt;

        return new WorkflowHistoryView(
            ProcessInstanceId: processInstanceId,
            ProcessDefinitionId: historic?.ProcessDefinitionId ?? execution?.ProcessDefinitionId,
            BusinessKey: historic?.BusinessKey ?? execution?.BusinessKey ?? record?.Base.BusinessId,
            Running: execution is not null,
            StartedAt: startedAt,
            EndedAt: endedAt,
            DurationInMilliseconds: historic?.DurationInMillis
                ?? (startedAt is { } from && endedAt is { } to ? (long?)(to - from).TotalMilliseconds : null),
            StepsSource: activities.Count > 0 ? WorkflowStepSource.EngineHistory : WorkflowStepSource.BusinessApprovals,
            Steps: steps,
            Approvals: approvals,
            Result: result);
    }

    /// <summary>
    /// 引擎历史的每个环节 → 视图环节，并把业务侧的审批记录关联回来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 关联方式有两条，按可靠性排序：
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b>按任务 id</b> —— 最准，但要求宿主的审批记录里存了引擎任务 id
    ///   （JSON 文件那种「想存什么就存什么」的宿主有，而真实业务表往往没有这一列）；
    ///   </description></item>
    ///   <item><description>
    ///   <b>按环节名 + 先后顺序认领</b> —— 兜底。引擎历史与审批记录都按时间有序，
    ///   同一环节被走过多次（驳回后重提）时，第 N 次历史就配第 N 条审批记录，
    ///   所以这里用「用过就不再复用」的贪心配对，而不是简单的 FirstOrDefault。
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static List<WorkflowStepRecord> ToSteps(
        IReadOnlyList<HistoricActivityInstance> activities,
        IReadOnlyList<WorkflowApprovalRecord> approvals)
    {
        var claimed = new HashSet<WorkflowApprovalRecord>();
        var steps = new List<WorkflowStepRecord>(activities.Count);

        foreach (var activity in activities)
        {
            steps.Add(ToStep(activity, approvals, claimed));
        }

        return steps;
    }

    private static WorkflowStepRecord ToStep(
        HistoricActivityInstance activity,
        IReadOnlyList<WorkflowApprovalRecord> approvals,
        HashSet<WorkflowApprovalRecord> claimed)
    {
        // 引擎历史里没有「审批意见」—— 得把业务侧那条审批记录找回来
        var approval = Claim(activity, approvals, claimed);

        return new WorkflowStepRecord(
            ActivityId: activity.ActivityId,
            ActivityName: activity.ActivityName,
            ActivityType: activity.ActivityType,
            TaskId: activity.TaskId,
            Assignee: approval?.Assignee ?? activity.Assignee,
            Decision: approval?.Decision,
            DecisionName: approval?.DecisionName,
            Comment: approval?.Comment,
            StartedAt: activity.StartTime,
            EndedAt: activity.EndTime);
    }

    /// <summary>给一个引擎环节认领它对应的那条审批记录（认领过的不再复用）。</summary>
    private static WorkflowApprovalRecord? Claim(
        HistoricActivityInstance activity,
        IReadOnlyList<WorkflowApprovalRecord> approvals,
        HashSet<WorkflowApprovalRecord> claimed)
    {
        WorkflowApprovalRecord? match = null;

        if (!string.IsNullOrEmpty(activity.TaskId))
        {
            match = approvals.FirstOrDefault(item =>
                !claimed.Contains(item)
                && string.Equals(item.TaskId, activity.TaskId, StringComparison.Ordinal));
        }

        // 审批记录里没有任务 id（业务表就是这样）：退化成按环节名按序认领
        match ??= approvals.FirstOrDefault(item =>
            !claimed.Contains(item)
            && !string.IsNullOrEmpty(activity.ActivityName)
            && string.Equals(item.ActivityName, activity.ActivityName, StringComparison.Ordinal));

        if (match is not null)
        {
            claimed.Add(match);
        }

        return match;
    }

    /// <summary>
    /// 用业务侧数据合成环节序列：每次审批记录算一个已结束环节，当前待办算一个进行中环节。
    /// </summary>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="approvals">审批记录（按提交先后）。</param>
    /// <param name="startedAt">实例发起时间，作为第一个环节的进入时刻。</param>
    /// <param name="ct">取消标记。</param>
    /// <remarks>
    /// 进入时刻取「上一环节的结束时刻」—— 顺序流转下它们就是同一时刻（令牌是同步移动的），
    /// 因此这条近似只在并行分支上才有偏差，而并行分支本来也不会因它得出错误结论。
    /// </remarks>
    private async ValueTask<IReadOnlyList<WorkflowStepRecord>> SynthesizeStepsAsync(
        string processInstanceId,
        IReadOnlyList<WorkflowApprovalRecord> approvals,
        DateTime? startedAt,
        CancellationToken ct)
    {
        var steps = new List<WorkflowStepRecord>(approvals.Count + 1);
        var cursor = startedAt;

        foreach (var approval in approvals)
        {
            steps.Add(new WorkflowStepRecord(
                // 业务表可能只存了环节名没存节点 id（deal_history 就是这样），退化为用名字顶上
                ActivityId: approval.ActivityId ?? approval.ActivityName ?? string.Empty,
                ActivityName: approval.ActivityName,
                ActivityType: "userTask",
                TaskId: approval.TaskId,
                Assignee: approval.Assignee,
                Decision: approval.Decision,
                DecisionName: approval.DecisionName,
                Comment: approval.Comment,
                StartedAt: cursor,
                EndedAt: approval.SubmittedAt));

            cursor = approval.SubmittedAt;
        }

        var tasks = await _engine
            .FindTasksAsync(new TaskQuery(ProcessInstanceId: processInstanceId), ct)
            .ConfigureAwait(false);

        foreach (var task in tasks)
        {
            steps.Add(new WorkflowStepRecord(
                ActivityId: task.TaskDefinitionKey ?? string.Empty,
                ActivityName: task.Name,
                ActivityType: "userTask",
                TaskId: task.Id,
                Assignee: task.Assignee,
                Decision: null,
                DecisionName: null,
                Comment: null,
                StartedAt: task.CreateTime ?? cursor,
                EndedAt: null));
        }

        return steps;
    }
}

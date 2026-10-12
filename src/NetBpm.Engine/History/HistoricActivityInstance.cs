namespace NetBpm.Engine.History;

/// <summary>
/// 历史活动实例。对应 Activiti 的 <c>HistoricActivityInstance</c> / <c>ACT_HI_ACTINST</c> 一行。
/// <para>
/// 🔑 它是<b>流程图着色</b>的数据源（§6.2 第 5 项）：按 <c>ACT_ID_</c> 与 <c>END_TIME_</c> 即可
/// 判断每个节点「没走过 / 走过 / 正在走」。
/// </para>
/// </summary>
/// <param name="Id"><c>ID_</c>（新生成的 id，与执行/任务 id 无关）。</param>
/// <param name="ProcessInstanceId"><c>PROC_INST_ID_</c>。</param>
/// <param name="ExecutionId"><c>EXECUTION_ID_</c>。</param>
/// <param name="ActivityId"><c>ACT_ID_</c>。</param>
/// <param name="ActivityName"><c>ACT_NAME_</c>。</param>
/// <param name="ActivityType"><c>ACT_TYPE_</c>（元素类名首字母小写）。</param>
/// <param name="TaskId"><c>TASK_ID_</c>（用户任务被回填）。</param>
/// <param name="Assignee"><c>ASSIGNEE_</c>（被回填）。</param>
/// <param name="StartTime"><c>START_TIME_</c>。</param>
/// <param name="EndTime"><c>END_TIME_</c>；为空表示该活动尚未结束。</param>
public sealed record HistoricActivityInstance(
    string Id,
    string ProcessInstanceId,
    string? ExecutionId,
    string ActivityId,
    string? ActivityName,
    string? ActivityType,
    string? TaskId,
    string? Assignee,
    DateTime? StartTime,
    DateTime? EndTime)
{
    /// <summary>是否正在执行该活动（<c>EndTime is null</c>）—— 流程图着色的判据。</summary>
    public bool IsActive => EndTime is null;
}

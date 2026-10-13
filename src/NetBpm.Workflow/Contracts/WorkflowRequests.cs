using System.Text.Json;

namespace NetBpm.Workflow;

/// <summary>
/// 发起流程申请。
/// </summary>
/// <remarks>
/// 只要给出申请人与业务上下文即可；业务参数（天数 / 事由之类）由后续环节的表单或
/// <see cref="ParaDict"/> 采集 —— 这样「发起」这件事对所有流程都是同一套入参。
/// </remarks>
/// <param name="Applicant">
/// 申请人（落成流程变量 <c>applyUser</c>，供 <c>assignee="${applyUser}"</c> 解析）。
/// </param>
/// <param name="BusinessKey">业务主键（可选，便于与外部单据关联；同时写入变量 <c>businessId</c>）。</param>
/// <param name="BusinessDeptId">业务部门 id（可选）。</param>
/// <param name="ApplyDeptId">申请部门 id（可选）。</param>
/// <param name="ParaDict">
/// 流程参数（可选，如 <c>{"days": 2, "reason": "年假"}</c>）。
/// <para>
/// 它有两个去处：<b>展开成扁平流程变量</b>（网关条件要用），
/// 以及<b>写入流程参数表</b>（业务侧留痕，见 <c>IWorkflowDataStore.SaveParametersAsync</c>）。
/// </para>
/// </param>
public sealed record StartWorkflowRequest(
    string Applicant,
    string? BusinessKey = null,
    string? BusinessDeptId = null,
    string? ApplyDeptId = null,
    IDictionary<string, JsonElement>? ParaDict = null);

/// <summary>
/// 提交流程审批。
/// </summary>
/// <param name="Values">
/// 表单值，键为 <c>activiti:formProperty</c> 的 <c>id</c>。
/// 保留 <see cref="JsonElement"/> 以区分「数字」与「字符串」（同 <c>NetBpm.Rest</c> 的做法），
/// 真正的类型转换由表单属性上的 <c>type</c> 决定。
/// </param>
/// <param name="ParaDict">
/// 流程参数更新（可选）。<b>有值就按 key 更新</b>，没提到的键保持原样。
/// <para>
/// 审批人通常不需要动参数（表单字段会自己进参数表）；这个入口是给「审批过程中要补参数」
/// 的场景预留的 —— 比如审批人指定 <c>city</c>、<c>backupUser</c> 之类。
/// 参数名与审批元数据字段重名会被拒（见 <see cref="ParaDict.ConflictingKeys"/>）。
/// </para>
/// </param>
/// <param name="Operator">
/// 办理人（可选）。未传则用任务上的 <c>assignee</c>。
/// <para>
/// 它的作用只有一个：让审批历史记得住「这一步是谁批的」——
/// 按候选组审批的任务在认领前 <c>assignee</c> 是空的，光看任务看不出提交人。
/// </para>
/// </param>
public sealed record SubmitWorkflowTaskRequest(
    IDictionary<string, JsonElement>? Values = null,
    IDictionary<string, JsonElement>? ParaDict = null,
    string? Operator = null);

using System.Text.Json.Serialization;

namespace NetBpm.Workflow;

/// <summary>
/// 流程业务基础模型 —— 所有审批类流程共用的一组字段。
/// </summary>
/// <remarks>
/// <para>
/// 这些字段在引擎里以<b>扁平流程变量</b>存在（一个字段一个变量），不是打包成一个对象。
/// 这么做是刻意的：网关条件可以直接写 <c>${result == 1}</c>，不必写成 <c>${baseModel.result == 1}</c>，
/// 与 Activiti 既有流程、以及设计器里画出来的图保持一致。
/// </para>
/// <para>字段来源分两类：</para>
/// <list type="bullet">
///   <item><description>
///   <b>发起时写入</b>（宿主提供）：<c>businessId</c>、<c>businessDeptId</c>、<c>applyUser</c>、
///   <c>applyTime</c>、<c>applyDeptId</c>；
///   </description></item>
///   <item><description>
///   <b>审批时写入</b>（审批人从表单填）：<c>result</c>、<c>comment</c> ——
///   对应流程定义里 <c>approvalResult</c> 与 <c>comment</c> 两个 <c>formProperty</c>。
///   </description></item>
/// </list>
/// <para>
/// ⚠️ 变量名是<b>契约</b>：BPMN 的 <c>formProperty/@variable</c>、网关条件、本模型三处必须一致。
/// 名字集中定义在 <see cref="BaseModelVariables"/>，不要在别处硬编码字符串。
/// </para>
/// </remarks>
/// <param name="BusinessId">业务 Id：与外部单据关联的主键（通常就是发起时传入的业务键）。</param>
/// <param name="BusinessDeptId">业务部门 id。</param>
/// <param name="Result">审批结果；尚未审批时为 <see langword="null"/>。</param>
/// <param name="Comment">审批说明；审批人在表单里填写。</param>
/// <param name="ApplyUser">申请人（同时也是发起人，用于 <c>assignee="${applyUser}"</c>）。</param>
/// <param name="ApplyTime">申请时间（发起时刻）。</param>
/// <param name="ApplyDeptId">申请部门 id。</param>
/// <param name="WorkflowTypeId">
/// 流程类型 id：宿主为每条流程分配的类型标识（如设计器的 modelId
/// 02830358-46ef-40e5-b3b9-4d5c4eb23f9f），随实例登记落库（<c>instances.workflow_type_id</c>）。
/// </param>
public sealed record BaseModel(
    string? BusinessId,
    string? BusinessDeptId,
    ApprovalResult? Result,
    string? Comment,
    string? ApplyUser,
    DateTime? ApplyTime,
    string? ApplyDeptId,
    string? WorkflowTypeId = null)
{
    /// <summary>全空的基础模型（字段齐备但都没有值）。</summary>
    public static BaseModel Empty { get; } = new(
        BusinessId: null,
        BusinessDeptId: null,
        Result: null,
        Comment: null,
        ApplyUser: null,
        ApplyTime: null,
        ApplyDeptId: null,
        WorkflowTypeId: null);

    /// <summary>是否已经产生审批结果（纯计算属性，不参与 JSON 序列化）。</summary>
    [JsonIgnore]
    public bool HasResult => Result is not null;

    /// <summary>
    /// 审批结果的中文名，<b>仅供展示</b>（不写进流程变量）。
    /// <para>
    /// 主要给前端用：这样「1/2/3 各代表什么」只有 <see cref="ApprovalResults"/> 一处定义，
    /// 页面不必再维护一份相同的映射表。
    /// </para>
    /// </summary>
    public string? ResultName => Result?.ToDisplayName();

    /// <summary>
    /// 发起时的基础模型：<c>result</c> 与 <c>comment</c> 此刻还不存在，由审批环节补上。
    /// </summary>
    /// <param name="businessId">业务 Id（可空）。</param>
    /// <param name="businessDeptId">业务部门 id（可空）。</param>
    /// <param name="applyUser">申请人。</param>
    /// <param name="applyTime">申请时间。</param>
    /// <param name="applyDeptId">申请部门 id（可空）。</param>
    /// <param name="workflowTypeId">流程类型 id（可空；宿主按流程信息枚举填，见 <see cref="BaseModel.WorkflowTypeId"/>）。</param>
    public static BaseModel Start(
        string applyUser,
        DateTime applyTime,
        string? businessId = null,
        string? businessDeptId = null,
        string? applyDeptId = null,
        string? workflowTypeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyUser);

        return new BaseModel(
            BusinessId: businessId,
            BusinessDeptId: businessDeptId,
            Result: null,
            Comment: null,
            ApplyUser: applyUser,
            ApplyTime: applyTime,
            ApplyDeptId: applyDeptId,
            WorkflowTypeId: workflowTypeId);
    }

    /// <summary>
    /// 展开成扁平流程变量，直接交给
    /// <c>StartProcessInstanceAsync</c> / <c>CompleteTaskAsync</c>。
    /// </summary>
    /// <remarks>
    /// 值为 <see langword="null"/> 的字段<b>不写入</b> —— 免得往引擎里塞一堆空变量，
    /// 既污染 <c>ACT_RU_VARIABLE</c>，也会让「变量到底有没有值」变得难以判断。
    /// </remarks>
    public Dictionary<string, object?> ToVariables()
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (BusinessId is not null)
        {
            variables[BaseModelVariables.BusinessId] = BusinessId;
        }

        if (BusinessDeptId is not null)
        {
            variables[BaseModelVariables.BusinessDeptId] = BusinessDeptId;
        }

        if (Result is { } result)
        {
            // 落成选项 id 字符串（"1"/"2"/"3"），与 BPMN 里 activiti:value/@id 一致
            variables[BaseModelVariables.Result] = result.ToOptionId();
        }

        if (Comment is not null)
        {
            variables[BaseModelVariables.Comment] = Comment;
        }

        if (ApplyUser is not null)
        {
            variables[BaseModelVariables.ApplyUser] = ApplyUser;
        }

        if (ApplyTime is { } applyTime)
        {
            variables[BaseModelVariables.ApplyTime] = applyTime;
        }

        if (ApplyDeptId is not null)
        {
            variables[BaseModelVariables.ApplyDeptId] = ApplyDeptId;
        }

        if (WorkflowTypeId is not null)
        {
            variables[BaseModelVariables.WorkflowTypeId] = WorkflowTypeId;
        }

        return variables;
    }

    /// <summary>
    /// 从流程变量重建基础模型。
    /// </summary>
    /// <param name="variables">流程变量快照；为 <see langword="null"/> 或缺少某字段时不算错。</param>
    /// <param name="fallback">
    /// 兜底值：某个字段在 <paramref name="variables"/> 里读不到（或读不出）时用它。
    /// <para>
    /// 典型场景是服务任务：它跑在子执行上，而变量落在流程实例根，一旦变量已被清理，
    /// 用宿主自己登记的那份兜底比拿到一堆 <c>null</c> 更有意义。
    /// </para>
    /// </param>
    public static BaseModel FromVariables(
        IReadOnlyDictionary<string, object?>? variables,
        BaseModel? fallback = null)
    {
        var basis = fallback ?? Empty;

        object? Read(string name) =>
            variables is not null && variables.TryGetValue(name, out var value) ? value : null;

        return new BaseModel(
            BusinessId: VariableValues.AsString(Read(BaseModelVariables.BusinessId)) ?? basis.BusinessId,
            BusinessDeptId: VariableValues.AsString(Read(BaseModelVariables.BusinessDeptId)) ?? basis.BusinessDeptId,
            Result: ApprovalResults.TryParse(Read(BaseModelVariables.Result)) ?? basis.Result,
            Comment: VariableValues.AsString(Read(BaseModelVariables.Comment)) ?? basis.Comment,
            ApplyUser: VariableValues.AsString(Read(BaseModelVariables.ApplyUser)) ?? basis.ApplyUser,
            ApplyTime: VariableValues.AsDateTime(Read(BaseModelVariables.ApplyTime)) ?? basis.ApplyTime,
            ApplyDeptId: VariableValues.AsString(Read(BaseModelVariables.ApplyDeptId)) ?? basis.ApplyDeptId,
            WorkflowTypeId: VariableValues.AsString(Read(BaseModelVariables.WorkflowTypeId)) ?? basis.WorkflowTypeId);
    }
}

/// <summary>
/// <see cref="BaseModel"/> 各字段对应的流程变量名。
/// </summary>
/// <remarks>
/// 单独抽出来是为了让「BPMN 里的 <c>variable</c> 属性」「网关条件」「宿主代码」三处引用同一个常量，
/// 改名时编译器会替我们找出所有遗漏点。
/// </remarks>
public static class BaseModelVariables
{
    /// <summary>业务 Id。</summary>
    public const string BusinessId = "businessId";

    /// <summary>业务部门 id。</summary>
    public const string BusinessDeptId = "businessDeptId";

    /// <summary>审批结果（取值 = <see cref="ApprovalResults.ToOptionId"/> 的字符串）。</summary>
    public const string Result = "result";

    /// <summary>审批说明。</summary>
    public const string Comment = "comment";

    /// <summary>申请人。</summary>
    public const string ApplyUser = "applyUser";

    /// <summary>申请时间。</summary>
    public const string ApplyTime = "applyTime";

    /// <summary>申请部门 id。</summary>
    public const string ApplyDeptId = "applyDeptId";

    /// <summary>流程类型 id（宿主为每条流程分配的类型标识，随实例登记落 <c>instances.workflow_type_id</c>）。</summary>
    public const string WorkflowTypeId = "workflowTypeId";

    /// <summary>全部变量名（按 <see cref="BaseModel"/> 的字段顺序）。</summary>
    public static IReadOnlyList<string> All { get; } =
        [BusinessId, BusinessDeptId, Result, Comment, ApplyUser, ApplyTime, ApplyDeptId, WorkflowTypeId];
}

namespace NetBpm.Demo.Dept;

/// <summary>
/// 部门审批流程的业务参数名 —— 与 BPMN 表单字段、流程参数表三处共用同一常量。
/// </summary>
/// <remarks>
/// <para>
/// 本流程的核心变量是 <c>applyDeptId</c>（申请部门 id）：它不是业务参数，而是
/// <see cref="NetBpm.Workflow.BaseModelVariables.ApplyDeptId"/>（发起时由
/// <c>StartWorkflowRequest.ApplyDeptId</c> 写入的扁平变量）。动态审批人解析器
/// <c>DynamicApproverResolver</c> 正是读它来定位「按部门查经理」的部门。
/// </para>
/// </remarks>
public static class DeptParameterNames
{
    /// <summary>申请部门 id（发起时经 <c>StartWorkflowRequest.ApplyDeptId</c> 写入流程变量 <c>applyDeptId</c>）。</summary>
    public const string ApplyDeptId = "applyDeptId";
}

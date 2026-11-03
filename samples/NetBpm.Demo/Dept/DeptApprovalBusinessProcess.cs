using NetBpm.Demo.Common;
using NetBpm.Demo.Workflow;
using NetBpm.Workflow;

namespace NetBpm.Demo.Dept;

/// <summary>
/// 部门审批流程的接入实现：单号（毫秒级精度，可由调用方指定）校验 / 发号 →
/// 组装 <c>applyDeptId</c>（申请部门，供动态审批人解析器定位「该部门的经理」）。
/// 无业务单表 —— 参数落流程参数表，无发起收尾。
/// </summary>
/// <remarks>
/// <para>
/// <b>演示点</b>：本流程的经理审批节点<b>不写任何静态审批人</b>。引擎进入该节点时，
/// 会以空办理人集合调用宿主注册的 <c>IApproverResolver</c>（见
/// <c>Approval/DynamicApproverResolver.cs</c>），解析器读流程变量 <c>applyDeptId</c> +
/// 节点 id（<c>task_manager_approve</c> → 职位「经理」）查外部系统，返回该部门的经理作为审批人。
/// </para>
/// <para>
/// 发起请求示例：<c>POST /api/dept/business/start</c>
/// <c>{ "applicant": "alice", "applyDeptId": "102" }</c>
/// （102 = 成本管理部 → 解析器返回 manager1；101 = 信息技术部 → leader1；103 → manager1）
/// </para>
/// </remarks>
public sealed class DeptApprovalBusinessProcess : IBusinessProcess
{
    /// <inheritdoc />
    public string ProcessKey => ConstField.DeptProcessKey;

    /// <inheritdoc />
    public string ResourceName => ConstField.DeptResourceName;

    /// <inheritdoc />
    public string DeploymentName => ConstField.DeptDeploymentName;

    /// <inheritdoc />
    public string ApiPrefix => ConstField.DeptApiPrefix;

    /// <inheritdoc />
    public string WorkflowTypeId => ProcessInfoEnum.Require(ConstField.DeptProcessKey).Id;

    /// <inheritdoc />
    public ValueTask<StartWorkflowRequest> PrepareStart(StartWorkflowRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(ResolveStartRequest(request));

    /// <summary>
    /// 发起前的业务处理（同步版）：单号留空自动发号（毫秒级精度），校验为正整数，
    /// 补默认申请部门（缺省 102 成本管理部，保证演示能定位到经理），并填入流程类型 id。
    /// </summary>
    public StartWorkflowRequest ResolveStartRequest(StartWorkflowRequest request)
    {
        var businessKey = string.IsNullOrWhiteSpace(request.BusinessKey)
            ? BusinessKeys.NextMilliseconds()
            : request.BusinessKey.Trim();

        if (!BusinessKeys.IsValid(businessKey))
        {
            throw new WorkflowValidationException(
                $"businessKey 必须是正整数（业务表里 business_id 是 bigint），当前为 '{request.BusinessKey}'。");
        }

        // applyDeptId 是动态审批人的关键输入：发起时给默认值，保证演示可复现。
        var applyDeptId = string.IsNullOrWhiteSpace(request.ApplyDeptId)
            ? "102"
            : request.ApplyDeptId.Trim();

        return request with
        {
            BusinessKey = businessKey,
            ApplyDeptId = applyDeptId,
            WorkflowTypeId = WorkflowTypeId,
        };
    }
}

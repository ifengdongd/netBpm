using NetBpm.Demo.Common;
using NetBpm.Demo.Workflow;
using NetBpm.Workflow;

namespace NetBpm.Demo.Leave;

/// <summary>
/// 请假审批流程的接入实现：单号（毫秒级精度，可由调用方指定）校验 / 发号 →
/// 组装流程参数（days / reason）。无业务单表 —— 参数落流程参数表，无发起收尾。
/// </summary>
public sealed class LeaveBusinessProcess : IBusinessProcess
{
    /// <inheritdoc />
    public string ProcessKey => ConstField.LeaveProcessKey;

    /// <inheritdoc />
    public string ResourceName => ConstField.LeaveResourceName;

    /// <inheritdoc />
    public string DeploymentName => ConstField.LeaveDeploymentName;

    /// <inheritdoc />
    public string ApiPrefix => ConstField.LeaveApiPrefix;

    /// <inheritdoc />
    public string WorkflowTypeId => ProcessInfoEnum.Require(ConstField.LeaveProcessKey).Id;

    /// <inheritdoc />
    /// <remarks>业务参数从 <c>ParaDict</c> 取（days / reason，键名见 <see cref="LeaveParameterNames"/>）。</remarks>
    public ValueTask<StartWorkflowRequest> PrepareStart(StartWorkflowRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(ResolveStartRequest(request));

    /// <summary>
    /// 发起前的业务处理（同步版）：单号留空自动发号（毫秒级精度），校验为正整数，
    /// 并填入流程类型 id（随实例登记落 workflow_type_id）。
    /// </summary>
    /// <remarks>框架的 <c>TransformStartRequest</c> 钩子是同步的，所以这里提供同步入口。</remarks>
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

        return request with
        {
            BusinessKey = businessKey,
            WorkflowTypeId = WorkflowTypeId,
        };
    }
}

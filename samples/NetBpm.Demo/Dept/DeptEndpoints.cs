using NetBpm.Demo.Common;
using NetBpm.Demo.Workflow;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Dept;

/// <summary>
/// 部门审批 Demo 的 HTTP 适配层。
/// </summary>
/// <remarks>
/// <para>
/// 与请假 / 订单一样，流程相关端点（发起 / 实例 / 历史 / 待办 / 提交 / 流程定义）全部来自公共的
/// <see cref="WorkflowEndpoints"/>，本类只注入部门审批的两件事：
/// <b>业务单号生成与校验</b>（<see cref="DeptApprovalBusinessProcess"/>，经 <c>TransformStartRequest</c>）、
/// <b>错误响应形状</b>。
/// </para>
/// <para>
/// 本流程的核心演示点是<b>动态审批人</b>：经理审批节点不写任何静态审批人，引擎进入时调用
/// 宿主注册的 <c>IApproverResolver</c>（<c>DynamicApproverResolver</c>）按 <c>applyDeptId</c> + 节点
/// 定位该部门的经理。因此实例视图不做业务列摊平（无 days/reason 这类业务参数），直接透传标准视图。
/// </para>
/// </remarks>
public static class DeptEndpoints
{
    public static RouteGroupBuilder MapDeptEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ConstField.DeptApiPrefix).WithTags("dept");

#pragma warning disable CS0618 // 演示宿主：旧面迁移期示例（与请假/订单保持一致）
        group.MapWorkflowEndpoints(string.Empty, new WorkflowEndpointOptions
        {
            ProcessKey = _ => ConstField.DeptProcessKey,

            // 发起前生成 / 校验业务单号、补默认申请部门：逻辑与 DeptApprovalBusinessProcess.PrepareStart 同源
            TransformStartRequest = (_, request) => new DeptApprovalBusinessProcess().ResolveStartRequest(request),

            // 本流程无业务列摊平需求，透传标准实例视图（动态审批人写入的候选用户由待办中心按 candidate 查询可见）
            DecorateInstanceView = (_, view) => ValueTask.FromResult<object?>(view),

            ErrorResponseFactory = (message, errors) => new ErrorResponse(message, errors),

            // ⚠️ 演示宿主没有鉴权，显式放开转办 / 跳转（与请假/订单一致；生产宿主必须校验管理 scope）
            AuthorizeAdminAction = (_, _, _) => ValueTask.FromResult(true),
        });
#pragma warning restore CS0618

        return group;
    }
}

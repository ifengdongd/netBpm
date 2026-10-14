using NetBpm.Workflow;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 请假审批 Demo 的 HTTP 适配层。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 流程相关端点（发起 / 实例列表与详情 / 历史 / 待办 / 提交 / 流程定义）全部来自公共的
/// <see cref="WorkflowEndpoints"/>，本类只注入请假业务的三件事：
/// <b>业务单号生成与校验</b>（<see cref="LeaveBusinessKey"/>，经 <c>TransformStartRequest</c>）、
/// <b>视图摊平</b>（天数 / 事由从流程参数摊成列）、<b>错误响应形状</b>（<see cref="LeaveErrorResponse"/>）。
/// 端点下面只剩业务数据核对入口与 DTO 映射；换成 gRPC、消息队列、控制台调用时，业务代码一行都不用改。
/// </para>
/// <para>设计要点：<b>前端不写死任何审批选项</b>。实例视图会把当前待办连同它的表单定义
/// （含 <c>approvalResult</c> 的候选值）一次返回，前端照着渲染；提交时回传所选值即可。</para>
/// </remarks>
public static class LeaveEndpoints
{
    public static RouteGroupBuilder MapLeaveEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/leave").WithTags("leave");

        // 流程通用的七个端点：发起 / 实例列表 / 实例详情 / 历史 / 待办 / 提交 / 流程定义。
        // 路由与既有 Demo、e2e 测试保持一致（挂在 /api/leave 前缀下）。
        group.MapWorkflowEndpoints(string.Empty, new WorkflowEndpointOptions
        {
            ProcessKey = _ => LeaveProcessBootstrapper.ProcessKey,

            // 发起前生成 / 校验业务单号：请假单号必须是正整数（业务表里 business_id 是 bigint）。
            // 校验不过抛 WorkflowValidationException → 统一映射成 400 连同明细。
            TransformStartRequest = (_, request) =>
            {
                var businessKey = string.IsNullOrWhiteSpace(request.BusinessKey)
                    ? LeaveBusinessKey.Next()
                    : request.BusinessKey.Trim();

                if (!LeaveBusinessKey.IsValid(businessKey))
                {
                    throw new WorkflowValidationException(
                        $"businessKey 必须是正整数（业务表里 business_id 是 bigint），当前为 '{request.BusinessKey}'。");
                }

                return request with { BusinessKey = businessKey };
            },

            DecorateInstanceView = (_, view) => ToInstanceView(view),
            DecorateInstanceSummary = (_, summary) => ToInstanceSummary(summary),
            ErrorResponseFactory = (message, errors) => new LeaveErrorResponse(message, errors),
        });

        // 监听器触发计数（观测用）：e2e 借它断言「监听器真的被引擎调到了」。
        group.MapGet("/listener-stats", GetListenerStatsAsync);

        // 业务数据核对入口（请假业务自有，与流程引擎无关）：
        // 落点自述 + 全部业务数据，方便打开核对。只依赖 ILeaveBusinessInspector，
        // 所以 Leave:Store 在 JSON / MySql 之间切换时这里一行都不用改
        // （键名由实现方给：JSON 实现给区块名，MySQL 实现给表名）。
        group.MapGet("/business", GetBusinessAsync);

        return group;
    }

    private static IResult GetListenerStatsAsync(ListenerStats stats) => Results.Ok(stats.Snapshot());

    private static async Task<IResult> GetBusinessAsync(
        ILeaveBusinessInspector inspector,
        CancellationToken ct)
    {
        var snapshot = await inspector.SnapshotAsync(ct).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>(snapshot, StringComparer.Ordinal)
        {
            ["store"] = inspector.Store,
            ["location"] = inspector.Location,
        };

        return Results.Ok(payload);
    }

    // ─────────────────────────── DTO 映射 ───────────────────────────

    /// <summary>应用层视图 → Demo 的 HTTP 视图（只差 <c>result</c> 的形状：天数 / 事由摊成两列）。</summary>
    private static InstanceView ToInstanceView(WorkflowInstanceView view) => new(
        ProcessInstanceId: view.ProcessInstanceId,
        BusinessKey: view.BusinessKey,
        ProcessDefinitionId: view.ProcessDefinitionId,
        Running: view.Running,
        Variables: view.Variables,
        Tasks: view.Tasks,
        Result: LeaveResultRecord.From(view.Result),
        StartedAt: view.StartedAt,
        Base: view.Base,
        Parameters: view.Parameters);

    /// <summary>应用层摘要 → Demo 的列表行（天数 / 事由同样从流程参数里取）。</summary>
    private static InstanceSummary ToInstanceSummary(WorkflowInstanceSummary summary) => new(
        ProcessInstanceId: summary.ProcessInstanceId,
        BusinessKey: summary.Base.BusinessId,
        Applicant: summary.Base.ApplyUser ?? string.Empty,
        Days: LeaveParameterNames.DaysOf(summary.Parameters),
        Reason: LeaveParameterNames.ReasonOf(summary.Parameters),
        StartedAt: summary.StartedAt,
        Status: summary.Status,
        Outcome: summary.Result?.ActivityName);
}

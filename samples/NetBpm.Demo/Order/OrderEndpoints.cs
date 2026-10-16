using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NetBpm.Demo.Common;
using NetBpm.Demo.Workflow;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Order;

/// <summary>
/// 订单审批 Demo 的 HTTP 适配层。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 流程相关端点（实例列表与详情 / 历史 / 待办 / 提交 / 流程定义）全部来自公共的
/// <see cref="WorkflowEndpoints"/>（挂在 <c>/api/order</c> 前缀下）；本类只补订单业务的三件事：
/// <b>提交申请</b>（<c>POST /bills</c>：建订单行 status=1 → 发起流程 → 回填实例 id）、
/// <b>订单视角的查询</b>（列表 / 详情 / 审批历史，按 <c>business_id</c> 反查）、
/// <b>错误响应形状</b>（<see cref="OrderErrorResponse"/>）。
/// </para>
/// <para>
/// 状态语义：<c>order_bill.status</c> 由三处写入 —— 提交申请置 1（审批中）；
/// 流程走到 <c>order.ApproveComplete</c> / <c>order.RefuseComplete</c> 时由委托回写 100 / -1。
/// </para>
/// </remarks>
public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ConstField.OrderApiPrefix).WithTags("order");

        // 流程通用端点（提交审批走这里：POST /api/order/tasks/{taskId}/submit）。
        // 注意：直接 POST /api/order/instances 发起流程不会建订单行，委托回写状态时会失败 ——
        // 业务入口请走 POST /api/order/bills（提交申请）。
        group.MapWorkflowEndpoints(string.Empty, new WorkflowEndpointOptions
        {
            ProcessKey = _ => ConstField.OrderProcessKey,
            ErrorResponseFactory = (message, errors) => new ErrorResponse(message, errors),
        });

        // ─── 订单业务（与流程引擎无关的 CRUD 视角） ───
        group.MapPost("/bills", CreateAsync);                              // 提交申请：建订单（status=1）并发起流程
        group.MapGet("/bills", ListAsync);                                 // 订单列表（含状态与当前环节）
        group.MapGet("/bills/{businessId:long}", GetAsync);                // 订单详情（含审批进度）
        group.MapGet("/bills/{businessId:long}/history", GetHistoryAsync); // 按订单反查审批历史
        group.MapDelete("/bills/{businessId:long}", DeleteAsync);          // 删除订单（伪删除：置 deleted_flag）

        return group;
    }

    // ─────────────────────────── 提交申请 ───────────────────────────

    /// <summary>
    /// 提交订单申请：委托给 <see cref="OrderBusinessProcess"/>（发号 → 建
    /// <c>order_bill</c> 行 → 发起流程 → 回填实例 id）。返回订单视图与首个待办。
    /// </summary>
    /// <remarks>页面兼容入口；统一业务入口是 <c>POST /api/order/business/start</c>（ParaDict 携带业务参数）。</remarks>
    private static async Task<IResult> CreateAsync(
        CreateOrderBillRequest request,
        IEnumerable<IBusinessProcess> processes,
        IWorkflowService workflow,
        IOrderBillStore store,
        CancellationToken ct)
    {
        var process = processes.First(p => p.ProcessKey == ConstField.OrderProcessKey);

        try
        {
            // 业务参数打进 ParaDict，走统一的 IBusinessProcess.PrepareStart（发号 + 建单 + 组参）
            var startRequest = new StartWorkflowRequest(
                Applicant: request.Applicant,
                BusinessDeptId: request.BusinessDeptId,
                ApplyDeptId: request.ApplyDeptId,
                ParaDict: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [OrderParameterNames.ProductName] = JsonSerializer.SerializeToElement(request.ProductName),
                    [OrderParameterNames.Amount] = JsonSerializer.SerializeToElement(request.Amount),
                    [OrderParameterNames.Remark] = JsonSerializer.SerializeToElement(request.Remark),
                });

            var prepared = await process.PrepareStart(startRequest, ct).ConfigureAwait(false);
            var view = await workflow
                .StartAsync(ConstField.OrderProcessKey, prepared, ct)
                .ConfigureAwait(false);

            if (prepared.BusinessKey is not { } businessKey)
            {
                return Results.NotFound(new ErrorResponse("发起成功但业务单号缺失（数据异常）。"));
            }

            await process.OnStartedAsync(businessKey, view.ProcessInstanceId, ct).ConfigureAwait(false);

            var bill = await store
                .FindAsync(long.Parse(businessKey, CultureInfo.InvariantCulture), ct)
                .ConfigureAwait(false);

            return bill is null
                ? Results.NotFound(new ErrorResponse($"订单 {businessKey} 刚创建却查不到（数据异常）。"))
                : Results.Created($"/api/order/bills/{bill.BusinessId}", ToView(bill, view));
        }
        catch (WorkflowValidationException exception)
        {
            return Results.BadRequest(new ErrorResponse(exception.Message, exception.Errors));
        }
    }

    // ─────────────────────────── 查询 ───────────────────────────

    /// <summary>订单列表：每行带上状态与当前环节（进行中的反查流程实例）。</summary>
    private static async Task<IResult> ListAsync(IOrderBillStore store, IWorkflowService workflow, CancellationToken ct)
    {
        var bills = await store.ListAsync(ct).ConfigureAwait(false);
        var views = new List<OrderBillView>(bills.Count);

        foreach (var bill in bills)
        {
            views.Add(await ToViewAsync(bill, workflow, ct).ConfigureAwait(false));
        }

        return Results.Ok(views);
    }

    /// <summary>订单详情：订单行 + 审批进度（当前环节 / 待办 id / 结论）。</summary>
    private static async Task<IResult> GetAsync(long businessId, IOrderBillStore store, IWorkflowService workflow, CancellationToken ct)
    {
        var bill = await store.FindAsync(businessId, ct).ConfigureAwait(false);

        if (bill is null)
        {
            return Results.NotFound(new ErrorResponse($"订单 {businessId} 不存在。"));
        }

        return Results.Ok(await ToViewAsync(bill, workflow, ct).ConfigureAwait(false));
    }

    /// <summary>按订单反查审批历史（环节序列 + 每次审批的提交记录 + 结论）。</summary>
    private static async Task<IResult> GetHistoryAsync(long businessId, IOrderBillStore store, IWorkflowService workflow, CancellationToken ct)
    {
        var bill = await store.FindAsync(businessId, ct).ConfigureAwait(false);

        if (bill is null)
        {
            return Results.NotFound(new ErrorResponse($"订单 {businessId} 不存在。"));
        }

        if (bill.ProcessInstanceId is not { } processInstanceId)
        {
            return Results.Ok(new ErrorResponse($"订单 {businessId} 尚未发起审批流程。"));
        }

        var history = await workflow.FindHistoryAsync(processInstanceId, ct).ConfigureAwait(false);

        return history is null
            ? Results.NotFound(new ErrorResponse($"订单 {businessId} 的流程实例 {processInstanceId} 不存在。"))
            : Results.Ok(history);
    }

    /// <summary>删除订单（伪删除：置 deleted_flag，列表与明细自动过滤）。返回是否删到。</summary>
    private static async Task<IResult> DeleteAsync(
        long businessId,
        IOrderBillStore store,
        CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(businessId, ct).ConfigureAwait(false);

        return deleted
            ? Results.Ok(new ErrorResponse($"订单 {businessId} 已删除（伪删除）。"))
            : Results.NotFound(new ErrorResponse($"订单 {businessId} 不存在（或已删除）。"));
    }

    // ─────────────────────────── 视图组装 ───────────────────────────

    /// <summary>发起流程的响应直接由环节视图组装（首个待办就是提交申请后的下一步）。</summary>
    private static OrderBillView ToView(OrderBillRow bill, WorkflowInstanceView view) => new(
        Bill: bill,
        ProcessInstanceId: view.ProcessInstanceId,
        Running: view.Running,
        CurrentStep: view.Tasks.Count > 0 ? view.Tasks[0].Name : null,
        TaskId: view.Tasks.Count > 0 ? view.Tasks[0].TaskId : null,
        Outcome: view.Result?.Outcome);

    private static async ValueTask<OrderBillView> ToViewAsync(OrderBillRow bill, IWorkflowService workflow, CancellationToken ct)
    {
        if (bill.ProcessInstanceId is not { } processInstanceId)
        {
            return new OrderBillView(bill, null, Running: false, null, null, null);
        }

        var view = await workflow.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        // 流程实例视图查不到（理论上不该发生）：按订单行自己的状态兜底
        if (view is null)
        {
            return new OrderBillView(
                bill,
                processInstanceId,
                Running: bill.Status == OrderBillStatus.UnderReview,
                CurrentStep: null,
                TaskId: null,
                Outcome: bill.Status == OrderBillStatus.Approved ? WorkflowOutcomes.Approved
                    : bill.Status == OrderBillStatus.Refused ? WorkflowOutcomes.Refused
                    : null);
        }

        return new OrderBillView(
            Bill: bill,
            ProcessInstanceId: view.ProcessInstanceId,
            Running: view.Running,
            CurrentStep: view.Tasks.Count > 0 ? view.Tasks[0].Name : null,
            TaskId: view.Tasks.Count > 0 ? view.Tasks[0].TaskId : null,
            Outcome: view.Result?.Outcome);
    }
}

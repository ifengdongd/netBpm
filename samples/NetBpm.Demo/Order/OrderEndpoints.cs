using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NetBpm.Demo.Common;
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
            ErrorResponseFactory = (message, errors) => new OrderErrorResponse(message, errors),
        });

        // ─── 订单业务（与流程引擎无关的 CRUD 视角） ───
        group.MapPost("/bills", CreateAsync);                              // 提交申请：建订单（status=1）并发起流程
        group.MapGet("/bills", ListAsync);                                 // 订单列表（含状态与当前环节）
        group.MapGet("/bills/{businessId:long}", GetAsync);                // 订单详情（含审批进度）
        group.MapGet("/bills/{businessId:long}/history", GetHistoryAsync); // 按订单反查审批历史

        return group;
    }

    // ─────────────────────────── 提交申请 ───────────────────────────

    /// <summary>
    /// 提交订单申请：生成订单号 → 建 <c>order_bill</c> 行（<see cref="OrderBillStatus.UnderReview"/>）→
    /// 发起订单审批流程 → 回填流程实例 id。返回订单视图与首个待办。
    /// </summary>
    private static async Task<IResult> CreateAsync(
        CreateOrderBillRequest request,
        IWorkflowService workflow,
        IOrderBillStore store,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Applicant))
        {
            return Results.BadRequest(new OrderErrorResponse("applicant（申请人）必填。"));
        }

        if (request.Amount is < 0)
        {
            return Results.BadRequest(new OrderErrorResponse("amount（订单金额）不能为负。"));
        }

        var tenantId = configuration.GetValue(ConstField.OrderTenantConfigKey, 1L);
        var businessId = long.Parse(OrderBusinessKey.Next(), CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;

        // ① 订单行落库：status = 1（审批中）。先有订单再有流程 —— 流程参数与 businessId 都指向它
        var bill = await store.AddAsync(
            new OrderBillRow(
                TenantId: tenantId,
                BusinessId: businessId,
                ProductName: request.ProductName,
                Amount: request.Amount,
                Remark: request.Remark,
                Status: OrderBillStatus.UnderReview,
                ApproveComment: null,
                ApplyUser: request.Applicant.Trim(),
                ApplyDeptId: request.ApplyDeptId,
                BusinessDeptId: request.BusinessDeptId,
                ProcessInstanceId: null,
                CreateTime: now,
                ModifyTime: null),
            ct).ConfigureAwait(false);

        // ② 发起流程：BaseModel 字段 + 订单参数（product_name / amount / remark）一起进流程。
        // WorkflowTypeId = 流程信息枚举里的流程类型 id（02830358-…），随实例登记落 workflow_type_id
        var view = await workflow.StartAsync(
            ConstField.OrderProcessKey,
            new StartWorkflowRequest(
                Applicant: bill.ApplyUser,
                BusinessKey: businessId.ToString(CultureInfo.InvariantCulture),
                BusinessDeptId: bill.BusinessDeptId,
                ApplyDeptId: bill.ApplyDeptId,
                ParaDict: BuildParaDict(bill),
                WorkflowTypeId: ProcessInfoEnum.Require(ConstField.OrderProcessKey).Id),
            ct).ConfigureAwait(false);

        // ③ 回填流程实例 id：之后「按订单查审批进度 / 历史」都靠它反查
        bill = await store.SetProcessInstanceAsync(businessId, view.ProcessInstanceId, ct).ConfigureAwait(false)
            ?? bill;

        return Results.Created($"/api/order/bills/{businessId}", ToView(bill, view));
    }

    /// <summary>订单参数 → 流程参数表（键名与 <c>order_bill</c> 真列同名）。</summary>
    private static Dictionary<string, JsonElement> BuildParaDict(OrderBillRow bill)
    {
        var paraDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (bill.ProductName is not null)
        {
            paraDict[OrderParameterNames.ProductName] = JsonSerializer.SerializeToElement(bill.ProductName);
        }

        if (bill.Amount is { } amount)
        {
            paraDict[OrderParameterNames.Amount] = JsonSerializer.SerializeToElement(amount);
        }

        if (bill.Remark is not null)
        {
            paraDict[OrderParameterNames.Remark] = JsonSerializer.SerializeToElement(bill.Remark);
        }

        return paraDict;
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
            return Results.NotFound(new OrderErrorResponse($"订单 {businessId} 不存在。"));
        }

        return Results.Ok(await ToViewAsync(bill, workflow, ct).ConfigureAwait(false));
    }

    /// <summary>按订单反查审批历史（环节序列 + 每次审批的提交记录 + 结论）。</summary>
    private static async Task<IResult> GetHistoryAsync(long businessId, IOrderBillStore store, IWorkflowService workflow, CancellationToken ct)
    {
        var bill = await store.FindAsync(businessId, ct).ConfigureAwait(false);

        if (bill is null)
        {
            return Results.NotFound(new OrderErrorResponse($"订单 {businessId} 不存在。"));
        }

        if (bill.ProcessInstanceId is not { } processInstanceId)
        {
            return Results.Ok(new OrderErrorResponse($"订单 {businessId} 尚未发起审批流程。"));
        }

        var history = await workflow.FindHistoryAsync(processInstanceId, ct).ConfigureAwait(false);

        return history is null
            ? Results.NotFound(new OrderErrorResponse($"订单 {businessId} 的流程实例 {processInstanceId} 不存在。"))
            : Results.Ok(history);
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

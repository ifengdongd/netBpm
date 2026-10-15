using System.Text.Json;
using NetBpm.Demo.Common;
using NetBpm.Demo.Workflow;
using NetBpm.Workflow;

namespace NetBpm.Demo.Order;

/// <summary>
/// 订单审批流程的接入实现：发号（秒级精度）→ 建 <c>order_bill</c> 行（status=1）→
/// 组装流程参数（product_name / amount / remark）→ 收尾回填流程实例 id。
/// </summary>
public sealed class OrderBusinessProcess(IOrderBillStore store, IConfiguration configuration) : IBusinessProcess
{
    /// <inheritdoc />
    public string ProcessKey => ConstField.OrderProcessKey;

    /// <inheritdoc />
    public string ResourceName => ConstField.OrderResourceName;

    /// <inheritdoc />
    public string DeploymentName => ConstField.OrderDeploymentName;

    /// <inheritdoc />
    public string ApiPrefix => ConstField.OrderApiPrefix;

    /// <inheritdoc />
    public string WorkflowTypeId => ProcessInfoEnum.Require(ConstField.OrderProcessKey).Id;

    /// <inheritdoc />
    /// <remarks>
    /// 业务参数从 <c>ParaDict</c> 取（product_name / amount / remark，键名见
    /// <see cref="OrderParameterNames"/>）；单号由 <see cref="BusinessKeys"/> 秒级发号。
    /// </remarks>
    public async ValueTask<StartWorkflowRequest> PrepareStart(StartWorkflowRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Applicant))
        {
            throw new WorkflowValidationException("applicant（申请人）必填。");
        }

        var amount = IBusinessProcess.GetDecimal(request.ParaDict, OrderParameterNames.Amount);

        if (amount is < 0)
        {
            throw new WorkflowValidationException("amount（订单金额）不能为负。");
        }

        var businessKey = BusinessKeys.NextSeconds();
        var now = DateTime.UtcNow;

        // ① 订单行落库：status = 1（审批中）。先有订单再有流程 —— 流程参数与 businessId 都指向它
        var bill = await store.AddAsync(
            new OrderBillRow(
                TenantId: configuration.GetValue(ConstField.OrderTenantConfigKey, 1L),
                BusinessId: long.Parse(businessKey, System.Globalization.CultureInfo.InvariantCulture),
                ProductName: IBusinessProcess.GetString(request.ParaDict, OrderParameterNames.ProductName),
                Amount: amount,
                Remark: IBusinessProcess.GetString(request.ParaDict, OrderParameterNames.Remark),
                Status: OrderBillStatus.UnderReview,
                ApproveComment: null,
                ApplyUser: request.Applicant.Trim(),
                ApplyDeptId: request.ApplyDeptId ?? "101",
                BusinessDeptId: request.BusinessDeptId,
                ProcessInstanceId: null,
                CreateTime: now,
                ModifyTime: null),
            ct).ConfigureAwait(false);

        // ② 流程参数与 businessId 一起交给引擎
        return request with
        {
            BusinessKey = businessKey,
            WorkflowTypeId = WorkflowTypeId,
            ParaDict = BuildParaDict(bill),
        };
    }

    /// <inheritdoc />
    /// <remarks>③ 收尾：回填流程实例 id，之后「按订单查审批进度 / 历史」都靠它反查。</remarks>
    public async ValueTask OnStartedAsync(string businessKey, string processInstanceId, CancellationToken ct = default)
    {
        var businessId = long.Parse(businessKey, System.Globalization.CultureInfo.InvariantCulture);
        await store.SetProcessInstanceAsync(businessId, processInstanceId, ct).ConfigureAwait(false);
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
}

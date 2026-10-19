using System.Text.Json;
using NetBpm.Demo.Common;
using NetBpm.Workflow;

namespace NetBpm.Demo.Workflow;

/// <summary>
/// <b>业务流程接入契约</b> —— 新建一条业务流程只需三步：
/// </summary>
/// <remarks>
/// <para>
/// ① 实现本接口（标识 + <see cref="PrepareStart"/> 业务入口 + <see cref="OnStartedAsync"/> 收尾）；
/// ② 提供两个审批收口委托（见 <c>Delegates/ApprovalCompletionDelegate</c>，把结论翻译成业务变更）；
/// ③ BPMN 定义放 <c>processes/</c>、<c>workflow_type</c> 表插一行。
/// </para>
/// <para>
/// 之后<b>全部流程能力自动获得</b>：启动即部署、发起（<c>POST {ApiPrefix}/business/start</c>）、
/// 实例/审批历史/待办/提交、流程图与流转状态图、部署列表（含历史版本）、
/// 跨流程待办中心与申请单列表 —— 无需再写任何端点或部署代码。
/// </para>
/// <para>
/// 流程类型 id 从 <see cref="ProcessInfoEnum"/>（workflow_type 表）按 <see cref="ProcessKey"/> 解析，
/// 随实例登记落 <c>instances.workflow_type_id</c>。
/// </para>
/// </remarks>
public interface IBusinessProcess
{
    /// <summary>流程 key（= BPMN 里 <c>process/@id</c> = workflow_type.name）。</summary>
    string ProcessKey { get; }

    /// <summary>BPMN 资源名（<see cref="ConstField.ProcessResourceDirectory"/> 目录下）。</summary>
    string ResourceName { get; }

    /// <summary>部署名（ACT_RE_DEPLOYMENT.NAME_）。</summary>
    string DeploymentName { get; }

    /// <summary>本流程的 HTTP 端点前缀（统一业务入口挂在它下面）。</summary>
    string ApiPrefix { get; }

    /// <summary>流程类型 id（= workflow_type.id，随实例登记落 workflow_type_id）。</summary>
    string WorkflowTypeId { get; }

    /// <summary>
    /// 发起前的业务处理：业务单号校验 / 发号、业务数据落库、流程参数组装。
    /// </summary>
    /// <remarks>
    /// 入参是通用请求（业务参数放 <c>ParaDict</c>，键名与本流程的参数名常量一致）；
    /// 出参是最终交给引擎的 <see cref="StartWorkflowRequest"/>（<c>BusinessKey</c> 与
    /// <c>WorkflowTypeId</c> 必须已填）。业务校验不过抛 <see cref="WorkflowValidationException"/>
    /// —— 端点统一翻译成 400。
    /// </remarks>
    ValueTask<StartWorkflowRequest> PrepareStart(StartWorkflowRequest request, CancellationToken ct = default);

    /// <summary>
    /// 发起成功后的收尾（如订单回填流程实例 id）；无收尾需求的流程用默认实现。
    /// </summary>
    ValueTask OnStartedAsync(string businessKey, string processInstanceId, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    // ───────────────── ParaDict 取值工具（实现方共用） ─────────────────

    /// <summary>读字符串参数；键不存在或不是字符串返回 <see langword="null"/>。</summary>
    public static string? GetString(IDictionary<string, JsonElement>? paraDict, string key) =>
        paraDict is not null && paraDict.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>读数值参数；键不存在或无法解析返回 <see langword="null"/>。</summary>
    public static decimal? GetDecimal(IDictionary<string, JsonElement>? paraDict, string key)
    {
        if (paraDict is null || !paraDict.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out var number) ? number : null,
            JsonValueKind.String => decimal.TryParse(element.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };
    }
}

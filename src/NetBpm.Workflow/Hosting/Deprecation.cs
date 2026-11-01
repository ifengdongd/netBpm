namespace NetBpm.Workflow;

/// <summary>
/// 旧面（<c>/api/**</c> 过渡形态）的废弃说明。
/// </summary>
/// <remarks>
/// <para>
/// 这一组端点是平台化改造（M1–M16）之前的形态：宿主把自己的业务 DTO 通过 Decorator 塞进响应，
/// 于是「同一份审批数据」在不同宿主的 HTTP 上长得不一样，SDK 无从生成、契约无从治理。
/// </para>
/// <para>
/// 平台面 <c>/workflow/v1</c> 把这两件事拆开了：
/// </para>
/// <list type="bullet">
///   <item><description><b>形状统一</b>：响应一律是 <c>NetBpm.Platform.Contracts</c> 的强类型（SDK 从这里生成）；</description></item>
///   <item><description><b>业务化外置</b>：页面地址 / 标签 / 入口由 <c>/admin/v1/processes</c> 的覆盖行声明，
///     参数列由 BPMN 的 <c>netbpm:parameter</c> 派生，业务单号由调用方生成后放进 <c>StartInstanceRequest.BusinessKey</c>。</description></item>
/// </list>
/// <para>
/// 因此 Decorator 不再是「让业务能落地」的必需品，而是「同一份数据多套形状」的来源。
/// 保留本类只为 Demo 与既有宿主在迁移期跑通，<b>新代码不要引入</b>。
/// </para>
/// </remarks>
internal static class Deprecation
{
    /// <summary><see cref="WorkflowEndpoints" /> / <see cref="WorkflowEndpointOptions" /> 的废弃文案。</summary>
    internal const string Message =
        "旧面（/api/** 过渡形态）。新宿主请用平台面 /workflow/v1：响应体是 NetBpm.Platform.Contracts 的强类型"
        + "（可生成 SDK），不再需要 Decorator —— 业务页面地址与标签由 /admin/v1/processes 的覆盖行声明，"
        + "参数列由 BPMN 的 netbpm:parameter 派生，业务单号由调用方生成后放进 StartInstanceRequest.BusinessKey。"
        + "保留本类只为 Demo 与既有宿主在迁移期跑通，新代码不要引入。";
}

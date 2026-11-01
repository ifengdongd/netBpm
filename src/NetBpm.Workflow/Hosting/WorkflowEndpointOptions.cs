using Microsoft.AspNetCore.Http;

namespace NetBpm.Workflow;

/// <summary>通用流程端点里的「管理动作」（会改变办理人或流程走向）。</summary>
public enum WorkflowAdminAction
{
    /// <summary>转办：把待办改派给另一个人。</summary>
    ReassignTask,

    /// <summary>跳转：把运行中的实例搬到指定节点。</summary>
    JumpToNode,
}

/// <summary>
/// 管理动作授权钩子。
/// <para>
/// 返回 <see langword="false"/> 表示拒绝（端点返回 403）。宿主应在此校验管理 scope
/// （例如 <c>netbpm.admin.task.moderate</c> / <c>netbpm.admin.instance.jump</c>）。
/// </para>
/// </summary>
public delegate ValueTask<bool> WorkflowAdminActionAuthorizer(
    HttpContext context,
    WorkflowAdminAction action,
    CancellationToken ct);

/// <summary>
/// <see cref="WorkflowEndpoints"/> 的定制点：流程 key 来源、请求/视图的业务化加工、错误响应形状。
/// </summary>
/// <remarks>
/// <para>
/// 端点本身对「什么流程、什么业务」一无所知；宿主通过本选项注入业务语义：
/// </para>
/// <list type="bullet">
///   <item><description>发起请求的加工（如生成业务单号、校验单号格式）在 <see cref="TransformStartRequest"/>；</description></item>
///   <item><description>响应视图的业务化（把参数摊成业务列、换成本业务的 DTO）在两个 Decorator；</description></item>
///   <item><description>错误响应的形状在 <see cref="ErrorResponseFactory"/>，状态码映射保持统一。</description></item>
/// </list>
/// </remarks>
[Obsolete(Deprecation.Message)]
public sealed class WorkflowEndpointOptions
{
    /// <summary>当前生效的流程 key（发起流程、查看流程定义时用）。通常来自宿主的启动引导器常量。</summary>
    public required Func<IServiceProvider, string> ProcessKey { get; init; }

    /// <summary>
    /// 发起请求的业务化加工（可选）：在进入应用层之前调用，返回加工后的请求。
    /// 校验不通过直接抛 <see cref="WorkflowValidationException"/>（会被映射成 400 连同明细）。
    /// </summary>
    public Func<IServiceProvider, StartWorkflowRequest, StartWorkflowRequest>? TransformStartRequest { get; init; }

    /// <summary>实例视图的业务化加工（可选）：返回值直接作为响应体。</summary>
    /// <remarks>
    /// 🔑 <b>返回值是 <c>object?</c>，这正是本属性被废弃的原因</b>：响应形状由宿主任意决定，
    /// 契约无法声明，SDK 无法生成。平台面把「业务化」外置成目录覆盖行（<c>pageUrls</c> / <c>labels</c>），
    /// 响应形状因此可以固定。迁移期的宿主可继续用。
    /// </remarks>
    [Obsolete("Decorator 让响应形状由宿主任意决定，SDK 与契约都无从生成。改用平台面 /workflow/v1 + 目录覆盖行（/admin/v1/processes 的 pageUrls / labels）。")]
    public Func<IServiceProvider, WorkflowInstanceView, ValueTask<object?>>? DecorateInstanceView { get; init; }

    /// <summary>实例摘要（列表行）的业务化加工（可选）：返回值直接作为响应体。</summary>
    [Obsolete("同 DecorateInstanceView：列表行的人员姓名这类联查应由读模型补充，而不是换掉整个响应形状。")]
    public Func<IServiceProvider, WorkflowInstanceSummary, ValueTask<object?>>? DecorateInstanceSummary { get; init; }

    /// <summary>
    /// 管理动作（转办 <c>POST /tasks/{id}/assignee</c>、跳转 <c>POST /instances/{id}/jump</c>）的授权钩子。
    /// <para>
    /// 🔑 <b>为 <see langword="null"/> 时端点一律 403（fail-closed）</b>：这两个动作能改变办理人、
    /// 把别人待办改给自己后再以办理人身份完成，属于高危操作。默认放行等于「任何宿主一行接入
    /// 就得到一个未授权后门」，因此必须由宿主<b>显式</b>选择策略。
    /// </para>
    /// <para>
    /// 无鉴权的演示宿主可以显式写 <c>(_, _, _) =&gt; ValueTask.FromResult(true)</c>，
    /// 生产宿主请校验管理 scope。
    /// </para>
    /// </summary>
    public WorkflowAdminActionAuthorizer? AuthorizeAdminAction { get; init; }

    /// <summary>
    /// 错误响应工厂：默认形状 <c>{ message, errors? }</c>。宿主可换成自己的错误 DTO。
    /// </summary>
    public Func<string, IReadOnlyList<string>?, object?> ErrorResponseFactory { get; init; } =
        (message, errors) => errors is { Count: > 0 } ? new { message, errors } : new { message };

    /// <summary>构造标准错误响应（400/404 共用的内容形状）。</summary>
    internal object? Error(string message, IReadOnlyList<string>? errors = null) =>
        ErrorResponseFactory(message, errors is { Count: > 0 } ? errors : null);
}

namespace NetBpm.Workflow;

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
    public Func<IServiceProvider, WorkflowInstanceView, object?>? DecorateInstanceView { get; init; }

    /// <summary>实例摘要（列表行）的业务化加工（可选）：返回值直接作为响应体。</summary>
    public Func<IServiceProvider, WorkflowInstanceSummary, object?>? DecorateInstanceSummary { get; init; }

    /// <summary>
    /// 错误响应工厂：默认形状 <c>{ message, errors? }</c>。宿主可换成自己的错误 DTO。
    /// </summary>
    public Func<string, IReadOnlyList<string>?, object?> ErrorResponseFactory { get; init; } =
        (message, errors) => errors is { Count: > 0 } ? new { message, errors } : new { message };

    /// <summary>构造标准错误响应（400/404 共用的内容形状）。</summary>
    internal object? Error(string message, IReadOnlyList<string>? errors = null) =>
        ErrorResponseFactory(message, errors is { Count: > 0 } ? errors : null);
    }

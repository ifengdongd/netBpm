namespace NetBpm.Demo.ExternalTasks;

/// <summary>一个已注册的 topic 与它的 handler 类型名（「注册」的可见形态）。</summary>
/// <param name="Topic">主题名。</param>
/// <param name="Handler">handler 类型名。</param>
public sealed record ExternalTaskHandlerInfo(string Topic, string Handler);

/// <summary>worker 运行态：订阅了哪些主题、是否被用例临时暂停。</summary>
/// <param name="WorkerId">锁持有者标识。</param>
/// <param name="Paused">是否暂停领取。</param>
/// <param name="Topics">订阅的主题。</param>
/// <param name="Handlers">已注册的 handler。</param>
public sealed record ExternalTaskWorkerInfo(
    string WorkerId,
    bool Paused,
    IReadOnlyList<string> Topics,
    IReadOnlyList<ExternalTaskHandlerInfo> Handlers);

/// <summary>
/// 外部任务 Demo 的端点：
/// <list type="bullet">
///   <item><c>GET /api/external-task/handlers</c>：看「注册」了什么（topic → handler）、worker 是否在跑；</item>
///   <item><c>POST /api/external-task/case/run</c>：跑一遍「注册 + 调用」闭环用例，返回分步断言结果。</item>
/// </list>
/// </summary>
public static class ExternalTaskCaseEndpoints
{
    /// <summary>挂上外部任务 Demo 的端点。</summary>
    public static IEndpointRouteBuilder MapExternalTaskCaseEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/external-task").WithTags("external-task");

        group.MapGet("/handlers", (IEnumerable<IExternalTaskHandler> handlers, DemoExternalTaskWorker worker) =>
        {
            var infos = handlers
                .Select(handler => new ExternalTaskHandlerInfo(handler.Topic, handler.GetType().Name))
                .ToArray();

            return Results.Ok(new ExternalTaskWorkerInfo(
                DemoExternalTaskWorker.WorkerId,
                worker.Paused,
                worker.Topics,
                infos));
        })
        .WithSummary("外部任务注册信息")
        .WithDescription("列出已注册的 topic（handler）与内置 worker 的订阅 / 暂停状态。");

        group.MapPost("/case/run", async (ExternalTaskCaseRunner runner, CancellationToken ct) =>
        {
            var report = await runner.RunAsync(ct).ConfigureAwait(false);

            return Results.Ok(report);
        })
        .WithSummary("运行外部任务闭环用例")
        .WithDescription(
            "部署并跑一遍 external_task_case：登记 → 原地等待 → worker 领取回执 → 网关按回执变量分支 → 结束。" +
            "返回分步断言结果；Passed 为 true 表示这条链路当前是通的。");

        return app;
    }
}

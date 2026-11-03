using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Console;
using NetBpm.ServerTaskWorker;

/*
 * 3007 侧入口：既提供业务 HTTP 接口，也把 worker 跑在同一个进程里。
 *
 * 业务接口和 worker 共用同一套处理器（ServerTaskHandlers.cs），所以：
 *   - 外部系统按旧方式 POST /api/serverTask/approveComplete 仍然可用（便于手工验证）；
 *   - worker 领到任务后直接调用同一个处理器，不存在「两套实现漂移」的问题。
 *
 * 生产上建议拆开跑：业务接口随主应用发布，worker 用独立进程（多副本时 workerId 天然不同）。
 *
 * 用法（PowerShell / bash 通用，环境变量与原 Node 版同名）：
 *   NETBPM_BASE_URL=http://localhost:5081 \
 *   NETBPM_CLIENT_KEY='nbc.cl_xxx.yyy' \
 *   dotnet run --project samples/NetBpm.ServerTaskWorker
 */

var builder = WebApplication.CreateBuilder(args);

// 兼容原 Node 版的环境变量名：先于其它配置源加入，因此优先级最高（后加者赢）。
builder.Configuration.AddInMemoryCollection(WorkerOptions.FromLegacyEnvironment());

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.ColorBehavior = LoggerColorBehavior.Disabled;
});

var options = builder.Configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>()
    ?? new WorkerOptions();

options.Normalize();

// 配置错了就别启动：宁可起不来，也别在线上用错的凭据/主题静默空转。
// 注意这里只拦「配错了」（格式不对、主题为空），不拦「还没配」—— 见下面的降级分支。
if (!options.TryValidate(out var configError))
{
    Console.Error.WriteLine($"[启动失败] {configError}");
    Console.Error.WriteLine("参考 samples/NetBpm.ServerTaskWorker/README.md，或 docs/leave-worker-3007-integration.md §1.2 注册客户端。");
    return 1;
}

// 没配密钥 = 还没接上引擎，不是配错了。
// 这时候照样把 HTTP 业务接口起来（本地能直接 curl 验业务逻辑），只是不起 worker。
// 早先这里直接 return 1，从 IDE 按「运行」的结果就是「什么都没打印就退出」。
var clientKeyMissing = !options.HasClientKey;

if (clientKeyMissing)
{
    options.RunWorker = false;

    StartupMessages.PrintMissingClientKey(options);
}

builder.WebHost.UseUrls(options.ListenUrl);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WorkerRuntimeState>();
builder.Services.AddSingleton<ILeaveBillStore, JsonLeaveBillStore>();
builder.Services.AddSingleton<IServerTaskHandler, ApproveCompleteHandler>();
builder.Services.AddSingleton<IServerTaskHandler, RefuseCompleteHandler>();
builder.Services.AddSingleton<ServerTaskDispatcher>();

builder.Services.AddHttpClient<NetBpmClient>((provider, client) =>
{
    var workerOptions = provider.GetRequiredService<WorkerOptions>();

    client.BaseAddress = new Uri(workerOptions.NormalizedBaseUrl + "/", UriKind.Absolute);

    // 必须比 fetch 的 waitSeconds 宽：服务端挂起 20s 时，超时设在 30s 会把正常等待当成网络故障。
    client.Timeout = TimeSpan.FromSeconds(Math.Max(60, workerOptions.WaitSeconds + 40));

    // 客户端密钥走 X-Api-Key（格式 nbc.{clientId}.{secret}）。
    // 租户不在这里传：它由客户端绑定决定，Worker:TenantId 只用于日志与健康检查展示。
    client.DefaultRequestHeaders.Add("X-Api-Key", workerOptions.ClientKey);

    // 标识调用来源，排障时服务端日志能看出是哪一个 worker 进程。
    client.DefaultRequestHeaders.Add("User-Agent", $"NetBpm.ServerTaskWorker/{workerOptions.WorkerId}");
});

if (options.RunWorker)
{
    builder.Services.AddHostedService<ServerTaskWorkerService>();
}

var app = builder.Build();

var runtimeState = app.Services.GetRequiredService<WorkerRuntimeState>();

if (clientKeyMissing)
{
    runtimeState.MarkDegraded("未配置客户端密钥（NETBPM_CLIENT_KEY / Worker:ClientKey），worker 未启动");
}

app.MapGet("/healthz", (
    WorkerOptions workerOptions,
    ServerTaskDispatcher dispatcher,
    WorkerRuntimeState state) => Results.Ok(new
{
    // 进程活着 ≠ 在干活：降级时明确说 degraded，别让人对着 ok 排查。
    status = state.DegradedReason is null ? "ok" : "degraded",
    degradedReason = state.DegradedReason,
    clientKeyConfigured = workerOptions.HasClientKey,
    workerId = workerOptions.WorkerId,
    engine = workerOptions.NormalizedBaseUrl,
    tenantId = workerOptions.TenantId,
    topics = workerOptions.Topics,
    registered = dispatcher.RegisteredTopics,
    runWorker = workerOptions.RunWorker,

    // fetch 一直失败时只有这个字段能看出来。
    lastPollAt = state.LastPollAt,
}));

app.MapPost("/api/serverTask/{name}", async (
    string name,
    HttpRequest request,
    ServerTaskDispatcher dispatcher,
    ILogger<Program> logger) =>
{
    ServerTaskRequest? body;

    try
    {
        body = await request.ReadFromJsonAsync<ServerTaskRequest>(WorkerJson.Options);
    }
    catch (JsonException exception)
    {
        return Results.Json(
            new { message = $"请求体不是合法 JSON：{exception.Message}" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (string.IsNullOrWhiteSpace(body?.ProcessInstanceId))
    {
        return Results.Json(
            new { message = "缺少 processInstanceId" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    // 允许只给 processInstanceId + payload，便于用 curl 手工验证业务逻辑。
    // businessKey 一起给，走的才是真实关联路径（业务表主键 = businessKey）。
    var task = new ExternalTask
    {
        Id = body.Id ?? "manual",
        Topic = name,
        ProcessInstanceId = body.ProcessInstanceId,
        BusinessKey = body.BusinessKey,
        ActivityId = body.ActivityId,
        Payload = body.Payload ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
    };

    try
    {
        var variables = await dispatcher.DispatchAsync(name, task, request.HttpContext.RequestAborted);

        return Results.Ok(new { ok = true, variables });
    }
    catch (BusinessRuleException exception)
    {
        // 422：业务规则拒绝。worker 见到它会把任务打回引擎并在重试耗尽后进死信。
        return Results.Json(
            new { message = exception.Message, details = exception.Details },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "处理 /api/serverTask/{Name} 出错：{Message}", name, exception.Message);

        return Results.Json(
            new { message = exception.Message },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();

// 先真起来再报「已监听」：端口被占时不该先打印一句「已监听」再抛异常。
try
{
    await app.StartAsync();
}
catch (IOException exception)
{
    Console.Error.WriteLine($"[启动失败] 监听 {options.ListenUrl} 失败：{exception.Message}");
    Console.Error.WriteLine("多半是端口已被占用：换一个 PORT（如 PORT=3017），或先停掉占用 3007 的进程。");
    return 1;
}

startupLogger.LogInformation(
    "业务接口已监听 {ListenUrl}/api/serverTask/{{{Topics}}}",
    options.ListenUrl,
    string.Join("|", app.Services.GetRequiredService<ServerTaskDispatcher>().RegisteredTopics));

if (clientKeyMissing)
{
    startupLogger.LogWarning("worker 未启动：缺少客户端密钥（见上方提示）。业务接口照常可用。");
}
else if (!options.RunWorker)
{
    startupLogger.LogInformation("RUN_WORKER=false：只提供业务接口，不领取外部任务");
}

await app.WaitForShutdownAsync();

return 0;

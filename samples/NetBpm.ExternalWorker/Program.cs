using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using NetBpm.ExternalWorker;

/*
 * 外部任务 worker —— 独立部署形态下「客户系统那一侧」的进程。
 *
 * 它刻意不引用 NetBpm 的任何程序集：与平台的唯一接口就是 HTTP + JSON。
 * 真实项目里这段逻辑完全可以换成 Go / Java / Python —— 本工程只是一份参考实现。
 *
 * 用法：
 *   dotnet run --project samples/NetBpm.ExternalWorker \
 *     --NetBpm:BaseUrl=http://localhost:8080 --NetBpm:Topics=invoice,notify
 * 或（容器内）：
 *   NetBpm__BaseUrl=http://platform:8080  NetBpm__Topics=invoice  NetBpm__ApiKey=nbc.cl_xxx.yyy
 */

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.ColorBehavior = LoggerColorBehavior.Disabled;
});

// Topics 在 appsettings 里是数组，但环境变量只能给字符串：这里把逗号分隔串摊平。
// 没有这一步，容器里 NetBpm__Topics=invoice,notify 会被解析成单个元素 "invoice,notify"。
builder.Configuration.AddInMemoryCollection(FlattenTopics(builder.Configuration));

builder.Services.Configure<ExternalWorkerOptions>(
    builder.Configuration.GetSection(ExternalWorkerOptions.SectionName));

builder.Services.AddSingleton<IExternalTaskHandler, InvoiceHandler>();
builder.Services.AddSingleton<IExternalTaskHandler, NotifyHandler>();

builder.Services.AddHttpClient(string.Empty, (provider, client) =>
{
    var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ExternalWorkerOptions>>().Value;

    client.BaseAddress = new Uri(options.NormalizedBaseUrl + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(30, options.PollIntervalSeconds + 20));

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        // 客户端密钥走 X-Api-Key（格式 nbc.{clientId}.{secret}）；演示档不配就是匿名访问
        client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey.Trim());
    }

    if (!string.IsNullOrWhiteSpace(options.TenantId))
    {
        client.DefaultRequestHeaders.Add("X-Tenant-Id", options.TenantId.Trim());
    }
});

builder.Services.AddHostedService<ExternalTaskPollingService>();

var host = builder.Build();

host.Run();

/// <summary>把逗号分隔的 NetBpm:Topics 摊平成数组，便于用环境变量覆盖。</summary>
static IEnumerable<KeyValuePair<string, string?>> FlattenTopics(IConfiguration configuration)
{
    var raw = configuration["NetBpm:Topics"];

    if (string.IsNullOrWhiteSpace(raw))
    {
        yield break;
    }

    var topics = raw
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    for (var index = 0; index < topics.Length; index++)
    {
        yield return new KeyValuePair<string, string?>($"NetBpm:Topics:{index}", topics[index]);
    }
}

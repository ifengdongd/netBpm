using NetBpm.Admin.Endpoints;
using NetBpm.Admin.Storage;
using NetBpm.Persistence;
using NetBpm.Rest;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Endpoints;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow.Hosting;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI
builder.Services.AddOpenApi();

// 请求级身份上下文（由鉴权中间件填充）
builder.Services.AddScoped<RequestContext>();

// 组合根：引擎 / 持久化 / 身份（方言随 NetBpm:DatabaseType）
builder.Services.AddNetBpm(builder.Configuration);

// 定时边界事件：后台轮询 ACT_RU_TIMER_JOB（NetBpm:Timer 可调间隔 / 关闭）
builder.Services.AddNetBpmTimerScheduler(builder.Configuration);

// 管理端业务存储：流程模型 + 流程类型注册表
builder.Services.AddAdminStores();

// 全局异常处理 + 时间口径
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()));

// 幂等记录定期清理
builder.Services.AddHostedService<IdempotencyCleanupService>();

// 管理动作审计（转办 / 跳转 / 变量直写）
builder.Services.AddNetBpmAuditing();

// 审计保留策略（NetBpm:Admin:AuditRetentionDays，默认 0 = 永久保留）
builder.Services.AddHostedService<AdminAuditCleanupService>();

// 可选 OpenTelemetry 接线（NetBpm:Otel:Enabled 或 OTEL_EXPORTER_OTLP_ENDPOINT）
builder.Services.AddNetBpmOpenTelemetry(builder.Configuration);

var app = builder.Build();
var startedAt = DateTime.UtcNow;

app.UseExceptionHandler();

// 鉴权 → 幂等：未配置 NetBpm:Security:ApiKey 时鉴权中间件放行
app.UseMiddleware<AuthenticationMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = "NetBpm",
    version = typeof(AdminEndpoints).Assembly.GetName().Version?.ToString(),
}));

app.MapGet("/api/admin/info", (IServiceProvider services) =>
{
    var options = services.GetRequiredService<NetBpmOptions>();

    return Results.Ok(new
    {
        engine = "NetBpm",
        version = typeof(AdminEndpoints).Assembly.GetName().Version?.ToString(),
        database = options.UsesDatabase ? options.DatabaseType.ToString() : "in-memory",
        historyEnabled = options.EnableHistory,
        schemaVersion = DatabaseSchemaChecker.ExpectedSchemaVersion,
        uptimeSeconds = (long)(DateTime.UtcNow - startedAt).TotalSeconds,
    });
});

// 引擎 REST 面 + 管理端扩展
app.MapDesignerEndpoints();
app.MapRuntimeEndpoints();
app.MapIdentityEndpoints();
app.MapAdminEndpoints();

// React 管理前端：静态文件 + SPA history 路由回退
app.UseDefaultFiles();
app.UseStaticFiles();

// 未匹配的 /api 路由返回 404（避免被 SPA 回退吞成 index.html）
app.Map("/api/{**slug}", (HttpContext context) =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    return Task.CompletedTask;
});

app.MapFallbackToFile("{*path:nonfile}", "index.html");

// 数据库模式：先按需建库/建表，再校验结构版本
var netBpmOptions = app.Services.GetRequiredService<NetBpmOptions>();

if (netBpmOptions.UsesDatabase)
{
    if (netBpmOptions.AutoMigrate)
    {
        var migration = app.Services.GetRequiredService<DatabaseMigrator>().Migrate();

        if (migration.AlreadyInitialized)
        {
            app.Logger.LogInformation("数据库已初始化，跳过建表脚本。");
        }
        else
        {
            app.Logger.LogInformation("已执行建表脚本：{Scripts}", string.Join(", ", migration.AppliedScripts));
        }
    }

    if (netBpmOptions.ValidateSchemaOnStartup)
    {
        app.Services.GetRequiredService<DatabaseSchemaChecker>().Check();
        app.Logger.LogInformation(
            "数据库结构版本校验通过：schema.version = {Version}",
            DatabaseSchemaChecker.ExpectedSchemaVersion);
    }
}

app.Run();

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;

using NetBpm.Persistence;
using NetBpm.Rest;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Endpoints;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow.Hosting;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI（P9 验收标准：Swagger 全端点可用）
builder.Services.AddOpenApi();

// 请求级身份上下文（由 API Key 中间件填充；未配置密钥时仍可读 X-User-Id / X-Tenant-Id）
builder.Services.AddScoped<RequestContext>();

// 组合根：模型 / 转换 / 校验 / 表达式 / 引擎 / 持久化 / 身份
builder.Services.AddNetBpm(builder.Configuration);

// 定时边界事件：后台轮询 ACT_RU_TIMER_JOB（NetBpm:Timer 可调间隔 / 关闭）
builder.Services.AddNetBpmTimerScheduler(builder.Configuration);

// 全局异常处理：未处理异常（如数据库不可达）返回带信息的 JSON 错误体，
// 而非纯文本 "Internal Server Error"（前端部署列表的「加载失败」提示来源）。
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(); // 无参 UseExceptionHandler() 的注册前置条件

// 时间契约：数据库读回的 DateTimeKind 是 Unspecified，响应统一补 UTC 设计符（'Z'）
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()));

// 幂等记录定期清理（否则 ACT_IDEMPOTENCY 无限增长）
builder.Services.AddHostedService<IdempotencyCleanupService>();

// 管理动作审计（转办 / 跳转 / 变量直写）：写审计存储，失败不影响业务
builder.Services.AddNetBpmAuditing();

// 审计保留策略（NetBpm:Admin:AuditRetentionDays，默认 0 = 永久保留）
builder.Services.AddHostedService<AdminAuditCleanupService>();

// 可选 OpenTelemetry 接线（NetBpm:Otel:Enabled 或 OTEL_EXPORTER_OTLP_ENDPOINT）
builder.Services.AddNetBpmOpenTelemetry(builder.Configuration);

var app = builder.Build();
var startedAt = DateTime.UtcNow;

app.UseExceptionHandler();

// 鉴权 → 幂等：未配置 NetBpm:Security:ApiKey 时鉴权中间件放行，保持开发体验
app.UseMiddleware<AuthenticationMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = "NetBpm",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
}));

// 存活 / 就绪分离：编排系统用 live 判断「要不要重启」，用 ready 判断「能不能接流量」
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", (IServiceProvider services) =>
{
    var options = services.GetRequiredService<NetBpmOptions>();

    if (!options.UsesDatabase)
    {
        return Results.Ok(new { status = "ready", database = "in-memory" });
    }

    try
    {
        var factory = services.GetRequiredService<IDbConnectionFactory>();

        // CreateConnection() 已负责打开连接（见 IDbConnectionFactory 契约）
        using var connection = factory.CreateConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "select 1";
        command.ExecuteScalar();

        return Results.Ok(new { status = "ready", database = options.DatabaseType.ToString() });
    }
    catch (Exception exception)
    {
        // 探针面向编排系统与负载均衡，不是排障入口：异常原文可能带服务器地址 / 账号，
        // 只进日志，响应固定为「数据库不可用」。
        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("NetBpm.Health")
            .LogError(exception, "就绪探针失败：数据库不可用（{Database}）", options.DatabaseType);

        return Results.Json(
            new { status = "unavailable", database = options.DatabaseType.ToString(), error = "database unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// 运维信息：版本 / 方言 / 历史开关 / 期望结构版本 / 运行时长
app.MapGet("/api/admin/info", (IServiceProvider services) =>
{
    var options = services.GetRequiredService<NetBpmOptions>();

    return Results.Ok(new
    {
        engine = "NetBpm",
        version = typeof(Program).Assembly.GetName().Version?.ToString(),
        database = options.UsesDatabase ? options.DatabaseType.ToString() : "in-memory",
        historyEnabled = options.EnableHistory,
        schemaVersion = DatabaseSchemaChecker.ExpectedSchemaVersion,
        uptimeSeconds = (long)(DateTime.UtcNow - startedAt).TotalSeconds,
    });
});

app.MapDesignerEndpoints();
app.MapRuntimeEndpoints();
app.MapIdentityEndpoints();

// 数据库模式：先按需建库/建表，再校验 ACT_GE_PROPERTY.schema.version（P5 验收标准之一）
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
        app.Logger.LogInformation("数据库结构版本校验通过：schema.version = {Version}", DatabaseSchemaChecker.ExpectedSchemaVersion);
    }
}

app.Run();

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;

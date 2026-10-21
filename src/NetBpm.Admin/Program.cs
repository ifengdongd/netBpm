using System.Threading.RateLimiting;
using Microsoft.OpenApi;
using NetBpm.Admin.Endpoints;
using NetBpm.Admin.Storage;
using NetBpm.Admin.Workflow;
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
// OpenAPI 文档：修复 System.Text.Json 对日期与数字的 schema 弱化（OpenAPI 3.1 + JSON Schema）。
//   - DateTime 只输出 format: date-time 而缺 type（dotnet/aspnetcore 已知问题）→ 前端生成 unknown；
//   - int 因 JsonNumberHandling 输出 ["integer","string"] 联合 → 前端生成 number | string。
// 两者都会让 openapi-typescript 产出不可用的契约类型，这里统一收敛。
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer((schema, _, _) =>
    {
        NormalizeOpenApiSchemaTypes(schema);

        return Task.CompletedTask;
    });
});

// 请求级身份上下文（由鉴权中间件填充）
builder.Services.AddScoped<RequestContext>();

// 组合根：引擎 / 持久化 / 身份（方言随 NetBpm:DatabaseType）
builder.Services.AddNetBpm(builder.Configuration);

// 定时边界事件：后台轮询 ACT_RU_TIMER_JOB（NetBpm:Timer 可调间隔 / 关闭）
builder.Services.AddNetBpmTimerScheduler(builder.Configuration);

// 异步作业（asyncBefore / asyncAfter）：后台轮询 ACT_RU_JOB（NetBpm:Jobs 可调间隔 / 关闭）
builder.Services.AddNetBpmAsyncJobExecutor(builder.Configuration);

// 事务性 outbox：需要 NetBpm:EnableOutbox=true 且宿主注册 IOutboxEventHandler 才会真正投递
builder.Services.AddNetBpmOutboxPublisher(builder.Configuration);

// 管理端业务存储：流程模型 + 流程类型注册表
builder.Services.AddAdminStores();

// 内置占位委托：示例流程（请假 / 订单审批）的 delegateExpression 在管理宿主没有业务实现，
// 默认登记只记日志的占位实现，保证「部署 → 发起 → 审批 → 流程图回显」能在控制台端到端跑通；
// 接入了业务宿主的部署用 NetBpm:Admin:BuiltInDelegates:Enabled=false 关掉，改用业务自己的实现。
if (builder.Configuration.GetValue("NetBpm:Admin:BuiltInDelegates:Enabled", true))
{
    builder.Services.AddServiceTaskDelegates(typeof(BuiltInApprovalCompletionDelegate).Assembly);
}

// 全局异常处理 + 时间口径
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()));

// 幂等记录定期清理
builder.Services.AddHostedService<IdempotencyCleanupService>();

// 管理动作审计（转办 / 跳转 / 变量直写）
builder.Services.AddNetBpmAuditing();

// 账号密码登录：账号存储（admin_user）+ 令牌签发（复用 NetBpm:Security:Jwt 的密钥）
builder.Services.AddNetBpmAuth(builder.Configuration);

// 审计保留策略（NetBpm:Admin:AuditRetentionDays，默认 0 = 永久保留）
builder.Services.AddHostedService<AdminAuditCleanupService>();
builder.Services.AddHostedService<AdminArchiveCleanupService>();

// 可选 OpenTelemetry 接线（NetBpm:Otel:Enabled 或 OTEL_EXPORTER_OTLP_ENDPOINT）
builder.Services.AddNetBpmOpenTelemetry(builder.Configuration);

// 限流（按客户端 IP 分区，匿名与已认证同口径）：
//   - 凭据校验探针 /api/auth/me（登录页用）每 IP 30 次/分钟，防密钥爆破；
//   - 其余 /api（含匿名可达的 /api/admin/info 运维信息）每 IP 600 次/分钟，防脚本失控
//     （正常管理操作远达不到）。
// 静态资源与 /health 不限流；阈值可经 NetBpm:Admin:RateLimit:ApiPerMinute /
// ProbePerMinute 覆盖，集成测试宿主用 NetBpm:Admin:RateLimit:Enabled=false 关闭。
var rateLimitEnabled = builder.Configuration.GetValue("NetBpm:Admin:RateLimit:Enabled", true);

if (rateLimitEnabled)
{
    var apiPerMinute = builder.Configuration.GetValue("NetBpm:Admin:RateLimit:ApiPerMinute", 600);
    var probePerMinute = builder.Configuration.GetValue("NetBpm:Admin:RateLimit:ProbePerMinute", 30);
    var loginPerMinute = builder.Configuration.GetValue("NetBpm:Admin:RateLimit:LoginPerMinute", 10);

    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

            // 凭据校验探针：登录页拿它判断手里的密钥 / 令牌是否可用，按 IP 收紧防爆破
            if (context.Request.Path.StartsWithSegments("/api/auth/me"))
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"probe:{ip}",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = probePerMinute, Window = TimeSpan.FromMinutes(1) });
            }

            // 登录接口：比探针更紧（口令爆破），阈值 NetBpm:Admin:RateLimit:LoginPerMinute
            if (context.Request.Path.StartsWithSegments("/api/auth/login"))
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"login:{ip}",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPerMinute, Window = TimeSpan.FromMinutes(1) });
            }

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"api:{ip}",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = apiPerMinute, Window = TimeSpan.FromMinutes(1) });
            }

            return RateLimitPartition.GetNoLimiter("unlimited");
        });
    });
}

var app = builder.Build();
var startedAt = DateTime.UtcNow;

// OpenAPI 文档默认只在开发环境暴露（契约生成 / 联调用）；
// 生产环境需要时显式打开 NetBpm:Admin:OpenApi:Enabled=true。
var openApiEnabled = builder.Configuration.GetValue(
    "NetBpm:Admin:OpenApi:Enabled",
    builder.Environment.IsDevelopment());

app.UseExceptionHandler();

if (rateLimitEnabled)
{
    app.UseRateLimiter();
}

// 鉴权 → 幂等：未配置 NetBpm:Security:ApiKey 时鉴权中间件放行
app.UseMiddleware<AuthenticationMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();

if (openApiEnabled)
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = "NetBpm",
    version = typeof(AdminEndpoints).Assembly.GetName().Version?.ToString(),
}));

// 存活 / 就绪分离（与 Rest 宿主同口径）：live 判断「要不要重启」，ready 判断「能不能接流量」
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
// 账号密码登录（/api/auth/login、/api/auth/me）：登录接口匿名可达，内部自带限流分区。
app.MapAuthEndpoints();

// React 管理前端：静态文件 + SPA history 路由回退。
// hash 文件名的资源（/assets、/designer/assets）长缓存 immutable；
// 其余（index.html 等）no-cache，保证发版后浏览器立即拿到新入口。
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.Value?.Contains("/assets/", StringComparison.Ordinal) == true
                ? "public, max-age=31536000, immutable"
                : "no-cache";
    },
});

// 未匹配的 /api 路由返回 404（避免被 SPA 回退吞成 index.html）
app.Map("/api/{**slug}", (HttpContext context) =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    return Task.CompletedTask;
});

app.MapFallbackToFile("{*path:nonfile}", "index.html");

// 数据库模式：执行幂等初始化脚本（库里缺什么补什么），再校验结构版本
var netBpmOptions = app.Services.GetRequiredService<NetBpmOptions>();

if (netBpmOptions.UsesDatabase)
{
    if (netBpmOptions.AutoMigrate)
    {
        var migration = app.Services.GetRequiredService<DatabaseMigrator>().Migrate();

        if (migration.AlreadyInitialized)
        {
            app.Logger.LogInformation(
                "数据库已存在，已按幂等脚本校验并补齐结构：{Scripts}",
                string.Join(", ", migration.AppliedScripts));
        }
        else
        {
            app.Logger.LogInformation(
                "已执行数据库初始化脚本：{Scripts}",
                string.Join(", ", migration.AppliedScripts));
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

/// <summary>
/// 收敛 System.Text.Json 生成的 OpenAPI schema 类型标注：
///   - <c>format: date-time / date</c> 但缺 <c>type</c> → 补 <c>string</c>（保留 Null 标志）；
///   - <c>format: int32 / int64</c> 且类型为 [integer, string] 联合 → 收敛为 <c>integer</c>。
/// 递归覆盖属性与数组元素；嵌套对象通过 $ref 引用，会各自进入本转换器。
/// </summary>
static void NormalizeOpenApiSchemaTypes(OpenApiSchema schema)
{
    const JsonSchemaType nullableFlag = JsonSchemaType.Null;

    if (schema.Format is "int32" or "int64"
        && schema.Type is { } intType
        && intType.HasFlag(JsonSchemaType.Integer))
    {
        var nullable = intType.HasFlag(nullableFlag) ? nullableFlag : 0;
        schema.Type = JsonSchemaType.Integer | nullable;
    }

    if (schema.Format is "date-time" or "date")
    {
        var nullable = schema.Type is { } dateType && dateType.HasFlag(nullableFlag) ? nullableFlag : 0;
        schema.Type = JsonSchemaType.String | nullable;
    }

    foreach (var property in schema.Properties?.Values.OfType<OpenApiSchema>() ?? Enumerable.Empty<OpenApiSchema>())
    {
        NormalizeOpenApiSchemaTypes(property);
    }

    if (schema.Items is OpenApiSchema items)
    {
        NormalizeOpenApiSchemaTypes(items);
    }
}

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;

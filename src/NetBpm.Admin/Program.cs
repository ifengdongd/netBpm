using Microsoft.OpenApi;
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

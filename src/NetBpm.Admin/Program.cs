using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using NetBpm.Admin.Endpoints;
using NetBpm.Admin.Storage;
using NetBpm.Admin.Workflow;
using NetBpm.Rest.Hosting;
using NetBpm.Runtime;

var builder = WebApplication.CreateBuilder(args);

// 子路径部署：反代「保留前缀」转发（/netbpm/api/x 原样到本宿主）时，用 NetBpm:Admin:PathBase 让宿主
// 自己认领前缀 —— 路由、Cookie Path、重定向都会自动带前缀，无需代理侧剥路径。
// 与「反代剥前缀」（OPMS 的当前做法）二选一，两个都配会把前缀吃掉两遍。
var adminPathBase = builder.Configuration["NetBpm:Admin:PathBase"];

// 反代头：限流按客户端 IP 分区、重定向生成绝对地址都依赖真实 scheme / 来源地址。
// 默认只信任本机（loopback）转发；代理在另一台机器或容器网络时需自行配置 KnownProxies。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// 管理端业务存储：流程模型 + 流程类型注册表
builder.Services.AddAdminStores();

// 内置占位委托：示例流程（请假 / 订单审批）的 delegateExpression 在管理宿主没有业务实现，
// 默认登记只记日志的占位实现，保证「部署 → 发起 → 审批 → 流程图回显」能在控制台端到端跑通；
// 接入了业务宿主的部署用 NetBpm:Admin:BuiltInDelegates:Enabled=false 关掉，改用业务自己的实现。
if (builder.Configuration.GetValue("NetBpm:Admin:BuiltInDelegates:Enabled", true))
{
    builder.Services.AddServiceTaskDelegates(typeof(BuiltInApprovalCompletionDelegate).Assembly);
}

// 兼容 0.1.x 的 NetBpm:Admin:RateLimit:*：新配置节存在时以新配置为准，
// 否则沿用旧节名读取（已上线的部署不需要改配置）。
var useLegacyRateLimitSection =
    !builder.Configuration.GetSection("NetBpm:RateLimit").Exists()
    && builder.Configuration.GetSection("NetBpm:Admin:RateLimit").Exists();

var hostOptions = builder.AddNetBpmHost(options =>
{
    options.VersionAssembly = typeof(AdminEndpoints).Assembly;

    if (useLegacyRateLimitSection)
    {
        options.RateLimitSection = "NetBpm:Admin:RateLimit";
    }

    // 收敛 System.Text.Json 生成的 OpenAPI schema 类型标注（前端契约生成用）：
    //   - DateTime 只输出 format: date-time 而缺 type → 前端生成 unknown；
    //   - int 因 JsonNumberHandling 输出 ["integer","string"] 联合 → 前端生成 number | string。
    options.ConfigureOpenApi = openApi => openApi.AddSchemaTransformer((schema, _, _) =>
    {
        NormalizeOpenApiSchemaTypes(schema);

        return Task.CompletedTask;
    });
});

var app = builder.Build();

// 反代头必须最先处理：后面的限流分区（按 RemoteIpAddress）与重定向（按 Scheme）都依赖它。
app.UseForwardedHeaders();

if (!string.IsNullOrWhiteSpace(adminPathBase))
{
    var pathBase = adminPathBase.StartsWith('/') ? adminPathBase : "/" + adminPathBase;

    app.UsePathBase(pathBase);

    app.Logger.LogInformation(
        "管理控制台挂载在子路径 {PathBase}（NetBpm:Admin:PathBase）；前端需用同一前缀构建：" +
        "管理端 NETBPM_WEB_BASE={WebBase}，设计器 NETBPM_DESIGNER_BASE={DesignerBase}。",
        pathBase,
        pathBase + "/",
        pathBase + "/designer/");
}

// 管道：异常处理 → 限流 → 鉴权 → 幂等
app.UseNetBpmHost(hostOptions);

// OpenAPI 文档默认只在开发环境暴露（契约生成 / 联调用）；
// 生产环境需要时显式打开 NetBpm:Admin:OpenApi:Enabled=true。
var openApiEnabled = builder.Configuration.GetValue(
    "NetBpm:Admin:OpenApi:Enabled",
    builder.Environment.IsDevelopment());

// 探针 / 运维信息 / 引擎端点 + 管理端扩展端点（模型 / 历史 / 发布 / 审计）
app.MapNetBpmHostEndpoints(
    hostOptions,
    mapOpenApi: openApiEnabled,
    mapAdditionalEndpoints: host => host.MapAdminEndpoints());

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
app.ApplyNetBpmDatabaseStartup();

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

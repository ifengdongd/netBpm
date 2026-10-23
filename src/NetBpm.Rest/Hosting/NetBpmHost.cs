using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using NetBpm.Persistence;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Endpoints;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Rest.Hosting;

/// <summary>
/// NetBpm 宿主的组合根选项。
/// </summary>
public sealed class NetBpmHostOptions
{
    /// <summary>限流配置节前缀（键：<c>Enabled</c> / <c>ApiPerMinute</c> / <c>ProbePerMinute</c> / <c>LoginPerMinute</c>）。</summary>
    public string RateLimitSection { get; set; } = "NetBpm:RateLimit";

    /// <summary>是否启用限流（被配置节 <c>Enabled</c> 覆盖）。</summary>
    public bool RateLimitEnabled { get; set; } = true;

    /// <summary>普通 <c>/api</c> 请求每 IP 每分钟上限。</summary>
    public int ApiPerMinute { get; set; } = 600;

    /// <summary>凭据探针（<c>/api/auth/me</c>）每 IP 每分钟上限。</summary>
    public int ProbePerMinute { get; set; } = 30;

    /// <summary>登录（<c>/api/auth/login</c>）每 IP 每分钟上限。</summary>
    public int LoginPerMinute { get; set; } = 10;

    /// <summary>
    /// 请求体上限（字节）。默认 10 MB：整段 BPMN XML / 整字典变量都在请求体里，
    /// 不设上限等于允许单请求把进程内存打满。
    /// </summary>
    public long MaxRequestBodySizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>版本号来源程序集（<c>/health</c>、<c>/api/admin/info</c> 用）。</summary>
    public Assembly VersionAssembly { get; set; } = typeof(NetBpmHostOptions).Assembly;

    /// <summary>OpenAPI 注册钩子（管理端用来收敛日期 / 数字的 schema 类型）。</summary>
    public Action<OpenApiOptions>? ConfigureOpenApi { get; set; }
}

/// <summary>
/// 宿主组合根：把「引擎 + 作业 + outbox + 鉴权 + 审计 + 异常 + 限流 + 探针」一次装好。
/// <para>
/// 🔑 存在的理由：REST 宿主与管理端宿主此前各自复制了同一套注册顺序（约 60% 逐行同构）。
/// 复制组合根的必然结果是<b>能力漂移</b>——例如限流只加在管理端，REST 宿主登录接口可被无限爆破、
/// 请求体没有上限。共享之后，安全基线一次修改、两个宿主同时生效。
/// </para>
/// <para>
/// 顺序敏感：<see cref="UseNetBpmHost"/> 之前必须先处理反向代理头与 PathBase。
/// </para>
/// </summary>
public static class NetBpmHost
{
    /// <summary>注册宿主全部服务，返回解析后的选项（后续 <see cref="UseNetBpmHost"/> / 映射端点都要用）。</summary>
    public static NetBpmHostOptions AddNetBpmHost(
        this WebApplicationBuilder builder,
        Action<NetBpmHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new NetBpmHostOptions();
        configure?.Invoke(options);

        var configuration = builder.Configuration;
        var section = options.RateLimitSection;

        options.RateLimitEnabled = configuration.GetValue($"{section}:Enabled", options.RateLimitEnabled);
        options.ApiPerMinute = configuration.GetValue($"{section}:ApiPerMinute", options.ApiPerMinute);
        options.ProbePerMinute = configuration.GetValue($"{section}:ProbePerMinute", options.ProbePerMinute);
        options.LoginPerMinute = configuration.GetValue($"{section}:LoginPerMinute", options.LoginPerMinute);
        options.MaxRequestBodySizeBytes = configuration.GetValue(
            "NetBpm:MaxRequestBodySizeBytes",
            options.MaxRequestBodySizeBytes);

        // OpenAPI（Swagger 全端点可用）
        builder.Services.AddOpenApi(openApi => options.ConfigureOpenApi?.Invoke(openApi));

        // 请求级身份上下文（由 API Key / JWT 中间件填充；未配置密钥时仍可读 X-User-Id / X-Tenant-Id）
        builder.Services.AddScoped<RequestContext>();

        // 组合根：模型 / 转换 / 校验 / 表达式 / 引擎 / 持久化 / 身份
        builder.Services.AddNetBpm(configuration);

        // 后台作业：定时边界事件 / 异步作业 / outbox
        builder.Services.AddNetBpmTimerScheduler(configuration);
        builder.Services.AddNetBpmAsyncJobExecutor(configuration);
        builder.Services.AddNetBpmOutboxPublisher(configuration);

        // 全局异常处理：未处理异常返回带 traceId 的 JSON 错误体
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();

        // 时间契约：数据库读回的 DateTimeKind 是 Unspecified，响应统一补 UTC 设计符（'Z'）
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()));

        // 清理类后台服务
        builder.Services.AddHostedService<IdempotencyCleanupService>();
        builder.Services.AddNetBpmAuditing();
        // 运行态应用服务（实例命令 / 任务命令 / 变量 / 历史查询）：端点只做 HTTP 形状
        builder.Services.AddNetBpmRuntimeServices();
        builder.Services.AddNetBpmAuth(configuration);
        builder.Services.AddHostedService<AdminAuditCleanupService>();
        builder.Services.AddHostedService<AdminArchiveCleanupService>();

        // 可选 OpenTelemetry 接线（NetBpm:Otel:Enabled 或 OTEL_EXPORTER_OTLP_ENDPOINT）
        builder.Services.AddNetBpmOpenTelemetry(configuration);

        // 请求体上限：BPMN XML / 变量字典都走请求体，没上限就是一个内存放大器
        if (options.MaxRequestBodySizeBytes > 0)
        {
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodySizeBytes);
        }

        if (options.RateLimitEnabled)
        {
            AddRateLimiting(builder, options);
        }

        return options;
    }

    /// <summary>装配中间件管道（反代头 / PathBase 已由宿主处理）。</summary>
    public static WebApplication UseNetBpmHost(this WebApplication app, NetBpmHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        app.UseExceptionHandler();

        if (options.RateLimitEnabled)
        {
            app.UseRateLimiter();
        }

        // 鉴权 → 幂等：未配置 NetBpm:Security:ApiKey 时鉴权中间件放行，保持开发体验
        app.UseMiddleware<AuthenticationMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();

        return app;
    }

    /// <summary>
    /// 映射引擎的标准 HTTP 面：探针、运维信息、设计器 / 运行态 / 身份 / 登录端点。
    /// </summary>
    /// <param name="app">宿主。</param>
    /// <param name="options">组合根选项。</param>
    /// <param name="mapOpenApi">是否暴露 OpenAPI 文档。</param>
    /// <param name="mapAdditionalEndpoints">
    /// 宿主自己的扩展端点（管理端传 <c>a =&gt; a.MapAdminEndpoints()</c>）。
    /// 用参数而不是全局钩子：避免可变静态状态，也让调用处一眼看到「还多映射了什么」。
    /// </param>
    public static WebApplication MapNetBpmHostEndpoints(
        this WebApplication app,
        NetBpmHostOptions options,
        bool mapOpenApi = true,
        Action<WebApplication>? mapAdditionalEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        var startedAt = DateTime.UtcNow;

        if (mapOpenApi)
        {
            app.MapOpenApi();
        }

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            engine = "NetBpm",
            version = VersionOf(options),
        }));

        // 存活 / 就绪分离：编排系统用 live 判断「要不要重启」，用 ready 判断「能不能接流量」
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", ReadinessProbe);

        // 运维信息：版本 / 方言 / 历史开关 / 期望结构版本 / 运行时长
        app.MapGet("/api/admin/info", (IServiceProvider services) =>
        {
            var engineOptions = services.GetRequiredService<NetBpmOptions>();

            return Results.Ok(new
            {
                engine = "NetBpm",
                version = VersionOf(options),
                database = engineOptions.UsesDatabase ? engineOptions.DatabaseType.ToString() : "in-memory",
                historyEnabled = engineOptions.EnableHistory,
                schemaVersion = DatabaseSchemaChecker.ExpectedSchemaVersion,
                uptimeSeconds = (long)(DateTime.UtcNow - startedAt).TotalSeconds,
            });
        });

        app.MapDesignerEndpoints();
        app.MapRuntimeEndpoints();
        app.MapIdentityEndpoints();

        mapAdditionalEndpoints?.Invoke(app);

        app.MapAuthEndpoints();

        return app;
    }

    /// <summary>
    /// 数据库启动步骤：按需执行幂等初始化脚本，再校验 <c>ACT_GE_PROPERTY.schema.version</c>。
    /// </summary>
    public static WebApplication ApplyNetBpmDatabaseStartup(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<NetBpmOptions>();

        if (!options.UsesDatabase)
        {
            return app;
        }

        if (options.AutoMigrate)
        {
            var migration = app.Services.GetRequiredService<DatabaseMigrator>().Migrate();
            var scripts = string.Join(", ", migration.AppliedScripts);

            if (migration.AlreadyInitialized)
            {
                app.Logger.LogInformation("数据库已存在，已按幂等脚本校验并补齐结构：{Scripts}", scripts);
            }
            else
            {
                app.Logger.LogInformation("已执行数据库初始化脚本：{Scripts}", scripts);
            }
        }

        if (options.ValidateSchemaOnStartup)
        {
            app.Services.GetRequiredService<DatabaseSchemaChecker>().Check();

            app.Logger.LogInformation(
                "数据库结构版本校验通过：schema.version = {Version}",
                DatabaseSchemaChecker.ExpectedSchemaVersion);
        }

        return app;
    }

    private static string? VersionOf(NetBpmHostOptions options) =>
        options.VersionAssembly.GetName().Version?.ToString();

    /// <summary>
    /// 就绪探针：面向编排系统 / 负载均衡，不是排障入口 ——
    /// 异常原文可能带服务器地址与账号，只进日志，响应固定为「数据库不可用」。
    /// </summary>
    private static IResult ReadinessProbe(IServiceProvider services)
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
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("NetBpm.Health")
                .LogError(exception, "就绪探针失败：数据库不可用（{Database}）", options.DatabaseType);

            return Results.Json(
                new { status = "unavailable", database = options.DatabaseType.ToString(), error = "database unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// 限流（按客户端 IP 分区，匿名与已认证同口径）：
    /// <list type="bullet">
    ///   <item><description>登录 <c>/api/auth/login</c>：默认 10 次/分钟，防口令爆破；</description></item>
    ///   <item><description>凭据探针 <c>/api/auth/me</c>：默认 30 次/分钟，防密钥爆破；</description></item>
    ///   <item><description>其余 <c>/api</c>：默认 600 次/分钟，防脚本失控；</description></item>
    ///   <item><description>静态资源与 <c>/health</c>：不限流。</description></item>
    /// </list>
    /// </summary>
    private static void AddRateLimiting(WebApplicationBuilder builder, NetBpmHostOptions options)
    {
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

                if (context.Request.Path.StartsWithSegments("/api/auth/login"))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"login:{ip}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = options.LoginPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                        });
                }

                if (context.Request.Path.StartsWithSegments("/api/auth/me"))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"probe:{ip}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = options.ProbePerMinute,
                            Window = TimeSpan.FromMinutes(1),
                        });
                }

                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"api:{ip}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = options.ApiPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                        });
                }

                return RateLimitPartition.GetNoLimiter("unlimited");
            });
        });
    }
}

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using NetBpm.Persistence;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Endpoints;
using NetBpm.Rest.Platform;
using NetBpm.Rest.RateLimiting;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Rest.Hosting;

/// <summary>
/// NetBpm 宿主的组合根选项。
/// </summary>
public sealed class NetBpmHostOptions
{
    /// <summary>限流配置节前缀（键：<c>Enabled</c> / <c>Store</c> / <c>ApiPerMinute</c> / <c>ProbePerMinute</c> / <c>LoginPerMinute</c> / <c>ClientPerMinute</c> / <c>Redis:*</c>）。</summary>
    public string RateLimitSection { get; set; } = "NetBpm:RateLimit";

    /// <summary>是否启用限流（被配置节 <c>Enabled</c> 覆盖）。</summary>
    public bool RateLimitEnabled { get; set; } = true;

    /// <summary>
    /// 配额计数器的形态（被配置节 <c>Store</c> 覆盖）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <see cref="RateLimitStore.Memory"/> 只在<b>单副本</b>下正确：多副本时每个副本各算一份，
    /// 实际配额 = 副本数 × 配置值。默认取内存是为了「不改配置也能跑」，
    /// 独立部署（<c>Platform.Service</c>）多副本时必须显式设成 <see cref="RateLimitStore.Redis"/>。
    /// </remarks>
    public RateLimitStore RateLimitStore { get; set; } = RateLimitStore.Memory;

    /// <summary>普通 <c>/api</c> 请求每 IP 每分钟上限。</summary>
    public int ApiPerMinute { get; set; } = 600;

    /// <summary>凭据探针（<c>/api/auth/me</c>）每 IP 每分钟上限。</summary>
    public int ProbePerMinute { get; set; } = 30;

    /// <summary>登录（<c>/api/auth/login</c>）每 IP 每分钟上限。</summary>
    public int LoginPerMinute { get; set; } = 10;

    /// <summary>
    /// 已注册客户端<b>未配置配额</b>时的兜底每分钟上限。
    /// <para>正常情况用客户端记录里的 <c>quota_per_minute</c>（一系统一配额），这里只是兜底。</para>
    /// </summary>
    public int ClientPerMinute { get; set; } = 600;

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
        options.RateLimitStore = configuration.GetValue($"{section}:Store", options.RateLimitStore);
        options.ApiPerMinute = configuration.GetValue($"{section}:ApiPerMinute", options.ApiPerMinute);
        options.ProbePerMinute = configuration.GetValue($"{section}:ProbePerMinute", options.ProbePerMinute);
        options.LoginPerMinute = configuration.GetValue($"{section}:LoginPerMinute", options.LoginPerMinute);
        options.ClientPerMinute = configuration.GetValue($"{section}:ClientPerMinute", options.ClientPerMinute);
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
        // 事件投递（M13）：outbox 的「发给谁」；没注册订阅就没有任何事件发生出去，开销为零
        builder.Services.AddNetBpmEventDelivery(configuration);
        builder.Services.AddNetBpmExternalTaskWatchdog(configuration);

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

        // 平台接入层：租户目录 + 客户端注册表（有库走库、无库走内存）。
        // 必须在 AddNetBpmAuth 之后：两者都依赖 NetBpmOptions / IDbConnectionFactory 的注册结果。
        builder.Services.AddNetBpmPlatformSecurity(configuration);

        // 平台业务面：流程目录（引擎定义 + BPMN 派生 + nb_process_catalog 覆盖）
        builder.Services.AddNetBpmProcessCatalog();

        // 平台业务面：审批语义字典（默认字典 + nb_approval_dictionary 流程声明）
        builder.Services.AddNetBpmApprovalVocabulary();

        // 平台业务面：待办读模型（TaskView.options 消费语义字典）
        builder.Services.AddNetBpmTaskCatalog();

        // 平台业务面：实例读模型（InstanceView 单一出口，供详情端点与提交结果复用）
        builder.Services.AddNetBpmInstanceCatalog();

        // 平台业务面：提交审批编排（校验 → 落变量 → 完成 → 组装结果）
        builder.Services.AddNetBpmTaskSubmit();

        // 平台业务面：发起流程编排（闸门 → 参数镜像/校验 → 发号 → 启动 → 组装结果）
        builder.Services.AddNetBpmInstanceStart();

        // 平台业务面：终止实例（原因必填 + 落时间线，补上管理干预的审计断档）
        builder.Services.AddNetBpmInstanceTermination();

        // 平台业务面：待办归属调整（认领 / 取消认领 / 转办，三个动作都落时间线）
        builder.Services.AddNetBpmTaskAssignment();

        // 平台业务面：审批记录时间线（append-only；写时定稿 + 老实例从引擎历史回填）
        builder.Services.AddNetBpmApprovalTimeline();

        // 平台业务面：实例列表读模型（运行态 + 历史双源合并，游标分页）
        builder.Services.AddNetBpmInstanceList();

        // 平台业务面：参与者索引（ACT_HI_IDENTITYLINK + 历史任务 + 发起人，三个来源合成「人与单据的关系」）
        builder.Services.AddNetBpmInstanceParticipation();

        // 平台业务面：表单快照（提交时把「字段定义 + 值」一起冻住，历史页按当时定义渲染）
        builder.Services.AddNetBpmApprovalSnapshot();

        // 平台业务面：实例流转状态图（走过的节点 / 连线标红，当前环节加粗）
        builder.Services.AddNetBpmInstanceDiagram();

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
        else
        {
            // 关闭限流也要注册选项与计数器：中间件不装、依赖仍然可解析（测试里会注入断言）
            builder.Services.AddSingleton(new ClientRateLimitOptions { Enabled = false });
            builder.Services.AddSingleton<IRateLimiter>(_ => new FixedWindowRateLimiter(1024));
        }

        return options;
    }

    /// <summary>装配中间件管道（反代头 / PathBase 已由宿主处理）。</summary>
    /// <remarks>
    /// 顺序不敏感是假的，这里的顺序是<b>结论</b>：
    /// <list type="number">
    ///   <item><description>异常处理：最先装，后面任何一环抛错都能被翻译成 JSON；</description></item>
    ///   <item><description>鉴权：必须先于限流。限流要按 <c>clientId</c> 分区，而 <c>clientId</c>
    ///     只有验过密钥才有——反过来就只能按 IP 分区（这正是本次要修掉的问题）；</description></item>
    ///   <item><description>限流：先于幂等。被 429 的请求不该写一条幂等记录，
    ///     否则调用方重试时会被「同键同体」直接回放一个 429，永远解不开；</description></item>
    ///   <item><description>幂等：紧贴业务端点。</description></item>
    /// </list>
    /// </remarks>
    public static WebApplication UseNetBpmHost(this WebApplication app, NetBpmHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        app.UseExceptionHandler();

        // 鉴权 → 限流 → 幂等：未配置凭据时鉴权中间件放行，保持开发体验
        app.UseMiddleware<AuthenticationMiddleware>();

        if (options.RateLimitEnabled)
        {
            app.UseMiddleware<ClientRateLimitMiddleware>();
        }

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

        // 公钥（JWKS）：必须匿名可达，否则外部系统无法自验签（校验令牌却要求先有令牌是死循环）。
        // 只暴露 RS256 公钥；HS256 内网模式下 keys 为空数组，调用方据此可知本服务不提供非对称验签。
        app.MapGet("/.well-known/jwks.json", (JwtKeyMaterial keys) => Results.Ok(keys.ToJwks()))
            .WithTags("security")
            .AllowAnonymous();

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

        // 平台业务面（对外契约）：流程目录。与 /api/** 的引擎原语面分开，变更纪律不同
        app.MapProcessCatalogEndpoints();

        // 平台业务面（对外契约）：待办（options 消费语义字典）
        app.MapTaskEndpoints();

        // 平台业务面（对外契约）：实例详情
        app.MapInstanceEndpoints();

        // 平台接入管理（租户 / 客户端）：两个宿主都要有，否则「在哪发密钥」取决于部署形态
        app.MapClientAdminEndpoints();

        // 事件订阅管理（M13）：与客户端注册同属接入配置，放同一组路径与鉴权口径下
        app.MapEventSubscriptionEndpoints();

        mapAdditionalEndpoints?.Invoke(app);

        app.MapAuthEndpoints();

        return app;
    }

    /// <summary>
    /// 数据库启动步骤：按需执行幂等初始化脚本，再校验 <c>ACT_GE_PROPERTY.schema.version</c>。
    /// </summary>
    /// <remarks>
    /// 末尾顺带做平台侧引导（确保默认租户存在）。放在这里而不是各宿主 <c>Program</c> 里，
    /// 是因为它必须发生在「建表脚本执行之后」——顺序错了会得到一个「表不存在」的启动错误。
    /// </remarks>
    public static WebApplication ApplyNetBpmDatabaseStartup(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<NetBpmOptions>();

        if (!options.UsesDatabase)
        {
            // 无库模式也要引导：内存注册表同样是空的，没有租户就注册不了客户端
            return app.ApplyNetBpmPlatformStartup();
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

        return app.ApplyNetBpmPlatformStartup();
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
    /// 限流：<b>按调用方分区</b>（客户端 id → 用户 → IP），不再是按 IP。
    /// <list type="bullet">
    ///   <item><description>已注册客户端：用记录里的 <c>quota_per_minute</c>（一系统一配额），未配置则 <see cref="NetBpmHostOptions.ClientPerMinute"/>；</description></item>
    ///   <item><description>JWT 用户：按（租户, 操作人）分桶，默认 600 次/分钟；</description></item>
    ///   <item><description>无身份：按 IP 分档——登录 10、凭据探针 30、其余 600；</description></item>
    ///   <item><description>静态资源与 <c>/health</c>：不限流。</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// 计数器形态由 <see cref="NetBpmHostOptions.RateLimitStore"/> 决定：
    /// <list type="bullet">
    ///   <item><description><see cref="RateLimitStore.Memory"/>：进程内固定窗口，单副本正确；</description></item>
    ///   <item><description><see cref="RateLimitStore.Redis"/>：跨副本共享同一个桶，配额才是全局的。</description></item>
    /// </list>
    /// </remarks>
    private static void AddRateLimiting(WebApplicationBuilder builder, NetBpmHostOptions options)
    {
        var rateLimit = new ClientRateLimitOptions
        {
            Enabled = true,
            ApiPerMinute = options.ApiPerMinute,
            ProbePerMinute = options.ProbePerMinute,
            LoginPerMinute = options.LoginPerMinute,
            ClientPerMinute = options.ClientPerMinute,
        };

        builder.Services.AddSingleton(rateLimit);

        var section = options.RateLimitSection;

        if (options.RateLimitStore == RateLimitStore.Redis)
        {
            builder.Services.AddOptions<RedisRateLimitOptions>()
                .Bind(builder.Configuration.GetSection(section + ":Redis"));

            // 惰性建连：与作业队列同理，不在启动时同步连接 Redis（连不上不该拖慢启动）
            builder.Services.AddSingleton<IRateLimiter>(provider => new RedisFixedWindowRateLimiter(
                provider.GetRequiredService<IOptions<RedisRateLimitOptions>>().Value,
                provider.GetService<TimeProvider>(),
                provider.GetService<ILogger<RedisFixedWindowRateLimiter>>()));

            return;
        }

        builder.Services.AddSingleton<IRateLimiter>(provider => new FixedWindowRateLimiter(
            rateLimit.MaxPartitions,
            provider.GetService<TimeProvider>()));
    }
}

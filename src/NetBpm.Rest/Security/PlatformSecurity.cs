using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.Rest.Security;

/// <summary>
/// 平台接入层的服务注册：租户目录 + 客户端注册表（有库走库、无库走内存）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>AddNetBpmAuth</c> 分开是为了让「谁能被注册为服务」这件事只有一个入口：
/// 鉴权中间件、管理端点、后续的平台 API 都从这里取同一份 <see cref="IClientRegistry"/>，
/// 不会出现「端点写了内存实现、中间件读的是库」这种半开状态。
/// </para>
/// <para>
/// 两个接口指向<b>同一个实例</b>：<see cref="ITenantDirectory"/> 是给鉴权路径用的只读视图，
/// 注册表另外持有一份同一个对象。若各注册一个，内存实现下会出现「注册表写了、鉴权看不见」。
/// </para>
/// </remarks>
public static class PlatformSecurityExtensions
{
    /// <summary>配置节：<c>NetBpm:Security:Clients</c>。</summary>
    public const string ClientsSection = "NetBpm:Security:Clients";

    /// <summary>注册租户与客户端注册表（单例：客户端数量是几十量级，不需要每次请求重建）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置。</param>
    public static IServiceCollection AddNetBpmPlatformSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(ReadOptions(configuration));

        services.AddSingleton<ITenantRegistry>(provider =>
        {
            var clock = provider.GetService<TimeProvider>();

            return UsesDatabase(provider)
                ? new DapperTenantRegistry(provider.GetRequiredService<IDbConnectionFactory>(), clock)
                : new InMemoryTenantRegistry(seed: null, clock);
        });

        // 同一实例的只读投影：鉴权路径只被注入 ITenantDirectory，从类型上就写不了租户
        services.AddSingleton<ITenantDirectory>(provider => provider.GetRequiredService<ITenantRegistry>());

        services.AddSingleton<IClientRegistry>(provider =>
        {
            var clock = provider.GetService<TimeProvider>();
            var options = provider.GetRequiredService<ClientRegistryOptions>();
            var tenants = provider.GetRequiredService<ITenantDirectory>();

            return UsesDatabase(provider)
                ? new DapperClientRegistry(
                    provider.GetRequiredService<IDbConnectionFactory>(),
                    tenants,
                    options,
                    clock)
                : new InMemoryClientRegistry(tenants, options, clock);
        });

        return services;
    }

    /// <summary>
    /// 首次部署引导：确保默认租户存在（<c>NetBpm:Security:Tenants:Default</c>，缺省 <c>default</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 注册客户端要求租户已存在（否则接入方拿到一个「租户不存在」的 400 却不知道要先建租户）。
    /// 引导是幂等的：已存在则原样返回，<b>不覆盖</b>运维手工调过的配额。
    /// </para>
    /// <para>
    /// 失败只记警告不阻断启动：老库可能还没跑过补表脚本，此时应用应该能起来并提示运维，
    /// 而不是因为「平台表不存在」整个服务起不来。
    /// </para>
    /// </remarks>
    /// <param name="app">宿主。</param>
    public static WebApplication ApplyNetBpmPlatformStartup(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var registry = app.Services.GetRequiredService<ITenantRegistry>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NetBpm.Platform");

        var tenantId = configuration["NetBpm:Security:Tenants:Default"]?.Trim();
        var name = configuration["NetBpm:Security:Tenants:DefaultName"]?.Trim();

        var registration = new TenantRegistration(
            string.IsNullOrEmpty(tenantId) ? "default" : tenantId,
            string.IsNullOrEmpty(name) ? "默认租户" : name);

        try
        {
            var tenant = registry.EnsureAsync(registration).AsTask().GetAwaiter().GetResult();

            logger.LogInformation("平台租户已就绪：{TenantId}（{Status}）", tenant.TenantId, tenant.Status);

            WarnIfAnonymousAccessStillOpen(app, configuration, logger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "平台租户引导失败（nb_tenant 表可能尚未创建）。请执行 db/{Dialect}/netbpm_init.sql 后重启；"
                + "在此之前客户端注册不可用。",
                "mysql|sqlserver");
        }

        return app;
    }

    /// <summary>
    /// 已注册客户端但没打开 <c>RequireCredential</c> 时告警。
    /// </summary>
    /// <remarks>
    /// 这是最容易踩的部署坑：运维注册了一批客户端，以为「API 已经受保护」，
    /// 实际上匿名请求照样放行（因为没配旧 API Key / JWT）。数据驱动的安全边界不可见，
    /// 所以必须在启动日志里指名道姓说出来。
    /// </remarks>
    private static void WarnIfAnonymousAccessStillOpen(
        WebApplication app,
        IConfiguration configuration,
        ILogger logger)
    {
        if (configuration.GetValue("NetBpm:Security:RequireCredential", false)
            || !string.IsNullOrWhiteSpace(configuration["NetBpm:Security:ApiKey"])
            || !string.IsNullOrWhiteSpace(configuration["NetBpm:Security:Jwt:SigningKey"]))
        {
            return;
        }

        var clients = app.Services
            .GetRequiredService<IClientRegistry>()
            .ListAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();

        if (clients.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "已注册 {Count} 个接入客户端，但未要求凭据：匿名请求仍可访问 /api/**。"
            + "如需「必须带凭据」，请设置 NetBpm:Security:RequireCredential=true。",
            clients.Count);
    }

    private static bool UsesDatabase(IServiceProvider provider) =>
        provider.GetRequiredService<NetBpmOptions>().UsesDatabase;

    private static ClientRegistryOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(ClientsSection);

        var options = new ClientRegistryOptions();

        if (int.TryParse(section["SecretValidDays"], out var validDays) && validDays > 0)
        {
            options = options with { SecretValidDays = validDays };
        }

        if (int.TryParse(section["RotationGraceDays"], out var graceDays) && graceDays >= 0)
        {
            options = options with { RotationGraceDays = graceDays };
        }

        if (Enum.TryParse<ClientStatus>(section["InitialStatus"], ignoreCase: true, out var initialStatus))
        {
            options = options with { InitialStatus = initialStatus };
        }

        return options;
    }
}

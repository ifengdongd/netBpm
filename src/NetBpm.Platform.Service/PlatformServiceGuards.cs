using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Platform.Service;

/// <summary>
/// 启动自检：把「容器起来了但其实是残的」变成<b>起不来</b>。
/// </summary>
/// <remarks>
/// <para>
/// 这几条全部来自同一个教训：配置缺失时系统往往不会报错，而是安静地退化成另一种形态继续跑。
/// 比如没配连接串，宿主会回落到内存引擎 —— 流程照跑、接口照通，直到重启才发现数据全没了；
/// 又比如要求凭据却没配任何校验材料，所有 <c>/api/**</c> 一律 401，
/// 业务侧看到的是「NetBpm 挂了」，而 <c>docker ps</c> 显示 healthy。
/// </para>
/// <para>
/// 因此这里的原则是：<b>缺关键配置就 fail fast</b>（非零退出码，容器编排会重启并记录），
/// 只有「形态降级但仍然可用」的情况才用警告。
/// </para>
/// </remarks>
public static class PlatformServiceGuards
{
    /// <summary>
    /// 在注册服务<b>之前</b>校验配置。
    /// </summary>
    /// <remarks>
    /// 必须早于 <c>AddNetBpmHost</c>：引擎自己也检查连接串，但给的是面向开发者的技术文案。
    /// 在容器里排查的是运维，「请设置 NetBpm__ConnectionString 环境变量」比
    /// 「UseInMemoryExecutionTree = false 时必须提供 ConnectionString」有用得多。
    /// </remarks>
    /// <exception cref="InvalidOperationException">关键配置缺失或自相矛盾。</exception>
    public static void VerifyConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        VerifyDatabase(configuration);
        VerifyJobQueue(configuration);
        VerifyRateLimit(configuration);
        VerifyCredentialMaterials(configuration);
    }

    /// <summary>数据库模式必须有连接串：没有连接串会静默退化成内存引擎，数据随容器消失。</summary>
    private static void VerifyDatabase(IConfiguration configuration)
    {
        var usesDatabase = !configuration.GetValue("NetBpm:UseInMemoryExecutionTree", true);

        if (!usesDatabase)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(configuration["NetBpm:ConnectionString"]))
        {
            throw new InvalidOperationException(
                "已启用数据库模式（NetBpm:UseInMemoryExecutionTree=false）但没有配置连接串。"
                + "请设置 NetBpm__ConnectionString 环境变量（容器内用双下划线代替冒号）。"
                + "不配连接串会让宿主退回内存引擎 —— 流程能跑、数据不落盘，是生产上最难受的一种故障形态。");
        }

        var dialect = configuration["NetBpm:DatabaseType"];

        if (string.IsNullOrWhiteSpace(dialect))
        {
            throw new InvalidOperationException(
                "已启用数据库模式但没有指定 NetBpm:DatabaseType（MySql / SqlServer）。");
        }
    }

    /// <summary>
    /// Redis 队列写了 provider 却没地址时，注册会失败或退化成内存队列；
    /// 后者在多副本部署下表现为「作业只有部分实例在执行」，比直接报错难查得多。
    /// </summary>
    private static void VerifyJobQueue(IConfiguration configuration)
    {
        var provider = configuration["NetBpm:Jobs:Queue:Provider"];

        if (!string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var connectionString = configuration["NetBpm:Jobs:Queue:Redis:ConnectionString"];
        var host = configuration["NetBpm:Jobs:Queue:Redis:Host"];

        if (string.IsNullOrWhiteSpace(connectionString) && string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException(
                "作业队列配置为 Redis（NetBpm:Jobs:Queue:Provider=Redis）但没有地址。"
                + "请设置 NetBpm__Jobs__Queue__Redis__ConnectionString 或 ...Redis__Host。");
        }
    }

    /// <summary>
    /// 全局配额写了 Redis 却没地址时，限流会静默退化（Redis 不可用时默认放行），
    /// 结果就是「配了全局配额、实际没有限流」——比直接报错难查得多。
    /// </summary>
    private static void VerifyRateLimit(IConfiguration configuration)
    {
        var store = configuration["NetBpm:RateLimit:Store"];

        if (!string.Equals(store, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var connectionString = configuration["NetBpm:RateLimit:Redis:ConnectionString"];
        var host = configuration["NetBpm:RateLimit:Redis:Host"];

        if (string.IsNullOrWhiteSpace(connectionString) && string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException(
                "配额存储配置为 Redis（NetBpm:RateLimit:Store=Redis）但没有地址。"
                + "请设置 NetBpm__RateLimit__Redis__ConnectionString 或 ...Redis__Host。");
        }
    }

    /// <summary>
    /// 要求凭据就必须至少有一种校验材料，否则每个接口都以 401 收场，
    /// 而健康检查仍然通过 —— 从编排视角看一切正常。
    /// </summary>
    private static void VerifyCredentialMaterials(IConfiguration configuration)
    {
        var requireCredential = configuration.GetValue("NetBpm:Security:RequireCredential", false);

        if (!requireCredential)
        {
            return;
        }

        var hasApiKey = !string.IsNullOrWhiteSpace(configuration["NetBpm:Security:ApiKey"]);

        var hasJwt =
            !string.IsNullOrWhiteSpace(configuration["NetBpm:Security:Jwt:SigningKey"])
            || !string.IsNullOrWhiteSpace(configuration["NetBpm:Security:Jwt:RsaPublicKey"])
            || !string.IsNullOrWhiteSpace(configuration["NetBpm:Security:Jwt:RsaPublicKeyPath"])
            || !string.IsNullOrWhiteSpace(configuration["NetBpm:Security:Jwt:JwksUri"]);

        var hasClients = configuration.GetValue("NetBpm:Security:Clients:Enabled", false);

        if (!hasApiKey && !hasJwt && !hasClients)
        {
            throw new InvalidOperationException(
                "NetBpm:Security:RequireCredential=true 但没有配置任何凭据校验方式（ApiKey / Jwt / Clients），"
                + "所有 /api/** 请求都会被拒绝，而健康检查仍然通过。"
                + "请至少配置 NetBpm__Security__Clients__Enabled=true（推荐：注册接入客户端）"
                + "或 NetBpm__Security__Jwt__SigningKey。");
        }
    }

    /// <summary>
    /// 打印生效配置横幅：排障时被问到的第一个问题永远是这个容器到底连的哪个库、什么队列、有没有开凭据。
    /// </summary>
    public static void WriteBanner(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        using var scope = app.Services.CreateScope();

        var configuration = app.Configuration;
        var options = scope.ServiceProvider.GetRequiredService<NetBpmOptions>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<WebApplication>>();

        // 启动横幅不是仪式感：排障时第一个被问到的永远是「这个容器到底连的哪个库、
        // 用的什么队列、有没有开凭据」，而这些都是运行时读到的值，日志里留下一条最省事。
        var queue = configuration["NetBpm:Jobs:Queue:Provider"] ?? "Memory";
        var quotaStore = configuration["NetBpm:RateLimit:Store"] ?? "Memory";
        var requireCredential = configuration.GetValue("NetBpm:Security:RequireCredential", false);
        var wire = options.UsesDatabase
            ? $"{configuration["NetBpm:DatabaseType"]}"
            : "InMemory（数据不持久！）";

        logger.LogInformation(
            "NetBpm Platform.Service 就绪：env={Environment} wire={Wire} history={History} outbox={Outbox} queue={Queue} quota={Quota} requireCredential={RequireCredential}",
            app.Environment.EnvironmentName,
            wire,
            options.EnableHistory,
            options.EnableOutbox,
            queue,
            quotaStore,
            requireCredential);

        if (string.Equals(quotaStore, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            // 多副本下内存配额的实际上限 = 副本数 × 配置值，而每个副本的日志看起来都是「没超」
            logger.LogWarning(
                "配额为进程内计数（NetBpm:RateLimit:Store=Memory）：多副本部署时实际配额 = 副本数 × 配置值。"
                + "多副本请设 NetBpm__RateLimit__Store=Redis 并配置 NetBpm__RateLimit__Redis__*。");
        }

        if (string.Equals(queue, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            // 队列在进程内 = 每个副本只看得见自己的作业，而异步作业 / 定时器的语义是「至少要跑一次」：
            // 多副本时同一作业会被每个副本各执行一遍，异步委托的副作用（发短信、扣库存）就重复了。
            // 与配额那条同源：配置写在注释里没人看，启动日志里说一次最省事。
            logger.LogWarning(
                "作业队列为进程内实现（NetBpm:Jobs:Queue:Provider=Memory）：多副本部署时同一作业会在每个副本各执行一次，"
                + "异步委托的副作用会重复。多副本请设 NetBpm__Jobs__Queue__Provider=Redis 并配置 NetBpm__Jobs__Queue__Redis__*。");
        }

        if (!requireCredential)
        {
            logger.LogWarning(
                "未要求凭据（NetBpm:Security:RequireCredential=false）：任何能访问该端口的调用方都可读写流程数据。仅用于本机验证。");
        }

        if (options.UsesDatabase && !options.EnableHistory)
        {
            // 平台面 /workflow/v1 的审批历史依赖 ACT_HI_ACTINST；关掉时端点不是报错，
            // 而是「查得到实例、读不出流水」，信息缺失比报错更难发现。
            logger.LogWarning(
                "已启用数据库但未开启历史（NetBpm:EnableHistory=false）：审批历史、参与者索引与已办列表会退化或空缺。");
        }

        if (options.UsesDatabase && !options.AutoMigrate)
        {
            logger.LogInformation(
                "未开启自动建表（NetBpm:AutoMigrate=false）：请确保已对目标库执行 DbMigrate migrate。");
        }
    }
}

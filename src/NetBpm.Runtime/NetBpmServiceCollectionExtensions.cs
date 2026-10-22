using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.El;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Command;
using NetBpm.Engine.Command.Interceptors;
using NetBpm.Engine.Events;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Runtime.Hosting.JobQueue;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Migration;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence;
using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Database;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Events;
using NetBpm.Persistence.Execution;
using NetBpm.Persistence.History;
using NetBpm.Persistence.Idempotency;
using NetBpm.Persistence.Jobs;
using NetBpm.Persistence.Migration;
using NetBpm.Persistence.Tasks;

namespace NetBpm.Runtime;

/// <summary>引擎组合根：把各模块装配成可用的服务图。</summary>
public static class NetBpmServiceCollectionExtensions
{
    /// <summary>
    /// 注册审批链预处理钩子（发起人本人跳过 / 连续相同审批人合并 / 空审批人转管理员）。
    /// </summary>
    /// <remarks>
    /// <b>必须显式调用</b>：审批链整形是产品策略 —— 「发起人是否跳过」在不同业务里
    /// 有相反的答案，引擎不应替业务做决定。不调用时行为与改造前完全一致。
    /// </remarks>
    public static IServiceCollection AddNetBpmApproverDeduplication(
        this IServiceCollection services,
        Action<ApproverDeduplicationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ApproverDeduplicationOptions();
        configure?.Invoke(options);

        return services.AddNetBpmApproverResolver(new ApproverDeduplicationResolver(options));
    }

    /// <summary>注册自定义的审批链预处理钩子实现。</summary>
    public static IServiceCollection AddNetBpmApproverResolver(
        this IServiceCollection services,
        IApproverResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolver);

        return services.AddSingleton(resolver);
    }

    /// <summary>
    /// 注册异步作业后台执行器（<c>asyncBefore</c> / <c>asyncAfter</c>）。
    /// <para>不注册时作业仍会落库，但只有调用 <c>IProcessEngine.ExecuteDueAsyncJobsAsync</c> 才会推进。</para>
    /// </summary>
    public static IServiceCollection AddNetBpmAsyncJobExecutor(
        this IServiceCollection services,
        Action<Hosting.AsyncJobExecutorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<Hosting.AsyncJobExecutorOptions>();

        if (configure is not null)
        {
            options.Configure(configure);
        }

        // 兜底默认：进程内队列。宿主若自行注册过 IJobQueue，TryAdd 不会覆盖。
        services.TryAddSingleton<IJobQueue, InMemoryJobQueue>();

        services.AddHostedService<Hosting.AsyncJobExecutorService>();

        return services;
    }

    /// <summary>
    /// 按 <c>NetBpm:Jobs:Queue:Provider</c> 选择延迟作业队列的实现。
    /// </summary>
    /// <remarks>
    /// 可选值：<c>Memory</c>（默认，进程内）/ <c>Redis</c>（多宿主共享）。
    /// Redis 段的键形如 <c>NetBpm:Jobs:Queue:Redis:Host</c>。
    /// </remarks>
    /// <exception cref="InvalidOperationException">配置了未知的实现名。</exception>
    public static IServiceCollection AddNetBpmJobQueue(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "NetBpm:Jobs:Queue") =>
        Hosting.JobQueue.JobQueueServiceCollectionExtensions
            .AddNetBpmJobQueue(services, configuration, sectionName);

    /// <summary>
    /// 注册 outbox 后台发布器。只有同时开启 <see cref="NetBpmOptions.EnableOutbox"/> 才有事件可发；
    /// 宿主需要注册 <c>IOutboxEventHandler</c> 实现，否则发布器只告警不消费。
    /// </summary>
    public static IServiceCollection AddNetBpmOutboxPublisher(
        this IServiceCollection services,
        Action<Hosting.OutboxPublisherOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<Hosting.OutboxPublisherOptions>();

        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddHostedService<Hosting.OutboxPublisherService>();

        return services;
    }

    /// <summary>注册异步作业执行器，参数从 <c>NetBpm:Jobs</c> 读取。</summary>
    public static IServiceCollection AddNetBpmAsyncJobExecutor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddNetBpmAsyncJobExecutor(options =>
        {
            var section = configuration.GetSection("NetBpm:Jobs");

            if (bool.TryParse(section["Enabled"], out var enabled))
            {
                options.Enabled = enabled;
            }

            if (int.TryParse(section["BatchSize"], out var batchSize) && batchSize > 0)
            {
                options.BatchSize = batchSize;
            }

            if (int.TryParse(section["PollIntervalSeconds"], out var pollSeconds) && pollSeconds > 0)
            {
                options.PollInterval = TimeSpan.FromSeconds(pollSeconds);
            }
        });
    }

    /// <summary>注册 outbox 发布器，参数从 <c>NetBpm:Outbox</c> 读取。</summary>
    public static IServiceCollection AddNetBpmOutboxPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddNetBpmOutboxPublisher(options =>
        {
            var section = configuration.GetSection("NetBpm:Outbox");

            if (bool.TryParse(section["Enabled"], out var enabled))
            {
                options.Enabled = enabled;
            }

            if (int.TryParse(section["BatchSize"], out var batchSize) && batchSize > 0)
            {
                options.BatchSize = batchSize;
            }

            if (int.TryParse(section["PollIntervalSeconds"], out var pollSeconds) && pollSeconds > 0)
            {
                options.PollInterval = TimeSpan.FromSeconds(pollSeconds);
            }
        });
    }

    /// <summary>
    /// 注册 NetBpm 引擎。
    /// <para>拦截器链顺序（§5.6）：Log → Transaction → CommandContext。</para>
    /// </summary>
    public static IServiceCollection AddNetBpm(
        this IServiceCollection services,
        Action<NetBpmOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new NetBpmOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        if (options.UsesDatabase && string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new EngineException(
                "NetBpmOptions.UseInMemoryExecutionTree = false 时必须提供 ConnectionString。" +
                "若只想跑单元测试 / POC，请保持 UseInMemoryExecutionTree = true。");
        }

        if (options.EnableHistory && !options.UsesDatabase)
        {
            throw new EngineException(
                "历史写入（ACT_HI_*）需要数据库宿主：请把 UseInMemoryExecutionTree 设为 false 并提供 ConnectionString。"
                + "内存宿主没有历史存储，静默忽略 EnableHistory 只会掩盖配置错误。");
        }

        // ── P2/P4：转换、校验 ─────────────────────────────────────────
        services.AddSingleton<IBpmnXmlConverter, BpmnXmlConverter>();
        services.AddSingleton<IProcessValidator, ProcessValidator>();

        // ── P3：表达式语言（当前为过渡实现，见 ExpressionManager 的说明）──
        services.AddSingleton<IExpressionManager, ExpressionManager>();

        // ── P7：身份（最小实现，可替换）──────────────────────────────
        // TryAdd 而不是 Add：宿主若在调用 AddNetBpm **之前**就注册了自己的实现（或调用了
        // AddNetBpmUserGroupManager<T>()），这里不能再补一份空实现 —— 否则容器里有两份，
        // 单服务解析取最后一份，候选组就会被悄悄展开成 0 个人。
        services.TryAddSingleton<IUserGroupManager, EmptyUserGroupManager>();

        // ── P5：持久化（按模式切换内存 / 数据库）──────────────────────
        RegisterPersistence(services, options);

        // ── 异步作业与事务性 outbox ─────────────────────────────────
        // 作业存储总是注册（模型里可以没有 async 标记，注册本身无成本）；
        // 是否真正启用 outbox 由 EnableOutbox 决定：关闭时不注册 IEventOutbox，
        // 事件分发器只走进程内监听器，避免给每个事件增加一次写入。
        if (options.UsesDatabase)
        {
            services.AddSingleton<IAsyncJobManager, DapperAsyncJobManager>();
            services.AddSingleton<IEventOutboxStore, Persistence.Events.DapperEventOutbox>();
        }
        else
        {
            services.AddSingleton<IAsyncJobManager, InMemoryAsyncJobManager>();
            services.AddSingleton<IEventOutboxStore, InMemoryEventOutbox>();
        }

        if (options.EnableOutbox)
        {
            services.AddSingleton<IEventOutbox>(
                provider => provider.GetRequiredService<IEventOutboxStore>());
        }

        // ── HTTP 幂等：内存宿主单进程去重，数据库宿主跨实例去重 ─────────
        if (options.UsesDatabase)
        {
            services.AddSingleton<IIdempotencyStore, DapperIdempotencyStore>();
        }
        else
        {
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        }

        // ── 命令级幂等（引擎层去重，与 HTTP 幂等表分开）────────────────
        // 数据库宿主才落库去重；内存宿主单进程内无意义，故不注册 ——
        // 引擎侧发现端口为 null 时自动按「不启用幂等」处理，行为与旧版一致。
        if (options.UsesDatabase)
        {
            services.AddSingleton<ICommandIdempotencyStore, DapperCommandIdempotencyStore>();
        }

        // ── 管理治理：动作审计 + 历史归档 ─────────────────────────────
        // 审计：内存宿主持有最近 N 条（开发可查）；数据库宿主可长期追溯。
        // 归档：只在数据库宿主可用 —— 内存宿主没有 ACT_HI_*，明确返回 501 而不是静默空操作。
        if (options.UsesDatabase)
        {
            services.AddSingleton<IAdminAuditStore, DapperAdminAuditStore>();
            services.AddSingleton<IHistoryArchiveStore, DapperHistoryArchiveStore>();
            services.AddSingleton<IAdminMetricsStore, DapperAdminMetricsStore>();
        }
        else
        {
            services.AddSingleton<IAdminAuditStore, InMemoryAdminAuditStore>();
            services.AddSingleton<IHistoryArchiveStore, UnsupportedHistoryArchiveStore>();
            services.AddSingleton<IAdminMetricsStore, UnsupportedAdminMetricsStore>();
        }

        // ── P6：引擎服务与议程 ──────────────────────────────────────
        services.AddSingleton<IActivityDelegateResolver, ServiceProviderActivityDelegateResolver>();

        // 延迟取用，打破 IEngineServices ⇄ IActivityBehaviorFactory 的构造循环
        services.AddSingleton<IActivityBehaviorFactory>(
            provider => new ActivityBehaviorFactory(() => provider.GetRequiredService<IEngineServices>()));

        services.AddSingleton<IEngineServices>(provider => EngineServices.Create(
            provider.GetRequiredService<IExecutionManager>(),
            provider.GetRequiredService<IHistoryManager>(),
            provider.GetRequiredService<IExpressionManager>(),
            provider.GetRequiredService<IIdGenerator>(),
            provider.GetRequiredService<IActivityBehaviorFactory>(),
            provider.GetRequiredService<IActivityDelegateResolver>(),
            provider.GetRequiredService<ITaskManager>(),
            provider.GetRequiredService<IProcessDefinitionResolver>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger("NetBpm.Engine"),
            provider.GetRequiredService<ITimerJobManager>(),
            provider.GetRequiredService<IAsyncJobManager>(),
            provider.GetService<IEventOutbox>(),
            provider.GetService<ICommandIdempotencyStore>(),
            provider.GetService<IApproverResolver>()));

        // ── 命令与拦截器 ────────────────────────────────────────────
        // 链序：Log → Retry → Transaction → CommandContext
        // Retry 必须在 Transaction 之外：一次重试 = 一个全新的 DbSession（新一级缓存）。
        // 数据库宿主还要把「死锁 / 锁等待超时」纳入重试 —— 并行 Join 与会签计数都会真实撞上。
        services.AddSingleton(
            options.UsesDatabase
                ? TransientDatabaseErrors.CreateRetryPolicy()
                : new CommandRetryPolicy());
        services.AddSingleton<ICommandInterceptor, LogInterceptor>();
        services.AddSingleton<ICommandInterceptor>(provider => new RetryInterceptor(
            provider.GetRequiredService<CommandRetryPolicy>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<RetryInterceptor>()));
        services.AddSingleton<ICommandInterceptor, TransactionInterceptor>();
        services.AddSingleton<ICommandInterceptor, CommandContextInterceptor>();
        services.AddSingleton<ICommandExecutor, CommandExecutor>();

        // ── 宿主门面 ────────────────────────────────────────────────
        services.AddSingleton<IProcessEngine, ProcessEngine>();

        return services;
    }

    /// <summary>
    /// 注册流程元素解析器 —— 两种宿主都需要：跨命令恢复等待型节点时，
    /// 执行实体只带 <c>ACT_ID_</c> 字符串，必须靠流程定义解析回元素对象。
    /// </summary>
    private static void RegisterDefinitionResolver(IServiceCollection services)
    {
        services.AddSingleton<IProcessDefinitionResolver, ProcessDefinitionResolver>();
    }

    /// <summary>从配置节（默认 <c>NetBpm</c>）读取 <see cref="NetBpmOptions"/> 并注册引擎。</summary>
    public static IServiceCollection AddNetBpm(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "NetBpm")
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(sectionName);

        return services.AddNetBpm(options =>
        {
            var databaseType = section["DatabaseType"];
            if (!string.IsNullOrWhiteSpace(databaseType)
                && Enum.TryParse<DatabaseType>(databaseType, ignoreCase: true, out var parsed))
            {
                options.DatabaseType = parsed;
            }

            var connectionString = section["ConnectionString"];
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                options.ConnectionString = connectionString;
            }

            if (int.TryParse(section["IdBlockSize"], out var blockSize) && blockSize > 0)
            {
                options.IdBlockSize = blockSize;
            }

            if (bool.TryParse(section["EnableHistory"], out var enableHistory))
            {
                options.EnableHistory = enableHistory;
            }

            if (bool.TryParse(section["UseInMemoryExecutionTree"], out var inMemory))
            {
                options.UseInMemoryExecutionTree = inMemory;
            }

            if (bool.TryParse(section["ValidateSchemaOnStartup"], out var validateOnStartup))
            {
                options.ValidateSchemaOnStartup = validateOnStartup;
            }

            if (bool.TryParse(section["AutoMigrate"], out var autoMigrate))
            {
                options.AutoMigrate = autoMigrate;
            }

            if (bool.TryParse(section["EnableOutbox"], out var enableOutbox))
            {
                options.EnableOutbox = enableOutbox;
            }
        });
    }

    private static void RegisterPersistence(IServiceCollection services, NetBpmOptions options)
    {
        if (!options.UsesDatabase)
        {
            // 内存宿主：单元测试与 POC 脚手架
            services.AddSingleton<IIdGenerator, InMemoryIdGenerator>();
            services.AddSingleton<ITimerJobManager, InMemoryTimerJobManager>();
            services.AddSingleton<IMigrationLog, InMemoryMigrationLog>();
            services.AddSingleton<IExecutionManager, InMemoryExecutionManager>();
            services.AddSingleton<IProcessDefinitionStore, InMemoryProcessDefinitionStore>();
            services.AddSingleton<ITaskManager, InMemoryTaskManager>();

            // 每个命令一个上下文；内存宿主没有事务边界
            services.AddSingleton<Func<CommandContext>>(
                provider => () => new CommandContext(provider.GetRequiredService<IEngineServices>()));

            RegisterDefinitionResolver(services);
            RegisterHistory(services, options);
            return;
        }

        // 数据库宿主（P5）
        services.AddSingleton<IDbConnectionFactory>(
            _ => new DbConnectionFactory(options.DatabaseType, options.ConnectionString!));
        services.AddSingleton<IDbSessionFactory, DbSessionFactory>();
        services.AddSingleton<IDbSessionProvider, CommandScopedDbSessionProvider>();

        services.AddSingleton<IPropertyStore, DapperPropertyStore>();
        services.AddSingleton<DatabaseSchemaChecker>();
        services.AddSingleton(provider => new DatabaseMigrator(
            provider.GetRequiredService<IDbSessionFactory>(),
            DatabaseScripts.ResolveDirectory(options.DatabaseType)));

        // ID 生成器：区块分配 + next.dbid 乐观锁（§7.3）
        services.AddSingleton<IIdGenerator>(
            provider => new DbIdGenerator(provider.GetRequiredService<IPropertyStore>(), options.IdBlockSize));

        services.AddSingleton<IVariableInstanceManager, DapperVariableInstanceManager>();
        services.AddSingleton<ITimerJobManager, DapperTimerJobManager>();
        services.AddSingleton<IMigrationLog, DapperMigrationLog>();
        services.AddSingleton<IExecutionManager, DapperExecutionManager>();
        services.AddSingleton<IProcessDefinitionStore, DapperProcessDefinitionStore>();
        services.AddSingleton<ITaskManager, DapperTaskManager>();

        // 每个命令一个 DbSession（= 一个事务 + 一份一级缓存）。
        // 传工厂而不是实例：重试时要能整体换掉会话（旧会话的脏实体不得复用）。
        services.AddSingleton<Func<CommandContext>>(provider => () =>
        {
            var engineServices = provider.GetRequiredService<IEngineServices>();
            var sessionFactory = provider.GetRequiredService<IDbSessionFactory>();

            return new CommandContext(
                engineServices,
                transactionFactory: () => sessionFactory.Create());
        });

        RegisterDefinitionResolver(services);
        RegisterHistory(services, options);
    }

    private static void RegisterHistory(IServiceCollection services, NetBpmOptions options)
    {
        if (!options.EnableHistory)
        {
            // 历史级别 NONE：对应 Activiti 的 NullHistoryManager
            services.AddSingleton<IHistoryManager, NullHistoryManager>();
            return;
        }

        // 历史级别 AUDIT：5 张 ACT_HI_* 表全部写入（见 DapperHistoryManager 的类注释）
        services.AddSingleton<IHistoryManager, DapperHistoryManager>();
    }
}

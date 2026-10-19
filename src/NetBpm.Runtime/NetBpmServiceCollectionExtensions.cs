using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.El;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Command;
using NetBpm.Engine.Command.Interceptors;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Execution;
using NetBpm.Persistence.History;
using NetBpm.Persistence.Idempotency;
using NetBpm.Persistence.Tasks;

namespace NetBpm.Runtime;

/// <summary>引擎组合根：把各模块装配成可用的服务图。</summary>
public static class NetBpmServiceCollectionExtensions
{
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
        services.AddSingleton<IUserGroupManager, EmptyUserGroupManager>();

        // ── P5：持久化（按模式切换内存 / 数据库）──────────────────────
        RegisterPersistence(services, options);

        // ── HTTP 幂等：内存宿主单进程去重，数据库宿主跨实例去重 ─────────
        if (options.UsesDatabase)
        {
            services.AddSingleton<IIdempotencyStore, DapperIdempotencyStore>();
        }
        else
        {
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
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
            provider.GetService<ILoggerFactory>()?.CreateLogger("NetBpm.Engine")));

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
        });
    }

    private static void RegisterPersistence(IServiceCollection services, NetBpmOptions options)
    {
        if (!options.UsesDatabase)
        {
            // 内存宿主：单元测试与 POC 脚手架
            services.AddSingleton<IIdGenerator, InMemoryIdGenerator>();
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

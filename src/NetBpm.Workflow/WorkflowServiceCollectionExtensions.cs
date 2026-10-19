using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NetBpm.Workflow;

/// <summary>
/// 审批流程应用层的组合根 —— 一行装好 <see cref="IWorkflowService"/> 与业务数据端口。
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddNetBpm(builder.Configuration);          // 引擎
/// builder.Services.AddNetBpmWorkflow&lt;LeaveBusinessStore&gt;();   // 应用层（本扩展）
/// builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly);
/// </code>
/// </example>
public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// 注册应用层，并指定业务数据端口的实现类型（由容器构造）。
    /// </summary>
    /// <typeparam name="TDataStore">业务数据端口实现（如落 JSON / 落库的那个类）。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <returns><paramref name="services"/>，便于链式调用。</returns>
    public static IServiceCollection AddNetBpmWorkflow<TDataStore>(this IServiceCollection services)
        where TDataStore : class, IWorkflowDataStore
    {
        ArgumentNullException.ThrowIfNull(services);

        // 具体类型与端口指向同一个单例 —— 宿主可以按具体类型注入，拿到的还是那一份
        services.TryAddSingleton<TDataStore>();
        services.TryAddSingleton<IWorkflowDataStore>(provider => provider.GetRequiredService<TDataStore>());
        services.TryAddSingleton<IWorkflowService, WorkflowService>();

        // 运行期上下文（流程定义 id + 环节名 → id 映射）：由 WorkflowProcessBootstrapper 在部署后填充。
        // 业务数据存储落库要用它做「环节名 ↔ 环节 id」换算，所以随应用层一起注册。
        services.TryAddSingleton<WorkflowRuntimeContext>();

        return services;
    }

    /// <summary>
    /// 注册应用层，业务数据端口由工厂提供（适合需要按配置决定实现 / 拿外部依赖的场景）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="storeFactory">端口工厂。</param>
    /// <returns><paramref name="services"/>，便于链式调用。</returns>
    public static IServiceCollection AddNetBpmWorkflow(
        this IServiceCollection services,
        Func<IServiceProvider, IWorkflowDataStore> storeFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storeFactory);

        services.TryAddSingleton(storeFactory);
        services.TryAddSingleton<IWorkflowService, WorkflowService>();
        services.TryAddSingleton<WorkflowRuntimeContext>();

        return services;
    }
}

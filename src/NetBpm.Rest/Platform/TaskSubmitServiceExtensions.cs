using Microsoft.Extensions.DependencyInjection;

namespace NetBpm.Rest.Platform;

/// <summary>提交审批编排服务的注册。</summary>
/// <remarks>
/// 提交编排依赖引擎门面、待办读模型（选项 + 归属）与实例读模型（结果组装），三者都已由宿主注册；
/// 这里只把 <see cref="ITaskSubmitService"/> 挂进容器。它是有状态的（每请求一个），用 Scoped。
/// </remarks>
public static class TaskSubmitServiceExtensions
{
    /// <summary>注册提交审批编排服务。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmTaskSubmit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITaskSubmitService, TaskSubmitService>();

        return services;
    }
}

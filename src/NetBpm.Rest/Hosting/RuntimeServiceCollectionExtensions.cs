using Microsoft.Extensions.DependencyInjection;
using NetBpm.Rest.Services;

namespace NetBpm.Rest.Hosting;

/// <summary>运行态应用服务（命令 / 变量 / 历史查询）的注册入口。</summary>
public static class RuntimeServiceCollectionExtensions
{
    /// <summary>
    /// 注册运行态端点依赖的应用服务。
    /// <para>
    /// 全部 <b>Scoped</b>：它们只依赖 <c>IProcessEngine</c> / <c>IExecutionManager</c> / <c>AdminActionAuditor</c>
    /// 这些本身按请求作用域装配的协作者（审计器是 scoped），跟着请求走最省心。
    /// </para>
    /// </summary>
    public static IServiceCollection AddNetBpmRuntimeServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<RuntimeAudit>();
        services.AddScoped<IInstanceCommandService, InstanceCommandService>();
        services.AddScoped<ITaskCommandService, TaskCommandService>();
        services.AddScoped<IVariableService, VariableService>();
        services.AddScoped<IHistoryQueryService, HistoryQueryService>();

        return services;
    }
}

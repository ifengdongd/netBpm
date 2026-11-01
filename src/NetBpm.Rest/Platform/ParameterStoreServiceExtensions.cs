using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 参数两块存储的注册：规范索引（<c>nb_process_parameter</c>）与修订历史（<c>nb_parameter_snapshot</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 两者的生命周期不同，故各占一个接口：
/// 索引每流程一份、可整组重建；修订历史每实例一份、只增不改。
/// 合成一个「参数存储」会逼出一个「有的方法有的宿主用不上」的胖接口。
/// </para>
/// <para>
/// 存储按单例注册（无可变状态，只是门到库的墙）。无库宿主自动落到内存实现，
/// 与时间线 / 表单快照同命运。
/// </para>
/// </remarks>
public static class ParameterStoreServiceExtensions
{
    /// <summary>注册参数规范索引与参数修订历史（有库走库、无库走内存）。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmParameterStores(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProcessParameterStore>(provider =>
            provider.GetRequiredService<NetBpmOptions>().UsesDatabase
                ? new DapperProcessParameterStore(provider.GetRequiredService<IDbConnectionFactory>())
                : new InMemoryProcessParameterStore());

        services.AddSingleton<IParameterSnapshotStore>(provider =>
            provider.GetRequiredService<NetBpmOptions>().UsesDatabase
                ? new DapperParameterSnapshotStore(provider.GetRequiredService<IDbConnectionFactory>())
                : new InMemoryParameterSnapshotStore());

        return services;
    }
}

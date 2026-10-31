using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>表单快照的服务注册。</summary>
/// <remarks>
/// 与时间线同命运：每次提交写一份，数量级跟随审批量。
/// 存储按单例注册（无可变状态，只是门到库的墙），平台服务用 Scoped。
/// 无库宿主自动落到内存实现，测试不需要额外配置。
/// </remarks>
public static class ApprovalSnapshotServiceExtensions
{
    /// <summary>注册表单快照（有库走库、无库走内存）。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmApprovalSnapshot(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IApprovalSnapshotStore>(provider =>
            provider.GetRequiredService<NetBpmOptions>().UsesDatabase
                ? new DapperApprovalSnapshotStore(provider.GetRequiredService<IDbConnectionFactory>())
                : new InMemoryApprovalSnapshotStore());

        services.AddScoped<IApprovalSnapshot, ApprovalSnapshot>();

        return services;
    }
}

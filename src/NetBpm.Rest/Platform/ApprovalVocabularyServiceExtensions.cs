using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>审批语义字典的服务注册。</summary>
/// <remarks>
/// 字典与目录一样是<b>读多写零</b>的（写路径属管理面）。解析结果要按「流程 key + 租户」缓存，
/// 因此依赖 <c>IMemoryCache</c>（目录扩展里已经注册过，这里幂等地再加一次也无害）。
/// </remarks>
public static class ApprovalVocabularyServiceExtensions
{
    /// <summary>注册审批语义字典（有库走库、无库走内存）。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmApprovalVocabulary(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();

        services.AddSingleton<IApprovalDictionaryStore>(provider =>
            provider.GetRequiredService<NetBpmOptions>().UsesDatabase
                ? new DapperApprovalDictionaryStore(provider.GetRequiredService<IDbConnectionFactory>())
                : new InMemoryApprovalDictionaryStore());

        services.AddScoped<IApprovalVocabulary, ApprovalVocabulary>();

        return services;
    }
}

namespace NetBpm.Admin.Storage;

/// <summary>Admin 存储层共享的配置常量（各 Store 保持同一口径）。</summary>
internal static class AdminStorageDefaults
{
    /// <summary>租户配置键（appsettings 里的 <c>NetBpm:AdminTenantId</c>）。</summary>
    internal const string TenantConfigKey = "NetBpm:AdminTenantId";

    /// <summary>未配置时使用的默认租户 id。</summary>
    internal const long DefaultTenantId = 1L;
}

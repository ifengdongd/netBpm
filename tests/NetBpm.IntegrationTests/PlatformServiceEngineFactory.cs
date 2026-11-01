extern alias PlatformHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 在进程内启动<b>独立部署宿主</b>（<c>NetBpm.Platform.Service</c>）—— 与 <see cref="RestEngineFactory"/>
/// 起的是两个不同的组合根，这正是 <see cref="DualModeTests"/> 要对比的东西。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="AdminEngineFactory"/> 相同的注意事项：必须用 <c>UseSetting</c>，
/// 因为宿主的 <c>Program.cs</c> 在<b>服务注册阶段</b>就读取了配置。
/// </para>
/// <para>
/// 这里把「生产默认」尽量保留：启动自检（<c>PlatformServiceGuards</c>）照常跑，
/// 只把凭据要求关掉（测试没有身份材料，且开放模式下 <c>X-User-Id</c> 才是身份来源）。
/// 关掉 <c>AutoMigrate</c> / <c>ValidateSchemaOnStartup</c> 是为了不让每个用例都跑一遍建表脚本。
/// </para>
/// </remarks>
public sealed class PlatformServiceEngineFactory : WebApplicationFactory<PlatformHost::Program>
{
    private readonly bool _useDatabase;

    public PlatformServiceEngineFactory(bool useDatabase = false, string? connectionString = null)
    {
        _useDatabase = useDatabase;
        ConnectionString = connectionString;
    }

    /// <summary>数据库模式的连接串；内存模式为 <see langword="null"/>。</summary>
    public string? ConnectionString { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("NetBpm:UseInMemoryExecutionTree", _useDatabase ? "false" : "true");
        builder.UseSetting("NetBpm:EnableHistory", "false");
        builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
        builder.UseSetting("NetBpm:AutoMigrate", "false");
        builder.UseSetting("NetBpm:Timer:Enabled", "false");
        builder.UseSetting("NetBpm:Jobs:Enabled", "false");
        builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
        builder.UseSetting("NetBpm:Security:RequireCredential", "false");
        builder.UseSetting("NetBpm:Security:Tenants:Default", DualModeTests.DefaultTenant);

        if (_useDatabase)
        {
            builder.UseSetting("NetBpm:DatabaseType", "MySql");
            builder.UseSetting("NetBpm:ConnectionString", ConnectionString);
        }
    }
}

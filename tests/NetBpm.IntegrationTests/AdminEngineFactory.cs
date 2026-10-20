extern alias AdminHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 在进程内启动<b>真实的管理端宿主</b>（模型保存 / 历史 / 回滚 / 发布）。
/// <para>
/// 与 <see cref="RestEngineFactory"/> 相同的注意事项：必须用 <c>UseSetting</c>，
/// 因为 <c>Program.cs</c> 在服务注册阶段就读取了配置。
/// </para>
/// </summary>
public sealed class AdminEngineFactory : WebApplicationFactory<AdminHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("NetBpm:DatabaseType", "MySql");
        builder.UseSetting("NetBpm:ConnectionString", MySqlTestDatabase.ConnectionString);
        builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "false");
        builder.UseSetting("NetBpm:EnableHistory", "true");
        builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
        builder.UseSetting("NetBpm:AutoMigrate", "false");
        builder.UseSetting("NetBpm:AdminTenantId", "1");
        builder.UseSetting("NetBpm:Timer:Enabled", "false");
    }
}

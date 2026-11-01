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
        builder.UseSetting("NetBpm:Admin:RateLimit:Enabled", "false");

        // 🔑 测试宿主必须显式「关掉凭据」：管理端 Development 配置里带了一把 dev JWT 签名密钥
        // （本地登录功能要用），而 AuthenticationMiddleware 只要看到「有校验密钥」就进入
        // 「必须带凭据」模式 —— 于是所有不带 Authorization 的请求一律 401，
        // 表现为模型治理 / 控制台这批用例全红，且与业务代码毫无关系。
        // 鉴权由 AdminGovernanceTests（显式 WithWebHostBuilder 注入自己的密钥并自签 token）、
        // JwtAuthenticationTests、LoginFlowTests 专门覆盖，这里验证的是业务链路。
        builder.UseSetting("NetBpm:Security:Jwt:SigningKey", string.Empty);
        builder.UseSetting("NetBpm:Security:RequireCredential", "false");
    }
}

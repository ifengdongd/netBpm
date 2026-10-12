using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 在进程内启动<b>真实的 REST 宿主</b>（P9 端到端验收）。
/// <para>
/// 只覆盖配置，不改任何代码路径 —— 走的就是生产组合根 <c>AddNetBpm</c>，
/// 因此这条链路验证的是「宿主 + DI + 引擎 + 持久化」的整体装配，而不是引擎单测。
/// </para>
/// </summary>
public sealed class RestEngineFactory : WebApplicationFactory<Program>
{
    /// <summary>使用真实 MySQL 且打开历史写入（历史是「查历史」端点的前提）。</summary>
    /// <remarks>
    /// ⚠️ 必须用 <c>UseSetting</c>（宿主设置）而不是 <c>ConfigureAppConfiguration</c>：
    /// 宿主的 <c>Program.cs</c> 在<b>服务注册阶段</b>就读取了配置（<c>AddNetBpm(builder.Configuration)</c>），
    /// 而 <c>ConfigureAppConfiguration</c> 的回调要等到 <c>Build()</c> 才生效 —— 那时配置早已被读走，
    /// 注入会被静默忽略（表现为「测试全跑在内存宿主上，数据库一行没写」）。
    /// 宿主设置参与 <c>WebApplicationBuilder.Configuration</c> 的构造，因此在注册阶段可见。
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("NetBpm:DatabaseType", "MySql");
        builder.UseSetting("NetBpm:ConnectionString", MySqlTestDatabase.ConnectionString);
        builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "false");
        builder.UseSetting("NetBpm:EnableHistory", "true");
        builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
        builder.UseSetting("NetBpm:AutoMigrate", "false");
    }
}

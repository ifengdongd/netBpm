using Microsoft.Extensions.Logging.Console;

namespace NetBpm.Platform.Service;

/// <summary>
/// 把宿主调成<b>容器形态</b>：端口、日志、以及「环境变量优先」的配置约定。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>NetBpm.Rest</c>（演示宿主）刻意分开的原因只有一个：<b>默认值必须是安全的</b>。
/// 演示宿主要让人一行命令就能跑起来，所以凭据、批量大小、清理策略统统放宽；
/// 独立部署形态反过来，宁可起不来也不能「起来却没人管」。同一个工程靠开关切换安全基线，
/// 迟早会出现「这次忘了设 RequireCredential」这种不上报的事故。
/// </para>
/// </remarks>
public static class PlatformServiceBootstrap
{
    /// <summary>环境变量里的监听端口（Cloud Run / Heroku / 多数 PaaS 的惯例）。</summary>
    public const string PortEnvironmentVariable = "PORT";

    /// <summary>默认监听端口（ASP.NET 官方镜像的约定），缺省 8080。</summary>
    public const int DefaultPort = 8080;

    /// <summary>
    /// 配置宿主：端口、日志格式、以及环境变量覆盖。
    /// </summary>
    /// <remarks>
    /// 环境变量不需要额外接线：<c>NetBpm__ConnectionString</c> 这类「双下划线」写法由
    /// ASP.NET 的环境变量配置源天然映射成 <c>NetBpm:ConnectionString</c>。这里显式支持的只有
    /// <see cref="PortEnvironmentVariable"/> —— 它是 PaaS 的事实标准，但 .NET 不认。
    /// </remarks>
    public static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ConfigureKestrel(builder);

        // 容器里的日志最终进 stdout，被采集器逐行收走。因此要点只有三个：
        // 单行（默认的 simple console 已经是）、UTC（跨时区排障不用换算）、不要颜色（ANSI 转义会污染取证）。
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            options.ColorBehavior = LoggerColorBehavior.Disabled;
        });

        // 反代场景下保留原始 scheme / host（否则 Location 头与回调地址会变成 http://localhost）
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
            // 容器网络里本来就至少一跳（ingress / sidecar），默认只认最后一跳会直接丢掉真实来源
            options.ForwardLimit = null;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder)
    {
        var rawPort = Environment.GetEnvironmentVariable(PortEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(rawPort))
        {
            if (!int.TryParse(rawPort, out var port) || port is <= 0 or > 65535)
            {
                throw new InvalidOperationException(
                    $"环境变量 {PortEnvironmentVariable} 的值 '{rawPort}' 不是合法端口（1-65535）。");
            }

            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

            return;
        }

        // 没给 PORT 就显式监听 8080：容器里 0.0.0.0 是必须的，绑 127.0.0.1 会让端口映射形同虚设，
        // 表现为「compose 起来了但宿主机 curl 不通」，而这种失败通常要排查很久。
        builder.WebHost.UseUrls($"http://0.0.0.0:{DefaultPort}");
    }
}

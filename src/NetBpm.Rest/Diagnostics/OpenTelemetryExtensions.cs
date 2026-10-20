using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NetBpm.Rest.Diagnostics;

/// <summary>
/// OpenTelemetry 的<b>可选</b>接线：默认关闭，开启后经 OTLP 导出。
/// <para>
/// 启用条件（满足其一）：
/// ① <c>NetBpm:Otel:Enabled=true</c>；② OTel 标准环境变量 <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
/// 已设置（容器编排里注入端点即生效，无需改应用配置）。
/// </para>
/// <para>
/// 只订阅<b>本引擎自定义</b>的 <see cref="AdminDiagnostics.MeterName"/> ——
/// HTTP / 运行时指标是否采集由宿主自己的仪表决定，不在这里替宿主做主（避免标签爆炸）。
/// 导出端点遵循 OTel 标准环境变量（默认 <c>http://localhost:4317</c>，gRPC）。
/// </para>
/// <para>
/// 未启用时本扩展是空操作：埋点（<see cref="AdminDiagnostics"/> 的计数器）只是无人订阅的静默计数。
/// </para>
/// </summary>
public static class OpenTelemetryExtensions
{
    /// <summary>显式开关的配置键。</summary>
    public const string EnabledKey = "NetBpm:Otel:Enabled";

    public static IServiceCollection AddNetBpmOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration.GetValue(EnabledKey, false)
            || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (!enabled)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: "NetBpm",
                    serviceVersion: typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString()
                        ?? "unknown"))
            .WithMetrics(metrics => metrics
                .AddMeter(AdminDiagnostics.MeterName)
                .AddOtlpExporter())
            // HTTP 入口 span：每次 /api 请求一条 trace，经 OTLP 导出（采样/过滤交给采集端）
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter());

        return services;
    }
}

using System.Diagnostics.Metrics;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Diagnostics;

/// <summary>
/// 管理治理的可观测指标（OpenTelemetry Meter）。
/// <para>
/// 只定义 Meter 与指标，<b>不绑定导出器</b>：宿主通过
/// <see cref="OpenTelemetryExtensions.AddNetBpmOpenTelemetry"/> 决定是否经 OTLP 导出；
/// 未接线时埋点只是无人订阅的静默计数，不产生日志噪声。
/// </para>
/// <para>
/// 指标清单（标签键全小写点分，遵循 OTel 命名约定）：
/// <list type="bullet">
///   <item><description><c>netbpm.admin.authorization.denied</c>：RBAC 拒绝次数
///     （<c>netbpm.admin.scope</c> / <c>netbpm.auth.mode</c>）；</description></item>
///   <item><description><c>netbpm.admin.audit.recorded</c>：审计留痕条数
///     （<c>netbpm.admin.action</c> / <c>netbpm.audit.success</c> / <c>netbpm.auth.mode</c>）；</description></item>
///   <item><description><c>netbpm.admin.audit.purged</c>：保留策略清理删除的条数。</description></item>
/// </list>
/// </para>
/// </summary>
public static class AdminDiagnostics
{
    /// <summary>Meter 名（OTel 仪表名；<c>AddMeter</c> 时使用）。</summary>
    public const string MeterName = "NetBpm.Admin";

    /// <summary>
    /// 共享 Meter（静态：埋点是无状态计数，不随请求生命周期变化）。
    /// </summary>
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>RBAC 拒绝（缺 scope，越权尝试被 403）：管理面与引擎面的拒绝共用一条通道。</summary>
    public static readonly Counter<long> AuthorizationDenied =
        Meter.CreateCounter<long>(
            "netbpm.admin.authorization.denied",
            unit: "{request}",
            description: "因缺少所需 scope（netbpm.admin.* 或 workflow:*）被拒绝的次数。");

    /// <summary>审计留痕（成功与失败都计）。</summary>
    public static readonly Counter<long> AuditRecorded =
        Meter.CreateCounter<long>(
            "netbpm.admin.audit.recorded",
            unit: "{record}",
            description: "写入管理动作审计的记录数。");

    /// <summary>审计保留策略清理。</summary>
    public static readonly Counter<long> AuditPurged =
        Meter.CreateCounter<long>(
            "netbpm.admin.audit.purged",
            unit: "{record}",
            description: "按保留策略删除的审计记录数。");

    /// <summary>标签键：所需的管理 scope。</summary>
    public const string TagScope = "netbpm.admin.scope";

    /// <summary>标签键：管理动作标识。</summary>
    public const string TagAction = "netbpm.admin.action";

    /// <summary>标签键：动作是否成功（"true" / "false"，OTel 标签值为字符串）。</summary>
    public const string TagSuccess = "netbpm.audit.success";

    /// <summary>标签键：鉴权方式（jwt / api_key / client / none）。</summary>
    public const string TagAuthMode = "netbpm.auth.mode";

    /// <summary>鉴权方式的标签值（与审计表 <c>AUTH_MODE_</c> 列同一套取值，见 <c>AdminActionAuditor.AuthModeName</c>）。</summary>
    public static string AuthModeTag(RequestAuthMode mode) => mode switch
    {
        RequestAuthMode.Jwt => "jwt",
        RequestAuthMode.ApiKey => "api_key",
        // 客户端凭据单独成桶：收紧后第一个要看的就是「哪个接入系统缺哪个 scope」，
        // 归到 none 里会和开发模式的噪声混在一起
        RequestAuthMode.Client => "client",
        _ => "none",
    };
}

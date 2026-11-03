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
///   <item><description><c>netbpm.admin.audit.write_failures</c>：审计落库失败的条数
///     （<c>netbpm.admin.action</c>）—— fail-open 的告警依据；</description></item>
///   <item><description><c>netbpm.admin.audit.purged</c>：保留策略清理删除的条数；</description></item>
///   <item><description><c>netbpm.platform.surface.denied</c>：注册客户端在引擎原语面做业务写入被拒的次数
///     （<c>netbpm.admin.action</c> / <c>netbpm.auth.mode</c>）。</description></item>
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

    /// <summary>
    /// 审计<b>落库</b>失败次数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="AuditRecorded"/> 的 <c>netbpm.audit.success</c> 标签不是一回事：那个标签说的是
    /// 「这次管理动作成功了没有」，本指标说的是「这条审计记录写进去了没有」。
    /// 业务成功但审计写失败，是「动作发生了、没人知道」——只有这条指标能看见它。
    /// </para>
    /// <para>
    /// 审计写入保持 <b>fail-open</b>（不让审计表故障阻断运维），所以这条指标必须配 page 级告警：
    /// fail-open 的前提是「有人会发现」，没人发现时它等价于静默关闭审计。
    /// </para>
    /// </remarks>
    public static readonly Counter<long> AuditWriteFailures =
        Meter.CreateCounter<long>(
            "netbpm.admin.audit.write_failures",
            unit: "{record}",
            description: "管理动作审计写入存储失败的次数（动作本身可能已经生效）。");

    /// <summary>审计保留策略清理。</summary>
    public static readonly Counter<long> AuditPurged =
        Meter.CreateCounter<long>(
            "netbpm.admin.audit.purged",
            unit: "{record}",
            description: "按保留策略删除的审计记录数。");

    /// <summary>
    /// 引擎原语面拒绝「业务写入」的次数（注册客户端被要求改走 <c>/workflow/v1</c>）。
    /// <para>
    /// 与 <see cref="AuthorizationDenied"/> 分开计数：那里是「缺 scope，补上就能过」，
    /// 这里是「这条路根本不给客户端走，补 scope 也没用」。两者的处置完全不同，
    /// 混在一个指标里会让值班的人一直去补一个补不上的 scope。
    /// </para>
    /// </summary>
    public static readonly Counter<long> BusinessSurfaceDenied =
        Meter.CreateCounter<long>(
            "netbpm.platform.surface.denied",
            unit: "{request}",
            description: "注册客户端在引擎原语面执行发起 / 办理 / 批量办理被拒绝的次数。");

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

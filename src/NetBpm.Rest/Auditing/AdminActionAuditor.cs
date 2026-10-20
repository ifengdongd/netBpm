using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetBpm.Persistence.Admin;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Auditing;

/// <summary>
/// 管理动作审计器：把「谁、在哪个租户、对什么、做了什么、结果如何」写入审计存储。
/// <para>
/// 约定：
/// <list type="bullet">
///   <item><description>审计写入失败<b>不影响</b>业务结果（只记 warning）——
///     不能因为审计表缺失就让转办 / 跳转不可用；但会留下明显日志，便于及时发现。</description></item>
///   <item><description><c>detail</c> 只放动作元数据（目标节点 / 原办理人 / 变量名），
///     <b>不放变量值</b>：变量可能是敏感业务数据，且会让审计表膨胀。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class AdminActionAuditor
{
    private const int MaxErrorLength = 500;

    private static readonly JsonSerializerOptions DetailJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAdminAuditStore _store;
    private readonly ILogger<AdminActionAuditor> _logger;

    public AdminActionAuditor(IAdminAuditStore store, ILogger<AdminActionAuditor> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <summary>记录一条审计。</summary>
    /// <param name="context">请求身份上下文。</param>
    /// <param name="action">动作标识（见 <see cref="AdminAuditActions"/>）。</param>
    /// <param name="targetType">目标类型（见 <see cref="AdminAuditTargets"/>）。</param>
    /// <param name="targetId">目标 id。</param>
    /// <param name="processInstanceId">关联流程实例 id（可选）。</param>
    /// <param name="detail">动作明细（可序列化对象；不要放变量值）。</param>
    /// <param name="success">动作是否成功。</param>
    /// <param name="error">失败原因。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task RecordAsync(
        RequestContext context,
        string action,
        string targetType,
        string targetId,
        string? processInstanceId = null,
        object? detail = null,
        bool success = true,
        string? error = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);

        // 动作发生即计数（不依赖审计存储是否写成功）：给审计量与失败率提供可观测面
        AdminDiagnostics.AuditRecorded.Add(
            1,
            new(AdminDiagnostics.TagAction, action),
            new(AdminDiagnostics.TagSuccess, success ? "true" : "false"),
            new(AdminDiagnostics.TagAuthMode, AdminDiagnostics.AuthModeTag(context.AuthMode)));

        try
        {
            var entry = new AdminAuditEntry(
                Guid.NewGuid().ToString("N"),
                context.EffectiveTenantId,
                context.UserId,
                AuthModeName(context.AuthMode),
                action,
                targetType,
                targetId,
                processInstanceId,
                detail is null ? null : JsonSerializer.Serialize(detail, DetailJsonOptions),
                success,
                Truncate(error),
                DateTime.UtcNow);

            await _store.RecordAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 请求已取消：不再追加审计，也不掩盖原始取消语义
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "管理动作审计写入失败：{Action} {TargetType}/{TargetId}（actor={Actor}）",
                action,
                targetType,
                targetId,
                context.UserId);
        }
    }

    private static string AuthModeName(RequestAuthMode mode) => mode switch
    {
        RequestAuthMode.Jwt => "jwt",
        RequestAuthMode.ApiKey => "api_key",
        _ => "none",
    };

    private static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}

/// <summary>审计器的注册入口。</summary>
public static class AuditingServiceCollectionExtensions
{
    /// <summary>注册管理动作审计器（依赖 <c>IAdminAuditStore</c>，由 <c>AddNetBpm</c> 提供）。</summary>
    public static IServiceCollection AddNetBpmAuditing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<AdminActionAuditor>();

        return services;
    }
}

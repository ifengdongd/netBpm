using Microsoft.AspNetCore.Http;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Auditing;

/// <summary>
/// 管理动作的执行包装：先执行、再按结果写审计（成功 / 失败都留痕），失败照常抛给全局异常处理器。
/// <para>
/// 约定：<b>授权必须在本类之前完成</b> —— 被 403 拒绝的请求不应产生审计记录，
/// 否则审计表会被越权尝试刷满，且「有没有执行」会被混淆。
/// </para>
/// <para>
/// 供运行时端点（转办 / 跳转 / 变量 / 实例生命周期）与设计器端点（部署 / 定义挂起 / 删部署）共用。
/// </para>
/// </summary>
public static class AuditedAction
{
    /// <summary>
    /// 执行 <paramref name="execute"/> 并按结果写审计。
    /// </summary>
    /// <param name="auditor">审计器。</param>
    /// <param name="context">请求身份上下文。</param>
    /// <param name="action">动作标识（见 <see cref="NetBpm.Persistence.Admin.AdminAuditActions"/>）。</param>
    /// <param name="targetType">目标类型（见 <see cref="NetBpm.Persistence.Admin.AdminAuditTargets"/>）。</param>
    /// <param name="targetId">目标 id。</param>
    /// <param name="processInstanceId">关联流程实例 id（可选）。</param>
    /// <param name="detail">动作明细（不要放变量值 / 敏感内容）。</param>
    /// <param name="execute">实际业务动作。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<IResult> RunAsync(
        AdminActionAuditor auditor,
        RequestContext context,
        string action,
        string targetType,
        string targetId,
        string? processInstanceId,
        object? detail,
        Func<Task<IResult>> execute,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(execute);

        try
        {
            var result = await execute().ConfigureAwait(false);

            var statusCode = (result as IStatusCodeHttpResult)?.StatusCode;
            var succeeded = statusCode is null or < StatusCodes.Status400BadRequest;

            await auditor.RecordAsync(
                context,
                action,
                targetType,
                targetId,
                processInstanceId,
                detail,
                succeeded,
                succeeded ? null : $"动作返回 HTTP {statusCode}。",
                ct).ConfigureAwait(false);

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await auditor.RecordAsync(
                context,
                action,
                targetType,
                targetId,
                processInstanceId,
                detail,
                success: false,
                error: exception.Message,
                ct: ct).ConfigureAwait(false);

            throw;
        }
    }
}

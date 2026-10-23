using NetBpm.Persistence.Admin;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Services;

/// <summary>
/// 运行态管理动作的域级审计包装：<b>成功与失败都留痕</b>，失败照常抛出交给全局异常处理器。
/// <para>
/// 与 <see cref="NetBpm.Rest.Auditing.AuditedAction"/> 的区别：那个版本以 <c>IResult</c> 为单位（HTTP 层），
/// 这个版本以「业务动作返回域对象」为单位，供 Service 使用 —— 否则 Service 只能返回 <c>IResult</c>，
/// 又退化成「端点逻辑换个文件」。
/// </para>
/// <para>
/// 约定：<b>授权必须在调用本类之前完成</b>（过滤器或 Service 的前置校验），
/// 被 403 拒绝的请求不产生审计记录，避免审计表被越权尝试刷满。
/// </para>
/// </summary>
internal sealed class RuntimeAudit(AdminActionAuditor auditor)
{
    /// <summary>执行动作并按结果写审计（异常即失败）。</summary>
    public async Task<T> RunAsync<T>(
        RequestContext context,
        string action,
        string targetType,
        string targetId,
        string? processInstanceId,
        object? detail,
        Func<Task<T>> execute,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execute);

        try
        {
            var result = await execute().ConfigureAwait(false);

            await RecordAsync(context, action, targetType, targetId, processInstanceId, detail, true, null, ct)
                .ConfigureAwait(false);

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordAsync(
                    context,
                    action,
                    targetType,
                    targetId,
                    processInstanceId,
                    detail,
                    success: false,
                    error: exception.Message,
                    ct)
                .ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>记录一次已完成的动作（批量端点逐项调用）。</summary>
    public Task RecordAsync(
        RequestContext context,
        string action,
        string targetType,
        string targetId,
        string? processInstanceId,
        object? detail,
        bool success,
        string? error,
        CancellationToken ct) =>
        auditor.RecordAsync(
            context,
            action,
            targetType,
            targetId,
            processInstanceId,
            detail,
            success,
            error,
            ct);
}

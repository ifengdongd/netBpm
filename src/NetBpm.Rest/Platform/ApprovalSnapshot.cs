using Microsoft.Extensions.Logging;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 表单快照的读写出口（提交时冻住「当时的字段定义 + 值」，<c>GET /tasks/{id}/form</c> 按需再拉）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么值要带着定义走：值离开定义就没有类型、没有下拉项、没有必填规则。
/// 只存值的快照在三个月后（定义升级之后）会变成一堆无法解释的裸字符串，
/// 而带着定义的快照能把历史办理页渲染得跟当时一模一样。
/// </para>
/// <para>
/// 写不到 <see langword="null"/>：本次<b>没记下</b>快照（写失败 / 没有表单），
/// 与写了空快照不是一回事，调用方据此决定要不要显示「当时怎么填的」这一屏。
/// </para>
/// </remarks>
public interface IApprovalSnapshot
{
    /// <summary>
    /// 冻住一次提交的表单：字段定义 + 表单值 + 附件引用。
    /// </summary>
    /// <remarks>
    /// 失败<b>不影响审批</b>：任务已经被引擎办结，此时抛错会让调用方以为提交没成功。
    /// 缺一份快照是遗憾，让业务方以为审批丢了才是事故。
    /// </remarks>
    /// <param name="view">办理前的待办视图（里面带着<b>所属定义版本</b>的表单定义）。</param>
    /// <param name="request">提交请求（表单值 / 附件引用）。</param>
    /// <param name="optionId">本次审批选项 id。</param>
    /// <param name="context">调用方身份（租户、主体、调用系统）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照写成功返回 <see langword="true"/>；重复提交或未写入返回 <see langword="false"/>。</returns>
    ValueTask<bool> CaptureAsync(
        TaskView view,
        SubmitTaskRequest request,
        string optionId,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 取某个任务的表单快照。
    /// </summary>
    /// <remarks>
    /// 租户与流程白名单在这里收口：没记下快照的任务按<b>不存在</b>处理（404），
    /// 而不是返回 403——后者会替调用方确认「这张单确实存在」。
    /// </remarks>
    /// <param name="taskId">任务 id。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<FormSnapshot?> FindAsync(
        string taskId,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>取某个实例的全部表单快照（历史详情页的多屏渲染 / 导出）。</summary>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<FormSnapshot>> ListByInstanceAsync(
        string instanceId,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ApprovalSnapshot : IApprovalSnapshot
{
    /// <summary>未声明租户时的默认租户（与鉴权中间件同口径）。</summary>
    private const string DefaultTenantId = "default";

    private readonly IApprovalSnapshotStore _store;
    private readonly ILogger<ApprovalSnapshot> _logger;

    /// <summary>构造。</summary>
    /// <param name="store">快照存储。</param>
    /// <param name="logger">日志。</param>
    public ApprovalSnapshot(IApprovalSnapshotStore store, ILogger<ApprovalSnapshot> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> CaptureAsync(
        TaskView view,
        SubmitTaskRequest request,
        string optionId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // 没有表单定义就没有「当时的字段」可冻：空表单的环节不放行，
        // 这里直接返回，既省一次写，也省得以后到处判空
        if (view.Form is not { Count: > 0 })
        {
            return false;
        }

        var record = new ApprovalSnapshotRecord(
            TaskId: view.TaskId,
            InstanceId: view.InstanceId,
            TenantId: context.EffectiveTenantId ?? DefaultTenantId,
            ProcessKey: view.ProcessKey,
            ProcessDefinitionId: view.ProcessDefinitionId,
            DefinitionVersion: view.DefinitionVersion ?? 0,
            ActivityId: view.ActivityId,
            ActivityName: view.ActivityName,
            ActorId: context.SubjectId,
            ActorCallerId: context.CallerId,
            OptionId: optionId,
            Fields: view.Form,
            Values: request.FormValues ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            Attachments: request.Attachments,
            CapturedAt: DateTimeOffset.UtcNow);

        try
        {
            return await _store.AppendAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "写表单快照失败（任务 {TaskId}，实例 {InstanceId}）：审批已完成，缺一份快照。",
                view.TaskId,
                view.InstanceId);

            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<FormSnapshot?> FindAsync(
        string taskId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(context);

        var record = await ReadAsync(taskId, context, ct).ConfigureAwait(false);

        return record is null ? null : ToSnapshot(record);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FormSnapshot>> ListByInstanceAsync(
        string instanceId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var records = await _store.ListByInstanceAsync(instanceId, ct).ConfigureAwait(false);

            return [.. records.Where(record => IsVisible(record, context)).Select(ToSnapshot)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "读实例 {InstanceId} 的表单快照失败：本次返回空列表。",
                instanceId);

            return [];
        }
    }

    /// <summary>读一条并做可见性判定（越权一律转成「没有这条记录」）。</summary>
    private async ValueTask<ApprovalSnapshotRecord?> ReadAsync(
        string taskId,
        RequestContext context,
        CancellationToken ct)
    {
        try
        {
            var record = await _store.FindAsync(taskId, ct).ConfigureAwait(false);

            return record is not null && IsVisible(record, context) ? record : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "读任务 {TaskId} 的表单快照失败。", taskId);

            return null;
        }
    }

    /// <summary>租户边界 + 流程白名单，与实例读模型同口径。</summary>
    private static bool IsVisible(ApprovalSnapshotRecord record, RequestContext context)
    {
        if (context.EffectiveTenantId is { Length: > 0 } tenant
            && !string.IsNullOrEmpty(record.TenantId)
            && !string.Equals(record.TenantId, tenant, StringComparison.Ordinal))
        {
            return false;
        }

        return record.ProcessKey is null || context.CanUseProcess(record.ProcessKey);
    }

    private static FormSnapshot ToSnapshot(ApprovalSnapshotRecord record) =>
        new(
            TaskId: record.TaskId,
            DefinitionVersion: record.DefinitionVersion,
            Schema: new FormSchema(
                record.ProcessKey ?? string.Empty,
                record.ActivityId,
                record.Fields),
            Values: record.Values,
            CapturedAt: record.CapturedAt,
            InstanceId: record.InstanceId,
            ActivityName: record.ActivityName,
            ActorId: record.ActorId,
            OptionId: record.OptionId,
            Attachments: record.Attachments);
}

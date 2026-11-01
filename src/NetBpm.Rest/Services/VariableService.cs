using NetBpm.Engine;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Admin;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Services;

/// <summary>
/// 变量服务：流程实例变量与任务局部变量的读 / 写 / 删。
/// <para>
/// 端点为什么不该直接调引擎：写变量是「引擎命令 + 管理动作审计」的组合，而且
/// <b>变量名里的冒号与值类型只用于审计明细</b>（不记值本身）—— 这些规则留在端点里就会
/// 在 7 个端点各写一遍，且很容易在新增入口时忘记留痕。
/// </para>
/// <para>
/// 授权仍在过滤器：实例变量用 <see cref="RuntimeAccess.RequireInstanceAccess"/> 的
/// <c>ReadVariables</c> / <c>WriteVariables</c>，任务变量用 <see cref="RuntimeAccess.RequireTaskAccess"/> 的同名级别。
/// </para>
/// </summary>
public interface IVariableService
{
    /// <summary>读流程实例变量（沿作用域链合并）。</summary>
    Task<IReadOnlyDictionary<string, object?>> GetInstanceVariablesAsync(
        string processInstanceId,
        CancellationToken ct);

    /// <summary>写单个流程实例变量（已有则更新，没有则建在实例作用域）。</summary>
    Task SetVariableAsync(
        string processInstanceId,
        string name,
        object? value,
        string valueType,
        RequestContext context,
        CancellationToken ct);

    /// <summary>批量写流程实例变量（同一事务）。</summary>
    Task SetVariablesAsync(
        string processInstanceId,
        IReadOnlyDictionary<string, object?> variables,
        RequestContext context,
        CancellationToken ct);

    /// <summary>删除流程实例变量；不存在时 404。</summary>
    Task RemoveVariableAsync(
        string processInstanceId,
        string name,
        RequestContext context,
        CancellationToken ct);

    /// <summary>读任务局部变量（不上溯实例作用域）。</summary>
    Task<IReadOnlyDictionary<string, object?>> GetTaskVariablesAsync(string taskId, CancellationToken ct);

    /// <summary>写任务局部变量。</summary>
    Task SetTaskVariableAsync(
        TaskEntity task,
        string name,
        object? value,
        string valueType,
        RequestContext context,
        CancellationToken ct);

    /// <summary>删除任务局部变量；不存在时 404。</summary>
    Task RemoveTaskVariableAsync(
        TaskEntity task,
        string name,
        RequestContext context,
        CancellationToken ct);
}

/// <summary><see cref="IVariableService"/> 的默认实现。</summary>
internal sealed class VariableService(
    IProcessEngine engine,
    RuntimeAudit audit,
    IApprovalTimeline timeline) : IVariableService
{
    /// <summary>单条时间线里变量值的最大长度（防止一个大 JSON 把审计行撑爆）。</summary>
    private const int MaxValueLength = 120;

    /// <summary>
    /// 变量直写后补一条 <see cref="TimelineEntryKind.ParametersUpdated"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 变量直写是<b>越过 schema</b> 的管理动作（<c>workflow:admin:variables</c>），它改的是单据的内容本身。
    /// 只写管理动作审计的话，单据的审批记录里看不到「金额在终审前被人改过」这种事，
    /// 而合规审计恰恰要能回答这个问题。所以这里把<b>变更前后值</b>写进 <c>Comment</c>。
    /// </para>
    /// <para>
    /// 记值是有代价的（变量可能含敏感信息），但它的替代方案是「根本不知道被改过」，
    /// 两者之间取前者；值一律截断，避免把整段 JSON 塞进审计行。
    /// </para>
    /// </remarks>
    private async ValueTask AppendChangeAsync(
        string processInstanceId,
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> written,
        RequestContext context,
        CancellationToken ct)
    {
        var parts = written.Keys
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                before.TryGetValue(name, out var oldValue);
                written.TryGetValue(name, out var newValue);

                return $"{name}: {Describe(oldValue)} → {Describe(newValue)}";
            });

        var comment = string.Join("；", parts);

        if (comment.Length > 1000)
        {
            comment = string.Concat(comment.AsSpan(0, 1000), "…（已截断）");
        }

        _ = await timeline.AppendAsync(
            processInstanceId,
            new TimelineEntry(
                Sequence: 0,
                InstanceId: processInstanceId,
                Kind: TimelineEntryKind.ParametersUpdated,
                ActivityId: null,
                ActivityName: null,
                TaskId: null,
                ActorId: context.SubjectId,
                ActorCallerId: null,
                OptionId: null,
                Semantic: null,
                Comment: comment,
                TargetNodeId: null,
                FormSnapshotTaskId: null,
                OccurredAt: DateTimeOffset.UtcNow),
            context,
            ct).ConfigureAwait(false);
    }

    /// <summary>变量值的审计表达：null 记空串、过长截断。</summary>
    private static string Describe(object? value)
    {
        var text = value?.ToString() ?? "（空）";

        return text.Length <= MaxValueLength
            ? text
            : string.Concat(text.AsSpan(0, MaxValueLength), "…");
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetInstanceVariablesAsync(
        string processInstanceId,
        CancellationToken ct) =>
        await engine.GetVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

    public async Task SetVariableAsync(
        string processInstanceId,
        string name,
        object? value,
        string valueType,
        RequestContext context,
        CancellationToken ct) =>
        await audit.RunAsync(
            context,
            AdminAuditActions.VariableWrite,
            AdminAuditTargets.Variable,
            $"{processInstanceId}:{name}",
            processInstanceId,
            new { name, valueType, scope = "process_instance" },
            async () =>
            {
                // 变更前后值要在写入<b>之前</b>读：写完再读就只剩新值，审计行会退化成一句「改过了」。
                var before = await engine.GetVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

                await engine.SetVariableAsync(processInstanceId, name, value, ct).ConfigureAwait(false);

                await AppendChangeAsync(
                    processInstanceId,
                    before,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { [name] = value },
                    context,
                    ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);

    public async Task SetVariablesAsync(
        string processInstanceId,
        IReadOnlyDictionary<string, object?> variables,
        RequestContext context,
        CancellationToken ct)
    {
        var names = variables.Keys.Order(StringComparer.Ordinal).ToArray();

        await audit.RunAsync(
            context,
            AdminAuditActions.VariableWrite,
            AdminAuditTargets.Variable,
            processInstanceId,
            processInstanceId,
            new { names, scope = "process_instance" },
            async () =>
            {
                var before = await engine.GetVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

                await engine.SetVariablesAsync(processInstanceId, variables, ct).ConfigureAwait(false);

                await AppendChangeAsync(
                    processInstanceId,
                    before,
                    variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    context,
                    ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task RemoveVariableAsync(
        string processInstanceId,
        string name,
        RequestContext context,
        CancellationToken ct) =>
        await audit.RunAsync(
            context,
            AdminAuditActions.VariableDelete,
            AdminAuditTargets.Variable,
            $"{processInstanceId}:{name}",
            processInstanceId,
            new { name, scope = "process_instance" },
            async () =>
            {
                var before = await engine.GetVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

                if (!await engine.RemoveVariableAsync(processInstanceId, name, ct).ConfigureAwait(false))
                {
                    throw new ResourceNotFoundException($"变量 '{name}' 不存在。");
                }

                // 删除也记：审计关心的是「这张单的内容被改过」，删掉一个字段同样是改。
                await AppendChangeAsync(
                    processInstanceId,
                    before,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { [name] = null },
                    context,
                    ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, object?>> GetTaskVariablesAsync(
        string taskId,
        CancellationToken ct) =>
        await engine.GetTaskVariablesAsync(taskId, ct).ConfigureAwait(false);

    public async Task SetTaskVariableAsync(
        TaskEntity task,
        string name,
        object? value,
        string valueType,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await audit.RunAsync(
            context,
            AdminAuditActions.TaskVariableWrite,
            AdminAuditTargets.Variable,
            $"{task.Id}:{name}",
            task.ProcessInstanceId,
            new { name, valueType, scope = "task", taskId = task.Id },
            async () =>
            {
                await engine.SetTaskVariableAsync(task.Id, name, value, ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task RemoveTaskVariableAsync(
        TaskEntity task,
        string name,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        await audit.RunAsync(
            context,
            AdminAuditActions.TaskVariableDelete,
            AdminAuditTargets.Variable,
            $"{task.Id}:{name}",
            task.ProcessInstanceId,
            new { name, scope = "task", taskId = task.Id },
            async () =>
            {
                if (!await engine.RemoveTaskVariableAsync(task.Id, name, ct).ConfigureAwait(false))
                {
                    throw new ResourceNotFoundException($"任务局部变量 '{name}' 不存在。");
                }

                return true;
            },
            ct).ConfigureAwait(false);
    }
}

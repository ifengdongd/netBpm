using NetBpm.Engine;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Admin;
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
internal sealed class VariableService(IProcessEngine engine, RuntimeAudit audit) : IVariableService
{
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
                await engine.SetVariableAsync(processInstanceId, name, value, ct).ConfigureAwait(false);

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
                await engine.SetVariablesAsync(processInstanceId, variables, ct).ConfigureAwait(false);

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
                if (!await engine.RemoveVariableAsync(processInstanceId, name, ct).ConfigureAwait(false))
                {
                    throw new ResourceNotFoundException($"变量 '{name}' 不存在。");
                }

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

using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>
/// 写流程实例作用域上的变量（运行期补偿 / 纠错）。
/// <para>
/// 语义与 Activiti <c>runtimeService.setVariable(processInstanceId, name, value)</c> 一致：
/// 本作用域已有则改本地，否则上溯父作用域，到顶仍无则建在流程实例上。
/// 历史行由变量管理器在写路径上同步（<c>ACT_HI_VARINST</c> 只增不删）。
/// </para>
/// </summary>
public sealed class SetVariablesCommand : ICommand<Unit>
{
    private readonly string _processInstanceId;
    private readonly IReadOnlyDictionary<string, object?> _variables;

    public SetVariablesCommand(string processInstanceId, IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentNullException.ThrowIfNull(variables);

        _processInstanceId = processInstanceId;
        _variables = variables;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var processInstance = await context.Services.ExecutionManager
            .FindByIdAsync(_processInstanceId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"流程实例 '{_processInstanceId}' 不存在（可能已结束）。");

        foreach (var (name, value) in _variables)
        {
            await processInstance.SetVariableAsync(name, value, ct).ConfigureAwait(false);
        }

        return Unit.Value;
    }
}

/// <summary>
/// 删除流程实例作用域上的变量（B2.4）。
/// <para>
/// 历史语义（有意为之）：<c>ACT_HI_VARINST</c> 行保留（历史账本只增不删，与历史身份关系同策略）；
/// longString 的字节行随运行行一并删除。
/// </para>
/// </summary>
public sealed class DeleteVariableCommand : ICommand<bool>
{
    private readonly string _processInstanceId;
    private readonly string _name;

    public DeleteVariableCommand(string processInstanceId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _processInstanceId = processInstanceId;
        _name = name;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var processInstance = await context.Services.ExecutionManager
            .FindByIdAsync(_processInstanceId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"流程实例 '{_processInstanceId}' 不存在（可能已结束）。");

        return await processInstance.RemoveVariableAsync(_name, ct).ConfigureAwait(false);
    }
}

/// <summary>写任务局部变量（B2.4）。变量落在 <c>ACT_RU_VARIABLE.TASK_ID_</c> 行，随任务存亡。</summary>
public sealed class SetTaskVariableCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly string _name;
    private readonly object? _value;

    public SetTaskVariableCommand(string taskId, string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _taskId = taskId;
        _name = name;
        _value = value;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var taskManager = context.Services.TaskManager
            ?? throw new EngineException("当前宿主没有装配任务管理器，无法写任务局部变量。");

        var task = await taskManager
            .FindTaskByIdAsync(_taskId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"任务 '{_taskId}' 不存在（可能已完成或流程已推进）。");

        await taskManager.SetTaskVariableAsync(task, _name, _value, ct).ConfigureAwait(false);

        return Unit.Value;
    }
}

/// <summary>删除任务局部变量（B2.4）。</summary>
public sealed class RemoveTaskVariableCommand : ICommand<bool>
{
    private readonly string _taskId;
    private readonly string _name;

    public RemoveTaskVariableCommand(string taskId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _taskId = taskId;
        _name = name;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var taskManager = context.Services.TaskManager
            ?? throw new EngineException("当前宿主没有装配任务管理器，无法删除任务局部变量。");

        return await taskManager.RemoveTaskVariableAsync(_taskId, _name, ct).ConfigureAwait(false);
    }
}

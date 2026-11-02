using System.Text.Json;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>指令类型（Camunda 的 <c>ProcessInstanceModificationInstruction</c> 裁剪版）。</summary>
public static class ModifyInstructionType
{
    /// <summary>取消指定活动上的活动实例（并行分支其余不受影响）。</summary>
    public const string Cancel = "cancel";

    /// <summary>在指定活动<b>之前</b>插入新的令牌。</summary>
    public const string StartBefore = "startBefore";

    /// <summary>在指定活动<b>之后</b>插入令牌（按出流推导目标；多出流时拒绝）。</summary>
    public const string StartAfter = "startAfter";

    /// <summary>写流程实例变量（<c>scope=task</c> 时写该活动上待办的局部）。</summary>
    public const string SetVariable = "setVariable";

    /// <summary>删流程实例变量（<c>scope=task</c> 时删该活动上待办的局部）。</summary>
    public const string RemoveVariable = "removeVariable";
}

/// <summary>变量作用域。</summary>
public static class ModifyVariableScope
{
    /// <summary>实例级变量（默认）。</summary>
    public const string Instance = "instance";

    /// <summary>
    /// 任务级（该活动上等待中的待办）。
    /// <para>
    /// ⚠️ 与 Camunda 的 <c>local</c> 不完全等价：Camunda 是<b>执行级</b>，本子集没有执行级变量存储，
    /// 这里落到「该活动当前待办的局部」。所以它的适用面更窄 —— 活动上没有待办时会明确报 409，
    /// 而不是静默写成实例变量。
    /// </para>
    /// </summary>
    public const string Task = "task";
}

/// <summary>
/// 一条修改指令。
/// </summary>
/// <param name="Type">见 <see cref="ModifyInstructionType"/>。</param>
/// <param name="ActivityId">cancel / startBefore / startAfter 的目标活动 id。</param>
/// <param name="VariableName">变量指令的变量名。</param>
/// <param name="VariableValue">setVariable 的值（其余指令忽略）。</param>
/// <param name="VariableScope">变量作用域，见 <see cref="ModifyVariableScope"/>；默认实例级。</param>
public sealed record ModifyInstruction(
    string Type,
    string? ActivityId = null,
    string? VariableName = null,
    object? VariableValue = null,
    string? VariableScope = null);

/// <summary>单条指令的执行结果（给审计与响应看，不落库）。</summary>
/// <param name="Type">指令类型。</param>
/// <param name="Target">人类可读的目标描述（活动 id 或变量名）。</param>
public sealed record ModifyInstructionResult(string Type, string? Target);

/// <summary>
/// 指令式流程实例修改（B2.17 的「完整 Camunda modify」）：一组指令在<b>一个命令、一个事务</b>里按序执行。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须自己攒成一个命令而不是外层循环调 N 个命令：一个命令 = 一个事务。
/// 循环调会让「前三步生效、第四步失败」变成既成事实 —— 那是 <c>BatchXxx</c> 的「逐项执行、非原子」口径，
/// 不是 modify 该有的语义。这里让所有指令共享同一个 <see cref="ICommandContext"/>（同一事务、同一议程）。
/// </para>
/// <para>
/// 因此顺序是：<b>先全量校验，再逐条执行</b>。校验阶段不动任何数据 ——
/// 拼错一个活动 id 就应该整批拒绝，而不是「前两条生效、第三条报错」。
/// </para>
/// <para>
/// <c>startAfter</c> 由出流推导目标活动：只有一条出流才可执行，多条出流时明确拒绝
/// （与「并行分支跳转」同一条口径 —— 分支语义不该由运维命令替流程定义猜）。
/// </para>
/// <para>
/// <b>幂等</b>：给了 <c>commandId</c>（或平台面透传的 <c>Idempotency-Key</c>）时，
/// 同一键重复改同一个实例只生效一次 —— 重投会回放首次的指令结果列表。
/// 占位行与业务写入同事务：整批回滚时占位一起消失，调用方可以用同键重试。
/// </para>
/// </remarks>
public sealed class ModifyProcessInstanceCommand : ICommand<IReadOnlyList<ModifyInstructionResult>>
{
    private const string DefaultReason = "修改流程实例";

    private readonly string _processInstanceId;
    private readonly IReadOnlyList<ModifyInstruction> _instructions;
    private readonly string? _reason;
    private readonly string? _commandId;

    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="instructions">指令列表（按序执行）。</param>
    /// <param name="reason">修改原因（写进任务历史 / 审计）。</param>
    /// <param name="commandId">幂等键；破坏性写操作建议显式给。</param>
    public ModifyProcessInstanceCommand(
        string processInstanceId,
        IReadOnlyList<ModifyInstruction> instructions,
        string? reason = null,
        string? commandId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentNullException.ThrowIfNull(instructions);

        if (instructions.Count == 0)
        {
            throw new ArgumentException("指令列表为空：修改什么都没说。", nameof(instructions));
        }

        _processInstanceId = processInstanceId;
        _instructions = instructions;
        _reason = string.IsNullOrWhiteSpace(reason) ? DefaultReason : reason.Trim();
        _commandId = commandId;
    }

    /// <inheritdoc />
    public string? CommandId => _commandId;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ModifyInstructionResult>> ExecuteAsync(
        ICommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;

        // ── 0) 引擎层幂等：同一 commandId 重复改同一实例 ⇒ 回放首次结果，不重复生效 ──
        // 占位必须在读数据之前写：占位与业务写入同事务，命令回滚时占位一起消失。
        var dedup = services.CommandIdempotency;
        var commandId = context.CommandId ?? CommandId;
        var dedupKey = dedup is not null && !string.IsNullOrWhiteSpace(commandId)
            ? CommandIdempotencyKeys.ForModify(commandId, _processInstanceId)
            : null;

        if (dedup is not null && dedupKey is not null)
        {
            var existing = await dedup.FindAsync(dedupKey, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                return DecodeResults(existing.ResultPayload);
            }

            var claimed = await dedup
                .TryRecordAsync(
                    new CommandExecutionRecord(
                        dedupKey,
                        nameof(ModifyProcessInstanceCommand),
                        ResultPayload: string.Empty,
                        CreatedAt: services.Clock.UtcNow,
                        ExpiresAt: services.Clock.UtcNow + CommandIdempotencyKeys.DefaultRetention),
                    ct)
                .ConfigureAwait(false);

            if (!claimed)
            {
                // 并发的另一方已占位（首次仍在执行）⇒ 同样不重复生效
                return [];
            }
        }

        var executionManager = services.ExecutionManager;
        var resolver = services.ProcessDefinitionResolver
            ?? throw new EngineNotImplementedException(
                "流程元素解析器（IProcessDefinitionResolver）",
                "运行期",
                "修改实例需要把活动 id 解析回元素对象，宿主必须注册流程元素解析器实现");

        var instance = await executionManager.FindByIdAsync(_processInstanceId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException(
                $"流程实例 '{_processInstanceId}' 不存在或已结束，无法修改。");

        // ── 1) 全量校验（不动数据）：拼错一个 id 就整批拒绝 ────────────────────────
        var planned = new List<(ModifyInstruction Instruction, string? Target)>(_instructions.Count);

        foreach (var instruction in _instructions)
        {
            planned.Add((instruction, await ValidateAsync(resolver, instance.ProcessDefinitionId, instruction, ct)
                .ConfigureAwait(false)));
        }

        // ── 2) 逐条执行：共享同一上下文 ⇒ 同一事务、同一议程 ──────────────────────
        var results = new List<ModifyInstructionResult>(planned.Count);

        foreach (var (instruction, target) in planned)
        {
            await ApplyAsync(context, instance.Id, instruction, target, ct).ConfigureAwait(false);

            results.Add(new ModifyInstructionResult(
                instruction.Type,
                target ?? instruction.VariableName ?? instruction.ActivityId));
        }

        // 回填结果快照：重投时回放同一份列表，而不是回一个「什么都没做」的空列表
        if (dedup is not null && dedupKey is not null)
        {
            await dedup.CompleteAsync(dedupKey, EncodeResults(results), ct).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>结果快照序列化：与 <see cref="DecodeResults"/> 用同一套默认选项。</summary>
    private static string EncodeResults(IReadOnlyList<ModifyInstructionResult> results) =>
        JsonSerializer.Serialize(results);

    private static List<ModifyInstructionResult> DecodeResults(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ModifyInstructionResult>>(payload) ?? [];
        }
        catch (JsonException)
        {
            // 快照坏了不该让重投失败：去重本身已经保证了「不会重复生效」，只是回放内容为空
            return [];
        }
    }

    private static async ValueTask<string?> ValidateAsync(
        IProcessDefinitionResolver resolver,
        string? processDefinitionId,
        ModifyInstruction instruction,
        CancellationToken ct)
    {
        switch (instruction.Type)
        {
            case ModifyInstructionType.Cancel:
            case ModifyInstructionType.StartBefore:
                {
                    RequireActivity(instruction);

                    _ = await resolver
                        .ResolveFlowElementAsync(processDefinitionId, instruction.ActivityId!, ct)
                        .ConfigureAwait(false)
                        ?? throw new EngineException(
                            $"活动 '{instruction.ActivityId}' 不在流程定义 '{processDefinitionId}' 中。");

                    return instruction.ActivityId;
                }

            case ModifyInstructionType.StartAfter:
                {
                    RequireActivity(instruction);

                    var source = await resolver
                        .ResolveFlowElementAsync(processDefinitionId, instruction.ActivityId!, ct)
                        .ConfigureAwait(false)
                        ?? throw new EngineException(
                            $"活动 '{instruction.ActivityId}' 不在流程定义 '{processDefinitionId}' 中。");

                    return ResolveSingleOutgoing(source, instruction.ActivityId!);
                }

            case ModifyInstructionType.SetVariable:
            case ModifyInstructionType.RemoveVariable:
                {
                    if (string.IsNullOrWhiteSpace(instruction.VariableName))
                    {
                        throw new EngineException($"{instruction.Type} 指令需要变量名（variableName）。");
                    }

                    if (instruction.VariableScope is { Length: > 0 } scope
                        && !string.Equals(scope, ModifyVariableScope.Instance, StringComparison.Ordinal)
                        && !string.Equals(scope, ModifyVariableScope.Task, StringComparison.Ordinal))
                    {
                        throw new EngineException(
                            $"未知的变量作用域 '{scope}'：只支持 instance / task。");
                    }

                    // task 作用域必须有活动 id，否则不知道写在哪张待办上
                    if (string.Equals(instruction.VariableScope, ModifyVariableScope.Task, StringComparison.Ordinal))
                    {
                        RequireActivity(instruction);
                    }

                    return instruction.VariableName;
                }

            default:
                throw new EngineException(
                    $"未知的修改指令 '{instruction.Type}'：" +
                    "支持 cancel / startBefore / startAfter / setVariable / removeVariable。");
        }
    }

    private static void RequireActivity(ModifyInstruction instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction.ActivityId))
        {
            throw new EngineException($"{instruction.Type} 指令需要活动 id（activityId）。");
        }
    }

    private static string ResolveSingleOutgoing(IFlowElement source, string activityId)
    {
        if (source is not FlowNode node || node.OutgoingFlows.Count == 0)
        {
            throw new EngineException($"活动 '{activityId}' 没有出流，startAfter 无处可去。");
        }

        if (node.OutgoingFlows.Count > 1)
        {
            throw new EngineException(
                $"活动 '{activityId}' 有 {node.OutgoingFlows.Count} 条出流，startAfter 的目标不唯一：" +
                "请用 startBefore 显式指定目标活动（分支语义不该由运维命令替流程定义猜）。");
        }

        return node.OutgoingFlows[0].TargetRef
            ?? throw new EngineException($"活动 '{activityId}' 的出流没有 targetRef。");
    }

    private async ValueTask ApplyAsync(
        ICommandContext context,
        string processInstanceId,
        ModifyInstruction instruction,
        string? target,
        CancellationToken ct)
    {
        switch (instruction.Type)
        {
            case ModifyInstructionType.Cancel:
                // 嵌套命令复用外层上下文（同一事务 / 同一议程），所以整批仍是原子的
                await new CancelActivityCommand(processInstanceId, instruction.ActivityId!, _reason)
                    .ExecuteAsync(context, ct)
                    .ConfigureAwait(false);

                return;

            case ModifyInstructionType.StartBefore:
            case ModifyInstructionType.StartAfter:
                await new StartBeforeActivityCommand(processInstanceId, target!)
                    .ExecuteAsync(context, ct)
                    .ConfigureAwait(false);

                return;

            case ModifyInstructionType.SetVariable:
                if (string.Equals(instruction.VariableScope, ModifyVariableScope.Task, StringComparison.Ordinal))
                {
                    var taskId = await FindTaskIdAsync(context, processInstanceId, instruction.ActivityId!, ct)
                        .ConfigureAwait(false);

                    await new SetTaskVariableCommand(taskId, instruction.VariableName!, instruction.VariableValue)
                        .ExecuteAsync(context, ct)
                        .ConfigureAwait(false);

                    return;
                }

                await new SetVariablesCommand(
                        processInstanceId,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            [instruction.VariableName!] = instruction.VariableValue,
                        })
                    .ExecuteAsync(context, ct)
                    .ConfigureAwait(false);

                return;

            case ModifyInstructionType.RemoveVariable:
                if (string.Equals(instruction.VariableScope, ModifyVariableScope.Task, StringComparison.Ordinal))
                {
                    var taskId = await FindTaskIdAsync(context, processInstanceId, instruction.ActivityId!, ct)
                        .ConfigureAwait(false);

                    await new RemoveTaskVariableCommand(taskId, instruction.VariableName!)
                        .ExecuteAsync(context, ct)
                        .ConfigureAwait(false);

                    return;
                }

                await new DeleteVariableCommand(processInstanceId, instruction.VariableName!)
                    .ExecuteAsync(context, ct)
                    .ConfigureAwait(false);

                return;

            default:
                // ValidateAsync 已经把所有未知类型挡在前面，走到这里是内部不一致
                throw new EngineException($"未知的修改指令 '{instruction.Type}'。");
        }
    }

    /// <summary>取指定活动上等待中的待办；没有就明确报错（不静默退化成实例变量）。</summary>
    private static async ValueTask<string> FindTaskIdAsync(
        ICommandContext context,
        string processInstanceId,
        string activityId,
        CancellationToken ct)
    {
        var taskManager = context.Services.TaskManager
            ?? throw new EngineNotImplementedException(
                "任务管理器（ITaskManager）",
                "运维",
                "task 作用域的变量指令需要定位待办，宿主必须注册任务管理器实现");

        var tasks = await taskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, ct).ConfigureAwait(false);
        var task = tasks.FirstOrDefault(
            candidate => string.Equals(candidate.TaskDefinitionKey, activityId, StringComparison.Ordinal));

        return task?.Id
            ?? throw new EngineException(
                $"活动 '{activityId}' 上没有等待中的待办，写不了 task 作用域的变量" +
                "（要写实例变量请用 scope=instance）。");
    }
}

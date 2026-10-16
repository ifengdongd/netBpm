using System.Collections;
using System.Globalization;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 多实例用户任务行为（会签 / 串签，任务清单 B1.2）。
/// 对应 Activiti 的 <c>MultiInstanceActivityBehavior</c> 家族
/// （<c>SequentialUserTaskActivityBehavior</c> / <c>ParallelUserTaskActivityBehavior</c> → 本方案 1 个类）。
/// <para>
/// <b>执行树布局</b>（与 Activiti 近似但有意简化，均为已登记偏差）：
/// <list type="bullet">
///   <item><description><b>串行</b>：主流程执行自己就是实例载体，一次只挂一个任务；
///     完成一个再建下一个，全部完成后离开节点。</description></item>
///   <item><description><b>并行</b>：进入节点时一次性建 N 个子执行，每个子执行挂一个任务；
///     全部完成（或完成条件为真）后删除全部实例执行，主执行离开节点 —— 复用并行网关的收口机制
///     （<c>EndExecutionOperation</c> 的「父执行无子执行 → 继续推进」）。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>循环变量</b>（名字与 Activiti 一致，落在流程实例作用域 —— 本子集变量只有这一个作用域）：
/// <c>nrOfInstances</c> / <c>nrOfActiveInstances</c> / <c>nrOfCompletedInstances</c> /
/// <c>loopCounter</c>（1 起）/ <c>elementVariable</c>（集合元素）。
/// </para>
/// <para>
/// ⚠️ <b>已登记的偏差</b>：
/// ① <c>ACT_HI_ACTINST</c> 里多实例节点只有 1 行（Activiti 每实例 1 行）——
/// 历史行随主执行收口，实例执行不单独产生活动行；
/// ② 并行模式下 <c>loopCounter</c> / <c>elementVariable</c> 变量只反映「最后创建的实例」
/// （共享作用域无法承载逐实例值）——任务的办理人 / 名称等在<b>创建时</b>逐实例求值，不受影响；
/// ③ 实例级 executionListener start/end 不逐实例触发（节点级照常）。
/// </para>
/// <para>
/// ⚠️ 实例数 / 完成数 / 集合元素通过<b>流程变量</b>在命令之间传递（跨命令重启后状态可恢复）；
/// 集合表达式在需要时重新求值（求值成本可接受，与 Activiti 的行为一致）。
/// </para>
/// </summary>
public sealed class MultiInstanceUserTaskBehavior : FlowNodeActivityBehavior
{
    /// <summary>实例总数（Activiti 同名循环变量）。</summary>
    public const string VarNrOfInstances = "nrOfInstances";

    /// <summary>当前活跃实例数（Activiti 同名循环变量）。</summary>
    public const string VarNrOfActiveInstances = "nrOfActiveInstances";

    /// <summary>已完成实例数（Activiti 同名循环变量）。</summary>
    public const string VarNrOfCompletedInstances = "nrOfCompletedInstances";

    /// <summary>当前实例序号（1 起，Activiti 同名循环变量）。</summary>
    public const string VarLoopCounter = "loopCounter";

    /// <summary>完成条件不满足时实例执行的等待语义说明（错误文案用）。</summary>
    private const string DeleteReason = "多实例完成条件已满足（或全部实例完成），取消剩余实例";

    private readonly UserTaskActivityBehavior _inner;

    public MultiInstanceUserTaskBehavior(IEngineServices services)
        : base(services)
    {
        _inner = new UserTaskActivityBehavior(services);
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var userTask = RequireUserTask(execution);
        var mi = RequireMultiInstance(userTask);

        var (instanceCount, elements) = await ResolveInstancesAsync(mi, execution, ct).ConfigureAwait(false);

        // 循环变量先落流程实例作用域（跨命令可恢复）
        await execution.SetVariableAsync(VarNrOfInstances, instanceCount, ct).ConfigureAwait(false);
        await execution.SetVariableAsync(VarNrOfCompletedInstances, 0, ct).ConfigureAwait(false);
        await execution.SetVariableAsync(VarNrOfActiveInstances, instanceCount, ct).ConfigureAwait(false);

        // 0 实例：节点直接完成（对应 Activiti 对空集合的语义）
        if (instanceCount == 0)
        {
            await LeaveAsync(execution, ct).ConfigureAwait(false);
            return;
        }

        if (mi.IsSequential)
        {
            await ExecuteSequentialInstanceAsync(userTask, mi, execution, elements, 1, ct).ConfigureAwait(false);
            return;
        }

        await ExecuteParallelInstancesAsync(userTask, mi, execution, elements, instanceCount, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 触发来源是「某实例的任务被完成」（<see cref="CompleteTaskCommand"/> → <c>TriggerExecutionOperation</c>）：
    /// 串行模式触发执行 = 主执行；并行模式触发执行 = 实例子执行（任务只建在子执行上）。
    /// </remarks>
    public override async ValueTask TriggerAsync(
        IDelegateExecution execution,
        string? signalName,
        object? signalData,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var userTask = RequireUserTask(execution);
        var mi = RequireMultiInstance(userTask);

        var completed = await IncrementCompletedAsync(execution, ct).ConfigureAwait(false);
        var instanceCount = await GetIntVariableAsync(execution, VarNrOfInstances, ct).ConfigureAwait(false);

        if (mi.IsSequential)
        {
            await TriggerSequentialAsync(userTask, mi, execution, instanceCount, completed, ct).ConfigureAwait(false);
            return;
        }

        await TriggerParallelAsync(userTask, mi, execution, instanceCount, completed, ct).ConfigureAwait(false);
    }

    // ─────────────────────────── 进入节点 ───────────────────────────

    /// <summary>串行：在主执行上建第 <paramref name="loopCounter"/> 个实例的任务（一次一个）。</summary>
    private async ValueTask ExecuteSequentialInstanceAsync(
        UserTask userTask,
        MultiInstanceLoopCharacteristics mi,
        IDelegateExecution execution,
        IReadOnlyList<object?>? elements,
        int loopCounter,
        CancellationToken ct)
    {
        await WriteLoopVariablesAsync(mi, execution, elements, loopCounter, ct).ConfigureAwait(false);

        // 单实例任务创建 = 内层用户任务行为的正常路径（建任务后等待）
        await _inner.ExecuteAsync(execution, ct).ConfigureAwait(false);
    }

    /// <summary>并行：一次性建 <paramref name="instanceCount"/> 个子执行，各挂一个实例任务。</summary>
    private async ValueTask ExecuteParallelInstancesAsync(
        UserTask userTask,
        MultiInstanceLoopCharacteristics mi,
        IDelegateExecution execution,
        IReadOnlyList<object?>? elements,
        int instanceCount,
        CancellationToken ct)
    {
        var executionManager = Services.ExecutionManager;

        for (var i = 1; i <= instanceCount; i++)
        {
            // 共享作用域承载「当前正在创建的实例」的循环变量（已登记偏差：留最后实例的值）
            await WriteLoopVariablesAsync(mi, execution, elements, i, ct).ConfigureAwait(false);

            var child = executionManager.CreateChildExecution(execution.AsEntity());
            child.CurrentFlowElement = userTask;

            await executionManager.InsertAsync(child, ct).ConfigureAwait(false);

            // 不走 PlanContinueProcessOperation：实例不单独产生活动历史行（见类注释偏差 ①）
            await _inner.ExecuteAsync(child, ct).ConfigureAwait(false);
        }
    }

    // ─────────────────────────── 任务完成后 ───────────────────────────

    private async ValueTask TriggerSequentialAsync(
        UserTask userTask,
        MultiInstanceLoopCharacteristics mi,
        IDelegateExecution execution,
        int instanceCount,
        int completed,
        CancellationToken ct)
    {
        await execution
            .SetVariableAsync(VarNrOfActiveInstances, instanceCount - completed, ct)
            .ConfigureAwait(false);

        // 完成条件：逐实例判定，真则提前结束（剩余实例不再创建）
        if (await IsCompletedAsync(mi, execution, instanceCount, completed, ct).ConfigureAwait(false))
        {
            await LeaveAsync(execution, ct).ConfigureAwait(false);
            return;
        }

        if (completed < instanceCount)
        {
            // 集合模式下重新求值集合取下一个元素；cardinality 模式没有集合
            IReadOnlyList<object?>? elements = string.IsNullOrEmpty(mi.Collection)
                ? null
                : await ResolveCollectionAsync(mi, execution, ct).ConfigureAwait(false);

            await ExecuteSequentialInstanceAsync(userTask, mi, execution, elements, completed + 1, ct)
                .ConfigureAwait(false);
            return;
        }

        // 全部完成 → 离开节点
        await LeaveAsync(execution, ct).ConfigureAwait(false);
    }

    private async ValueTask TriggerParallelAsync(
        UserTask userTask,
        MultiInstanceLoopCharacteristics mi,
        IDelegateExecution execution,
        int instanceCount,
        int completed,
        CancellationToken ct)
    {
        // 并行模式：触发执行 = 实例子执行；主执行 = 它的父执行
        var root = await ResolveRootAsync(execution, ct).ConfigureAwait(false);

        await execution
            .SetVariableAsync(VarNrOfActiveInstances, instanceCount - completed, ct)
            .ConfigureAwait(false);

        var earlyComplete = await IsCompletedAsync(mi, execution, instanceCount, completed, ct).ConfigureAwait(false);

        if (!earlyComplete && completed < instanceCount)
        {
            // 还有实例未完成：主执行继续等待（不发事件、不推进）
            return;
        }

        // 全部完成（或完成条件为真）：清掉全部实例执行（含当前触发执行 —— 任务已删，执行行还在）
        await DeleteInstanceExecutionsAsync(root, userTask, ct).ConfigureAwait(false);

        // 主执行离开节点（CurrentFlowElement 跨命令为空，必须先补上；
        // TakeOutgoingSequenceFlows 会收口节点的历史行并触发 end 监听器）
        root.CurrentFlowElement = userTask;
        Services.Agenda.PlanTakeOutgoingSequenceFlowsOperation(root, evaluateConditions: true);
    }

    /// <summary>完成判定：全部完成，或 <c>completionCondition</c> 求值为真。</summary>
    private async ValueTask<bool> IsCompletedAsync(
        MultiInstanceLoopCharacteristics mi,
        IDelegateExecution execution,
        int instanceCount,
        int completed,
        CancellationToken ct)
    {
        if (completed >= instanceCount)
        {
            return true;
        }

        if (string.IsNullOrEmpty(mi.CompletionCondition))
        {
            return false;
        }

        var value = await Services.ExpressionManager
            .CreateExpression(mi.CompletionCondition)
            .GetValueAsync(execution, ct)
            .ConfigureAwait(false);

        return value is true;
    }

    // ─────────────────────────── 实例数与集合 ───────────────────────────

    /// <summary>解析实例总数与集合元素（cardinality 或 collection 二选一，部署期已校验）。</summary>
    private async ValueTask<(int InstanceCount, IReadOnlyList<object?>? Elements)> ResolveInstancesAsync(
        MultiInstanceLoopCharacteristics mi,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(mi.Collection))
        {
            var elements = await ResolveCollectionAsync(mi, execution, ct).ConfigureAwait(false);
            return (elements.Count, elements);
        }

        var count = await EvaluateCardinalityAsync(mi.LoopCardinality!, execution, ct).ConfigureAwait(false);
        return (count, null);
    }

    /// <summary>集合表达式求值。结果须为集合（字符串不是集合 —— 与 Activiti 一致，避免歧义）。</summary>
    private async ValueTask<IReadOnlyList<object?>> ResolveCollectionAsync(
        MultiInstanceLoopCharacteristics mi,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        var value = await Services.ExpressionManager
            .CreateExpression(mi.Collection!)
            .GetValueAsync(execution, ct)
            .ConfigureAwait(false);

        return value switch
        {
            null => [],
            string => throw new EngineException(
                $"多实例集合表达式 '{mi.Collection}' 求值结果是字符串而不是集合（用户任务 '{execution.CurrentActivityId}'）。"),
            IEnumerable<object?> enumerable => [.. enumerable],
            IEnumerable enumerable and not string => [.. enumerable.Cast<object?>()],
            _ => throw new EngineException(
                $"多实例集合表达式 '{mi.Collection}' 求值结果类型 '{value.GetType().Name}' 不是集合。"),
        };
    }

    /// <summary>实例数求值（<c>loopCardinality</c>）。结果须为非负整数。</summary>
    private async ValueTask<int> EvaluateCardinalityAsync(
        string cardinality,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        var value = await Services.ExpressionManager
            .CreateExpression(cardinality)
            .GetValueAsync(execution, ct)
            .ConfigureAwait(false);

        var count = value switch
        {
            null => throw new EngineException($"多实例实例数表达式 '{cardinality}' 求值结果为 null。"),
            int number => number,
            long number => checked((int)number),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            IConvertible convertible => convertible.ToInt32(CultureInfo.InvariantCulture),
            _ => throw new EngineException(
                $"多实例实例数表达式 '{cardinality}' 求值结果类型 '{value.GetType().Name}' 不是数字。"),
        };

        return count >= 0
            ? count
            : throw new EngineException($"多实例实例数不能为负数（表达式 '{cardinality}' 求值结果 {count}）。");
    }

    /// <summary>把当前实例的 <c>loopCounter</c> / <c>elementVariable</c> 写入作用域。</summary>
    private static async ValueTask WriteLoopVariablesAsync(
        MultiInstanceLoopCharacteristics mi,
        IDelegateExecution execution,
        IReadOnlyList<object?>? elements,
        int loopCounter,
        CancellationToken ct)
    {
        await execution.SetVariableAsync(VarLoopCounter, loopCounter, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(mi.ElementVariable))
        {
            var element = elements is not null && loopCounter >= 1 && loopCounter <= elements.Count
                ? elements[loopCounter - 1]
                : null;

            await execution.SetVariableAsync(mi.ElementVariable, element, ct).ConfigureAwait(false);
        }
    }

    /// <summary><c>nrOfCompletedInstances</c> 自增并返回新值。</summary>
    private static async ValueTask<int> IncrementCompletedAsync(
        IDelegateExecution execution,
        CancellationToken ct)
    {
        var completed = await GetIntVariableAsync(execution, VarNrOfCompletedInstances, ct).ConfigureAwait(false) + 1;
        await execution.SetVariableAsync(VarNrOfCompletedInstances, completed, ct).ConfigureAwait(false);
        return completed;
    }

    private static async ValueTask<int> GetIntVariableAsync(
        IDelegateExecution execution,
        string name,
        CancellationToken ct)
    {
        var value = await execution.GetVariableAsync(name, ct).ConfigureAwait(false);

        return value switch
        {
            null => 0,
            int number => number,
            long number => checked((int)number),
            IConvertible convertible => convertible.ToInt32(CultureInfo.InvariantCulture),
            _ => throw new EngineException($"循环变量 '{name}' 的值类型 '{value.GetType().Name}' 不是数字。"),
        };
    }

    // ─────────────────────────── 收尾 ───────────────────────────

    /// <summary>
    /// 删除主执行下「挂在该多实例节点上」的全部实例执行（含其任务 / 变量 / 候选关系）。
    /// <para>
    /// ⚠️ 只删挂在本节点的子执行：主执行可能还有并行网关分出的其它分支（挂在不同节点上），不能误删。
    /// </para>
    /// </summary>
    private async ValueTask DeleteInstanceExecutionsAsync(
        IExecutionEntity root,
        UserTask userTask,
        CancellationToken ct)
    {
        var executionManager = Services.ExecutionManager;
        var taskManager = Services.TaskManager;

        var candidates = await executionManager
            .FindActiveChildExecutionsByProcessInstanceIdAsync(root.ProcessInstanceId ?? root.Id, ct)
            .ConfigureAwait(false);

        var instanceExecutions = candidates
            .Where(execution => string.Equals(execution.ParentId, root.Id, StringComparison.Ordinal)
                && string.Equals(execution.CurrentActivityId, userTask.Id, StringComparison.Ordinal))
            .ToList();

        foreach (var instanceExecution in instanceExecutions)
        {
            // InMemory 执行删除不级联任务，先显式删任务（同时收口任务历史行）
            if (taskManager is not null)
            {
                var tasks = await taskManager
                    .FindTasksByExecutionIdAsync(instanceExecution.Id, ct)
                    .ConfigureAwait(false);

                foreach (var task in tasks)
                {
                    await taskManager.DeleteTaskAsync(task, DeleteReason, ct).ConfigureAwait(false);
                }
            }

            await executionManager
                .DeleteExecutionAndRelatedDataAsync(instanceExecution.AsEntity(), DeleteReason, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>解析并行模式的根执行（实例执行的父执行），跨命令从仓储读回。</summary>
    private static async ValueTask<IExecutionEntity> ResolveRootAsync(
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (execution is IExecutionEntity { Parent: { } attached })
        {
            return attached;
        }

        if (execution is IExecutionEntity { ParentId: { } parentId, ParentLoader: { } loader })
        {
            return await loader.FindByIdAsync(parentId, ct).ConfigureAwait(false)
                ?? throw new EngineException(
                    $"找不到多实例实例执行 '{execution.Id}' 的父执行 '{parentId}'。");
        }

        throw new EngineException(
            $"多实例实例执行 '{execution.Id}' 缺少父执行（ParentId / ParentLoader 均为空）。");
    }

    // ─────────────────────────── 模型解析 ───────────────────────────

    private static UserTask RequireUserTask(IDelegateExecution execution) =>
        execution.CurrentFlowElement as UserTask
        ?? throw new EngineException(
            "Programmatic error: multi-instance behaviour can only be applied to a UserTask instance, " +
            $"but got an instance of {execution.CurrentFlowElement}");

    private static MultiInstanceLoopCharacteristics RequireMultiInstance(UserTask userTask) =>
        userTask.LoopCharacteristics
        ?? throw new EngineException(
            $"Programmatic error: 用户任务 '{userTask.Id}' 没有多实例配置，却路由到了多实例行为。");
}

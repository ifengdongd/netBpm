using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Migration;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Deployment;

namespace NetBpm.Runtime.Migration;

/// <summary>一条执行的迁移动作（内部结构，不对外暴露）。</summary>
internal sealed record ExecutionMigration(
    IExecutionEntity Execution,
    FlowElement Target,
    TaskEntity? Task,
    bool RecreateTask);

/// <summary>计划 + 执行所需的目标模型（预演只返回 <see cref="Plan"/>）。</summary>
internal sealed record MigrationPlanDetails(
    MigrationPlan Plan,
    IReadOnlyList<IExecutionEntity> AllExecutions,
    IReadOnlyList<ExecutionMigration> Moves,
    ProcessDefinition TargetDefinition,
    Process TargetProcess);

/// <summary>
/// 实例迁移计划器：负责「能不能迁、迁到哪、有哪些风险」。
/// <para>
/// 规则（与 Activiti 的 <c>ProcessInstanceMigrationValidation</c> 同思路，范围按本子集裁剪）：
/// </para>
/// <list type="number">
///   <item><description>实例必须运行中、未挂起；目标定义必须同 key / 同租户 / 未挂起，且不能是当前定义；</description></item>
///   <item><description>逐执行决定目标节点：显式映射优先，否则同 id 且类型兼容；</description></item>
///   <item><description>有待办的执行只能迁到用户任务；停在网关 Join 上的非活动执行只能迁到同类型网关；</description></item>
///   <item><description>多实例（会签/串签）节点直接拒绝 —— 实例级迁移会打乱 <c>nrOf*</c> 计数；</description></item>
///   <item><description>缺失映射、类型不兼容、目标节点不存在一律聚合报错，不做「尽力而为」的静默降级。</description></item>
/// </list>
/// </summary>
internal sealed class ProcessInstanceMigrationPlanner
{
    private readonly IExecutionManager _executions;
    private readonly ITaskManager _tasks;
    private readonly IProcessDefinitionStore _definitions;

    public ProcessInstanceMigrationPlanner(
        IExecutionManager executions,
        ITaskManager tasks,
        IProcessDefinitionStore definitions)
    {
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(definitions);

        _executions = executions;
        _tasks = tasks;
        _definitions = definitions;
    }

    public async ValueTask<MigrationPlanDetails> BuildAsync(
        ProcessInstanceMigrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ProcessInstanceId))
        {
            throw new EngineException("迁移失败：processInstanceId 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(request.TargetProcessDefinitionId))
        {
            throw new EngineException("迁移失败：targetProcessDefinitionId 不能为空。");
        }

        var instance = await _executions.FindByIdAsync(request.ProcessInstanceId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException(
                $"流程实例 '{request.ProcessInstanceId}' 不存在或已结束，无法迁移。");

        if (!string.Equals(instance.ProcessInstanceId, instance.Id, StringComparison.Ordinal))
        {
            throw new EngineException(
                $"'{request.ProcessInstanceId}' 不是流程实例根执行（属于实例 '{instance.ProcessInstanceId}'），无法迁移。");
        }

        if (instance.SuspensionState != 1)
        {
            throw new EngineConflictException(
                $"流程实例 '{instance.Id}' 已挂起：请先激活再迁移，否则迁移后新建的待办会与实例状态不一致。");
        }

        var sourceDefinitionId = instance.ProcessDefinitionId
            ?? throw new EngineException($"流程实例 '{instance.Id}' 没有流程定义 id（执行树损坏）。");

        var targetDefinitionId = request.TargetProcessDefinitionId.Trim();

        if (string.Equals(sourceDefinitionId, targetDefinitionId, StringComparison.Ordinal))
        {
            throw new EngineException($"流程实例 '{instance.Id}' 已经在定义 '{targetDefinitionId}' 上，无需迁移。");
        }

        var sourceDefinition = await _definitions.FindByIdAsync(sourceDefinitionId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"当前流程定义 '{sourceDefinitionId}' 不存在。");

        var targetDefinition = await _definitions.FindByIdAsync(targetDefinitionId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"目标流程定义 '{targetDefinitionId}' 不存在。");

        if (!string.Equals(sourceDefinition.Key, targetDefinition.Key, StringComparison.Ordinal))
        {
            throw new EngineException(
                $"不能跨流程迁移：当前 key '{sourceDefinition.Key}'，目标 key '{targetDefinition.Key}'。");
        }

        var sourceTenant = await _definitions.GetTenantIdAsync(sourceDefinitionId, ct).ConfigureAwait(false) ?? string.Empty;
        var targetTenant = await _definitions.GetTenantIdAsync(targetDefinitionId, ct).ConfigureAwait(false) ?? string.Empty;

        if (!string.Equals(sourceTenant, targetTenant, StringComparison.Ordinal))
        {
            throw new EngineException(
                $"不能跨租户迁移：当前租户 '{sourceTenant}'，目标租户 '{targetTenant}'。");
        }

        if (await _definitions.IsSuspendedAsync(targetDefinitionId, ct).ConfigureAwait(false))
        {
            throw new EngineConflictException(
                $"目标流程定义 '{targetDefinitionId}' 已挂起：请先激活，否则迁移后的实例无法继续办理。");
        }

        var sourceModel = await _definitions.GetBpmnModelAsync(sourceDefinitionId, ct).ConfigureAwait(false)
            ?? throw new EngineException($"当前流程定义 '{sourceDefinitionId}' 的 BPMN 模型缺失。");

        var targetModel = await _definitions.GetBpmnModelAsync(targetDefinitionId, ct).ConfigureAwait(false)
            ?? throw new EngineException($"目标流程定义 '{targetDefinitionId}' 的 BPMN 模型缺失。");

        var sourceProcess = sourceModel.GetMainProcess()
            ?? throw new EngineException($"当前流程定义 '{sourceDefinitionId}' 中没有流程。");

        var targetProcess = targetModel.GetMainProcess()
            ?? throw new EngineException($"目标流程定义 '{targetDefinitionId}' 中没有流程。");

        var mappings = request.NormalizedMappings();
        var errors = new List<string>();
        var warnings = new List<string>();

        ValidateMappingKeys(mappings, sourceProcess, targetProcess, errors);

        if (targetDefinition.Version <= sourceDefinition.Version)
        {
            warnings.Add(
                $"目标版本 v{targetDefinition.Version} 不高于当前版本 v{sourceDefinition.Version}（回退迁移，请确认这是预期操作）。");
        }

        var executions = await _executions.FindAllByProcessInstanceIdAsync(instance.Id, ct).ConfigureAwait(false);

        if (executions.Count == 0)
        {
            throw new EngineException($"流程实例 '{instance.Id}' 的执行树为空，无法迁移。");
        }

        var tasks = await _tasks.FindTasksByProcessInstanceIdAsync(instance.Id, ct).ConfigureAwait(false);
        var taskByExecution = new Dictionary<string, TaskEntity>(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.ExecutionId))
            {
                errors.Add($"待办 '{task.Id}' 未关联执行（数据损坏），无法安全迁移。");
                continue;
            }

            if (!taskByExecution.TryAdd(task.ExecutionId, task))
            {
                errors.Add($"执行 '{task.ExecutionId}' 上存在多个待办，迁移语义未定义。");
            }
        }

        var moves = new List<ExecutionMigration>(executions.Count);

        foreach (var execution in executions)
        {
            var fromActivityId = execution.CurrentActivityId;

            // 作用域执行（根执行 / 会签父执行）只承载变量与子执行，没有当前节点；
            // 它们的 PROC_DEF_ID_ 仍要跟着切换，但不需要活动映射。
            if (string.IsNullOrWhiteSpace(fromActivityId))
            {
                continue;
            }

            var sourceElement = sourceProcess.GetFlowElement(fromActivityId);

            if (sourceElement is null)
            {
                errors.Add($"执行 '{execution.Id}' 的当前节点 '{fromActivityId}' 不在当前定义中（数据损坏）。");
                continue;
            }

            if (sourceElement is UserTask { LoopCharacteristics: not null })
            {
                errors.Add(
                    $"节点 '{fromActivityId}' 是多实例（会签/串签），暂不支持迁移：实例级迁移会与 nrOf* 计数脱节，" +
                    "请等会签结束或直接删除实例后在新版本重跑。");
                continue;
            }

            var hasTask = taskByExecution.TryGetValue(execution.Id, out var task);
            var toActivityId = mappings.TryGetValue(fromActivityId, out var mapped) ? mapped : fromActivityId;

            if (string.IsNullOrWhiteSpace(toActivityId))
            {
                errors.Add($"映射 '{fromActivityId}' 的目标节点为空。");
                continue;
            }

            var targetElement = targetProcess.GetFlowElement(toActivityId);

            if (targetElement is null)
            {
                errors.Add(
                    $"节点 '{fromActivityId}' 在目标定义中没有同 id 节点：请通过 activityMappings 指定目标" +
                    $"（例如 \"{fromActivityId}\": \"新节点id\"）。");
                continue;
            }

            if (targetElement is UserTask { LoopCharacteristics: not null })
            {
                errors.Add($"目标节点 '{toActivityId}' 是多实例（会签/串签），暂不支持迁入。");
                continue;
            }

            if (hasTask)
            {
                if (targetElement is not UserTask targetTask)
                {
                    errors.Add(
                        $"节点 '{fromActivityId}' 当前有待办，只能迁移到用户任务（目标 '{toActivityId}' 是 " +
                        $"{targetElement.GetType().Name}）。");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(targetTask.Assignee)
                    && targetTask.CandidateUsers.Count == 0
                    && targetTask.CandidateGroups.Count == 0)
                {
                    warnings.Add($"目标节点 '{toActivityId}' 未配置办理人/候选用户/候选组，迁移后将产生无人可办的待办。");
                }

                if (!string.IsNullOrWhiteSpace(targetTask.SkipExpression))
                {
                    warnings.Add($"目标节点 '{toActivityId}' 配置了 skipExpression，迁移后可能被立即跳过。");
                }

                if (HasBoundaryTimer(targetProcess, targetTask.Id!))
                {
                    warnings.Add($"目标节点 '{toActivityId}' 带定时边界事件：迁移后定时器会从当前时刻重新计时。");
                }
            }
            else if (sourceElement is Gateway sourceGateway)
            {
                if (targetElement is not Gateway targetGateway)
                {
                    errors.Add(
                        $"执行 '{execution.Id}' 停在网关 '{fromActivityId}'，只能迁移到同类型网关" +
                        $"（目标 '{toActivityId}' 是 {targetElement.GetType().Name}）。");
                    continue;
                }

                if (targetGateway.GetType() != sourceGateway.GetType())
                {
                    errors.Add(
                        $"网关 '{fromActivityId}'（{sourceGateway.GetType().Name}）不能迁移到 " +
                        $"'{toActivityId}'（{targetGateway.GetType().Name}）：Join 判定语义不同。");
                    continue;
                }
            }
            else if (sourceElement is UserTask)
            {
                errors.Add($"执行 '{execution.Id}' 停在用户任务 '{fromActivityId}' 但没有待办（数据不一致），无法迁移。");
                continue;
            }
            else
            {
                errors.Add(
                    $"执行 '{execution.Id}' 当前在 {sourceElement.GetType().Name} '{fromActivityId}'，" +
                    "不是可迁移的等待点（只支持用户任务与网关 Join）。");
                continue;
            }

            moves.Add(new ExecutionMigration(execution, (FlowElement)targetElement, task, hasTask));
        }

        var movedExecutionIds = moves
            .Select(move => move.Execution.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var executionId in taskByExecution.Keys.Where(id => !movedExecutionIds.Contains(id)))
        {
            errors.Add($"待办所属执行 '{executionId}' 没有可迁移的当前节点（数据损坏），无法安全迁移。");
        }

        foreach (var (from, _) in mappings)
        {
            if (!moves.Any(move => string.Equals(move.Execution.CurrentActivityId, from, StringComparison.Ordinal)))
            {
                warnings.Add($"映射 '{from}' 没有命中任何运行中的执行（可能该节点上已无令牌）。");
            }
        }

        if (errors.Count > 0)
        {
            throw new EngineException(
                $"流程实例 '{instance.Id}' 迁移校验失败：\n- " + string.Join("\n- ", errors));
        }

        var activities = moves
            .GroupBy(move => move.Execution.CurrentActivityId!, StringComparer.Ordinal)
            .Select(group => new ActivityMigration(
                group.Key,
                group.First().Target.Id!,
                group.Count(),
                group.Any(move => move.RecreateTask)))
            .OrderBy(activity => activity.FromActivityId, StringComparer.Ordinal)
            .ToList();

        var plan = new MigrationPlan(
            instance.Id,
            sourceDefinitionId,
            targetDefinitionId,
            activities,
            moves.Count(move => move.RecreateTask),
            warnings);

        return new MigrationPlanDetails(plan, executions, moves, targetDefinition, targetProcess);
    }

    private static void ValidateMappingKeys(
        IReadOnlyDictionary<string, string> mappings,
        Process sourceProcess,
        Process targetProcess,
        List<string> errors)
    {
        foreach (var (from, to) in mappings)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                errors.Add("activityMappings 中存在空的源节点或目标节点 id。");
                continue;
            }

            if (sourceProcess.GetFlowElement(from) is null)
            {
                errors.Add($"activityMappings 的源节点 '{from}' 不在当前流程定义中。");
            }

            if (targetProcess.GetFlowElement(to) is null)
            {
                errors.Add($"activityMappings 的目标节点 '{to}' 不在目标流程定义中。");
            }
        }
    }

    private static bool HasBoundaryTimer(Process process, string activityId) =>
        process.FlowElements
            .OfType<BoundaryEvent>()
            .Any(boundary => boundary.Timer is not null
                && string.Equals(boundary.AttachedToRef, activityId, StringComparison.Ordinal));
}

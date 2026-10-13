using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence.Deployment;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// <see cref="IWorkflowService"/> 的默认实现 —— 只做编排，不碰存储细节（那是 <see cref="IWorkflowDataStore"/> 的事）。
/// </summary>
/// <remarks>
/// <para>
/// 编排顺序里有两处是<b>不能调换</b>的，都跟「流程可能在本步结束」有关：
/// </para>
/// <list type="number">
///   <item><description>
///   「先落业务数据、再推进流程」—— 任务一完成，运行态变量与任务行都可能被删掉，之后就读不到了；
///   </description></item>
///   <item><description>
///   「参数先于结论落盘」—— 服务任务里写结论时要用到参数快照（如请假天数）。
///   </description></item>
/// </list>
/// <para>
/// 因此 <see cref="SubmitAsync"/> 的顺序固定为：
/// 校验 → 写业务登记 → 写参数表 → 完成引擎任务 → 写当前待办 → 记审批记录 → 回视图。
/// </para>
/// <para>
/// 「实例登记先于参数表」也是刻意的：业务库里的参数往往是<b>业务单表的列</b>
/// （如请假单的 <c>days</c>），而业务单要靠实例登记里的业务键才能定位 ——
/// 反过来的话，第一次落参数时根本不知道该更新哪一行。
/// </para>
/// </remarks>
public sealed class WorkflowService : IWorkflowService
{
    private readonly IProcessEngine _engine;
    private readonly IExecutionManager _executionManager;
    private readonly ITaskManager _taskManager;
    private readonly IUserGroupManager _users;
    private readonly IWorkflowDataStore _store;
    private readonly WorkflowInstanceViewBuilder _instanceViews;
    private readonly WorkflowHistoryBuilder _histories;

    /// <summary>构造门面。</summary>
    /// <param name="engine">引擎门面。</param>
    /// <param name="executionManager">执行树管理器（读运行态变量、判断实例是否还在）。</param>
    /// <param name="taskManager">任务管理器（按 id 取待办）。</param>
    /// <param name="users">身份模块（把候选组展开成审批人）。</param>
    /// <param name="store">业务数据端口。</param>
    public WorkflowService(
        IProcessEngine engine,
        IExecutionManager executionManager,
        ITaskManager taskManager,
        IUserGroupManager users,
        IWorkflowDataStore store)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(executionManager);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(store);

        _engine = engine;
        _executionManager = executionManager;
        _taskManager = taskManager;
        _users = users;
        _store = store;
        _instanceViews = new WorkflowInstanceViewBuilder(engine, executionManager, store);
        _histories = new WorkflowHistoryBuilder(engine, executionManager, store);
    }

    /// <inheritdoc />
    public async ValueTask<string?> ResolveDefinitionIdAsync(
        string processDefinitionKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionKey);

        IReadOnlyList<DeployedProcessDefinition> definitions =
            await _engine.FindProcessDefinitionsAsync(ct).ConfigureAwait(false);

        return definitions
            .Where(definition => string.Equals(definition.Key, processDefinitionKey, StringComparison.Ordinal))
            .OrderByDescending(definition => definition.Version)
            .Select(definition => definition.Id)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowInstanceView> StartAsync(
        string processDefinitionKey,
        StartWorkflowRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionKey);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Applicant))
        {
            throw new WorkflowValidationException("applicant 不能为空。");
        }

        var definitionId = await ResolveDefinitionIdAsync(processDefinitionKey, ct).ConfigureAwait(false)
            ?? throw new WorkflowValidationException(
                $"流程定义 '{processDefinitionKey}' 尚未部署，请先启动宿主（会自动部署）。");

        // 发起时只能写 BaseModel 的前五个字段；result / comment 要等审批环节的表单产生。
        // ⚠️ applyUser 同时是 BPMN 里 assignee="${applyUser}" 的取值来源 —— 变量名是流程契约。
        var baseModel = BaseModel.Start(
            applyUser: request.Applicant,
            applyTime: DateTime.UtcNow,
            businessId: request.BusinessKey,
            businessDeptId: request.BusinessDeptId,
            applyDeptId: request.ApplyDeptId);

        var paraDict = ParaDict.FromJson(request.ParaDict);
        EnsureNoConflicts(paraDict);

        // 流程参数：既要展开成扁平变量（网关条件要用），也要落进流程参数表
        var variables = baseModel.ToVariables();

        foreach (var (name, value) in paraDict.ToVariables())
        {
            variables[name] = value;
        }

        var processInstanceId = await _engine.StartProcessInstanceAsync(
            definitionId,
            request.BusinessKey,
            request.Applicant,
            variables,
            ct).ConfigureAwait(false);

        // 实例登记先落盘：业务侧的参数往往是业务单的列（如请假单的 days），
        // 而业务单要靠登记里的业务键才能定位 —— 顺序反了，第一次写参数就没有行可更新。
        await _store.SaveInstanceAsync(
            new WorkflowInstanceRecord(processInstanceId, baseModel),
            ct).ConfigureAwait(false);

        await _store.SaveParametersAsync(processInstanceId, paraDict.Values, ct).ConfigureAwait(false);

        // 流程此刻已经停在第一个环节上：把「停在哪儿、该谁办」登记下来
        await ReplaceCurrentTasksAsync(processInstanceId, ct).ConfigureAwait(false);

        return await _instanceViews.BuildAsync(processInstanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"流程实例 '{processInstanceId}' 刚发起就查不到（引擎与业务数据都没有）。");
    }

    /// <inheritdoc />
    public ValueTask<WorkflowInstanceView?> FindInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return _instanceViews.BuildAsync(processInstanceId, ct);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowTaskView>> FindTasksAsync(
        TaskQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.HasCriteria)
        {
            throw new WorkflowValidationException("至少需要 assignee / candidateUser / processInstanceId 之一。");
        }

        var tasks = await _engine.FindTasksAsync(query, ct).ConfigureAwait(false);
        var views = new List<WorkflowTaskView>(tasks.Count);

        foreach (var task in tasks)
        {
            views.Add(WorkflowFormMapper.ToView(
                task,
                await WorkflowFormMapper.FindUserTaskAsync(_engine, task, ct).ConfigureAwait(false)));
        }

        return views;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowInstanceView?> SubmitAsync(
        string taskId,
        SubmitWorkflowTaskRequest? request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var task = await _taskManager.FindTaskByIdAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new WorkflowNotFoundException($"任务 '{taskId}' 不存在（可能已完成）。");

        var definition = await WorkflowFormMapper
            .FindUserTaskAsync(_engine, task, ct)
            .ConfigureAwait(false);

        var mapping = WorkflowFormMapper.MapToVariables(definition, request?.Values);

        if (!mapping.IsValid)
        {
            throw new WorkflowValidationException("表单校验不通过。", mapping.Errors);
        }

        // 参数更新：有值就按 key 更新，没提到的键保持原样
        var parameterUpdates = ParaDict.FromJson(request?.ParaDict);
        EnsureNoConflicts(parameterUpdates);

        // 本次提交要写进引擎的变量 = 表单字段（days / reason / result / comment）+ 显式更新的参数
        var variables = new Dictionary<string, object?>(mapping.Variables, StringComparer.Ordinal);

        foreach (var (name, value) in parameterUpdates.ToVariables())
        {
            variables[name] = value;
        }

        if (task.ProcessInstanceId is null)
        {
            // 不在任何流程实例上的任务（本子集不会产生）：能完成，但无从谈「实例视图」
            await _engine.CompleteTaskAsync(
                taskId,
                variables.Count == 0 ? null : variables,
                ct).ConfigureAwait(false);

            return null;
        }

        var processInstanceId = task.ProcessInstanceId;

        // 表单字段本身就是「流程中的参数」（采集入口不同而已）；显式更新覆盖它
        var parameterUpdatesDelta = CollectParameterUpdates(mapping.Variables, parameterUpdates);

        // 先把业务数据补上再推进流程：实例可能在本步就走到结束，
        // 那时执行树行已被删除，变量与任务都读不到了。
        // 登记仍先于参数 —— 业务库里的参数列（days 之类）要靠登记里的业务键才能定位。
        var record = await _store.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        if (record is not null)
        {
            var merged = await MergeWithExecutionVariablesAsync(task, variables, ct).ConfigureAwait(false);

            await _store.SaveInstanceAsync(
                record with { Base = BaseModel.FromVariables(merged, record.Base) },
                ct).ConfigureAwait(false);
        }

        await _store.SaveParametersAsync(processInstanceId, parameterUpdatesDelta, ct).ConfigureAwait(false);

        await _engine.CompleteTaskAsync(
            taskId,
            variables.Count == 0 ? null : variables,
            ct).ConfigureAwait(false);

        // 推进之后才知道流程停到了哪儿：这一次查询同时喂给「当前待办」与审批记录的「流转到」
        var nextTasks = await FindTasksAsync(
            new TaskQuery(ProcessInstanceId: processInstanceId), ct).ConfigureAwait(false);

        await ReplaceCurrentTasksAsync(processInstanceId, nextTasks, ct).ConfigureAwait(false);

        // 审批记录同样放在推进之后：提交后才知道流转到哪儿、是不是就此结束了
        await _store.SaveApprovalAsync(
            BuildApprovalRecord(
                task,
                definition,
                mapping,
                request?.Operator,
                ToActivityRefs(nextTasks),
                await _store.FindResultAsync(processInstanceId, ct).ConfigureAwait(false)),
            ct).ConfigureAwait(false);

        return await _instanceViews.BuildAsync(processInstanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"流程实例 '{processInstanceId}' 在提交后查不到（既无运行态也无业务数据）。");
    }

    /// <inheritdoc />
    public ValueTask<WorkflowHistoryView?> FindHistoryAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return _histories.BuildAsync(processInstanceId, ct);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowInstanceSummary>> FindInstancesAsync(
        CancellationToken ct = default)
    {
        var records = await _store.FindInstancesAsync(ct).ConfigureAwait(false);
        var summaries = new List<WorkflowInstanceSummary>(records.Count);

        foreach (var record in records)
        {
            var result = await _store.FindResultAsync(record.ProcessInstanceId, ct).ConfigureAwait(false);
            var parameters = await _store.FindParametersAsync(record.ProcessInstanceId, ct).ConfigureAwait(false);

            summaries.Add(new WorkflowInstanceSummary(
                ProcessInstanceId: record.ProcessInstanceId,
                Base: record.Base,
                StartedAt: record.Base.ApplyTime,
                Running: result is null,
                Result: result,
                Parameters: parameters));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(
        IDelegateExecution execution,
        string outcome,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        var processInstanceId = execution.ProcessInstanceId ?? execution.Id;

        // 当前节点就是那个 serviceTask —— 节点名直接进业务数据，便于审计
        var activity = execution.CurrentFlowElement as ServiceTask;

        // 业务上下文用宿主自己的登记兜底：服务任务跑在子执行上，变量一旦被清就什么都读不到，
        // 那时宁可拿到发起时的上下文，也不要一堆 null。
        var record = await _store.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        // 流程变量 → BaseModel（审批人提交的 result / comment 就在这里）
        var variables = await execution.GetVariablesAsync(ct).ConfigureAwait(false);

        var baseModel = BaseModel.FromVariables(
            new Dictionary<string, object?>(variables, StringComparer.Ordinal),
            record?.Base);

        // 参数快照：流程一结束运行态变量就没了，天数这类值只能在此时记下来
        var parameters = await _store.FindParametersAsync(processInstanceId, ct).ConfigureAwait(false);

        await _store.SaveResultAsync(
            new WorkflowOutcomeRecord(
                ProcessInstanceId: processInstanceId,
                Base: baseModel,
                Outcome: outcome,
                ActivityId: activity?.Id ?? execution.CurrentActivityId ?? string.Empty,
                ActivityName: activity?.Name ?? string.Empty,
                CompletedAt: DateTime.UtcNow,
                Parameters: parameters),
            ct).ConfigureAwait(false);
    }

    /// <summary>参数名撞上 <see cref="BaseModelVariables"/> 就报错 —— 静默放行只会污染审批元数据。</summary>
    private static void EnsureNoConflicts(ParaDict paraDict)
    {
        var conflicts = paraDict.ConflictingKeys();

        if (conflicts.Count > 0)
        {
            throw new WorkflowValidationException(
                $"流程参数名与审批元数据字段冲突：{string.Join(", ", conflicts)}。",
                [$"这些名字已由 BaseModel 占用：{string.Join(", ", BaseModelVariables.All)}。"]);
        }
    }

    /// <summary>
    /// 本次提交要写进参数表的<b>增量</b>：表单字段里属于业务参数的那些，再被显式更新覆盖。
    /// </summary>
    /// <remarks>
    /// ⚠️ 审批元数据字段（<c>result</c> / <c>comment</c> 等）<b>不进参数表</b> ——
    /// 它们归 <see cref="BaseModel"/>，重复登记只会让同一个值有两个来源。
    /// </remarks>
    private static Dictionary<string, string?> CollectParameterUpdates(
        IReadOnlyDictionary<string, object?> submitted,
        ParaDict updates)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (name, value) in submitted)
        {
            if (!BaseModelVariables.All.Contains(name, StringComparer.Ordinal))
            {
                parameters[name] = VariableValues.AsString(value);
            }
        }

        foreach (var (name, value) in updates.Values)
        {
            parameters[name] = value;
        }

        return parameters;
    }

    /// <summary>执行上的现有变量为底，叠加本次提交的值（用于把审批结论补进业务登记）。</summary>
    private async ValueTask<Dictionary<string, object?>> MergeWithExecutionVariablesAsync(
        TaskEntity task,
        IReadOnlyDictionary<string, object?> submitted,
        CancellationToken ct)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (task.ExecutionId is not null)
        {
            var execution = await _executionManager.FindByIdAsync(task.ExecutionId, ct).ConfigureAwait(false);

            if (execution is not null)
            {
                foreach (var (name, value) in await execution.GetVariablesAsync(ct).ConfigureAwait(false))
                {
                    merged[name] = value;
                }
            }
        }

        foreach (var (name, value) in submitted)
        {
            merged[name] = value;
        }

        return merged;
    }

    /// <summary>待办视图 → 环节引用（审批记录里的「流转到」）。</summary>
    private static IReadOnlyList<WorkflowActivityRef> ToActivityRefs(IReadOnlyList<WorkflowTaskView> tasks) =>
        [
            .. tasks
                .Where(task => !string.IsNullOrEmpty(task.DefinitionKey))
                .Select(task => new WorkflowActivityRef(task.DefinitionKey!, task.Name)),
        ];

    /// <summary>查一次该实例此刻的待办，然后整体替换掉登记的那份。</summary>
    private async ValueTask ReplaceCurrentTasksAsync(string processInstanceId, CancellationToken ct)
    {
        var tasks = await FindTasksAsync(
            new TaskQuery(ProcessInstanceId: processInstanceId), ct).ConfigureAwait(false);

        await ReplaceCurrentTasksAsync(processInstanceId, tasks, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把实例此刻的待办整体登记下来（<paramref name="tasks"/> 为空即「实例已结束，没有待办」）。
    /// </summary>
    /// <remarks>
    /// 「该谁办」在这里定：任务上写的是候选关系（组 / 用户），业务侧要的是具体的人，
    /// 所以候选组要借身份模块展开 —— 这一步只有应用层做得了，宿主的存储层拿不到身份信息。
    /// </remarks>
    private async ValueTask ReplaceCurrentTasksAsync(
        string processInstanceId,
        IReadOnlyList<WorkflowTaskView> tasks,
        CancellationToken ct)
    {
        var records = new List<WorkflowCurrentTaskRecord>(tasks.Count);

        foreach (var task in tasks)
        {
            records.Add(new WorkflowCurrentTaskRecord(
                ProcessInstanceId: processInstanceId,
                TaskId: task.TaskId,
                ActivityId: task.DefinitionKey,
                ActivityName: task.Name,
                Approvers: await ResolveApproversAsync(task, ct).ConfigureAwait(false),
                CandidateUsers: task.CandidateUsers,
                CandidateGroups: task.CandidateGroups,
                FormKey: task.FormKey,
                CreatedAt: task.CreateTime));
        }

        await _store.ReplaceCurrentTasksAsync(processInstanceId, records, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 「这一步该谁办」：任务已经指派给人就是那个人，否则把候选用户与候选组的成员并起来。
    /// </summary>
    /// <remarks>
    /// ⚠️ 候选组展开<b>可能得到空集</b>（权限体系不给成员，或组里确实没人）——
    /// 那表示「审批人未知」，不是错误，照常落一条审批人为空的记录即可。
    /// 结果去重后排序，保证同一实例多次落库时顺序稳定、便于比对。
    /// </remarks>
    private async ValueTask<IReadOnlyList<string>> ResolveApproversAsync(
        WorkflowTaskView task,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(task.Assignee))
        {
            return [task.Assignee];
        }

        var approvers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var user in task.CandidateUsers)
        {
            if (!string.IsNullOrWhiteSpace(user) && seen.Add(user))
            {
                approvers.Add(user);
            }
        }

        foreach (var group in task.CandidateGroups)
        {
            foreach (var user in await _users.FindUserIdsByGroupAsync(group, ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(user) && seen.Add(user))
                {
                    approvers.Add(user);
                }
            }
        }

        approvers.Sort(StringComparer.Ordinal);

        return approvers;
    }

    /// <summary>把一次提交整理成审批记录（历史的数据源）。</summary>
    private static WorkflowApprovalRecord BuildApprovalRecord(
        TaskEntity task,
        UserTask? definition,
        WorkflowFormMapping mapping,
        string? submitter,
        IReadOnlyList<WorkflowActivityRef> nextSteps,
        WorkflowOutcomeRecord? result)
    {
        var values = VariableValues.ToText(mapping.Variables);
        var decision = values.GetValueOrDefault(BaseModelVariables.Result);

        return new WorkflowApprovalRecord(
            ProcessInstanceId: task.ProcessInstanceId ?? string.Empty,
            TaskId: task.Id,
            ActivityId: task.TaskDefinitionKey,
            ActivityName: task.Name,
            FormKey: definition?.FormKey,
            // 请求里给的办理人优先 —— 按候选组审批的任务在认领前 assignee 是空的
            Assignee: string.IsNullOrWhiteSpace(submitter) ? task.Assignee : submitter,
            Decision: decision,
            // 中文名以流程定义里的选项名为准（那才是选项的真身），枚举只是回退
            DecisionName: WorkflowFormMapper.FindOptionName(definition, ApprovalResults.FormPropertyId, decision)
                ?? ApprovalResults.TryParseOptionId(decision)?.ToDisplayName(),
            Comment: values.GetValueOrDefault(BaseModelVariables.Comment),
            Values: values,
            NextSteps: nextSteps,
            Outcome: result?.Outcome,
            SubmittedAt: DateTime.UtcNow);
    }
}

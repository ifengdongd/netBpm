using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence.Deployment;

namespace NetBpm.Runtime;

/// <summary>默认引擎门面。</summary>
public sealed class ProcessEngine : IProcessEngine
{
    private readonly ICommandExecutor _commandExecutor;
    private readonly IBpmnXmlConverter _xmlConverter;
    private readonly IProcessValidator _validator;
    private readonly IProcessDefinitionStore _definitionStore;
    private readonly ITaskManager _taskManager;
    private readonly IUserGroupManager _userGroupManager;
    private readonly IExecutionManager _executionManager;
    private readonly IHistoryManager _historyManager;

    public ProcessEngine(
        ICommandExecutor commandExecutor,
        IBpmnXmlConverter xmlConverter,
        IProcessValidator validator,
        IProcessDefinitionStore definitionStore,
        ITaskManager taskManager,
        IUserGroupManager userGroupManager,
        IExecutionManager executionManager,
        IHistoryManager historyManager)
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(xmlConverter);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(definitionStore);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentNullException.ThrowIfNull(userGroupManager);
        ArgumentNullException.ThrowIfNull(executionManager);
        ArgumentNullException.ThrowIfNull(historyManager);

        _commandExecutor = commandExecutor;
        _xmlConverter = xmlConverter;
        _validator = validator;
        _definitionStore = definitionStore;
        _taskManager = taskManager;
        _userGroupManager = userGroupManager;
        _executionManager = executionManager;
        _historyManager = historyManager;
    }

    /// <inheritdoc />
    public ValueTask<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
        _commandExecutor.ExecuteAsync(command, ct);

    /// <inheritdoc />
    public async ValueTask<ProcessDefinition> DeployAsync(
        Stream bpmnXml,
        string resourceName,
        string? deploymentName = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var result = await DeployWithValidationAsync(bpmnXml, resourceName, deploymentName, tenantId, ct)
            .ConfigureAwait(false);

        return result.ProcessDefinition;
    }

    /// <inheritdoc />
    public async ValueTask<DeployResult> DeployWithValidationAsync(
        Stream bpmnXml,
        string resourceName,
        string? deploymentName = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bpmnXml);

        // B2.5：先截下原始字节 —— ACT_GE_BYTEARRAY.BYTES_ 存调用方给的原文
        // （与 Activiti 一致），而不是重新序列化的 XML（解析 / 校验仍走模型）
        using var rawBuffer = new MemoryStream();
        await bpmnXml.CopyToAsync(rawBuffer, ct).ConfigureAwait(false);
        var rawBytes = rawBuffer.ToArray();

        using var parseStream = new MemoryStream(rawBytes);
        var bpmnModel = _xmlConverter.ConvertToBpmnModel(parseStream);

        var process = bpmnModel.GetMainProcess()
            ?? throw new NetBpm.Engine.EngineException("部署失败：BPMN 文件中没有流程定义。");

        // 前端只读渲染用的即时校验不可信边界，后端必须复校（§6.2 第 4 项）。
        // 只有阻断级问题才抛异常；提示级问题随结果返回给设计器
        var warnings = _validator.ValidateAndThrow(process);

        // 落库必须在一个命令（= 一个事务）内完成，避免中途失败留下孤儿部署行
        var command = new DeployProcessCommand(
            _definitionStore,
            bpmnModel,
            resourceName,
            deploymentName,
            rawBytes,
            tenantId);

        var definition = await _commandExecutor.ExecuteAsync(command, ct).ConfigureAwait(false);

        return new DeployResult(definition, warnings);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ValidationError>> ValidateAsync(
        Stream bpmnXml,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bpmnXml);

        using var buffer = new MemoryStream();
        await bpmnXml.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        var bpmnModel = _xmlConverter.ConvertToBpmnModel(buffer);

        var process = bpmnModel.GetMainProcess()
            ?? throw new EngineException("校验失败：BPMN 文件中没有流程定义。");

        return _validator.Validate(process);
    }

    /// <inheritdoc />
    public async ValueTask<string> StartProcessInstanceAsync(
        string processDefinitionId,
        string? businessKey = null,
        string? startUserId = null,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        var bpmnModel = await _definitionStore.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false)
            ?? throw new NetBpm.Engine.EngineException($"流程定义 '{processDefinitionId}' 不存在。");

        var process = bpmnModel.GetMainProcess()
            ?? throw new NetBpm.Engine.EngineException($"流程定义 '{processDefinitionId}' 中没有流程。");

        // 挂起的定义不再接受新实例（定义级；运行中的实例是否可办由
        // suspendProcessInstances 决定，见 SuspendProcessDefinitionAsync）
        if (await _definitionStore.IsSuspendedAsync(processDefinitionId, ct).ConfigureAwait(false))
        {
            throw new EngineException(
                $"流程定义 '{processDefinitionId}' 已挂起，不能启动新实例（请先激活）。");
        }

        // B2.3：实例继承定义的租户（与 Activiti 一致，启动接口不单独收租户参数）
        var tenantId = await _definitionStore.GetTenantIdAsync(processDefinitionId, ct).ConfigureAwait(false);

        var command = new StartProcessInstanceCommand(
            process,
            processDefinitionId,
            businessKey,
            startUserId,
            variables,
            tenantId);

        return await _commandExecutor.ExecuteAsync(command, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string> StartProcessInstanceByKeyAsync(
        string processDefinitionKey,
        string? businessKey = null,
        string? startUserId = null,
        IDictionary<string, object?>? variables = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionKey);

        var definition = await _definitionStore
            .FindLatestByKeyAsync(processDefinitionKey, tenantId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineException(
                tenantId is null
                    ? $"流程定义 key '{processDefinitionKey}' 不存在（无租户）。"
                    : $"流程定义 key '{processDefinitionKey}' 在租户 '{tenantId}' 下不存在。");

        // 版本选出来之后仍然走 StartProcessInstanceAsync：挂起检查、租户继承、模型加载都只有一份实现
        return await StartProcessInstanceAsync(definition.Id, businessKey, startUserId, variables, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IExecutionEntity>> FindProcessInstancesAsync(
        ProcessInstanceQuery query,
        CancellationToken ct = default)
    {
        var instances = await CollectProcessInstancesAsync(query, ct).ConfigureAwait(false);

        return
        [
            .. ApplyPaging(
                instances,
                query.FirstResult,
                query.MaxResults,
                instance => instance.StartTime,
                instance => instance.Id,
                descending: true),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountProcessInstancesAsync(
        ProcessInstanceQuery query,
        CancellationToken ct = default) =>
        (await CollectProcessInstancesAsync(query, ct).ConfigureAwait(false)).Count;

    private async ValueTask<IReadOnlyList<IExecutionEntity>> CollectProcessInstancesAsync(
        ProcessInstanceQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);

        var instances = await _executionManager.FindProcessInstancesAsync(ct).ConfigureAwait(false);

        HashSet<string>? definitionIds = null;
        if (!string.IsNullOrWhiteSpace(query.ProcessDefinitionKey))
        {
            definitionIds = await ResolveDefinitionIdsByKeyAsync(query.ProcessDefinitionKey, query.TenantId, ct)
                .ConfigureAwait(false);
        }

        return
        [
            .. instances.Where(instance => Matches(instance, query, definitionIds)),
        ];
    }

    private static bool Matches(
        IExecutionEntity instance,
        ProcessInstanceQuery query,
        HashSet<string>? definitionIds) =>
        (string.IsNullOrWhiteSpace(query.ProcessDefinitionId)
            || string.Equals(instance.ProcessDefinitionId, query.ProcessDefinitionId, StringComparison.Ordinal))
        && (definitionIds is null || definitionIds.Contains(instance.ProcessDefinitionId ?? string.Empty))
        && (string.IsNullOrWhiteSpace(query.BusinessKey)
            || string.Equals(instance.BusinessKey, query.BusinessKey, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(query.StartUserId)
            || string.Equals(instance.StartUserId, query.StartUserId, StringComparison.Ordinal))
        && (query.TenantId is null
            || string.Equals(instance.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal))
        && (query.IsActive is null || instance.IsActive == query.IsActive)
        && (query.StartedAfter is null
            || (instance.StartTime is { } startedAfter && startedAfter >= query.StartedAfter))
        && (query.StartedBefore is null
            || (instance.StartTime is { } startedBefore && startedBefore <= query.StartedBefore));

    /// <summary>把流程 key 解析成该 key 下全部版本的流程定义 id（用于按 key 过滤实例 / 历史）。</summary>
    private async ValueTask<HashSet<string>> ResolveDefinitionIdsByKeyAsync(
        string processDefinitionKey,
        string? tenantId,
        CancellationToken ct)
    {
        var definitions = await _definitionStore.FindAllAsync(tenantId, ct).ConfigureAwait(false);

        return definitions
            .Where(definition => string.Equals(definition.Key, processDefinitionKey, StringComparison.Ordinal))
            .Select(definition => definition.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<string?> GetProcessDefinitionXmlAsync(string processDefinitionId, CancellationToken ct = default) =>
        _definitionStore.GetResourceXmlAsync(processDefinitionId, ct);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindProcessDefinitionsAsync(
        string? tenantId = null,
        CancellationToken ct = default) =>
        _commandExecutor.ExecuteAsync(new FindProcessDefinitionsCommand(_definitionStore, tenantId), ct);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindProcessDefinitionsAsync(
        ProcessDefinitionQuery query,
        CancellationToken ct = default)
    {
        var definitions = await CollectDefinitionsAsync(query, ct).ConfigureAwait(false);

        return
        [
            .. ApplyPaging(
                definitions,
                query.FirstResult,
                query.MaxResults,
                definition => definition.DeployTime,
                definition => definition.Id,
                descending: true),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountProcessDefinitionsAsync(
        ProcessDefinitionQuery query,
        CancellationToken ct = default) =>
        (await CollectDefinitionsAsync(query, ct).ConfigureAwait(false)).Count;

    private async ValueTask<IReadOnlyList<DeployedProcessDefinition>> CollectDefinitionsAsync(
        ProcessDefinitionQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);

        var definitions = await _definitionStore.FindAllAsync(query.TenantId, ct).ConfigureAwait(false);

        return
        [
            .. definitions.Where(definition =>
                string.IsNullOrWhiteSpace(query.Key)
                || string.Equals(definition.Key, query.Key, StringComparison.Ordinal)),
        ];
    }

    /// <inheritdoc />
    public async ValueTask SuspendProcessDefinitionAsync(
        string processDefinitionId,
        bool suspendProcessInstances = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        // 定义位 + 运行实例 + 任务必须在同一个命令里改（见命令注释）
        await _commandExecutor
            .ExecuteAsync(
                new SetProcessDefinitionSuspensionStateCommand(
                    _definitionStore,
                    processDefinitionId,
                    suspended: true,
                    affectRunningInstances: suspendProcessInstances),
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ActivateProcessDefinitionAsync(
        string processDefinitionId,
        bool activateProcessInstances = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        await _commandExecutor
            .ExecuteAsync(
                new SetProcessDefinitionSuspensionStateCommand(
                    _definitionStore,
                    processDefinitionId,
                    suspended: false,
                    affectRunningInstances: activateProcessInstances),
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SuspendProcessInstanceAsync(string processInstanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await _commandExecutor
            .ExecuteAsync(new SetProcessInstanceSuspensionStateCommand(processInstanceId, suspended: true), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ActivateProcessInstanceAsync(string processInstanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await _commandExecutor
            .ExecuteAsync(new SetProcessInstanceSuspensionStateCommand(processInstanceId, suspended: false), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteDeploymentAsync(
        string deploymentId,
        bool cascade = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        await _commandExecutor
            .ExecuteAsync(new DeleteDeploymentCommand(_definitionStore, deploymentId, cascade), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<BpmnModel?> GetBpmnModelAsync(string processDefinitionId, CancellationToken ct = default) =>
        _definitionStore.GetBpmnModelAsync(processDefinitionId, ct);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TaskEntity>> FindTasksAsync(
        TaskQuery query,
        CancellationToken ct = default)
    {
        var tasks = await CollectTasksAsync(query, ct).ConfigureAwait(false);

        return [.. ApplyPaging(tasks, query)];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountTasksAsync(TaskQuery query, CancellationToken ct = default)
    {
        var tasks = await CollectTasksAsync(query, ct).ConfigureAwait(false);

        return tasks.Count;
    }

    /// <inheritdoc />
    public async ValueTask<TaskEntity?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return await _taskManager.FindTaskByIdAsync(taskId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IdentityLinkRecord>> FindTaskIdentityLinksAsync(
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var links = await _taskManager.FindIdentityLinksByTaskIdAsync(taskId, ct).ConfigureAwait(false);

        return [.. links];
    }

    /// <summary>
    /// 按条件取回全部待办（不排序、不分页）—— <see cref="FindTasksAsync"/> 与
    /// <see cref="CountTasksAsync"/> 共用同一套条件语义，避免两者的过滤口径漂移。
    /// </summary>
    private async ValueTask<IReadOnlyList<TaskEntity>> CollectTasksAsync(TaskQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.HasCriteria)
        {
            throw new EngineException(
                "任务查询至少需要一个条件（assignee / candidateUser / processInstanceId）。" +
                "本子集不提供「查全部任务」，避免全表扫描。");
        }

        ValidatePaging(query.FirstResult, query.MaxResults);

        // 先用仓储能表达的条件取候选集（实例 / 办理人 / 候选人 / 候选组），
        // 再统一在内存里做剩余过滤 —— 与「交集在内存求」的既定口径一致，
        // 也避免每加一个过滤条件就复制一份分支逻辑
        IList<TaskEntity> candidates;

        if (!string.IsNullOrWhiteSpace(query.ProcessInstanceId))
        {
            candidates = await _taskManager
                .FindTasksByProcessInstanceIdAsync(query.ProcessInstanceId, ct)
                .ConfigureAwait(false);

            // 「按实例 + 候选人」组合：两个已有查询取交集。
            // 候选人语义与单独查询同口径 —— 候选人 / 所属组可认领且未签收（ASSIGNEE_ is null）
            if (!string.IsNullOrWhiteSpace(query.CandidateUser))
            {
                candidates = await IntersectCandidateUserAsync(candidates, query.CandidateUser, ct)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(query.CandidateGroup))
            {
                candidates = await IntersectCandidateGroupAsync(candidates, query.CandidateGroup, ct)
                    .ConfigureAwait(false);
            }
        }
        else if (!string.IsNullOrWhiteSpace(query.CandidateUser))
        {
            // 候选组需要展开用户所属组，否则候选组任务查不出来
            candidates = await FindByCandidateUserAsync(query.CandidateUser, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(query.CandidateGroup))
            {
                candidates = await IntersectCandidateGroupAsync(candidates, query.CandidateGroup, ct)
                    .ConfigureAwait(false);
            }
        }
        else if (!string.IsNullOrWhiteSpace(query.CandidateGroup))
        {
            candidates = await _taskManager
                .FindTasksByCandidateGroupAsync(query.CandidateGroup, ct)
                .ConfigureAwait(false);
        }
        else
        {
            candidates = await _taskManager.FindTasksByAssigneeAsync(query.Assignee!, ct).ConfigureAwait(false);
        }

        return [.. candidates.Where(task => Matches(task, query))];
    }

    private async ValueTask<IList<TaskEntity>> FindByCandidateUserAsync(string candidateUser, CancellationToken ct)
    {
        var groupIds = await _userGroupManager
            .FindGroupIdsByUserAsync(candidateUser, ct)
            .ConfigureAwait(false);

        return await _taskManager
            .FindTasksByCandidateUserAsync(candidateUser, groupIds, ct)
            .ConfigureAwait(false);
    }

    private async ValueTask<IList<TaskEntity>> IntersectCandidateUserAsync(
        IList<TaskEntity> tasks,
        string candidateUser,
        CancellationToken ct)
    {
        var candidateTasks = await FindByCandidateUserAsync(candidateUser, ct).ConfigureAwait(false);
        var candidateTaskIds = candidateTasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);

        return [.. tasks.Where(task => candidateTaskIds.Contains(task.Id))];
    }

    private async ValueTask<IList<TaskEntity>> IntersectCandidateGroupAsync(
        IList<TaskEntity> tasks,
        string candidateGroup,
        CancellationToken ct)
    {
        var groupTasks = await _taskManager
            .FindTasksByCandidateGroupAsync(candidateGroup, ct)
            .ConfigureAwait(false);

        var groupTaskIds = groupTasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);

        return [.. tasks.Where(task => groupTaskIds.Contains(task.Id))];
    }

    /// <summary>
    /// 附加过滤（内存求交）：办理人 / 流程定义 / 节点 id。
    /// <para>
    /// 未给出的条件视为「不限制」；给出时按序数比较（Activiti 的 id 与用户名按字节比较，不做大小写折叠）。
    /// </para>
    /// </summary>
    private static bool Matches(TaskEntity task, TaskQuery query) =>
        (string.IsNullOrWhiteSpace(query.Assignee)
            || string.Equals(task.Assignee, query.Assignee, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(query.ProcessDefinitionId)
            || string.Equals(task.ProcessDefinitionId, query.ProcessDefinitionId, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(query.TaskDefinitionKey)
            || string.Equals(task.TaskDefinitionKey, query.TaskDefinitionKey, StringComparison.Ordinal))
        && (query.Suspended is null
            || (query.Suspended.Value
                ? task.SuspensionState == TaskEntity.SuspendedSuspensionState
                : task.SuspensionState == TaskEntity.ActiveSuspensionState))
        && (query.DueBefore is null
            || (task.DueDate is { } dueDate && dueDate <= query.DueBefore))
        && (query.TenantId is null
            || string.Equals(task.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal));

    /// <summary>
    /// 稳定排序 + 切片。
    /// <para>
    /// ⚠️ 先排序再分页不是锦上添花：底层查询没有 <c>order by</c>（<c>ITaskManager</c> 的查询接口
    /// 保持"条件查询"语义），不排序就分页会出现「第 2 页混进第 1 页的待办」。
    /// 排序键取 (CREATE_TIME_, ID_)：创建时间相同（同一毫秒内批量创建）时用 id 兜底。
    /// </para>
    /// </summary>
    private static IEnumerable<TaskEntity> ApplyPaging(IEnumerable<TaskEntity> tasks, TaskQuery query) =>
        ApplyPaging(tasks, query.FirstResult, query.MaxResults, task => task.CreateTime, task => task.Id);

    /// <summary>通用分页：稳定排序（时间 → id）后切片。待办与历史任务共用同一口径。</summary>
    /// <param name="items">待切片序列。</param>
    /// <param name="firstResult">跳过的条数。</param>
    /// <param name="maxResults">最多返回的条数。</param>
    /// <param name="timeSelector">时间排序键。</param>
    /// <param name="idSelector">id 兜底排序键。</param>
    /// <param name="descending">是否按时间倒序（实例 / 定义列表用倒序，待办用正序）。</param>
    private static IEnumerable<T> ApplyPaging<T>(
        IEnumerable<T> items,
        int firstResult,
        int? maxResults,
        Func<T, DateTime?> timeSelector,
        Func<T, string> idSelector,
        bool descending = false)
    {
        IEnumerable<T> ordered = descending
            ? items
                .OrderByDescending(item => timeSelector(item) ?? DateTime.MinValue)
                .ThenByDescending(idSelector, StringComparer.Ordinal)
            : items
                .OrderBy(item => timeSelector(item) ?? DateTime.MinValue)
                .ThenBy(idSelector, StringComparer.Ordinal);

        if (firstResult > 0)
        {
            ordered = ordered.Skip(firstResult);
        }

        if (maxResults is int take)
        {
            ordered = ordered.Take(take);
        }

        return ordered;
    }

    /// <summary>分页参数校验：非法取值显式报错，不静默当成"不限"。</summary>
    private static void ValidatePaging(int firstResult, int? maxResults)
    {
        if (firstResult < 0)
        {
            throw new EngineException($"firstResult 不能为负数（实际：{firstResult}）。");
        }

        if (maxResults is <= 0)
        {
            throw new EngineException($"maxResults 必须大于 0（实际：{maxResults}）；不限制请留空。");
        }
    }

    /// <inheritdoc />
    public async ValueTask CompleteTaskAsync(
        string taskId,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await _commandExecutor
            .ExecuteAsync(new CompleteTaskCommand(taskId, variables), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CompleteTaskAsync(
        string taskId,
        IDictionary<string, object?>? variables,
        int? expectedRevision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await _commandExecutor
            .ExecuteAsync(new CompleteTaskCommand(taskId, variables, expectedRevision), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ClaimTaskAsync(string taskId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await _commandExecutor
            .ExecuteAsync(new ClaimTaskCommand(taskId, userId), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ClaimTaskAsync(
        string taskId,
        string userId,
        int? expectedRevision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await _commandExecutor
            .ExecuteAsync(new ClaimTaskCommand(taskId, userId, expectedRevision), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask UnclaimTaskAsync(string taskId, int? expectedRevision = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await _commandExecutor
            .ExecuteAsync(new ClaimTaskCommand(taskId, null, expectedRevision), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SetAssigneeAsync(string taskId, string? assignee, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await _commandExecutor
            .ExecuteAsync(new SetAssigneeCommand(taskId, assignee), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask UpdateTaskAsync(
        string taskId,
        TaskUpdate update,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(update);

        await _commandExecutor
            .ExecuteAsync(
                new UpdateTaskCommand(
                    taskId,
                    update.Name,
                    update.Description,
                    update.Priority,
                    update.DueDate,
                    update.Category,
                    update.Owner,
                    update.ClearDueDate,
                    update.ClearDescription),
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AddTaskCandidateUsersAsync(
        string taskId,
        IReadOnlyCollection<string> userIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(userIds);

        await _commandExecutor
            .ExecuteAsync(new AddTaskCandidatesCommand(taskId, users: userIds), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AddTaskCandidateGroupsAsync(
        string taskId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(groupIds);

        await _commandExecutor
            .ExecuteAsync(new AddTaskCandidatesCommand(taskId, groups: groupIds), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RemoveTaskCandidateUserAsync(
        string taskId,
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await _commandExecutor
            .ExecuteAsync(new RemoveTaskCandidatesCommand(taskId, users: [userId]), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RemoveTaskCandidateGroupAsync(
        string taskId,
        string groupId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        await _commandExecutor
            .ExecuteAsync(new RemoveTaskCandidatesCommand(taskId, groups: [groupId]), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DelegateTaskAsync(string taskId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await _commandExecutor
            .ExecuteAsync(new DelegateTaskCommand(taskId, userId), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ResolveTaskAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await _commandExecutor
            .ExecuteAsync(new ResolveTaskCommand(taskId), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteProcessInstanceAsync(
        string processInstanceId,
        string? deleteReason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await _commandExecutor
            .ExecuteAsync(new DeleteProcessInstanceCommand(processInstanceId, deleteReason), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MoveActivityToAsync(
        string processInstanceId,
        string targetActivityId,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActivityId);

        await _commandExecutor
            .ExecuteAsync(new MoveActivityCommand(processInstanceId, targetActivityId, reason), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, object?>> GetVariablesAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        // 变量落在流程实例执行上；GetVariablesAsync 会先懒加载再沿作用域链合并
        var processInstance = await FindProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);
        var variables = await processInstance.GetVariablesAsync(ct).ConfigureAwait(false);

        return new Dictionary<string, object?>(variables, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveVariableAsync(string processInstanceId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _commandExecutor.ExecuteAsync(new DeleteVariableCommand(processInstanceId, name), ct);
    }

    /// <inheritdoc />
    public ValueTask SetVariableAsync(
        string processInstanceId,
        string name,
        object? value,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return SetVariablesAsync(
            processInstanceId,
            new Dictionary<string, object?>(StringComparer.Ordinal) { [name] = value },
            ct);
    }

    /// <inheritdoc />
    public async ValueTask SetVariablesAsync(
        string processInstanceId,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentNullException.ThrowIfNull(variables);

        if (variables.Count == 0)
        {
            return;
        }

        await _commandExecutor
            .ExecuteAsync(new SetVariablesCommand(processInstanceId, variables), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SetTaskVariableAsync(
        string taskId,
        string name,
        object? value,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _commandExecutor
            .ExecuteAsync(new SetTaskVariableCommand(taskId, name, value), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<object?> GetTaskVariableAsync(string taskId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var task = await _taskManager.FindTaskByIdAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new EngineException($"任务 '{taskId}' 不存在（可能已完成或流程已推进）。");

        return await _taskManager.GetTaskVariableAsync(taskId, name, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, object?>> GetTaskVariablesAsync(
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var task = await _taskManager.FindTaskByIdAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new EngineException($"任务 '{taskId}' 不存在（可能已完成或流程已推进）。");

        var variables = await _taskManager.GetTaskVariablesAsync(taskId, ct).ConfigureAwait(false);

        return new Dictionary<string, object?>(variables, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveTaskVariableAsync(string taskId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _commandExecutor.ExecuteAsync(new RemoveTaskVariableCommand(taskId, name), ct);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HistoricActivityInstance>> FindHistoricActivitiesAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return _historyManager.FindActivityInstancesAsync(processInstanceId, ct);
    }

    /// <inheritdoc />
    public ValueTask<HistoricProcessInstance?> FindHistoricProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return _historyManager.FindProcessInstanceAsync(processInstanceId, ct);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HistoricTaskInstance>> FindHistoricTasksAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return _historyManager.FindTaskInstancesAsync(processInstanceId, ct);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricTaskInstance>> FindHistoricTasksAsync(
        HistoricTaskQuery query,
        CancellationToken ct = default)
    {
        var tasks = await CollectHistoricTasksAsync(query, ct).ConfigureAwait(false);

        return [.. ApplyPaging(tasks, query.FirstResult, query.MaxResults, task => task.StartTime, task => task.Id)];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountHistoricTasksAsync(
        HistoricTaskQuery query,
        CancellationToken ct = default)
    {
        var tasks = await CollectHistoricTasksAsync(query, ct).ConfigureAwait(false);

        return tasks.Count;
    }

    /// <summary>
    /// 取历史任务候选集：先用历史管理器按实例取回，再在内存里做办理人 / 已办过滤
    /// （与待办查询同口径 —— 本子集不给历史表做任意组合 SQL）。
    /// </summary>
    private async ValueTask<IReadOnlyList<HistoricTaskInstance>> CollectHistoricTasksAsync(
        HistoricTaskQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);

        if (!query.HasCriteria)
        {
            throw new EngineException(
                "历史任务查询至少需要一个条件（processInstanceId / assignee / processDefinitionId / taskDefinitionKey）。"
                + "本子集不提供无条件的历史全表扫描。");
        }

        // 按实例查是收窄查询；只给办理人时走跨实例（「我的已办」）
        var tasks = string.IsNullOrWhiteSpace(query.ProcessInstanceId)
            ? await _historyManager.FindAllTaskInstancesAsync(ct).ConfigureAwait(false)
            : await _historyManager.FindTaskInstancesAsync(query.ProcessInstanceId, ct).ConfigureAwait(false);

        return
        [
            .. tasks.Where(task => Matches(task, query)),
        ];
    }

    private static bool Matches(HistoricTaskInstance task, HistoricTaskQuery query) =>
        (string.IsNullOrWhiteSpace(query.Assignee)
            || string.Equals(task.Assignee, query.Assignee, StringComparison.Ordinal))
        && (query.TenantId is null
            || string.Equals(task.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal))
        && (!query.OnlyFinished || task.EndTime is not null)
        && (string.IsNullOrWhiteSpace(query.ProcessDefinitionId)
            || string.Equals(task.ProcessDefinitionId, query.ProcessDefinitionId, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(query.TaskDefinitionKey)
            || string.Equals(task.TaskDefinitionKey, query.TaskDefinitionKey, StringComparison.Ordinal))
        && (query.FinishedAfter is null
            || (task.EndTime is { } finishedAfter && finishedAfter >= query.FinishedAfter))
        && (query.FinishedBefore is null
            || (task.EndTime is { } finishedBefore && finishedBefore <= query.FinishedBefore));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricProcessInstance>> FindHistoricProcessInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct = default)
    {
        var instances = await CollectHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false);

        return
        [
            .. ApplyPaging(
                instances,
                query.FirstResult,
                query.MaxResults,
                instance => instance.StartTime,
                instance => instance.Id,
                descending: true),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountHistoricProcessInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct = default) =>
        (await CollectHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false)).Count;

    private async ValueTask<IReadOnlyList<HistoricProcessInstance>> CollectHistoricProcessInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);

        var instances = await _historyManager.FindProcessInstancesAsync(ct).ConfigureAwait(false);

        HashSet<string>? definitionIds = null;
        if (!string.IsNullOrWhiteSpace(query.ProcessDefinitionKey))
        {
            definitionIds = await ResolveDefinitionIdsByKeyAsync(query.ProcessDefinitionKey, query.TenantId, ct)
                .ConfigureAwait(false);
        }

        return
        [
            .. instances.Where(instance => Matches(instance, query, definitionIds)),
        ];
    }

    private static bool Matches(
        HistoricProcessInstance instance,
        HistoricProcessInstanceQuery query,
        HashSet<string>? definitionIds) =>
        (string.IsNullOrWhiteSpace(query.ProcessDefinitionId)
            || string.Equals(instance.ProcessDefinitionId, query.ProcessDefinitionId, StringComparison.Ordinal))
        && (definitionIds is null || definitionIds.Contains(instance.ProcessDefinitionId))
        && (string.IsNullOrWhiteSpace(query.BusinessKey)
            || string.Equals(instance.BusinessKey, query.BusinessKey, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(query.StartUserId)
            || string.Equals(instance.StartUserId, query.StartUserId, StringComparison.Ordinal))
        && (query.TenantId is null
            || string.Equals(instance.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal))
        && (query.Finished is null || instance.IsRunning != query.Finished.Value)
        && (query.Deleted is null
            || (instance.DeleteReason is not null) == query.Deleted.Value)
        && (query.StartedAfter is null
            || (instance.StartTime is { } startedAfter && startedAfter >= query.StartedAfter))
        && (query.StartedBefore is null
            || (instance.StartTime is { } startedBefore && startedBefore <= query.StartedBefore))
        && (query.EndedAfter is null
            || (instance.EndTime is { } endedAfter && endedAfter >= query.EndedAfter))
        && (query.EndedBefore is null
            || (instance.EndTime is { } endedBefore && endedBefore <= query.EndedBefore));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HistoricVariableInstance>> FindHistoricVariablesAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return _historyManager.FindVariableInstancesAsync(processInstanceId, ct);
    }

    /// <inheritdoc />
    public async ValueTask<DiagramData?> GetDiagramAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        // 运行中的实例走执行树；已结束的实例运行时行已被删除，回退到历史
        var execution = await _executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);
        var historic = execution is null
            ? await _historyManager.FindProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false)
            : null;

        var processDefinitionId = execution?.ProcessDefinitionId ?? historic?.ProcessDefinitionId;

        if (processDefinitionId is null)
        {
            return null;
        }

        var activities = await _historyManager.FindActivityInstancesAsync(processInstanceId, ct).ConfigureAwait(false);
        var bpmnXml = await _definitionStore.GetResourceXmlAsync(processDefinitionId, ct).ConfigureAwait(false);

        return new DiagramData(
            processInstanceId,
            processDefinitionId,
            bpmnXml,
            IsRunning: execution is not null || (historic?.IsRunning ?? false),
            activities);
    }

    private async ValueTask<IExecutionEntity> FindProcessInstanceAsync(string processInstanceId, CancellationToken ct) =>
        await _executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false)
        ?? throw new EngineException($"流程实例 '{processInstanceId}' 不存在（可能已结束）。");
}

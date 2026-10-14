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
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bpmnXml);

        var bpmnModel = _xmlConverter.ConvertToBpmnModel(bpmnXml);

        var process = bpmnModel.GetMainProcess()
            ?? throw new NetBpm.Engine.EngineException("部署失败：BPMN 文件中没有流程定义。");

        // 前端只读渲染用的即时校验不可信边界，后端必须复校（§6.2 第 4 项）
        _validator.ValidateAndThrow(process);

        // 落库必须在一个命令（= 一个事务）内完成，避免中途失败留下孤儿部署行
        var command = new DeployProcessCommand(_definitionStore, bpmnModel, resourceName, deploymentName);
        return await _commandExecutor.ExecuteAsync(command, ct).ConfigureAwait(false);
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

        var command = new StartProcessInstanceCommand(process, processDefinitionId, businessKey, startUserId, variables);
        return await _commandExecutor.ExecuteAsync(command, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<string?> GetProcessDefinitionXmlAsync(string processDefinitionId, CancellationToken ct = default) =>
        _definitionStore.GetResourceXmlAsync(processDefinitionId, ct);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindProcessDefinitionsAsync(CancellationToken ct = default) =>
        _commandExecutor.ExecuteAsync(new FindProcessDefinitionsCommand(_definitionStore), ct);

    /// <inheritdoc />
    public ValueTask<BpmnModel?> GetBpmnModelAsync(string processDefinitionId, CancellationToken ct = default) =>
        _definitionStore.GetBpmnModelAsync(processDefinitionId, ct);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TaskEntity>> FindTasksAsync(
        TaskQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.HasCriteria)
        {
            throw new EngineException(
                "任务查询至少需要一个条件（assignee / candidateUser / processInstanceId）。" +
                "本子集不提供「查全部任务」，避免全表扫描。");
        }

        if (!string.IsNullOrWhiteSpace(query.ProcessInstanceId))
        {
            var tasks = await _taskManager
                .FindTasksByProcessInstanceIdAsync(query.ProcessInstanceId, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(query.CandidateUser))
            {
                throw new EngineException(
                    "「按流程实例 + 候选人」的组合查询尚未实现：请分别用 processInstanceId 或 candidateUser 查询。"
                    + "（候选人要展开用户所属组，组合语义需要额外确认，不猜。）");
            }

            // 办理人条件在内存求交（本子集不给任务表做任意组合 SQL）
            return
            [
                .. tasks.Where(task => string.IsNullOrWhiteSpace(query.Assignee)
                    || string.Equals(task.Assignee, query.Assignee, StringComparison.Ordinal)),
            ];
        }

        if (!string.IsNullOrWhiteSpace(query.Assignee))
        {
            var tasks = await _taskManager.FindTasksByAssigneeAsync(query.Assignee, ct).ConfigureAwait(false);
            return [.. tasks];
        }

        // 候选组需要展开用户所属组，否则候选组任务查不出来
        var groupIds = await _userGroupManager
            .FindGroupIdsByUserAsync(query.CandidateUser!, ct)
            .ConfigureAwait(false);

        var candidateTasks = await _taskManager
            .FindTasksByCandidateUserAsync(query.CandidateUser!, groupIds, ct)
            .ConfigureAwait(false);

        return [.. candidateTasks];
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
    public async ValueTask ClaimTaskAsync(string taskId, string? userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await _commandExecutor
            .ExecuteAsync(new ClaimTaskCommand(taskId, userId), ct)
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

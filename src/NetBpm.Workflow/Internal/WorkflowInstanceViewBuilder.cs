using NetBpm.Engine.Execution;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 组装「流程环节信息」视图（运行中 / 已结束两种形态统一出口）。
/// </summary>
/// <remarks>
/// 内部类型：宿主只该看到 <see cref="WorkflowInstanceView"/>，不该关心它是查出来的还是拼出来的。
/// </remarks>
internal sealed class WorkflowInstanceViewBuilder
{
    private readonly IProcessEngine _engine;
    private readonly IExecutionManager _executionManager;
    private readonly IWorkflowDataStore _store;

    internal WorkflowInstanceViewBuilder(
        IProcessEngine engine,
        IExecutionManager executionManager,
        IWorkflowDataStore store)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(executionManager);
        ArgumentNullException.ThrowIfNull(store);

        _engine = engine;
        _executionManager = executionManager;
        _store = store;
    }

    /// <summary>组装实例视图；引擎与业务侧都查不到时返回 <see langword="null"/>。</summary>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="ct">取消标记。</param>
    internal async ValueTask<WorkflowInstanceView?> BuildAsync(string processInstanceId, CancellationToken ct)
    {
        var execution = await _executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);
        var result = await _store.FindResultAsync(processInstanceId, ct).ConfigureAwait(false);
        var record = await _store.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);
        var parameters = await _store.FindParametersAsync(processInstanceId, ct).ConfigureAwait(false);

        if (execution is null)
        {
            if (result is null && record is null)
            {
                return null;
            }

            // 已结束：运行态行已删除，只剩业务数据。
            // 此时拿不到流程定义 id（要它请走 FindHistoryAsync —— 历史表里才有），故留空串。
            var finished = record?.Base ?? result?.Base ?? BaseModel.Empty;

            return new WorkflowInstanceView(
                ProcessInstanceId: processInstanceId,
                BusinessKey: finished.BusinessId,
                ProcessDefinitionId: string.Empty,
                Running: false,
                Variables: new Dictionary<string, object?>(StringComparer.Ordinal),
                Tasks: [],
                Result: result,
                StartedAt: finished.ApplyTime,
                Base: finished,
                Parameters: parameters);
        }

        var variables = await execution.GetVariablesAsync(ct).ConfigureAwait(false);
        var snapshot = new Dictionary<string, object?>(variables, StringComparer.Ordinal);
        var tasks = await _engine
            .FindTasksAsync(new TaskQuery(ProcessInstanceId: processInstanceId), ct)
            .ConfigureAwait(false);

        var views = new List<WorkflowTaskView>(tasks.Count);

        foreach (var task in tasks)
        {
            views.Add(WorkflowFormMapper.ToView(
                task,
                await WorkflowFormMapper.FindUserTaskAsync(_engine, task, ct).ConfigureAwait(false)));
        }

        return new WorkflowInstanceView(
            ProcessInstanceId: processInstanceId,
            BusinessKey: execution.BusinessKey,
            ProcessDefinitionId: execution.ProcessDefinitionId ?? string.Empty,
            Running: true,
            Variables: snapshot,
            Tasks: views,
            Result: result,
            StartedAt: record?.Base.ApplyTime,
            Base: BaseModel.FromVariables(snapshot, record?.Base),
            Parameters: parameters);
    }
}

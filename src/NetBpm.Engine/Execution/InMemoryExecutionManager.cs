using NetBpm.Bpmn.Model;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Execution;

/// <summary>
/// 内存版 <see cref="IExecutionManager"/>。
/// <para>
/// 用途：单元测试与「无数据库宿主」（POC 脚手架）。生产必须使用 <c>NetBpm.Persistence</c>
/// 中的 Dapper 实现（P5），否则失去 §9.2 的库级对拍能力。
/// </para>
/// <para>
/// ⚠️ <b>已验证的 Activiti 事实（与开发计划 §5.1 的表述有出入，需记录为计划修正）</b>：
/// 在 Activiti 9.0.0 中 <c>IS_CONCURRENT_</c> 对应的 setter（<c>setConcurrent</c>）在
/// <c>activiti-engine</c> 内<b>从未被调用</b>（全仓库仅存在于 <c>ExecutionEntityImpl</c> 与
/// <c>DelegateExecution</c> 的定义处），字段默认 <c>false</c>。因此：
/// <list type="bullet">
///   <item><description>分支产生的子执行 <b>不会</b> 被置为 <c>IS_CONCURRENT_ = 1</c>；</description></item>
///   <item><description>本实现刻意<b>不</b>设置该标志，以保证与 Activiti 的库级对拍一致（§9.2）；</description></item>
///   <item><description>计划 §5.1「分支产生的子执行 IsConcurrent=true」应据此更正，
///     否则 P10 对拍会在 <c>ACT_RU_EXECUTION.IS_CONCURRENT_</c> 列上全线出现系统性差异。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class InMemoryExecutionManager : IExecutionManager
{
    private readonly IIdGenerator _idGenerator;
    private readonly ITaskManager _taskManager;
    private readonly IHistoryManager _historyManager;
    private readonly Jobs.ITimerJobManager? _timerJobs;
    private readonly Jobs.IAsyncJobManager? _asyncJobs;
    private readonly Dictionary<string, IExecutionEntity> _executions = new(StringComparer.Ordinal);
    private readonly Lock _syncRoot = new();

    public InMemoryExecutionManager(
        IIdGenerator idGenerator,
        ITaskManager taskManager,
        IHistoryManager historyManager,
        Jobs.ITimerJobManager? timerJobs = null,
        Jobs.IAsyncJobManager? asyncJobs = null)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentNullException.ThrowIfNull(historyManager);

        _idGenerator = idGenerator;
        _taskManager = taskManager;
        _historyManager = historyManager;
        _timerJobs = timerJobs;
        _asyncJobs = asyncJobs;
    }

    /// <summary>当前执行数（诊断用）。</summary>
    public int ExecutionCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _executions.Count;
            }
        }
    }

    /// <summary>当前全部执行（诊断 / 测试用的快照）。</summary>
    public IReadOnlyList<IExecutionEntity> Executions
    {
        get
        {
            lock (_syncRoot)
            {
                return _executions.Values.ToList();
            }
        }
    }

    /// <inheritdoc />
    public IExecutionEntity CreateProcessInstanceExecution(
        Process process,
        string processDefinitionId,
        string? businessKey,
        string? startUserId,
        string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        var processInstance = new ExecutionEntity
        {
            Id = _idGenerator.NextId(),
            ProcessDefinitionId = processDefinitionId,
            BusinessKey = businessKey,
            StartUserId = startUserId,
            TenantId = tenantId,
            StartTime = DateTime.UtcNow,
            // Activiti：process instance is always a scope for all child executions
            IsScope = true,
            IsActive = true,
            IsConcurrent = false,
        };

        // Need to be after insert, cause we need the id
        processInstance.ProcessInstanceId = processInstance.Id;
        processInstance.RootProcessInstanceId = processInstance.Id;
        return processInstance;
    }

    /// <inheritdoc />
    public IExecutionEntity CreateChildExecution(IExecutionEntity parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var child = new ExecutionEntity
        {
            Id = _idGenerator.NextId(),
            ParentId = parent.Id,
            Parent = parent,
            ProcessInstanceId = parent.ProcessInstanceId ?? parent.Id,
            RootProcessInstanceId = parent.RootProcessInstanceId ?? parent.ProcessInstanceId ?? parent.Id,
            ProcessDefinitionId = parent.ProcessDefinitionId,
            TenantId = parent.TenantId,
            StartTime = DateTime.UtcNow,
            IsScope = false,
            IsActive = true,
            // 见类注释：Activiti 9 不设置 IS_CONCURRENT_
            IsConcurrent = false,
        };

        parent.Children.Add(child);
        return child;
    }

    /// <inheritdoc />
    public ValueTask InsertAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        lock (_syncRoot)
        {
            _executions[execution.Id] = execution;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask UpdateAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        lock (_syncRoot)
        {
            if (_executions.TryGetValue(execution.Id, out var existing) && !ReferenceEquals(existing, execution))
            {
                _executions[execution.Id] = execution;
            }

            execution.Revision++;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IExecutionEntity?> FindByIdAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult(_executions.GetValueOrDefault(executionId));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IExecutionEntity>> FindAllByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        lock (_syncRoot)
        {
            IReadOnlyList<IExecutionEntity> result =
            [
                .. _executions.Values
                    .Where(execution => string.Equals(
                        execution.ProcessInstanceId ?? execution.Id,
                        processInstanceId,
                        StringComparison.Ordinal))
                    .OrderBy(execution => execution.Id, StringComparer.Ordinal),
            ];

            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<IExecutionEntity>> FindInactiveExecutionsByActivityIdAndProcessInstanceIdAsync(
        string activityId,
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        lock (_syncRoot)
        {
            var result = _executions.Values
                .Where(execution => !execution.IsActive
                    && string.Equals(execution.CurrentActivityId, activityId, StringComparison.Ordinal)
                    && string.Equals(execution.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .ToList();

            return ValueTask.FromResult<IList<IExecutionEntity>>(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CountActiveChildExecutionsForProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        lock (_syncRoot)
        {
            var count = _executions.Values.Count(execution =>
                execution.IsActive
                && !string.Equals(processInstanceId, execution.Id, StringComparison.Ordinal)
                && string.Equals(execution.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));

            return ValueTask.FromResult(count);
        }
    }

    /// <inheritdoc />
    public ValueTask<IList<IExecutionEntity>> FindActiveChildExecutionsByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        lock (_syncRoot)
        {
            var result = _executions.Values
                .Where(execution => execution.IsActive
                    && !string.Equals(execution.Id, processInstanceId, StringComparison.Ordinal)
                    && string.Equals(execution.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .ToList();

            return ValueTask.FromResult<IList<IExecutionEntity>>(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CountChildExecutionsForExecutionAsync(
        string parentExecutionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);

        lock (_syncRoot)
        {
            var count = _executions.Values.Count(execution =>
                string.Equals(execution.ParentId, parentExecutionId, StringComparison.Ordinal));

            return ValueTask.FromResult(count);
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteExecutionAndRelatedDataAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        // 与 Dapper 实现一致：先收口该执行上的历史活动行，再删执行（清理不发事件）
        await _historyManager.RecordActivityEndQuietlyAsync(execution, deleteReason, ct).ConfigureAwait(false);

        lock (_syncRoot)
        {
            DeleteRecursive(execution);
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteChildExecutionsAsync(
        IExecutionEntity parentExecution,
        string? deleteReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parentExecution);

        IExecutionEntity[] children;

        lock (_syncRoot)
        {
            children = [.. parentExecution.Children];
        }

        foreach (var child in children)
        {
            // 子执行删除：只收口历史行，不发 ACTIVITY_COMPLETED
            await _historyManager.RecordActivityEndQuietlyAsync(child, deleteReason, ct).ConfigureAwait(false);

            lock (_syncRoot)
            {
                DeleteRecursive(child);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteProcessInstanceExecutionEntityAsync(
        string processInstanceId,
        string? currentFlowElementId = null,
        string? deleteReason = null,
        bool cancel = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        // 与 Dapper 实现保持同一顺序：先清定时作业、任务与流程实例级身份关系，再删执行树
        if (_timerJobs is not null)
        {
            await _timerJobs.DeleteByProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);
        }

        if (_asyncJobs is not null)
        {
            await _asyncJobs.DeleteByProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);
        }

        await _taskManager
            .DeleteTasksByProcessInstanceAsync(processInstanceId, deleteReason, ct)
            .ConfigureAwait(false);

        List<IExecutionEntity> toDelete;

        lock (_syncRoot)
        {
            toDelete = _executions.Values
                .Where(execution => string.Equals(execution.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .ToList();

            foreach (var execution in toDelete)
            {
                _executions.Remove(execution.Id);
            }

            if (_executions.Remove(processInstanceId, out var root))
            {
                root.Children.Clear();
            }
        }

        foreach (var execution in toDelete)
        {
            // 实例结束的清理：只收口历史行，不发 ACTIVITY_COMPLETED
            await _historyManager.RecordActivityEndQuietlyAsync(execution, deleteReason, ct).ConfigureAwait(false);
        }

        await _historyManager
            .RecordProcessInstanceEndAsync(processInstanceId, deleteReason, currentFlowElementId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<int> UpdateSuspensionStateByProcessInstanceIdAsync(
        string processInstanceId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var state = suspended ? SuspendedState : ActiveState;

        lock (_syncRoot)
        {
            var executions = _executions.Values
                .Where(execution => string.Equals(execution.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .ToList();

            if (executions.Count == 0)
            {
                return ValueTask.FromResult(0);
            }

            foreach (var execution in executions)
            {
                execution.SuspensionState = state;
            }

            return ValueTask.FromResult(1);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> UpdateSuspensionStateByProcessDefinitionIdAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        var state = suspended ? SuspendedState : ActiveState;

        lock (_syncRoot)
        {
            // 根执行用来定位实例，但库里 SUSPENSION_STATE_ 是逐行存的，
            // 因此子执行也要一起改（否则重新加载子执行时会读到「激活」）
            var roots = _executions.Values
                .Where(execution => execution.ParentId is null
                    && string.Equals(execution.ProcessDefinitionId, processDefinitionId, StringComparison.Ordinal))
                .ToList();

            foreach (var root in roots)
            {
                foreach (var execution in _executions.Values
                    .Where(execution => string.Equals(execution.ProcessInstanceId, root.Id, StringComparison.Ordinal)))
                {
                    execution.SuspensionState = state;
                }
            }

            return ValueTask.FromResult(roots.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> FindProcessInstanceIdsByProcessDefinitionIdAsync(
        string processDefinitionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        lock (_syncRoot)
        {
            IReadOnlyList<string> ids =
            [
                .. _executions.Values
                    .Where(execution => execution.ParentId is null
                        && string.Equals(execution.ProcessDefinitionId, processDefinitionId, StringComparison.Ordinal))
                    .Select(execution => execution.Id),
            ];

            return ValueTask.FromResult(ids);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IExecutionEntity>> FindProcessInstancesAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            IReadOnlyList<IExecutionEntity> result =
            [
                .. _executions.Values.Where(execution => execution.ParentId is null),
            ];

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>与 <c>ACT_RU_EXECUTION.SUSPENSION_STATE_</c> 的取值一致（1 = 激活，2 = 挂起）。</summary>
    private const int ActiveState = 1;

    /// <summary>挂起态。</summary>
    private const int SuspendedState = 2;

    private void DeleteRecursive(IExecutionEntity execution)
    {
        foreach (var child in execution.Children.ToList())
        {
            DeleteRecursive(child);
        }

        execution.Children.Clear();
        execution.IsEnded = true;
        _executions.Remove(execution.Id);

        if (execution.Parent is not null)
        {
            execution.Parent.Children.Remove(execution);
        }
        else if (execution.ParentId is not null
            && _executions.TryGetValue(execution.ParentId, out var parent))
        {
            var match = parent.Children.FirstOrDefault(child => string.Equals(child.Id, execution.Id, StringComparison.Ordinal));
            if (match is not null)
            {
                parent.Children.Remove(match);
            }
        }
    }
}

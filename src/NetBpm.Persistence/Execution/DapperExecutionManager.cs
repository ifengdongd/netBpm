using System.Runtime.CompilerServices;
using Dapper;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Database;

// BPMN 模型里也有一个 Task 类型（对应 <task>），会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.Persistence.Execution;

/// <summary>
/// <c>ACT_RU_EXECUTION</c> 的 Dapper 实现（开发计划 P5）。SQL 逐条对应 Activiti 的
/// <c>db/mapping/entity/Execution.xml</c>。
/// <para>
/// 🔑 <b>四条必须照搬的机制</b>：
/// </para>
/// <list type="number">
///   <item><description>
///   <b>一级缓存 + 提交前 flush</b>：命令期间对执行树的内存改动（<c>ACT_ID_</c>、
///   <c>IS_ACTIVE_</c> …）在提交前统一写库；缓存与 DbSession 同生共死
///   （通过 <see cref="ConditionalWeakTable{TKey,TValue}"/> 挂在会话上）。
///   对应 <c>DbSqlSession</c> 的 entity cache。
///   </description></item>
///   <item><description>
///   <b>查询前先 flush</b>：Activiti 的 <c>DbSqlSession</c> 在执行任何查询前都会 flush。
///   否则并行网关 Join 的「把自己置为非活动 → 再查非活动执行」会查不到自己，
///   Join 永远无法达成期望值。这是风险 R4 的关键点。
///   </description></item>
///   <item><description>
///   <b>同一行只有一个实体实例</b>：<see cref="FindByIdAsync"/> 一律先查缓存，
///   否则同一行会出现两个对象、flush 时互相覆盖。
///   </description></item>
///   <item><description>
///   <b><c>REV_</c> 乐观锁</b>：UPDATE / DELETE 都带 <c>and REV_ = @revision</c>，
///   影响行数不为 1 即抛 <see cref="OptimisticLockingException"/>。
///   </description></item>
/// </list>
/// <para>
/// 与 Activiti 的一处实现差异（不产生可见差异）：查询语句省略了
/// <c>LEFT OUTER JOIN ACT_RU_EXECUTION S ON E.SUPER_EXEC_ = S.ID_</c>。
/// 该 JOIN 只为取「父流程实例 id」（调用活动的场景）而存在，本子集不支持调用活动，
/// 故省略；选取的列与行内容完全一致。
/// </para>
/// </summary>
public sealed class DapperExecutionManager : IExecutionManager, IExecutionQueryManager, IDbFlushParticipant
{
    private readonly IDbSessionProvider _sessions;
    private readonly IIdGenerator _idGenerator;
    private readonly ITaskManager _taskManager;
    private readonly IVariableInstanceManager _variableInstances;
    private readonly IHistoryManager _historyManager;
    private readonly ITimerJobManager? _timerJobs;
    private readonly IAsyncJobManager? _asyncJobs;
    private readonly ConditionalWeakTable<IDbSession, SessionCache> _caches = new();

    public DapperExecutionManager(
        IDbSessionProvider sessions,
        IIdGenerator idGenerator,
        ITaskManager taskManager,
        IVariableInstanceManager variableInstances,
        IHistoryManager historyManager,
        ITimerJobManager? timerJobs = null,
        IAsyncJobManager? asyncJobs = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentNullException.ThrowIfNull(variableInstances);
        ArgumentNullException.ThrowIfNull(historyManager);

        _sessions = sessions;
        _idGenerator = idGenerator;
        _taskManager = taskManager;
        _variableInstances = variableInstances;
        _historyManager = historyManager;
        _timerJobs = timerJobs;
        _asyncJobs = asyncJobs;
    }

    /// <summary>
    /// 给执行装上持久化钩子：
    /// <list type="bullet">
    ///   <item><description><c>VariableInstanceManager</c> —— 变量跨命令读回（<c>ACT_RU_VARIABLE</c>）；</description></item>
    ///   <item><description><c>ParentLoader</c> —— 父执行懒加载，变量作用域上溯必需。</description></item>
    /// </list>
    /// </summary>
    private ExecutionEntity Wire(ExecutionEntity execution)
    {
        execution.VariableInstanceManager = _variableInstances;
        execution.ParentLoader = this;

        return execution;
    }

    // ─────────────────────────── 创建 ───────────────────────────

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

        var id = _idGenerator.NextId();

        var processInstance = new ExecutionEntity
        {
            Id = id,
            // REV_ == 0 表示"尚未 INSERT"（对应 Activiti 的 revision == 0）
            Revision = 0,
            ProcessInstanceId = id,
            RootProcessInstanceId = id,
            ProcessDefinitionId = processDefinitionId,
            BusinessKey = businessKey,
            StartUserId = startUserId,
            TenantId = tenantId,
            StartTime = DateTime.UtcNow,
            IsScope = true,
            IsActive = true,
            IsConcurrent = false,
            SuspensionState = 1,
        };

        Track(processInstance);
        return Wire(processInstance);
    }

    /// <inheritdoc />
    public IExecutionEntity CreateChildExecution(IExecutionEntity parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var child = new ExecutionEntity
        {
            Id = _idGenerator.NextId(),
            Revision = 0,
            ParentId = parent.Id,
            Parent = parent,
            ProcessInstanceId = parent.ProcessInstanceId ?? parent.Id,
            RootProcessInstanceId = parent.RootProcessInstanceId ?? parent.ProcessInstanceId ?? parent.Id,
            ProcessDefinitionId = parent.ProcessDefinitionId,
            TenantId = parent.TenantId,
            StartTime = DateTime.UtcNow,
            IsScope = false,
            IsActive = true,
            IsConcurrent = false,
            SuspensionState = 1,
        };

        parent.Children.Add(child);

        Track(child);
        return Wire(child);
    }

    // ─────────────────────────── 写 ───────────────────────────

    /// <inheritdoc />
    public async ValueTask InsertAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        using var lease = _sessions.Acquire();
        await InsertInternalAsync(lease.Session, GetCache(lease.Session), execution, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 强制写库并令 <c>REV_</c> 自增 —— 对应 Activiti 的 <c>forceUpdate()</c>。
    /// 并行网关 Join 用它锁定父作用域行（见 <c>GatewayActivityBehavior.lockFirstParentScope</c>）。
    /// </remarks>
    public async ValueTask UpdateAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        if (cache.Deleted.Contains(execution.Id))
        {
            return;
        }

        if (!cache.InDatabase.Contains(execution.Id))
        {
            await InsertInternalAsync(session, cache, execution, ct).ConfigureAwait(false);
            return;
        }

        var baseline = cache.Persisted[execution.Id];

        var affected = await ExecuteAsync(
            session,
            UpdateSql,
            BuildParameters(execution, baseline.Revision + 1, baseline.Revision),
            ct).ConfigureAwait(false);

        if (affected != 1)
        {
            throw OptimisticLockFailed(execution, baseline.Revision);
        }

        execution.Revision = baseline.Revision + 1;
        cache.Persisted[execution.Id] = ExecutionState.From(execution);
    }

    // ─────────────────────────── 读 ───────────────────────────

    /// <inheritdoc />
    public async ValueTask<IExecutionEntity?> FindByIdAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        if (cache.Deleted.Contains(executionId))
        {
            return null;
        }

        if (cache.Entities.TryGetValue(executionId, out var cached))
        {
            return cached;
        }

        await FlushAsync(session, ct).ConfigureAwait(false);

        var states = await QueryStatesAsync(session, SelectByIdSql, new { executionId }, ct).ConfigureAwait(false);

        return states.Count == 0 ? null : Materialize(cache, states[0]);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IExecutionEntity>> FindAllByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        await FlushAsync(session, ct).ConfigureAwait(false);

        var states = await QueryStatesAsync(
            session,
            SelectByProcessInstanceSql,
            new { processInstanceId },
            ct).ConfigureAwait(false);

        return [.. states.Select(state => (IExecutionEntity)Materialize(cache, state))];
    }

    /// <inheritdoc />
    public async ValueTask<IList<IExecutionEntity>> FindInactiveExecutionsByActivityIdAndProcessInstanceIdAsync(
        string activityId,
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        await FlushAsync(session, ct).ConfigureAwait(false);

        var states = await QueryStatesAsync(
            session,
            SelectInactiveInActivitySql,
            new { activityId, processInstanceId },
            ct).ConfigureAwait(false);

        return states.Select(state => (IExecutionEntity)Materialize(cache, state)).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IList<IExecutionEntity>> FindActiveChildExecutionsByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        await FlushAsync(session, ct).ConfigureAwait(false);

        var states = await QueryStatesAsync(
            session,
            SelectChildExecutionsOfProcessInstanceSql,
            new { processInstanceId },
            ct).ConfigureAwait(false);

        var result = new List<IExecutionEntity>();

        foreach (var state in states)
        {
            if (state.IsActive && !string.Equals(state.Id, processInstanceId, StringComparison.Ordinal))
            {
                result.Add(Materialize(cache, state));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<int> CountActiveChildExecutionsForProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 先 flush：本命令内新建/激活的执行必须计入
        await FlushAsync(session, ct).ConfigureAwait(false);

        // 对应 getNumberOfActiveChildExecutionsForProcessInstance。
        // 只取计数，不再把整棵执行树查回来物化 —— Join / 结束判定每次都会调用它。
        return await session.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                CountActiveChildrenOfProcessInstanceSql,
                new { processInstanceId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> CountChildExecutionsForExecutionAsync(
        string parentExecutionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        await FlushAsync(session, ct).ConfigureAwait(false);

        // 对应 findChildExecutionsByParentExecutionId + 过滤 BoundaryEvent（子集无边界事件）。
        // 只取计数，避免为了一次判断把所有子执行读进内存。
        return await session.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                CountChildrenSql,
                new { parentExecutionId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    // ─────────────────────────── 删 ───────────────────────────

    /// <inheritdoc />
    public async ValueTask DeleteExecutionAndRelatedDataAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 对应 deleteExecutionAndRelatedData：先收口该执行上的历史活动行，再删执行。
        // 删除清理只收口历史行，不发 ACTIVITY_COMPLETED（与 Activiti 一致：清理不分发事件）
        await _historyManager.RecordActivityEndQuietlyAsync(execution, deleteReason, ct).ConfigureAwait(false);

        await DeleteRecursiveAsync(session, GetCache(session), execution, deleteReason, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteChildExecutionsAsync(
        IExecutionEntity parentExecution,
        string? deleteReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parentExecution);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        await FlushAsync(session, ct).ConfigureAwait(false);

        // 以库为准查子执行（内存里的 Children 可能只是部分视图）
        var children = await QueryStatesAsync(
            session,
            SelectChildrenSql,
            new { parentExecutionId = parentExecution.Id },
            ct).ConfigureAwait(false);

        foreach (var child in children)
        {
            await DeleteRecursiveAsync(session, cache, Materialize(cache, child), deleteReason, ct).ConfigureAwait(false);
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

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        await FlushAsync(session, ct).ConfigureAwait(false);

        // 定时作业：实例结束/终止/被删除后必须一并清掉，否则轮询会一直对着已消失的执行空转
        if (_timerJobs is not null)
        {
            await _timerJobs.DeleteByProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);
        }

        // 异步作业同理：实例结束后作业必须消失，否则 JobExecutor 会一直捞到陈旧作业
        if (_asyncJobs is not null)
        {
            await _asyncJobs.DeleteByProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);
        }

        // 流程实例级联清理：先摘掉挂在流程实例上的外部引用（这些外键在 DDL 里没有级联删除）
        // 任务与流程实例级身份关系的清理交给任务管理器（对应 TaskEntityManagerImpl.deleteTasksByProcessInstanceId）
        await _taskManager
            .DeleteTasksByProcessInstanceAsync(processInstanceId, deleteReason, ct)
            .ConfigureAwait(false);

        // 变量行 → 字节行（ACT_FK_VAR_BYTEARRAY 为 RESTRICT，先取 id 再删）
        await DeleteVariablesAndByteArraysAsync(
            session,
            CaptureByteArrayIdsByProcessInstanceSql,
            DeleteVariablesByProcessInstanceSql,
            new { processInstanceId },
            ct).ConfigureAwait(false);

        // 先删子执行（深层在前），再删根执行 —— 满足 ACT_FK_EXE_PARENT
        var children = await QueryStatesAsync(
            session,
            SelectChildExecutionsOfProcessInstanceSql,
            new { processInstanceId },
            ct).ConfigureAwait(false);

        foreach (var state in children)
        {
            await DeleteRecursiveAsync(session, cache, Materialize(cache, state), deleteReason, ct).ConfigureAwait(false);
        }

        var rootStates = await QueryStatesAsync(
            session,
            SelectByIdSql,
            new { executionId = processInstanceId },
            ct).ConfigureAwait(false);

        if (rootStates.Count > 0)
        {
            await DeleteRecursiveAsync(session, cache, Materialize(cache, rootStates[0]), deleteReason, ct)
                .ConfigureAwait(false);
        }

        // 对应 deleteProcessInstanceExecutionEntity 的最后一步：历史流程实例收口
        // （END_ACT_ID_ 取传入的 currentFlowElementId，DELETE_REASON_ 正常结束为 null）
        await _historyManager
            .RecordProcessInstanceEndAsync(processInstanceId, deleteReason, currentFlowElementId, ct)
            .ConfigureAwait(false);
    }

    // ─────────────────────────── 挂起 / 激活 ───────────────────────────

    /// <inheritdoc />
    public async ValueTask<int> UpdateSuspensionStateByProcessInstanceIdAsync(
        string processInstanceId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 先落库会话内待写的变化：否则 UPDATE 之后 flush 会用实体上的旧状态覆盖回去
        await FlushAsync(session, ct).ConfigureAwait(false);

        var state = suspended ? SuspendedState : ActiveState;

        var affected = await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateSuspensionStateByProcessInstanceIdSql,
                new { processInstanceId, state },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (affected == 0)
        {
            return 0;
        }

        // 同步一级缓存里的实体与脏检查基线，避免同一命令内后续 flush 把旧状态写回去
        SyncSuspensionState(GetCache(session), state, execution =>
            string.Equals(execution.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));

        return 1;
    }

    /// <inheritdoc />
    public async ValueTask<int> UpdateSuspensionStateByProcessDefinitionIdAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        await FlushAsync(session, ct).ConfigureAwait(false);

        var state = suspended ? SuspendedState : ActiveState;

        // 实例数 = 根执行数（子执行是同一实例的一部分，不能重复计数）
        var instanceIds = await QueryStringListAsync(
            session,
            SelectProcessInstanceIdsByProcessDefinitionIdSql,
            new { processDefinitionId },
            ct).ConfigureAwait(false);

        if (instanceIds.Count == 0)
        {
            return 0;
        }

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateSuspensionStateByProcessDefinitionIdSql,
                new { processDefinitionId, state },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        SyncSuspensionState(GetCache(session), state, execution =>
            string.Equals(execution.ProcessDefinitionId, processDefinitionId, StringComparison.Ordinal));

        return instanceIds.Count;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> FindProcessInstanceIdsByProcessDefinitionIdAsync(
        string processDefinitionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var ids = await QueryStringListAsync(
            session,
            SelectProcessInstanceIdsByProcessDefinitionIdSql,
            new { processDefinitionId },
            ct).ConfigureAwait(false);

        return [.. ids];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IExecutionEntity>> FindProcessInstancesAsync(
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        // 与其它查询一致：先 flush，保证本命令内新建的实例也能被列出
        await FlushAsync(session, ct).ConfigureAwait(false);

        var states = await QueryStatesAsync(session, SelectProcessInstancesSql, null, ct).ConfigureAwait(false);

        return states.Select(state => (IExecutionEntity)Materialize(cache, state)).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IExecutionEntity>> QueryProcessInstancesAsync(
        ProcessInstanceFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var cache = GetCache(session);

        await FlushAsync(session, ct).ConfigureAwait(false);

        var parameters = new DynamicParameters();
        var where = BuildProcessInstanceWhere(filter, parameters);
        var direction = filter.Descending ? "desc" : "asc";
        var paging = BuildPaging(session.DatabaseType, filter.FirstResult, filter.MaxResults, parameters);

        var sql = $"""
            select {ExecutionColumns.All}
            from ACT_RU_EXECUTION
            {where}
            order by START_TIME_ {direction}, ID_ {direction}
            {paging}
            """;

        var states = await QueryStatesAsync(session, sql, parameters, ct).ConfigureAwait(false);

        return states.Select(state => (IExecutionEntity)Materialize(cache, state)).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<int> CountProcessInstancesAsync(
        ProcessInstanceFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        await FlushAsync(session, ct).ConfigureAwait(false);

        var parameters = new DynamicParameters();
        var where = BuildProcessInstanceWhere(filter, parameters);

        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_RU_EXECUTION {where}",
            parameters,
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static string BuildProcessInstanceWhere(ProcessInstanceFilter filter, DynamicParameters parameters)
    {
        var conditions = new List<string> { "PARENT_ID_ is null" };

        if (!string.IsNullOrWhiteSpace(filter.ProcessDefinitionId))
        {
            conditions.Add("PROC_DEF_ID_ = @processDefinitionId");
            parameters.Add("processDefinitionId", filter.ProcessDefinitionId);
        }

        if (filter.ProcessDefinitionIds is { Count: > 0 })
        {
            conditions.Add("PROC_DEF_ID_ in @definitionIds");
            parameters.Add("definitionIds", filter.ProcessDefinitionIds);
        }

        if (!string.IsNullOrWhiteSpace(filter.BusinessKey))
        {
            conditions.Add("BUSINESS_KEY_ = @businessKey");
            parameters.Add("businessKey", filter.BusinessKey);
        }

        if (!string.IsNullOrWhiteSpace(filter.StartUserId))
        {
            conditions.Add("START_USER_ID_ = @startUserId");
            parameters.Add("startUserId", filter.StartUserId);
        }

        if (filter.TenantId is not null)
        {
            conditions.Add("coalesce(TENANT_ID_, '') = @tenantId");
            parameters.Add("tenantId", filter.TenantId);
        }

        if (filter.IsActive is { } isActive)
        {
            conditions.Add("IS_ACTIVE_ = @isActive");
            parameters.Add("isActive", isActive ? 1 : 0);
        }

        if (filter.StartedAfter is { } startedAfter)
        {
            conditions.Add("START_TIME_ >= @startedAfter");
            parameters.Add("startedAfter", startedAfter);
        }

        if (filter.StartedBefore is { } startedBefore)
        {
            conditions.Add("START_TIME_ <= @startedBefore");
            parameters.Add("startedBefore", startedBefore);
        }

        return " where " + string.Join(" and ", conditions);
    }

    /// <summary>方言分页子句（空 <paramref name="take"/> 表示调用方显式要求不限条数）。</summary>
    private static string BuildPaging(
        DatabaseType databaseType,
        int firstResult,
        int? take,
        DynamicParameters parameters)
    {
        var skip = Math.Max(0, firstResult);

        if (take is null)
        {
            if (skip == 0)
            {
                return string.Empty;
            }

            parameters.Add("skip", skip);
            return databaseType == DatabaseType.MySql
                ? " limit 100000 offset @skip"
                : " offset @skip rows fetch next 100000 rows only";
        }

        parameters.Add("skip", skip);
        parameters.Add("take", Math.Max(1, take.Value));

        return databaseType == DatabaseType.MySql
            ? " limit @take offset @skip"
            : " offset @skip rows fetch next @take rows only";
    }

    /// <summary>把会话内已跟踪实体的挂起状态与脏检查基线一起改写。</summary>
    private static void SyncSuspensionState(
        SessionCache cache,
        int state,
        Func<IExecutionEntity, bool> predicate)
    {
        foreach (var execution in cache.Entities.Values.Where(predicate))
        {
            execution.SuspensionState = state;

            if (cache.Persisted.TryGetValue(execution.Id, out var persisted))
            {
                cache.Persisted[execution.Id] = persisted with { SuspensionState = state };
            }
        }
    }

    private static async Task<List<string>> QueryStringListAsync(
        IDbSession session,
        string sql,
        object parameters,
        CancellationToken ct)
    {
        var rows = await session.Connection
            .QueryAsync<string>(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        return [.. rows];
    }

    // ─────────────────────────── flush ───────────────────────────

    /// <inheritdoc />
    public async ValueTask FlushAsync(IDbSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var cache = GetCache(session);

        if (cache.Entities.Count == 0)
        {
            return;
        }

        await InsertPendingAsync(session, cache, ct).ConfigureAwait(false);

        // 快照一份 key 列表：flush 过程中会修改缓存
        foreach (var execution in cache.Entities.Values.ToList())
        {
            if (cache.Deleted.Contains(execution.Id) || !cache.InDatabase.Contains(execution.Id))
            {
                continue;
            }

            var baseline = cache.Persisted[execution.Id];
            var current = ExecutionState.From(execution);

            if (current == baseline)
            {
                // 只写脏实体（Activiti 的实体缓存同样如此）
                continue;
            }

            var affected = await ExecuteAsync(
                session,
                UpdateSql,
                BuildParameters(execution, baseline.Revision + 1, baseline.Revision),
                ct).ConfigureAwait(false);

            if (affected != 1)
            {
                throw OptimisticLockFailed(execution, baseline.Revision);
            }

            execution.Revision = baseline.Revision + 1;
            cache.Persisted[execution.Id] = ExecutionState.From(execution);
        }
    }

    // ─────────────────────────── 内部实现 ───────────────────────────

    private static async Task InsertPendingAsync(IDbSession session, SessionCache cache, CancellationToken ct)
    {
        // 外键 PARENT_ID_ → ID_ 要求父行先落库；按「父已入库」的拓扑顺序分批插入
        var pending = cache.Entities.Values
            .Where(execution => !cache.Deleted.Contains(execution.Id) && !cache.InDatabase.Contains(execution.Id))
            .ToList();

        while (pending.Count > 0)
        {
            var progressed = false;
            var stillPending = new List<IExecutionEntity>(pending.Count);

            foreach (var execution in pending)
            {
                var parentReady = execution.ParentId is null
                    || cache.InDatabase.Contains(execution.ParentId)
                    || !cache.Entities.ContainsKey(execution.ParentId);

                if (!parentReady)
                {
                    stillPending.Add(execution);
                    continue;
                }

                await InsertInternalAsync(session, cache, execution, ct).ConfigureAwait(false);
                progressed = true;
            }

            if (!progressed)
            {
                throw new EngineException(
                    "执行树的父子关系存在环，无法按外键顺序写入 ACT_RU_EXECUTION。");
            }

            pending = stillPending;
        }
    }

    private static async Task InsertInternalAsync(
        IDbSession session,
        SessionCache cache,
        IExecutionEntity execution,
        CancellationToken ct)
    {
        TrackIn(cache, execution);

        if (cache.Deleted.Contains(execution.Id) || cache.InDatabase.Contains(execution.Id))
        {
            return;
        }

        await ExecuteAsync(session, InsertSql, BuildParameters(execution), ct).ConfigureAwait(false);

        // INSERT 语句里 REV_ 写死为 1
        execution.Revision = 1;
        cache.InDatabase.Add(execution.Id);
        cache.Persisted[execution.Id] = ExecutionState.From(execution);
    }

    private async Task DeleteRecursiveAsync(
        IDbSession session,
        SessionCache cache,
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct)
    {
        if (cache.Deleted.Contains(execution.Id))
        {
            return;
        }

        var children = await QueryStatesAsync(
            session,
            SelectChildrenSql,
            new { parentExecutionId = execution.Id },
            ct).ConfigureAwait(false);

        foreach (var child in children)
        {
            await DeleteRecursiveAsync(session, cache, Materialize(cache, child), deleteReason, ct).ConfigureAwait(false);
        }

        // 对应 deleteProcessInstanceExecutionEntity 里的 deleteChildExecutions：
        // 每个被删的执行都要先收口自己在 ACT_HI_ACTINST 上未结束的行（清理不发事件）
        await _historyManager.RecordActivityEndQuietlyAsync(execution, deleteReason, ct).ConfigureAwait(false);

        await DeleteSingleAsync(session, cache, execution, deleteReason, ct).ConfigureAwait(false);
    }

    private async Task DeleteSingleAsync(
        IDbSession session,
        SessionCache cache,
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct)
    {
        if (cache.Deleted.Contains(execution.Id))
        {
            return;
        }

        // 单个执行被删（终止结束事件 / 取消活动 / 多实例收口）时，其异步作业一并作废
        if (_asyncJobs is not null)
        {
            await _asyncJobs.DeleteByExecutionAsync(execution.Id, ct).ConfigureAwait(false);
        }

        var persisted = cache.InDatabase.Contains(execution.Id) || execution.Revision > 0;

        if (persisted)
        {
            // 先摘除挂在执行上的外部引用（这些外键在 DDL 里没有级联删除）
            await ExecuteAsync(session, DeleteIdentityLinksByExecutionSql, new { id = execution.Id }, ct)
                .ConfigureAwait(false);

            // 任务必须走任务管理器删除：它负责收口 ACT_HI_TASKINST（结束时间 + 删除原因）、
            // 清理任务变量与候选关系。直接 delete ACT_RU_TASK 会留下「有开始无结束」的历史行，
            // 而这条路径被并行 Join、多实例提前完成、取消活动、迁移共用。
            var tasks = await _taskManager
                .FindTasksByExecutionIdAsync(execution.Id, ct)
                .ConfigureAwait(false);

            foreach (var task in tasks)
            {
                await _taskManager.DeleteTaskAsync(task, deleteReason, ct).ConfigureAwait(false);
            }

            await DeleteVariablesAndByteArraysAsync(
                session,
                CaptureByteArrayIdsByExecutionSql,
                DeleteVariablesByExecutionSql,
                new { id = execution.Id },
                ct).ConfigureAwait(false);

            // delete from ACT_RU_EXECUTION where ID_ = @id and REV_ = @revision
            var affected = await ExecuteAsync(
                session,
                DeleteSql,
                new { id = execution.Id, revision = execution.Revision },
                ct).ConfigureAwait(false);

            if (affected != 1)
            {
                throw new OptimisticLockingException(
                    $"删除执行 '{execution.Id}' 失败：预期影响 1 行，实际 {affected} 行（REV_ = {execution.Revision}）。" +
                    "该行可能已被其它命令修改或删除。");
            }
        }

        cache.Deleted.Add(execution.Id);
        cache.InDatabase.Remove(execution.Id);
        cache.Persisted.Remove(execution.Id);

        execution.IsEnded = true;

        if (execution.Parent is not null)
        {
            execution.Parent.Children.Remove(execution);
        }
        else if (
            execution.ParentId is not null
            && cache.Entities.TryGetValue(execution.ParentId, out var parentEntity))
        {
            var match = parentEntity.Children
                .FirstOrDefault(child => string.Equals(child.Id, execution.Id, StringComparison.Ordinal));

            if (match is not null)
            {
                parentEntity.Children.Remove(match);
            }
        }
    }

    private ExecutionEntity Materialize(SessionCache cache, ExecutionState state)
    {
        if (cache.Entities.TryGetValue(state.Id, out var tracked))
        {
            return (ExecutionEntity)tracked;
        }

        var entity = state.ToEntity();
        cache.Entities[state.Id] = entity;
        cache.Persisted[state.Id] = state;

        // 关键：从库里读回来的行已经存在，绝不能再被 flush 当成待插入
        cache.InDatabase.Add(state.Id);

        return Wire(entity);
    }

    private void Track(IExecutionEntity execution)
    {
        using var lease = _sessions.Acquire();
        TrackIn(GetCache(lease.Session), execution);
    }

    private static void TrackIn(SessionCache cache, IExecutionEntity execution)
    {
        if (!cache.Entities.ContainsKey(execution.Id))
        {
            cache.Entities[execution.Id] = execution;
        }
    }

    private SessionCache GetCache(IDbSession session) =>
        _caches.GetValue(session, key =>
        {
            // 注册为 flush 参与方：命令提交前先把一级缓存写库
            key.RegisterFlusher(this);
            return new SessionCache();
        });

    private static Task<int> ExecuteAsync(IDbSession session, string sql, object? parameters, CancellationToken ct) =>
        session.Connection.ExecuteAsync(
            new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct));

    private static async Task<List<ExecutionState>> QueryStatesAsync(
        IDbSession session,
        string sql,
        object? parameters,
        CancellationToken ct)
    {
        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);
        var states = new List<ExecutionState>();

        foreach (IDictionary<string, object> row in rows)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var pair in row)
            {
                values[pair.Key] = pair.Value;
            }

            states.Add(ExecutionColumns.Read(values));
        }

        return states;
    }

    /// <summary>查询单列字符串集合（longString 字节行 id 捕获用）。</summary>
    private static async Task<List<string>> QueryColumnAsync(
        IDbSession session,
        string sql,
        object? parameters,
        CancellationToken ct)
    {
        var values = await session.Connection
            .QueryAsync<string>(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        return [.. values];
    }

    private static DynamicParameters BuildParameters(
        IExecutionEntity execution,
        int? revisionNext = null,
        int? revision = null)
    {
        var parameters = new DynamicParameters();

        parameters.Add("Id", execution.Id);
        parameters.Add("ProcessInstanceId", execution.ProcessInstanceId);
        parameters.Add("BusinessKey", execution.BusinessKey);
        parameters.Add("ProcessDefinitionId", execution.ProcessDefinitionId);
        parameters.Add("ActivityId", execution.CurrentActivityId);
        parameters.Add("IsActive", Flag(execution.IsActive));
        parameters.Add("IsConcurrent", Flag(execution.IsConcurrent));
        parameters.Add("IsScope", Flag(execution.IsScope));
        parameters.Add("IsEventScope", Flag(execution.IsEventScope));
        parameters.Add("ParentId", execution.ParentId);
        parameters.Add("SuperExecutionId", execution.SuperExecutionId);
        parameters.Add("RootProcessInstanceId", execution.RootProcessInstanceId);
        parameters.Add("SuspensionState", execution.SuspensionState);
        parameters.Add("TenantId", execution.TenantId ?? string.Empty);
        parameters.Add("Name", execution is ExecutionEntity entity ? entity.Name : null);
        parameters.Add("StartTime", execution.StartTime);
        parameters.Add("StartUserId", execution.StartUserId);

        if (revisionNext is not null)
        {
            parameters.Add("RevisionNext", revisionNext.Value);
        }

        if (revision is not null)
        {
            parameters.Add("Revision", revision.Value);
        }

        return parameters;
    }

    /// <summary>布尔标志位统一以 0/1 落库，避免各方言对 boolean 的驱动差异。</summary>
    private static int Flag(bool value) => value ? 1 : 0;

    private static OptimisticLockingException OptimisticLockFailed(IExecutionEntity execution, int revision) =>
        new(
            $"更新执行 '{execution.Id}' 失败：REV_ = {revision} 的行不存在。" +
            "该行可能已被其它命令并发修改（乐观锁冲突）。");

    // ─────────────────────────── SQL（对应 Execution.xml）───────────────────────────

    private const string InsertSql = """
        insert into ACT_RU_EXECUTION (
            ID_, REV_, PROC_INST_ID_, BUSINESS_KEY_, PROC_DEF_ID_, ACT_ID_,
            IS_ACTIVE_, IS_CONCURRENT_, IS_SCOPE_, IS_EVENT_SCOPE_, IS_MI_ROOT_,
            PARENT_ID_, SUPER_EXEC_, ROOT_PROC_INST_ID_, SUSPENSION_STATE_, TENANT_ID_,
            NAME_, START_TIME_, START_USER_ID_, IS_COUNT_ENABLED_, EVT_SUBSCR_COUNT_, TASK_COUNT_,
            JOB_COUNT_, TIMER_JOB_COUNT_, SUSP_JOB_COUNT_, DEADLETTER_JOB_COUNT_, VAR_COUNT_,
            ID_LINK_COUNT_, APP_VERSION_)
        values (
            @Id, 1, @ProcessInstanceId, @BusinessKey, @ProcessDefinitionId, @ActivityId,
            @IsActive, @IsConcurrent, @IsScope, @IsEventScope, 0,
            @ParentId, @SuperExecutionId, @RootProcessInstanceId, @SuspensionState, @TenantId,
            @Name, @StartTime, @StartUserId, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, null)
        """;

    private const string UpdateSql = """
        update ACT_RU_EXECUTION set
            REV_ = @RevisionNext,
            BUSINESS_KEY_ = @BusinessKey,
            PROC_DEF_ID_ = @ProcessDefinitionId,
            ACT_ID_ = @ActivityId,
            IS_ACTIVE_ = @IsActive,
            IS_CONCURRENT_ = @IsConcurrent,
            IS_SCOPE_ = @IsScope,
            IS_EVENT_SCOPE_ = @IsEventScope,
            IS_MI_ROOT_ = 0,
            PARENT_ID_ = @ParentId,
            SUPER_EXEC_ = @SuperExecutionId,
            ROOT_PROC_INST_ID_ = @RootProcessInstanceId,
            SUSPENSION_STATE_ = @SuspensionState,
            NAME_ = @Name,
            IS_COUNT_ENABLED_ = 0,
            EVT_SUBSCR_COUNT_ = 0,
            TASK_COUNT_ = 0,
            JOB_COUNT_ = 0,
            TIMER_JOB_COUNT_ = 0,
            SUSP_JOB_COUNT_ = 0,
            DEADLETTER_JOB_COUNT_ = 0,
            VAR_COUNT_ = 0,
            ID_LINK_COUNT_ = 0,
            START_TIME_ = @StartTime,
            APP_VERSION_ = null
        where ID_ = @Id and REV_ = @Revision
        """;

    private const string DeleteSql =
        "delete from ACT_RU_EXECUTION where ID_ = @id and REV_ = @revision";

    /// <summary><c>ACT_RU_EXECUTION.SUSPENSION_STATE_</c> 的取值（与 Activiti <c>SuspensionState</c> 一致）。</summary>
    private const int ActiveState = 1;

    /// <summary>挂起态。</summary>
    private const int SuspendedState = 2;

    /// <summary>对应 Activiti <c>updateProcessInstanceSuspensionStateByProcessInstanceId</c>（按实例）。</summary>
    private const string UpdateSuspensionStateByProcessInstanceIdSql =
        "update ACT_RU_EXECUTION set SUSPENSION_STATE_ = @state where PROC_INST_ID_ = @processInstanceId";

    /// <summary>对应 Activiti <c>updateProcessInstanceSuspensionStateByProcessDefinitionId</c>（按定义）。</summary>
    private const string UpdateSuspensionStateByProcessDefinitionIdSql =
        "update ACT_RU_EXECUTION set SUSPENSION_STATE_ = @state where PROC_DEF_ID_ = @processDefinitionId";

    /// <summary>运行中的流程实例根执行（<c>PARENT_ID_ is null</c> 排除子执行）。</summary>
    private const string SelectProcessInstanceIdsByProcessDefinitionIdSql =
        "select ID_ from ACT_RU_EXECUTION where PROC_DEF_ID_ = @processDefinitionId and PARENT_ID_ is null";

    /// <summary>全部运行中的流程实例（根执行）。</summary>
    private const string SelectProcessInstancesSql =
        "select " + ExecutionColumns.All + " from ACT_RU_EXECUTION where PARENT_ID_ is null";

    private const string SelectByIdSql =
        "select " + ExecutionColumns.All + " from ACT_RU_EXECUTION where ID_ = @executionId";

    private const string SelectChildrenSql =
        "select " + ExecutionColumns.All + " from ACT_RU_EXECUTION where PARENT_ID_ = @parentExecutionId";

    /// <summary>与 <see cref="SelectChildrenSql"/> 同口径，只取数量。</summary>
    private const string CountChildrenSql =
        "select count(*) from ACT_RU_EXECUTION where PARENT_ID_ = @parentExecutionId";

    /// <summary>
    /// 与「按流程实例取子执行后过滤 IS_ACTIVE_ 且排除根执行」同口径，只取数量。
    /// 根执行的 PARENT_ID_ 为 null，因此 PARENT_ID_ is not null 已排除根。
    /// </summary>
    private const string CountActiveChildrenOfProcessInstanceSql =
        "select count(*) from ACT_RU_EXECUTION " +
        "where PROC_INST_ID_ = @processInstanceId and PARENT_ID_ is not null and IS_ACTIVE_ = 1";

    /// <summary>整棵执行树（根执行的 PROC_INST_ID_ = ID_，因此同一条 SQL 覆盖根与子执行）。</summary>
    private const string SelectByProcessInstanceSql =
        "select " + ExecutionColumns.All +
        " from ACT_RU_EXECUTION where PROC_INST_ID_ = @processInstanceId order by ID_";

    private const string SelectChildExecutionsOfProcessInstanceSql =
        "select " + ExecutionColumns.All +
        " from ACT_RU_EXECUTION where PROC_INST_ID_ = @processInstanceId and PARENT_ID_ is not null";

    private const string SelectInactiveInActivitySql =
        "select " + ExecutionColumns.All +
        " from ACT_RU_EXECUTION where ACT_ID_ = @activityId and PROC_INST_ID_ = @processInstanceId and IS_ACTIVE_ = 0";

    private const string DeleteVariablesByExecutionSql =
        "delete from ACT_RU_VARIABLE where EXECUTION_ID_ = @id";

    private const string DeleteVariablesByProcessInstanceSql =
        "delete from ACT_RU_VARIABLE where PROC_INST_ID_ = @processInstanceId";

    private const string CaptureByteArrayIdsByExecutionSql =
        "select BYTEARRAY_ID_ from ACT_RU_VARIABLE where EXECUTION_ID_ = @id and BYTEARRAY_ID_ is not null";

    private const string CaptureByteArrayIdsByProcessInstanceSql =
        "select BYTEARRAY_ID_ from ACT_RU_VARIABLE where PROC_INST_ID_ = @processInstanceId and BYTEARRAY_ID_ is not null";

    private const string DeleteByteArraysByIdsSql =
        "delete from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is null and ID_ in @ids";

    /// <summary>删除变量行 + 其 longString 字节行（先取 id → 删变量行 → 按 id 删字节行，满足 RESTRICT 外键）。</summary>
    private static async Task DeleteVariablesAndByteArraysAsync(
        IDbSession session,
        string captureByteArrayIdsSql,
        string deleteVariablesSql,
        object args,
        CancellationToken ct)
    {
        var byteArrayIds = (await QueryColumnAsync(session, captureByteArrayIdsSql, args, ct).ConfigureAwait(false))
            .ToList();

        await ExecuteAsync(session, deleteVariablesSql, args, ct).ConfigureAwait(false);

        if (byteArrayIds.Count > 0)
        {
            await ExecuteAsync(session, DeleteByteArraysByIdsSql, new { ids = byteArrayIds }, ct)
                .ConfigureAwait(false);
        }
    }

    private const string DeleteIdentityLinksByExecutionSql =
        "delete from ACT_RU_IDENTITYLINK where TASK_ID_ in (select ID_ from ACT_RU_TASK where EXECUTION_ID_ = @id)";

    /// <summary>会话级一级缓存。</summary>
    private sealed class SessionCache
    {
        /// <summary>会话内跟踪的执行实体（id → 实体，保证同一行只有一个实例）。</summary>
        public Dictionary<string, IExecutionEntity> Entities { get; } = new(StringComparer.Ordinal);

        /// <summary>已落库的基线状态（脏检查依据）。</summary>
        public Dictionary<string, ExecutionState> Persisted { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// 本次会话中<b>确认已在库里存在</b>的执行 id：
        /// 既包括本会话 INSERT 的，也包括从库读回来的（<see cref="Materialize"/>）。
        /// <para>
        /// ⚠️ 漏掉后者会造成严重错误：新会话把库里的行当成「待插入」，
        /// flush 时再 INSERT 一次直接撞主键（已实测）。
        /// </para>
        /// </summary>
        public HashSet<string> InDatabase { get; } = new(StringComparer.Ordinal);

        /// <summary>本次会话已删除的执行 id。</summary>
        public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);
    }
}

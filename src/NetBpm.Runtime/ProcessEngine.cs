using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Diagnostics;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Migration;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence.Deployment;
using NetBpm.Runtime.Hosting.JobQueue;
using NetBpm.Runtime.Migration;

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
    private readonly ITimerJobManager? _timerJobs;
    private readonly IAsyncJobManager? _asyncJobs;
    private readonly IExternalTaskBroker? _externalTasks;
    private readonly IMigrationLog? _migrationLog;
    private readonly ILogger<ProcessEngine>? _logger;
    private readonly NetBpmEngineOptions _engineOptions;
    private readonly IClock _clock;
    private readonly string _jobOwner;
    private readonly IJobQueue _queue;

    public ProcessEngine(
        ICommandExecutor commandExecutor,
        IBpmnXmlConverter xmlConverter,
        IProcessValidator validator,
        IProcessDefinitionStore definitionStore,
        ITaskManager taskManager,
        IUserGroupManager userGroupManager,
        IExecutionManager executionManager,
        IHistoryManager historyManager,
        ITimerJobManager? timerJobs = null,
        IMigrationLog? migrationLog = null,
        ILogger<ProcessEngine>? logger = null,
        IAsyncJobManager? asyncJobs = null,
        NetBpmEngineOptions? engineOptions = null,
        IJobQueue? jobQueue = null,
        IClock? clock = null,
        IExternalTaskBroker? externalTasks = null)
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
        _timerJobs = timerJobs;
        _asyncJobs = asyncJobs;
        _externalTasks = externalTasks;
        _migrationLog = migrationLog;
        _logger = logger;
        _engineOptions = engineOptions ?? new NetBpmEngineOptions();
        _clock = clock ?? SystemClock.Instance;
        _queue = jobQueue ?? new InMemoryJobQueue();

        // 宿主实例标识：多实例部署时用于作业租约抢占。
        // 默认用机器名 + 进程 id，保证同一台机器上跑多个宿主也不冲突。
        _jobOwner = string.IsNullOrWhiteSpace(_engineOptions.JobOwner)
            ? $"{Environment.MachineName}#{Environment.ProcessId}"
            : _engineOptions.JobOwner!.Trim();
    }

    /// <inheritdoc />
    public ValueTask<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
        _commandExecutor.ExecuteAsync(command, ct);

    /// <inheritdoc />
    public async ValueTask<int> TriggerDueTimersAsync(
        DateTime utcNow,
        int maxJobs = 50,
        CancellationToken ct = default)
    {
        if (_timerJobs is null)
        {
            return 0;
        }

        var due = await _timerJobs.FindDueAsync(utcNow, Math.Max(1, maxJobs), ct).ConfigureAwait(false);

        var fired = 0;
        var failed = 0;

        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // 挂起实例的作业本轮让位（数据库宿主已在 SQL 里过滤，内存宿主在这里兜底）：
                // 否则一条挂起实例的作业会永远占据批次头部，饿死真正该触发的作业。
                if (_timerJobs is InMemoryTimerJobManager)
                {
                    var execution = await _executionManager.FindByIdAsync(job.ExecutionId, ct).ConfigureAwait(false);

                    if (execution is { SuspensionState: not 1 })
                    {
                        await _timerJobs.RescheduleAsync(job.Id, utcNow.AddMinutes(1), ct).ConfigureAwait(false);
                        continue;
                    }
                }

                // 每条作业一个独立事务：某一条失败（并发完成 / 已删除 / 数据损坏）不影响其它作业
                if (await _commandExecutor
                        .ExecuteAsync(new TriggerTimerJobCommand(job.Id), ct)
                        .ConfigureAwait(false))
                {
                    fired++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                await RecordJobFailureAsync(job, exception, utcNow).ConfigureAwait(false);
            }
        }

        if (failed > 0)
        {
            _logger?.LogWarning(
                "本轮 {Failed} 条定时作业执行失败（已按退避重试，超过上限进入死信）。",
                failed);
        }

        return fired;
    }

    /// <summary>
    /// 记录作业失败：递减重试次数、按退避改期；用尽后进死信。
    /// <para>
    /// 必须用<b>独立事务</b>写入：失败命令已经回滚，作业行仍在表里；
    /// 这里再失败（数据库不可用）只记日志，不能让整批轮询停摆。
    /// </para>
    /// </summary>
    private async ValueTask RecordJobFailureAsync(TimerJobEntity job, Exception exception, DateTime utcNow)
    {
        try
        {
            var failedAttempts = Math.Max(1, TimerJobRetry.DefaultMaxRetries - job.Retries + 1);
            var remaining = Math.Max(0, job.Retries - 1);
            var nextDue = remaining == 0 ? utcNow : utcNow + TimerJobRetry.Backoff(failedAttempts);

            await _timerJobs!
                .MarkFailureAsync(job.Id, exception.Message, utcNow, nextDue, CancellationToken.None)
                .ConfigureAwait(false);

            EngineDiagnostics.TimerJobFailures.Add(
                1,
                new KeyValuePair<string, object?>("dead_letter", remaining == 0));

            _logger?.LogWarning(
                exception,
                "定时作业 {JobId} 执行失败（剩余重试 {Remaining}，下次到期 {NextDue:O}）。",
                job.Id,
                remaining,
                nextDue);
        }
        catch (Exception markException)
        {
            _logger?.LogError(markException, "记录定时作业 {JobId} 失败状态时出错。", job.Id);
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> ExecuteAsyncJobsByIdAsync(
        IReadOnlyList<string> jobIds,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        if (_asyncJobs is null)
        {
            return 0;
        }

        var executed = 0;
        var failed = 0;

        foreach (var jobId in jobIds)
        {
            ct.ThrowIfCancellationRequested();

            AsyncJobEntity? job = null;

            try
            {
                // 先取快照：失败时要按退避改期，需要知道它当前的重试余量。
                // 取快照也放进 try —— 数据库抖动同样只应影响这一条，而不是整批。
                job = await _asyncJobs.FindByIdAsync(jobId, ct).ConfigureAwait(false);

                // 该入口没有外层抢占：由命令自己做租约抢占
                if (await ExecuteJobWithLeaseHeartbeatAsync(jobId, leaseAlreadyHeld: false, ct).ConfigureAwait(false))
                {
                    executed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 一条作业失败不能中断整批（与 ExecuteDueAsyncJobsAsync 同口径）：
                // 外部系统抖动 / 数据损坏只影响这一条，其余照常推进。
                failed++;

                if (job is not null)
                {
                    await RecordAsyncJobFailureAsync(job, exception, utcNow).ConfigureAwait(false);
                }
                else
                {
                    _logger?.LogWarning(
                        exception,
                        "异步作业 {JobId} 执行失败（作业行已不存在，未记录失败状态）。",
                        jobId);
                }
            }
            finally
            {
                // 队列提示只是「可能有活」：无论成败都要消费掉。
                // 失败已按退避改期（数据库是唯一权威），把提示留在在途集合里只会白等超时。
                await CompleteQueueHintAsync(jobId).ConfigureAwait(false);
            }
        }

        if (failed > 0)
        {
            _logger?.LogWarning(
                "本轮 {Failed} 条异步作业执行失败（已按退避重试，超过上限进入死信）。",
                failed);
        }

        return executed;
    }

    /// <summary>消费作业队列里的一条提示（失败只告警，不影响主流程）。</summary>
    private async ValueTask CompleteQueueHintAsync(string jobId)
    {
        try
        {
            await _queue.CompleteAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "确认作业队列提示 {JobId} 失败（提示会在在途超时后重投，不影响作业正确性）。",
                jobId);
        }
    }

    /// <summary>
    /// 执行一条异步作业，并在执行期间<b>心跳续租</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 租约式抢占（<c>TryAcquireAsync</c>）只在开始时标记归属；如果执行时间超过租约时长
    /// （外部系统卡顿、大并行分支、GC 停顿），另一台宿主会判定持有者已崩溃并接管 ——
    /// 同一作业就被执行了两次，业务副作用重复。续租把这个窗口关掉：
    /// 只要进程还活着，租约就一直有效。
    /// </para>
    /// <para>
    /// 🔑 心跳任务<b>必须抑制执行上下文流转</b>：<c>CommandContext</c> 用 <c>AsyncLocal</c> 传当前命令，
    /// 若心跳继承它，<c>IDbSessionProvider.Acquire()</c> 会把<b>命令正在用的连接/事务</b>交给心跳线程，
    /// 造成同一连接跨线程并发使用（驱动层会报错，或更糟：事务状态错乱）。
    /// </para>
    /// </remarks>
    private async ValueTask<bool> ExecuteJobWithLeaseHeartbeatAsync(
        string jobId,
        bool leaseAlreadyHeld,
        CancellationToken ct)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 显式写全名：本文件同时引入了 NetBpm.Bpmn.Model，那里的 Task 是 BPMN 用户任务模型
        System.Threading.Tasks.Task? heartbeat = null;

        if (_asyncJobs is not null)
        {
            using (ExecutionContext.SuppressFlow())
            {
                heartbeat = System.Threading.Tasks.Task.Run(
                    () => RenewLeaseLoopAsync(jobId, heartbeatCts.Token),
                    CancellationToken.None);
            }
        }

        try
        {
            return await _commandExecutor
                .ExecuteAsync(new ExecuteAsyncJobCommand(jobId, _jobOwner, leaseAlreadyHeld), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (heartbeat is not null)
            {
                await heartbeatCts.CancelAsync().ConfigureAwait(false);

                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 正常收尾（心跳以取消结束）
                }
            }
        }
    }

    /// <summary>按租约时长的 1/3 周期续租，直到被取消或租约已被别人接管。</summary>
    private async System.Threading.Tasks.Task RenewLeaseLoopAsync(string jobId, CancellationToken ct)
    {
        var lease = _engineOptions.JobLease;
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, lease.Ticks / 3));

        try
        {
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(interval, ct).ConfigureAwait(false);

                if (!await _asyncJobs!
                        .RenewAsync(jobId, _jobOwner, _clock.UtcNow, lease, CancellationToken.None)
                        .ConfigureAwait(false))
                {
                    // 租约已被接管（本进程可能停顿过久）。无法回滚已发生的业务动作，
                    // 但继续续租会掩盖问题 —— 停止心跳并让日志说清楚。
                    _logger?.LogWarning(
                        "异步作业 {JobId} 的租约已被其它宿主接管，停止续租（本次执行可能重复，需依赖幂等）。",
                        jobId);

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 作业执行结束，正常退出
        }
        catch (Exception exception)
        {
            // 续租失败（数据库抖动）不能影响作业本身：最坏情况退化成「无续租」，
            // 与修复前一致；下一次心跳还会重试。
            _logger?.LogWarning(exception, "异步作业 {JobId} 续租失败（下次心跳重试）。", jobId);
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> ExecuteDueAsyncJobsAsync(
        DateTime utcNow,
        int maxJobs = 50,
        CancellationToken ct = default)
    {
        if (_asyncJobs is null)
        {
            return 0;
        }

        var due = await _asyncJobs.FindDueAsync(utcNow, Math.Max(1, maxJobs), ct).ConfigureAwait(false);

        var executed = 0;
        var failed = 0;

        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();

            // 租约式抢占：先标记归属，执行成功后才删除。
            // 若本轮并发的其它宿主已抢到（或上一轮崩溃的租约尚未过期），跳过即可。
            if (!await _asyncJobs
                    .TryAcquireAsync(job.Id, _jobOwner, utcNow, _engineOptions.JobLease, ct)
                    .ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                // 每条作业一个独立事务：某一条失败（外部系统抖动 / 数据损坏）不影响其它作业。
                // 上面已按租约抢占，命令内不再重复抢占。
                if (await ExecuteJobWithLeaseHeartbeatAsync(job.Id, leaseAlreadyHeld: true, ct).ConfigureAwait(false))
                {
                    executed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                await RecordAsyncJobFailureAsync(job, exception, utcNow).ConfigureAwait(false);
            }
        }

        if (failed > 0)
        {
            _logger?.LogWarning(
                "本轮 {Failed} 条异步作业执行失败（已按退避重试，超过上限进入死信）。",
                failed);
        }

        return executed;
    }

    /// <summary>记录异步作业失败：递减重试次数、按退避改期；用尽后进死信（独立事务）。</summary>
    private async ValueTask RecordAsyncJobFailureAsync(AsyncJobEntity job, Exception exception, DateTime utcNow)
    {
        try
        {
            var failedAttempts = Math.Max(1, AsyncJobRetry.DefaultMaxRetries - job.Retries + 1);
            var remaining = Math.Max(0, job.Retries - 1);
            var nextDue = remaining == 0 ? utcNow : utcNow + AsyncJobRetry.Backoff(failedAttempts);

            // 数据库宿主：作业的「抢占删除」随失败命令回滚，行仍在 → 直接改失败状态。
            // 内存宿主没有事务：抢占删除已经生效 → 用失败前快照重新插回。
            var existing = await _asyncJobs!.FindByIdAsync(job.Id, CancellationToken.None).ConfigureAwait(false);

            if (existing is null)
            {
                job.Retries = remaining;
                job.ExceptionMessage = exception.Message;
                job.LastFailureTime = utcNow;
                job.DueDate = nextDue;
                job.DeadLetter = remaining == 0;

                await _asyncJobs.InsertAsync(job, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _asyncJobs
                    .MarkFailureAsync(job.Id, exception.Message, utcNow, nextDue, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            EngineDiagnostics.AsyncJobFailures.Add(
                1,
                new KeyValuePair<string, object?>("dead_letter", remaining == 0));

            _logger?.LogWarning(
                exception,
                "异步作业 {JobId}（{ActivityId}/{Phase}）执行失败（剩余重试 {Remaining}，下次到期 {NextDue:O}）。",
                job.Id,
                job.ActivityId,
                job.PhaseValue,
                remaining,
                nextDue);
        }
        catch (Exception markException)
        {
            _logger?.LogError(markException, "记录异步作业 {JobId} 失败状态时出错。", job.Id);
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExecuteAsyncJobAsync(string jobId, CancellationToken ct = default)
    {
        if (_asyncJobs is null)
        {
            return false;
        }

        return await _commandExecutor
            .ExecuteAsync(new ExecuteAsyncJobCommand(jobId), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AsyncJobEntity?> FindAsyncJobAsync(string jobId, CancellationToken ct = default) =>
        _asyncJobs is null ? null : await _asyncJobs.FindByIdAsync(jobId, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AsyncJobEntity>> FindAsyncJobsAsync(
        AsyncJobQuery query,
        CancellationToken ct = default) =>
        _asyncJobs is null
            ? []
            : await _asyncJobs.FindAsync(query, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<int> CountAsyncJobsAsync(AsyncJobQuery query, CancellationToken ct = default) =>
        _asyncJobs is null ? 0 : await _asyncJobs.CountAsync(query, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<int> CountAsyncJobsAsync(CancellationToken ct = default) =>
        _asyncJobs is null ? 0 : await _asyncJobs.CountAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> RetryAsyncJobAsync(string jobId, CancellationToken ct = default) =>
        _asyncJobs is not null
        && await _asyncJobs.RetryDeadLetterAsync(jobId, _clock.UtcNow, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsyncJobAsync(string jobId, CancellationToken ct = default) =>
        _asyncJobs is not null
        && await _asyncJobs.DeleteDeadLetterAsync(jobId, ct).ConfigureAwait(false);

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

        var process = ResolveDeployableProcess(bpmnModel, "部署");

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

        var process = ResolveDeployableProcess(bpmnModel, "校验");

        return _validator.Validate(process);
    }

    /// <summary>
    /// 取唯一可执行流程；文件里有多个 <c>isExecutable="true"</c> 时显式拒绝。
    /// <para>
    /// 过去只取第一个可执行流程，其余流程<b>既不校验也不部署</b> —— 用户以为传上去的是多流程，
    /// 实际只有一条生效。非可执行流程（协作图 / 文档流程）不属于部署对象，照旧忽略。
    /// </para>
    /// </summary>
    private static Process ResolveDeployableProcess(BpmnModel model, string action)
    {
        var executable = model.Processes.Where(candidate => candidate.IsExecutable).ToArray();

        if (executable.Length > 1)
        {
            throw new EngineException(
                $"{action}失败：BPMN 文件中有 {executable.Length} 个 isExecutable=\"true\" 的流程" +
                $"（{string.Join(", ", executable.Select(candidate => candidate.Id))}）。" +
                "一个部署只能包含一个可执行流程：请拆成多个文件，或把其余流程标记为 isExecutable=\"false\"。");
        }

        return model.GetMainProcess()
            ?? throw new EngineException($"{action}失败：BPMN 文件中没有流程定义。");
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
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);

        if (_executionManager is IExecutionQueryManager pushdown)
        {
            var filter = await BuildProcessInstanceFilterAsync(query, ct).ConfigureAwait(false);

            return filter is null
                ? []
                : await pushdown.QueryProcessInstancesAsync(filter, ct).ConfigureAwait(false);
        }

        var instances = await CollectProcessInstancesAsync(query, ct).ConfigureAwait(false);

        return
        [
            .. ApplyPaging(
                instances,
                query.FirstResult,
                query.MaxResults,
                instance => instance.StartTime,
                instance => instance.Id,
                descending: query.Descending),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountProcessInstancesAsync(
        ProcessInstanceQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(0, null);

        if (_executionManager is IExecutionQueryManager pushdown)
        {
            var filter = await BuildProcessInstanceFilterAsync(query, ct).ConfigureAwait(false);

            return filter is null
                ? 0
                : await pushdown
                    .CountProcessInstancesAsync(filter with { FirstResult = 0, MaxResults = null }, ct)
                    .ConfigureAwait(false);
        }

        return (await CollectProcessInstancesAsync(query, ct).ConfigureAwait(false)).Count;
    }

    /// <summary>
    /// 把门面查询翻译成持久化过滤条件；按 key 解析不出任何定义时返回 <see langword="null"/>
    /// （语义是「零匹配」，不能退化成「不加过滤」）。
    /// </summary>
    private async ValueTask<ProcessInstanceFilter?> BuildProcessInstanceFilterAsync(
        ProcessInstanceQuery query,
        CancellationToken ct)
    {
        // 空集合的语义是「跟任何实例都无关」，必须落成「零匹配」。
        // 不能把它当成「没有这个过滤条件」下传：Dapper 侧的 `Count > 0` 判断会让它悄悄消失，
        // 结果是「我参与过的单据」变成「全部单据」——这是越权，不是性能问题。
        // 定义 id 白名单同理（平台侧目前会在更早的地方拦掉，但这层契约不该依赖调用方自觉）。
        if (query.ProcessInstanceIds is { Count: 0 }
            || query.ProcessDefinitionIds is { Count: 0 })
        {
            return null;
        }

        IReadOnlyCollection<string>? definitionIds = null;

        if (!string.IsNullOrWhiteSpace(query.ProcessDefinitionKey))
        {
            var resolved = await ResolveDefinitionIdsByKeyAsync(query.ProcessDefinitionKey, query.TenantId, ct)
                .ConfigureAwait(false);

            if (resolved.Count == 0)
            {
                return null;
            }

            definitionIds = resolved;
        }

        // 显式给的定义 id 白名单与 key 过滤取交集（两者都有时语义是「这些 key 里我只关心这几个版本」）
        var effectiveIds = query.ProcessDefinitionIds;

        if (definitionIds is not null)
        {
            effectiveIds = effectiveIds is null
                ? definitionIds
                : [.. effectiveIds.Intersect(definitionIds, StringComparer.Ordinal)];

            if (effectiveIds.Count == 0)
            {
                return null;
            }
        }

        return new ProcessInstanceFilter(
            ProcessDefinitionId: query.ProcessDefinitionId,
            ProcessDefinitionIds: effectiveIds,
            ProcessInstanceIds: query.ProcessInstanceIds,
            BusinessKey: query.BusinessKey,
            StartUserId: query.StartUserId,
            TenantId: query.TenantId,
            TenantIds: query.TenantIds,
            IsActive: query.IsActive,
            StartedAfter: query.StartedAfter,
            StartedBefore: query.StartedBefore,
            FirstResult: query.FirstResult,
            MaxResults: query.MaxResults,
            Descending: query.Descending);
    }

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
        && (query.ProcessDefinitionIds is null
            || query.ProcessDefinitionIds.Contains(instance.ProcessDefinitionId ?? string.Empty))
        && (query.ProcessInstanceIds is null || query.ProcessInstanceIds.Contains(instance.Id))
        && (string.IsNullOrWhiteSpace(query.BusinessKey)
            || string.Equals(instance.BusinessKey, query.BusinessKey, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(query.StartUserId)
            || string.Equals(instance.StartUserId, query.StartUserId, StringComparison.Ordinal))
        && (query.TenantId is null
            || string.Equals(instance.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal))
        && (query.TenantIds is null
            || query.TenantIds.Contains(instance.TenantId ?? string.Empty))
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
    public async ValueTask<IReadOnlyList<IdentityLinkRecord>> FindProcessInstanceIdentityLinksAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var links = await _taskManager
            .FindIdentityLinksByProcessInstanceIdAsync(processInstanceId, ct)
            .ConfigureAwait(false);

        return [.. links];
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

        if (!query.HasCriteria && !query.AllowsUnfiltered)
        {
            throw new EngineException(
                "任务查询至少需要一个条件（assignee / candidateUser / processInstanceId）。" +
                "本子集不提供「查全部任务」，避免全表扫描；管理视图的全量待办请显式声明 TaskQuery.AllUsers。");
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
        else if (query.AllowsUnfiltered)
        {
            // 管理视图的显式全量（TaskQuery.AllUsers）：整张待办表作候选集，
            // 剩余过滤（定义 / 节点 / 挂起 / 租户…）仍走统一的 Matches
            candidates = await _taskManager.FindAllOpenTasksAsync(ct).ConfigureAwait(false);
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
    private static IEnumerable<TaskEntity> ApplyPaging(IEnumerable<TaskEntity> tasks, TaskQuery query)
    {
        var sortBy = string.IsNullOrWhiteSpace(query.SortBy) ? "createTime" : query.SortBy;

        if (!TaskQuery.AllowedSortFields.Contains(sortBy, StringComparer.Ordinal))
        {
            throw new EngineException(
                $"不支持的排序字段 '{sortBy}'；可选：{string.Join(" / ", TaskQuery.AllowedSortFields)}。");
        }

        IOrderedEnumerable<TaskEntity> ordered = sortBy switch
        {
            "dueDate" => query.Descending
                ? tasks.OrderByDescending(task => task.DueDate ?? DateTime.MinValue)
                    .ThenByDescending(task => task.Id, StringComparer.Ordinal)
                : tasks.OrderBy(task => task.DueDate ?? DateTime.MinValue)
                    .ThenBy(task => task.Id, StringComparer.Ordinal),

            "priority" => query.Descending
                ? tasks.OrderByDescending(task => task.Priority)
                    .ThenByDescending(task => task.Id, StringComparer.Ordinal)
                : tasks.OrderBy(task => task.Priority)
                    .ThenBy(task => task.Id, StringComparer.Ordinal),

            _ => query.Descending
                ? tasks.OrderByDescending(task => task.CreateTime)
                    .ThenByDescending(task => task.Id, StringComparer.Ordinal)
                : tasks.OrderBy(task => task.CreateTime)
                    .ThenBy(task => task.Id, StringComparer.Ordinal),
        };

        IEnumerable<TaskEntity> result = ordered;

        if (query.FirstResult > 0)
        {
            result = result.Skip(query.FirstResult);
        }

        return query.MaxResults is int take ? result.Take(take) : result;
    }

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
        // tie-break 恒为 id 升序（两个方向都一样）：这是与 SQL 下推（`order by START_TIME_ {dir}, ID_ asc`）
        // 以及平台游标共用的全序。如果倒序时 id 也跟着倒，同刻多条在两种排序下顺序不同，
        // 「分页下推 + 合并」与「取全量再排序」就会得到不同的页边界。
        IEnumerable<T> ordered = descending
            ? items
                .OrderByDescending(item => timeSelector(item) ?? DateTime.MinValue)
                .ThenBy(idSelector, StringComparer.Ordinal)
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
    public async ValueTask<IReadOnlyList<ExternalTask>> FetchExternalTasksAsync(
        string workerId,
        IReadOnlyList<string> topics,
        int maxTasks = 10,
        TimeSpan? lockDuration = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        var request = new ExternalTaskFetchRequest(
            workerId,
            [.. topics.Select(topic => new ExternalTaskTopicRequest(topic, lockDuration))],
            maxTasks,
            lockDuration,
            tenantId);

        return await _commandExecutor
            .ExecuteAsync(new FetchExternalTasksCommand(request), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CompleteExternalTaskAsync(
        string taskId,
        string workerId,
        IDictionary<string, object?>? variables = null,
        string? tenantId = null,
        string? commandId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        await _commandExecutor
            .ExecuteAsync(new CompleteExternalTaskCommand(taskId, workerId, variables, tenantId, commandId), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask FailExternalTaskAsync(
        string taskId,
        string workerId,
        ExternalTaskFailure failure,
        string? tenantId = null,
        string? commandId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(failure);

        await _commandExecutor
            .ExecuteAsync(new FailExternalTaskCommand(taskId, workerId, failure, tenantId, commandId), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ExternalTask>> FindExternalTasksAsync(
        ExternalTaskQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var broker = _externalTasks
            ?? throw new EngineException("未注册 IExternalTaskBroker，无法查询外部任务。");

        // 只读：不必走命令（命令是事务边界，这里没有要保护的一致性）
        return await broker.FindAsync(query, ct).ConfigureAwait(false);
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

    /// <summary>
    /// 带<b>幂等键</b>地完成任务。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <c>CompleteTaskAsync</c> 的差别只在：同一个 <paramref name="commandId"/> 重复调用时，
    /// 第二次及以后直接返回，不会再次推进流程 —— 从而让「outbox 重投 / 作业重试 / 消息重复消费」
    /// 这些绕过 HTTP 的重复驱动也安全。
    /// </para>
    /// <para>
    /// 幂等记录与本次业务写入<b>同事务</b>：命令回滚 ⇒ 占位行一并消失 ⇒ 客户端可以用同一个键重试。
    /// </para>
    /// </remarks>
    /// <param name="taskId">任务 id。</param>
    /// <param name="commandId">幂等键（如消息 id / outbox 事件 id）；为空则退化成非幂等完成。</param>
    /// <param name="variables">随完成写入的流程变量。</param>
    /// <param name="expectedRevision">期望版本号（乐观锁）。</param>
    /// <param name="ct">取消标记。</param>
    public async ValueTask CompleteTaskIdempotentlyAsync(
        string taskId,
        string commandId,
        IDictionary<string, object?>? variables = null,
        int? expectedRevision = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new EngineException("幂等完成必须提供 commandId；不需要幂等请改用 CompleteTaskAsync。");
        }

        await _commandExecutor
            .ExecuteAsync(new CompleteTaskCommand(taskId, variables, expectedRevision, commandId), ct)
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
                    update.ClearDescription,
                    update.ExpectedRevision),
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
    public async ValueTask<MigrationPlan> ValidateMigrationAsync(
        ProcessInstanceMigrationRequest request,
        CancellationToken ct = default)
    {
        var planner = new ProcessInstanceMigrationPlanner(
            _executionManager,
            _taskManager,
            _definitionStore);

        var details = await planner.BuildAsync(request, ct).ConfigureAwait(false);

        return details.Plan;
    }

    /// <inheritdoc />
    public async ValueTask<MigrationPlan> MigrateProcessInstanceAsync(
        ProcessInstanceMigrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _commandExecutor
            .ExecuteAsync(new MigrateProcessInstanceCommand(_definitionStore, request, _migrationLog), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MigrationRecord>> FindMigrationsAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        if (_migrationLog is null)
        {
            return [];
        }

        return await _migrationLog
            .FindByProcessInstanceAsync(processInstanceId, ct)
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
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);
        EnsureHistoricTaskCriteria(query);

        if (_historyManager is IHistoricQueryManager pushdown)
        {
            return await pushdown
                .QueryTaskInstancesAsync(ToFilter(query), ct)
                .ConfigureAwait(false);
        }

        var tasks = await CollectHistoricTasksAsync(query, ct).ConfigureAwait(false);

        return [.. ApplyPaging(tasks, query.FirstResult, query.MaxResults, task => task.StartTime, task => task.Id)];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountHistoricTasksAsync(
        HistoricTaskQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureHistoricTaskCriteria(query);

        if (_historyManager is IHistoricQueryManager pushdown)
        {
            // 只取计数：不再为得到一个数字把整张历史任务表读进内存
            return await pushdown.CountTaskInstancesAsync(ToFilter(query) with { MaxResults = null }, ct)
                .ConfigureAwait(false);
        }

        var tasks = await CollectHistoricTasksAsync(query, ct).ConfigureAwait(false);

        return tasks.Count;
    }

    /// <inheritdoc />
    public async ValueTask<PagedQueryResult<HistoricTaskInstance>> FindHistoricTasksWithCountAsync(
        HistoricTaskQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);
        EnsureHistoricTaskCriteria(query);

        if (_historyManager is IHistoricQueryManager pushdown)
        {
            // 取页与计数由持久化层在一次会话租借内完成（同一条连接、同一个事务）
            return await pushdown
                .QueryTaskInstancesWithCountAsync(ToFilter(query), ct)
                .ConfigureAwait(false);
        }

        var tasks = await FindHistoricTasksAsync(query, ct).ConfigureAwait(false);
        var total = await CountHistoricTasksAsync(query, ct).ConfigureAwait(false);

        return new PagedQueryResult<HistoricTaskInstance>(tasks, total);
    }

    private static void EnsureHistoricTaskCriteria(HistoricTaskQuery query)
    {
        if (!query.HasCriteria)
        {
            throw new EngineException(
                "历史任务查询至少需要一个条件（processInstanceId / assignee / processDefinitionId / taskDefinitionKey）。"
                + "本子集不提供无条件的历史全表扫描。");
        }
    }

    private static HistoricTaskFilter ToFilter(HistoricTaskQuery query) =>
        new(
            ProcessInstanceId: query.ProcessInstanceId,
            Assignee: query.Assignee,
            OnlyFinished: query.OnlyFinished,
            ProcessDefinitionId: query.ProcessDefinitionId,
            TaskDefinitionKey: query.TaskDefinitionKey,
            FinishedAfter: query.FinishedAfter,
            FinishedBefore: query.FinishedBefore,
            TenantId: query.TenantId,
            FirstResult: query.FirstResult,
            MaxResults: query.MaxResults);

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
    public async ValueTask<IReadOnlyList<HistoricIdentityLink>> FindHistoricIdentityLinksAsync(
        HistoricIdentityLinkQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);

        if (!query.HasCriteria)
        {
            throw new EngineException(
                "历史身份关系查询至少需要一个条件（userId / groupIds / processInstanceId / taskId）。"
                + "本子集不提供无条件的历史全表扫描。");
        }

        if (_historyManager is IHistoricQueryManager pushdown)
        {
            return await pushdown
                .QueryIdentityLinksAsync(
                    new HistoricIdentityLinkFilter(
                        UserId: string.IsNullOrWhiteSpace(query.UserId) ? null : query.UserId,
                        GroupIds: query.GroupIds is { Count: > 0 } ? query.GroupIds : null,
                        ProcessInstanceId: string.IsNullOrWhiteSpace(query.ProcessInstanceId)
                            ? null
                            : query.ProcessInstanceId,
                        TaskId: string.IsNullOrWhiteSpace(query.TaskId) ? null : query.TaskId,
                        Type: string.IsNullOrWhiteSpace(query.Type) ? null : query.Type,
                        FirstResult: query.FirstResult,
                        MaxResults: query.MaxResults),
                    ct)
                .ConfigureAwait(false);
        }

        // 没有下推能力（历史关闭 / 内存宿主）：历史身份关系本来就是历史才能产出的数据源，
        // 这里只能如实返回空——不能退回运行态冒充「已办」，否则会漏掉全部已结束的任务。
        return [];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HistoricProcessInstance>> FindHistoricProcessInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);

        if (_historyManager is IHistoricQueryManager pushdown)
        {
            var filter = await BuildHistoricProcessInstanceFilterAsync(query, ct).ConfigureAwait(false);

            return filter is null
                ? []
                : await pushdown.QueryProcessInstancesAsync(filter, ct).ConfigureAwait(false);
        }

        var instances = await CollectHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false);

        return
        [
            .. ApplyPaging(
                instances,
                query.FirstResult,
                query.MaxResults,
                instance => instance.StartTime,
                instance => instance.Id,
                descending: query.Descending),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountHistoricProcessInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(0, null);

        if (_historyManager is IHistoricQueryManager pushdown)
        {
            var filter = await BuildHistoricProcessInstanceFilterAsync(query, ct).ConfigureAwait(false);

            return filter is null
                ? 0
                : await pushdown
                    .CountProcessInstancesAsync(filter with { FirstResult = 0, MaxResults = null }, ct)
                    .ConfigureAwait(false);
        }

        return (await CollectHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false)).Count;
    }

    /// <inheritdoc />
    public async ValueTask<PagedQueryResult<HistoricProcessInstance>> FindHistoricProcessInstancesWithCountAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePaging(query.FirstResult, query.MaxResults);

        if (_historyManager is IHistoricQueryManager pushdown)
        {
            var filter = await BuildHistoricProcessInstanceFilterAsync(query, ct).ConfigureAwait(false);

            // 零匹配（key 解析不出定义 / 白名单为空）：不必查库，页与总数一起归零
            if (filter is null)
            {
                return new PagedQueryResult<HistoricProcessInstance>([], 0);
            }

            return await pushdown.QueryProcessInstancesWithCountAsync(filter, ct).ConfigureAwait(false);
        }

        var instances = await FindHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false);
        var total = await CountHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false);

        return new PagedQueryResult<HistoricProcessInstance>(instances, total);
    }

    /// <summary>
    /// 把门面查询翻译成持久化过滤条件；按 key 解析不出任何定义时返回 <see langword="null"/>
    /// （语义是「零匹配」，不能退化成「不加过滤」）。
    /// </summary>
    private async ValueTask<HistoricProcessInstanceFilter?> BuildHistoricProcessInstanceFilterAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct)
    {
        // 同运行态：空集合 = 「跟任何实例都无关」，必须落成零匹配，不能退化成不过滤。
        if (query.ProcessInstanceIds is { Count: 0 }
            || query.ProcessDefinitionIds is { Count: 0 })
        {
            return null;
        }

        IReadOnlyCollection<string>? definitionIds = null;

        if (!string.IsNullOrWhiteSpace(query.ProcessDefinitionKey))
        {
            var resolved = await ResolveDefinitionIdsByKeyAsync(query.ProcessDefinitionKey, query.TenantId, ct)
                .ConfigureAwait(false);

            if (resolved.Count == 0)
            {
                return null;
            }

            definitionIds = resolved;
        }

        // 显式给的定义 id 白名单与 key 过滤取交集（两者都有时语义是「这些 key 里我只关心这几个版本」）
        var effectiveIds = query.ProcessDefinitionIds;

        if (definitionIds is not null)
        {
            effectiveIds = effectiveIds is null
                ? definitionIds
                : [.. effectiveIds.Intersect(definitionIds, StringComparer.Ordinal)];

            if (effectiveIds.Count == 0)
            {
                return null;
            }
        }

        return new HistoricProcessInstanceFilter(
            ProcessDefinitionId: query.ProcessDefinitionId,
            ProcessDefinitionIds: effectiveIds,
            ProcessInstanceIds: query.ProcessInstanceIds,
            BusinessKey: query.BusinessKey,
            StartUserId: query.StartUserId,
            Finished: query.Finished,
            Deleted: query.Deleted,
            StartedAfter: query.StartedAfter,
            StartedBefore: query.StartedBefore,
            EndedAfter: query.EndedAfter,
            EndedBefore: query.EndedBefore,
            TenantId: query.TenantId,
            TenantIds: query.TenantIds,
            FirstResult: query.FirstResult,
            MaxResults: query.MaxResults,
            Descending: query.Descending);
    }

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
        && (query.ProcessDefinitionIds is null || query.ProcessDefinitionIds.Contains(instance.ProcessDefinitionId))
        && (query.ProcessInstanceIds is null || query.ProcessInstanceIds.Contains(instance.Id))
        && (string.IsNullOrWhiteSpace(query.BusinessKey)
            || string.Equals(instance.BusinessKey, query.BusinessKey, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(query.StartUserId)
            || string.Equals(instance.StartUserId, query.StartUserId, StringComparison.Ordinal))
        && (query.TenantId is null
            || string.Equals(instance.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal))
        && (query.TenantIds is null
            || query.TenantIds.Contains(instance.TenantId ?? string.Empty))
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

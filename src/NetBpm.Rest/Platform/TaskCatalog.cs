using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow;
using PlatformTaskQuery = NetBpm.Platform.Contracts.TaskQuery;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 待办读模型（<c>/workflow/v1/tasks</c>）——把引擎的 <see cref="TaskEntity"/> 组装成
/// 调用方能直接渲染审批页的 <see cref="TaskView"/> 与待办箱的 <see cref="TaskSummary"/>。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IProcessCatalog"/> 的分工：目录回答「有哪些流程、每条流程是什么样」，
/// 本服务回答「我名下有哪些待办、这条待办我能点哪几个按钮」。
/// </para>
/// <para>
/// <see cref="TaskView.Options"/> 是<b>服务端算好</b>的可选项集（三码模型），调用方只按
/// <see cref="ApprovalOption.Semantic"/> / <see cref="ApprovalOption.OutcomeClass"/> 渲染按钮，
/// 永远不按 <c>optionId</c> 硬编码——流程定义怎么改选项，调用方代码都不用动。
/// </para>
/// <para>
/// 数据级授权（租户边界、流程白名单、任务归属）与 scope（能力上限）两层都要过：
/// scope 决定「能不能查待办」，归属决定「这条待办是不是你的」。
/// </para>
/// </remarks>
public interface ITaskCatalog
{
    /// <summary>分页查当前调用方的待办（已按租户 + 流程白名单过滤）。</summary>
    /// <param name="query">查询条件（scope 决定查谁）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="FormatException">游标格式不正确。</exception>
    ValueTask<CursorPage<TaskSummary>> ListAsync(
        PlatformTaskQuery query,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 查单条待办详情（含 options）。未授权、不存在、或已完成的待办都返回 <see langword="null"/>。
    /// </summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<TaskView?> FindAsync(
        string taskId,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 按流程实例组装<b>全部</b>待办视图（含 options），供实例读模型复用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是「实例上的待办长什么样」的单一出口：实例读模型（<c>InstanceView.Tasks</c>）与
    /// 提交结果（<c>SubmitTaskResult.NextTasks</c>）都从这里取，保证同一个待办在两个响应里长得一样，
    /// 而不是各自再拼一份（口径分叉的来源）。
    /// </para>
    /// <para>授权口径与 <see cref="FindAsync"/> 一致：租户边界 + 流程白名单在结果层过滤，不泄露归属。</para>
    /// </remarks>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<TaskView>> BuildViewsForInstanceAsync(
        string processInstanceId,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TaskCatalog : ITaskCatalog
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    private readonly IProcessEngine _engine;
    private readonly IProcessDefinitionStore _definitionStore;
    private readonly IApprovalVocabulary _vocabulary;
    private readonly IApprovalTimelineStore _timelineStore;
    private readonly ILogger<TaskCatalog> _logger;

    /// <summary>构造。</summary>
    /// <param name="engine">引擎门面（任务真源）。</param>
    /// <param name="definitionStore">流程定义仓储（任务 → 流程 key / 名称）。</param>
    /// <param name="vocabulary">审批语义字典（options 的语义解析）。</param>
    /// <param name="timelineStore">审批时间线（「我批了什么」只在写时定稿的那份里有）。</param>
    /// <param name="logger">日志。</param>
    public TaskCatalog(
        IProcessEngine engine,
        IProcessDefinitionStore definitionStore,
        IApprovalVocabulary vocabulary,
        IApprovalTimelineStore timelineStore,
        ILogger<TaskCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(definitionStore);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(timelineStore);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _definitionStore = definitionStore;
        _vocabulary = vocabulary;
        _timelineStore = timelineStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<CursorPage<TaskSummary>> ListAsync(
        PlatformTaskQuery query,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        // 「我的已办」与「我的待办」是两个数据源（历史任务 vs 运行任务），
        // 在这里分岔：一个方法只认一条数据路径，比在一个循环里 if/else 两种来源好维护
        return query.Scope == TaskScope.Done
            ? await ListDoneAsync(query, context, limit, ct).ConfigureAwait(false)
            : await ListPendingAsync(query, context, limit, ct).ConfigureAwait(false);
    }

    /// <summary>分页查待办（运行态任务，<c>ACT_RU_TASK</c>）。</summary>
    private async ValueTask<CursorPage<TaskSummary>> ListPendingAsync(
        PlatformTaskQuery query,
        RequestContext context,
        int limit,
        CancellationToken ct)
    {
        // scope 决定「查谁的待办」；ALL 需要管理权限，服务端判定，不靠调用方自觉。
        var (assignee, candidateUser, allUsers) = ResolveScope(query.Scope, context);

        var engineQuery = new Runtime.TaskQuery(
            Assignee: assignee,
            CandidateUser: candidateUser,
            ProcessDefinitionId: null,
            TaskDefinitionKey: query.ActivityId,
            AllUsers: allUsers,
            SortBy: "createTime",
            Descending: false);

        var tasks = await _engine.FindTasksAsync(engineQuery, ct).ConfigureAwait(false);

        // 客户端白名单：调用方看不到自己未被授权的流程（按「不存在」处理，不标灰）。
        var summaries = new List<TaskSummary>(tasks.Count);

        foreach (var task in tasks)
        {
            if (!IsAuthorizedForTask(task, context))
            {
                continue;
            }

            var definition = await ResolveDefinitionAsync(task, ct).ConfigureAwait(false);
            var userTask = await ResolveUserTaskAsync(task, ct).ConfigureAwait(false);

            // 客户端白名单：调用方看不到自己未被授权流程的待办（按「不存在」处理，不标灰）
            if (definition is not null && !context.CanUseProcess(definition.Key))
            {
                continue;
            }

            var summary = BuildSummary(task, definition, userTask);

            if (summary is not null)
            {
                summaries.Add(summary);
            }
        }

        // 关键词过滤（标题 / 业务主键 / 环节名），在内存里做——引擎任务表不承载标题。
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            summaries = summaries
                .Where(item =>
                    (item.Title?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.BusinessKey?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.ActivityName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.Summary?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        if (query.OverdueOnly == true)
        {
            var now = DateTimeOffset.UtcNow;
            summaries = summaries
                .Where(item => item.DueAt is not null && item.DueAt < now)
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(query.ProcessKey))
        {
            summaries = summaries
                .Where(item => string.Equals(item.ProcessKey, query.ProcessKey, StringComparison.Ordinal))
                .ToList();
        }

        return Page(summaries, query.Cursor, limit);
    }

    /// <summary>
    /// 分页查<b>我的已办</b>（<c>ACT_HI_TASKINST</c> 中办理人是我的任务）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 数据路径与待办完全不同：待办在 <c>ACT_RU_TASK</c>，任务一完成行就被删除，
    /// 所以「已办」<b>只能</b>查历史任务。这也是为什么历史关闭的宿主（多数内存宿主）里
    /// 这条列表恒为空——不是 bug，而是数据当时就没写进去。
    /// </para>
    /// <para>
    /// 排序按<b>办结时间倒序</b>（最近办的在前）：已办列表的使用场景是回溯
    /// 「我最近批了什么」，按创建时间排会让刚办完的单子埋在前面几条之外。
    /// </para>
    /// <para>
    /// 无真人主体（纯服务令牌）时返回空页而不是报错：服务令牌没有「我」这层身份，
    /// 端点的 scope 已经保证它进不来（<c>workflow:history:read</c>），这里再抛错是重复劳动。
    /// </para>
    /// </remarks>
    private async ValueTask<CursorPage<TaskSummary>> ListDoneAsync(
        PlatformTaskQuery query,
        RequestContext context,
        int limit,
        CancellationToken ct)
    {
        if (context.SubjectId is not { Length: > 0 } subject)
        {
            return new CursorPage<TaskSummary>([], null, false);
        }

        // 已办的判定只有一个条件：历史任务的办理人。这个条件 SQL 能直接下推（见 HistoricTaskFilter.Assignee）
        var tasks = await _engine
            .FindHistoricTasksAsync(new HistoricTaskQuery(Assignee: subject), ct)
            .ConfigureAwait(false);

        var summaries = new List<TaskSummary>(tasks.Count);

        foreach (var task in tasks)
        {
            if (!IsAuthorizedForHistoricTask(task, context))
            {
                continue;
            }

            var definition = await ResolveDefinitionByIdAsync(task.ProcessDefinitionId, ct: ct).ConfigureAwait(false);

            // 客户端白名单：未授权流程的已办按「不存在」处理（与待办同口径）
            if (definition is not null && !context.CanUseProcess(definition.Key))
            {
                continue;
            }

            summaries.Add(await BuildDoneSummaryAsync(task, definition, ct).ConfigureAwait(false));
        }

        if (!string.IsNullOrWhiteSpace(query.ActivityId))
        {
            summaries = summaries
                .Where(item => string.Equals(item.ActivityName, query.ActivityId, StringComparison.Ordinal))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            summaries = summaries
                .Where(item =>
                    (item.Title?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.BusinessKey?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (item.ActivityName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(query.ProcessKey))
        {
            summaries = summaries
                .Where(item => string.Equals(item.ProcessKey, query.ProcessKey, StringComparison.Ordinal))
                .ToList();
        }

        // 「批了什么」只能从时间线补：引擎的历史任务表里压根没有审批意见这一列
        summaries.Sort(static (left, right) =>
        {
            // 办结时间倒序；同刻按任务 id 升序保证翻页稳定
            var byTime = (right.CompletedAt ?? DateTimeOffset.MinValue).CompareTo(
                left.CompletedAt ?? DateTimeOffset.MinValue);

            return byTime != 0 ? byTime : string.CompareOrdinal(left.TaskId, right.TaskId);
        });

        // 分页之后才补「我批了什么」：只给真正要返回的这一页查时间线，
        // 而不是给过滤后的全部结果查一遍（已办是内存分页，量级就是用户批过的全部任务）
        var donePage = Page(summaries, query.Cursor, limit);

        return donePage.Items.Count == 0
            ? donePage
            : donePage with { Items = await AttachDecisionsAsync(donePage.Items, ct).ConfigureAwait(false) };
    }

    /// <summary>
    /// 给一批已办补上「当时批了什么」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 数据来源只有一处：<b>写时定稿</b>的时间线。引擎的 <c>ACT_HI_TASKINST</c> 记的是
    /// 「谁在几点把任务点了完成」，审批意见与选项语义从来没进过引擎的表
    /// （那是业务概念）。所以这里查不到时要老实返回原样，不能「按 result 变量倒推」——
    /// 倒推出来的是一条编出来的决定，比缺一条更糟。
    /// </para>
    /// <para>
    /// 同一任务若有多条 <c>TASK_COMPLETED</c>（退回重办会产生新任务 id，理论上不会重号；
    /// 真重了就取最后一条），与 <c>afterSequence</c> 增量拉取同一口径。
    /// </para>
    /// <para>
    /// 查失败只降级：少「我批了什么」不至于让已办列表打不开，反过来才是事故。
    /// </para>
    /// </remarks>
    private async ValueTask<IReadOnlyList<TaskSummary>> AttachDecisionsAsync(
        IReadOnlyList<TaskSummary> tasks,
        CancellationToken ct)
    {
        if (tasks.Count == 0)
        {
            return tasks;
        }

        Dictionary<string, Decision> decisions;

        try
        {
            var entries = await _timelineStore
                .ListDecisionsByTaskIdsAsync([.. tasks.Select(task => task.TaskId)], ct)
                .ConfigureAwait(false);

            decisions = [];

            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.TaskId))
                {
                    // 升序读取后同 key 取最后一条 = 序号最大的那条
                    decisions[entry.TaskId] = ToDecision(entry);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "读取已办的审批决定失败（{Count} 条）：本次结果不带 decision。",
                tasks.Count);

            return tasks;
        }

        if (decisions.Count == 0)
        {
            return tasks;
        }

        var result = new TaskSummary[tasks.Count];

        for (var i = 0; i < tasks.Count; i++)
        {
            result[i] = decisions.TryGetValue(tasks[i].TaskId, out var decision)
                ? tasks[i] with { Decision = decision }
                : tasks[i];
        }

        return result;
    }

    private static Decision ToDecision(TimelineEntry entry) =>
        new(
            OptionId: entry.OptionId,
            Semantic: entry.Semantic,
            Comment: entry.Comment,
            DecidedAt: entry.OccurredAt);

    /// <summary>已办任务是否对当前调用方可见（租户边界，与待办同口径）。</summary>
    private static bool IsAuthorizedForHistoricTask(HistoricTaskInstance task, RequestContext context)
    {
        if (!string.IsNullOrEmpty(task.TenantId)
            && context.EffectiveTenantId is not null
            && !string.Equals(task.TenantId, context.EffectiveTenantId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>把一条历史任务组装成已办摘要。</summary>
    private async ValueTask<TaskSummary> BuildDoneSummaryAsync(
        HistoricTaskInstance task,
        ProcessDefinition? definition,
        CancellationToken ct)
    {
        // 业务主键不在任务表上：按实例补一次（历史实例一实例一行，代价可控）
        var businessKey = await TryResolveBusinessKeyAsync(task.ProcessInstanceId, ct).ConfigureAwait(false);

        return new TaskSummary(
            TaskId: task.Id,
            InstanceId: task.ProcessInstanceId ?? string.Empty,
            ProcessKey: definition?.Key ?? task.ProcessDefinitionId ?? string.Empty,
            ProcessName: definition?.Name ?? definition?.Key ?? task.ProcessDefinitionId ?? string.Empty,
            BusinessKey: businessKey,
            Title: null,
            ActivityName: task.Name ?? task.TaskDefinitionKey,
            Assignee: task.Assignee,
            CreatedAt: AsOffset(task.StartTime),
            DueAt: null,
            StartedBy: null,
            Summary: null,
            EntryUrl: null,
            CompletedAt: AsOffset(task.EndTime));
    }

    /// <summary>补业务主键：历史查不到就返回空（不该让一条已办因为拿不到单号而消失）。</summary>
    private async ValueTask<string?> TryResolveBusinessKeyAsync(
        string? processInstanceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(processInstanceId))
        {
            return null;
        }

        try
        {
            var instance = await _engine
                .FindHistoricProcessInstanceAsync(processInstanceId, ct)
                .ConfigureAwait(false);

            return instance?.BusinessKey;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "已办列表解析业务主键失败（{ProcessInstanceId}）。",
                processInstanceId);

            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<TaskView?> FindAsync(
        string taskId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(context);

        var task = await _engine.GetTaskAsync(taskId, ct).ConfigureAwait(false);

        if (task is null)
        {
            return null;
        }

        if (!IsAuthorizedForTask(task, context))
        {
            // 未授权与不存在同口径：返回 404，不泄露「这条待办存在」
            return null;
        }

        var definition = await ResolveDefinitionAsync(task, ct).ConfigureAwait(false);

        // 客户端白名单：未授权流程的待办按「不存在」处理（404），不泄露归属
        if (definition is not null && !context.CanUseProcess(definition.Key))
        {
            return null;
        }

        var userTask = await ResolveUserTaskAsync(task, ct).ConfigureAwait(false);

        return await BuildViewAsync(task, definition, userTask, context, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TaskView>> BuildViewsForInstanceAsync(
        string processInstanceId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentNullException.ThrowIfNull(context);

        var tasks = await _engine.FindTasksAsync(
            new Runtime.TaskQuery(ProcessInstanceId: processInstanceId), ct).ConfigureAwait(false);

        var views = new List<TaskView>(tasks.Count);

        foreach (var task in tasks)
        {
            if (!IsAuthorizedForTask(task, context))
            {
                continue;
            }

            var definition = await ResolveDefinitionAsync(task, ct).ConfigureAwait(false);

            // 客户端白名单：未授权流程的待办按「不存在」处理（与 FindAsync 同口径）
            if (definition is not null && !context.CanUseProcess(definition.Key))
            {
                continue;
            }

            var userTask = await ResolveUserTaskAsync(task, ct).ConfigureAwait(false);
            var view = await BuildViewAsync(task, definition, userTask, context, ct).ConfigureAwait(false);

            if (view is not null)
            {
                views.Add(view);
            }
        }

        return views;
    }

    /// <summary>
    /// 解析 scope 到引擎查询条件。ALL 需要管理权限（超管或历史全读），否则回落到 MINE。
    /// </summary>
    private static (string? Assignee, string? CandidateUser, bool AllUsers) ResolveScope(
        TaskScope scope,
        RequestContext context)
    {
        return scope switch
        {
            TaskScope.Claimed => (context.SubjectId, null, false),
            TaskScope.All when CanReadAll(context) => (null, null, true),
            _ => (null, context.SubjectId, false),
        };
    }

    /// <summary>查全员的待办需要管理权限（超管 scope 或跨人历史读取）。</summary>
    private static bool CanReadAll(RequestContext context) =>
        context.IsAdmin || PlatformAuthorization.HasScope(context, PlatformScopes.HistoryReadAll);

    /// <summary>任务是否对当前调用方可见：租户边界 + 流程白名单。</summary>
    private static bool IsAuthorizedForTask(TaskEntity task, RequestContext context)
    {
        // 租户边界：无租户部署（TENANT_ID_ = ''）视为全局共享，不因客户端租户而拦截；
        // 任务带租户时，必须与调用方租户一致。
        if (!string.IsNullOrEmpty(task.TenantId)
            && context.EffectiveTenantId is not null
            && !string.Equals(task.TenantId, context.EffectiveTenantId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>解析任务所属的流程定义（拿 key / 名称）。</summary>
    private ValueTask<ProcessDefinition?> ResolveDefinitionAsync(
        TaskEntity task,
        CancellationToken ct) =>
        ResolveDefinitionByIdAsync(task.ProcessDefinitionId, task.Id, ct);

    /// <summary>
    /// 按定义 id 取流程定义。已办（历史任务）与待办（运行任务）共用这一条：
    /// 两者的白名单判定必须一致，否则会出现「待办看不到、已办看得到」的越权漏洞。
    /// </summary>
    private async ValueTask<ProcessDefinition?> ResolveDefinitionByIdAsync(
        string? processDefinitionId,
        string? taskId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
        {
            return null;
        }

        try
        {
            return await _definitionStore
                .FindByIdAsync(processDefinitionId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "解析任务 {TaskId} 的流程定义失败（{ProcessDefinitionId}）。",
                taskId ?? "(历史任务)",
                processDefinitionId);

            return null;
        }
    }

    /// <summary>解析任务对应的 BPMN userTask（拿候选关系 / 表单字段）。</summary>
    private async ValueTask<UserTask?> ResolveUserTaskAsync(TaskEntity task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.ProcessDefinitionId)
            || string.IsNullOrWhiteSpace(task.TaskDefinitionKey))
        {
            return null;
        }

        try
        {
            var model = await _engine.GetBpmnModelAsync(task.ProcessDefinitionId, ct).ConfigureAwait(false);
            var process = model?.GetMainProcess();

            return process?
                .FindFlowElementsOfType<UserTask>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, task.TaskDefinitionKey, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "解析任务 {TaskId} 的 userTask 失败（{ProcessDefinitionId}/{TaskDefinitionKey}）。",
                task.Id,
                task.ProcessDefinitionId,
                task.TaskDefinitionKey);

            return null;
        }
    }

    /// <summary>组装待办摘要。</summary>
    private static TaskSummary? BuildSummary(
        TaskEntity task,
        ProcessDefinition? definition,
        UserTask? userTask)
    {
        if (definition is null)
        {
            // 定义被删（理论上外键会挡住），降级：拿不到流程名仍给出可用摘要
            return new TaskSummary(
                TaskId: task.Id,
                InstanceId: task.ProcessInstanceId ?? string.Empty,
                ProcessKey: definition?.Key ?? task.ProcessDefinitionId ?? string.Empty,
                ProcessName: definition?.Name ?? task.ProcessDefinitionId ?? string.Empty,
                BusinessKey: task.BusinessKey,
                Title: null,
                ActivityName: userTask?.Name ?? task.Name,
                Assignee: task.Assignee,
                CreatedAt: AsOffset(task.CreateTime),
                DueAt: AsOffset(task.DueDate),
                StartedBy: null,
                Summary: null,
                EntryUrl: null);
        }

        var processKey = definition.Key;
        var processName = definition.Name ?? processKey;
        var activityName = userTask?.Name ?? task.Name ?? task.TaskDefinitionKey;

        return new TaskSummary(
            TaskId: task.Id,
            InstanceId: task.ProcessInstanceId ?? string.Empty,
            ProcessKey: processKey,
            ProcessName: processName,
            BusinessKey: task.BusinessKey,
            Title: null,
            ActivityName: activityName,
            Assignee: task.Assignee,
            CreatedAt: AsOffset(task.CreateTime),
            DueAt: AsOffset(task.DueDate),
            StartedBy: null,
            Summary: null,
            EntryUrl: null);
    }

    /// <summary>组装待办详情（含 options）。</summary>
    private async ValueTask<TaskView?> BuildViewAsync(
        TaskEntity task,
        ProcessDefinition? definition,
        UserTask? userTask,
        RequestContext context,
        CancellationToken ct)
    {
        var processKey = definition?.Key ?? task.ProcessDefinitionId ?? string.Empty;

        // options：从 approvalResult 的 activiti:value 派生，语义由字典解析
        var optionIds = ReadApprovalOptionIds(userTask);

        var options = optionIds.Count == 0
            ? []
            : await _vocabulary.ResolveOptionsAsync(
                processKey,
                context.EffectiveTenantId,
                task.TaskDefinitionKey,
                optionIds,
                ct).ConfigureAwait(false);

        // 字段派生规则与流程级表单规范（/processes/{key}/form/schema）共用一份
        var form = FormSchemaBuilder.BuildFields(userTask);

        return new TaskView(
            TaskId: task.Id,
            InstanceId: task.ProcessInstanceId ?? string.Empty,
            ActivityId: task.TaskDefinitionKey,
            ActivityName: userTask?.Name ?? task.Name,
            Assignee: task.Assignee,
            CandidateUsers: [.. userTask?.CandidateUsers ?? []],
            CandidateGroups: [.. userTask?.CandidateGroups ?? []],
            CreatedAt: AsOffset(task.CreateTime),
            DueAt: AsOffset(task.DueDate),
            Options: options,
            Form: form,
            CurrentUserCanSubmit: CanSubmit(task, userTask, context),
            ProcessKey: definition?.Key,
            ProcessDefinitionId: definition?.Id ?? task.ProcessDefinitionId,
            DefinitionVersion: definition?.Version);
    }

    /// <summary>从 approvalResult 的表单属性读出选项 id 与显示名。</summary>
    private static IReadOnlyList<(string OptionId, string? Name)> ReadApprovalOptionIds(UserTask? userTask)
    {
        var property = userTask?.FormProperties
            .FirstOrDefault(item => string.Equals(
                item.Id, ApprovalResults.FormPropertyId, StringComparison.Ordinal));

        return property is null
            ? []
            : [.. property.Values
                .Where(value => !string.IsNullOrWhiteSpace(value.Id))
                .Select(value => (value.Id!, value.Name))];
    }

    /// <summary>
    /// 当前主体能否办理：必须有个真人主体，且是办理人、候选用户，或候选组命中。
    /// 服务端已算好，调用方不必自己猜——猜错了就是一次 403。
    /// </summary>
    /// <remarks>
    /// 候选组命中靠 JWT 的 <c>groups</c> 声明（<see cref="RequestContext.Groups"/>）；
    /// 客户端凭据模式下「系统代用户」没有组声明，此时只能命中显式 assignee 或候选用户。
    /// </remarks>
    private static bool CanSubmit(TaskEntity task, UserTask? userTask, RequestContext context)
    {
        if (!context.CanSubmitApproval || string.IsNullOrWhiteSpace(context.SubjectId))
        {
            return false;
        }

        // 已是办理人
        if (string.Equals(task.Assignee, context.SubjectId, StringComparison.Ordinal))
        {
            return true;
        }

        // 候选用户命中
        if (userTask?.CandidateUsers.Contains(context.SubjectId, StringComparer.Ordinal) == true)
        {
            return true;
        }

        // 候选组命中（令牌里带 groups 声明）
        if (userTask is not null
            && context.Groups.Count > 0
            && userTask.CandidateGroups.Any(group => context.Groups.Contains(group)))
        {
            return true;
        }

        return false;
    }

    /// <summary>把可空 <see cref="DateTime"/> 转成 <see cref="DateTimeOffset"/>（数据库读回是 Unspecified，按 UTC）。</summary>
    private static DateTimeOffset? AsOffset(DateTime? value) =>
        value is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    /// <summary>cursor 分页（服务端生成的不透明游标）。</summary>
    private static CursorPage<TaskSummary> Page(
        List<TaskSummary> items,
        string? cursor,
        int limit)
    {
        var start = 0;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!int.TryParse(cursor, out start) || start < 0)
            {
                throw new FormatException("分页游标不合法。");
            }
        }

        var page = items.Skip(start).Take(limit).ToList();
        var nextStart = start + page.Count;
        var hasMore = nextStart < items.Count;

        return new CursorPage<TaskSummary>(
            Items: page,
            NextCursor: hasMore ? nextStart.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            HasMore: hasMore);
    }
}

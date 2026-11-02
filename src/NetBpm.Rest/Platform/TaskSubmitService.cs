using Microsoft.Extensions.Logging;
using NetBpm.Engine;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 提交审批（<c>POST /workflow/v1/tasks/{id}/submit</c>）的编排服务。
/// </summary>
/// <remarks>
/// <para>
/// 职责边界：校验「这个决定合不合法」→ 落流程变量 → 完成引擎任务 →（驳回时）改道到目标节点 → 组装提交结果。
/// 校验只依赖 <see cref="ITaskCatalog.FindAsync"/> 返回的 <see cref="TaskView.Options"/>
/// （服务端已用语义字典算好），因此「调用方看到的按钮」与「服务端认可的按钮」是同一份，
/// 不会出现「前端能点、后端 422」的分裂。
/// </para>
/// <para>
/// 两层授权在进入校验前先过：scope（<see cref="PlatformScopes.TaskSubmit"/>，端点查）与
/// 真人主体 + 任务归属（本服务查）。归属不满足与任务不存在<b>同口径 404</b>，不泄露待办归属。
/// </para>
/// </remarks>
public interface ITaskSubmitService
{
    /// <summary>
    /// 提交审批。校验失败抛 <see cref="SubmitDecisionException"/>（携带稳定错误码与状态码）。
    /// </summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="request">提交请求。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<SubmitTaskResult> SubmitAsync(
        string taskId,
        SubmitTaskRequest request,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TaskSubmitService : ITaskSubmitService
{
    private readonly IProcessEngine _engine;
    private readonly ITaskCatalog _taskCatalog;
    private readonly IInstanceCatalog _instanceCatalog;
    private readonly IApprovalTimeline _timeline;
    private readonly IApprovalSnapshot _snapshots;
    private readonly IParameterSnapshotStore _parameterSnapshots;
    private readonly IProcessCatalog _processCatalog;
    private readonly ILogger<TaskSubmitService> _logger;

    /// <summary>构造。</summary>
    /// <param name="engine">引擎门面（完成任务）。</param>
    /// <param name="taskCatalog">待办读模型（选项与归属判定）。</param>
    /// <param name="instanceCatalog">实例读模型（提交结果的 Instance 单一出口）。</param>
    /// <param name="timeline">审批记录时间线（写 <c>TASK_COMPLETED</c> 与结束条目）。</param>
    /// <param name="snapshots">表单快照（冻住「当时的字段定义 + 值」）。</param>
    /// <param name="parameterSnapshots">参数修订历史（这次提交改了哪些参数）。</param>
    /// <param name="processCatalog">流程目录（参数规范：写侧按 <c>writableBy</c> 拒绝）。</param>
    /// <param name="logger">日志。</param>
    public TaskSubmitService(
        IProcessEngine engine,
        ITaskCatalog taskCatalog,
        IInstanceCatalog instanceCatalog,
        IApprovalTimeline timeline,
        IApprovalSnapshot snapshots,
        IParameterSnapshotStore parameterSnapshots,
        IProcessCatalog processCatalog,
        ILogger<TaskSubmitService> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(taskCatalog);
        ArgumentNullException.ThrowIfNull(instanceCatalog);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(parameterSnapshots);
        ArgumentNullException.ThrowIfNull(processCatalog);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _taskCatalog = taskCatalog;
        _instanceCatalog = instanceCatalog;
        _timeline = timeline;
        _snapshots = snapshots;
        _parameterSnapshots = parameterSnapshots;
        _processCatalog = processCatalog;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<SubmitTaskResult> SubmitAsync(
        string taskId,
        SubmitTaskRequest request,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // 真人主体：审批结论必须能追到一个人（纯服务令牌即便持有 submit scope 也拒绝）
        if (!context.CanSubmitApproval || string.IsNullOrWhiteSpace(context.SubjectId))
        {
            throw new SubmitDecisionException(
                ErrorCodes.AuthSubjectRequired,
                "缺少操作主体",
                StatusCodes.Status403Forbidden,
                "审批结论必须落到一个真人主体；纯服务令牌不得提交审批。");
        }

        var view = await _taskCatalog.FindAsync(taskId, context, ct).ConfigureAwait(false);

        // 待办不存在、已完成，或对当前调用方不可见 → 404（与读模型同口径，不泄露归属）
        if (view is null || !view.CurrentUserCanSubmit)
        {
            throw new SubmitDecisionException(
                ErrorCodes.TaskNotFound,
                "待办不存在",
                StatusCodes.Status404NotFound,
                $"待办 '{taskId}' 不存在、已完成，或当前调用方无权办理。");
        }

        var option = ResolveOption(view, request.OptionId);
        ValidateDecision(option, request);

        var variables = await BuildVariablesAsync(view, request, context, ct).ConfigureAwait(false);

        try
        {
            await _engine.CompleteTaskAsync(
                taskId,
                variables.Count == 0 ? null : variables,
                ToExpectedRevision(request.ExpectedRevision),
                ct).ConfigureAwait(false);
        }
        catch (OptimisticLockingException exception)
        {
            throw new SubmitDecisionException(
                ErrorCodes.ConcurrencyRevisionMismatch,
                "并发冲突",
                StatusCodes.Status409Conflict,
                exception.Message);
        }
        catch (EngineObjectNotFoundException)
        {
            // 校验通过但完成时任务已被并发办结 → 与「不存在」同口径 404
            throw new SubmitDecisionException(
                ErrorCodes.TaskNotFound,
                "待办不存在",
                StatusCodes.Status404NotFound,
                $"待办 '{taskId}' 已被办结。");
        }

        // 驳回：结论已生效，再把流程搬回目标节点（只在提交带了 targetNodeId 时发生）
        var jumpedToTarget = await MoveToTargetNodeAsync(view.InstanceId, request, ct).ConfigureAwait(false);

        // 任务已办结：参数变更另起一版修订（写失败不阻断——审批结论已经落地，
        // 这里抛错只会让调用方重试一次早已生效的提交）
        await AppendParameterRevisionAsync(view, request, context, ct).ConfigureAwait(false);

        return await BuildResultAsync(view, option, request, jumpedToTarget, context, ct).ConfigureAwait(false);
    }

    /// <summary>把这次提交携带的参数落成新一版修订（没有参数就什么都不写）。</summary>
    /// <remarks>
    /// 只记<b>本次提交的</b>参数，不把整份变量快照拍一遍：修订的语义是「这一次改了什么」，
    /// 每版都存全量会让「谁改了什么」重新变成一个需要 diff 才知道答案的问题。
    /// </remarks>
    private async ValueTask AppendParameterRevisionAsync(
        TaskView view,
        SubmitTaskRequest request,
        RequestContext context,
        CancellationToken ct)
    {
        if (request.Parameters is not { Count: > 0 } parameters)
        {
            return;
        }

        try
        {
            await _parameterSnapshots.AppendAsync(
                new ParameterRevision(
                    InstanceId: view.InstanceId,
                    TenantId: context.EffectiveTenantId ?? "default",
                    ProcessKey: view.ProcessKey,
                    Source: ParameterRevisionSources.Submit,
                    TaskId: view.TaskId,
                    ActivityId: view.ActivityId,
                    Values: parameters,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    UpdatedBy: context.SubjectId),
                ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "参数修订历史写入失败（{InstanceId} / 任务 {TaskId}）：审批已生效，但这次参数变更没有留痕。",
                view.InstanceId,
                view.TaskId);
        }
    }

    /// <summary>
    /// 驳回落地：完成审批之后把流程改道到 <c>targetNodeId</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 顺序是刻意的：<b>先完成任务，再改道</b>。先改道会让本次任务被「跳转」删掉而不是「审批完成」，
    /// 结论变量与任务历史都落不下来，审批页面就查无对证了。
    /// </para>
    /// <para>
    /// 代价要说清楚：改道的落点是「完成后的<b>下一个等待点</b>」，因此在搬到目标节点之前，
    /// 流程会照常从当前节点推进一步（这中间的 serviceTask 会照常执行）。
    /// 本引擎的跳转就是「定位当前等待点 → 改道目标节点」，没有「回到过去某个节点」的第二套语义。
    /// </para>
    /// <para>
    /// 搬不动时不回滚也不抛：结论已经生效，抛错只会让调用方重试一次「任务已不存在」的提交。
    /// 三种实情（实例在提交后已结束 / 停在多个并行分支 / 目标节点不在定义里）都写进日志，
    /// 时间线里的 <c>targetNodeId</c> 仍然记着「这次决定想去哪」—— 审计能看出「意图与结果不一致」。
    /// </para>
    /// </remarks>
    private async ValueTask<bool> MoveToTargetNodeAsync(
        string instanceId,
        SubmitTaskRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TargetNodeId))
        {
            return false;
        }

        try
        {
            await _engine
                .MoveActivityToAsync(instanceId, request.TargetNodeId, request.Comment, ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (EngineObjectNotFoundException)
        {
            _logger.LogWarning(
                "审批后跳转未完成（实例 {InstanceId} → 节点 {TargetNodeId}）：提交后流程已结束。",
                instanceId,
                request.TargetNodeId);
        }
        catch (EngineException exception)
        {
            _logger.LogWarning(
                exception,
                "审批后跳转未完成（实例 {InstanceId} → 节点 {TargetNodeId}）：审批结论已生效，流程停在流转后的位置。",
                instanceId,
                request.TargetNodeId);
        }

        return false;
    }

    /// <summary>在服务端已算好的选项集里找调用方回传的选项；没有就是 422。</summary>
    private static ApprovalOption ResolveOption(TaskView view, string optionId)
    {
        var option = view.Options.FirstOrDefault(item =>
            string.Equals(item.OptionId, optionId, StringComparison.Ordinal));

        if (option is null)
        {
            throw new SubmitDecisionException(
                ErrorCodes.DecisionNotInOptionSet,
                "选项不属于该节点",
                StatusCodes.Status422UnprocessableEntity,
                $"选项 '{optionId}' 不在该待办的可选项里（可选项："
                + string.Join(" / ", view.Options.Select(item => item.OptionId)) + "）。");
        }

        return option;
    }

    /// <summary>按 <see cref="ApprovalOption"/> 的校验规则核验本次决定。</summary>
    private static void ValidateDecision(ApprovalOption option, SubmitTaskRequest request)
    {
        // 一致性：调用方声明的 semantic 必须与字典定义一致（挡住「流程定义被改而调用方不知情」）
        if (request.Semantic is { } semantic && semantic != option.Semantic)
        {
            throw new SubmitDecisionException(
                ErrorCodes.DecisionSemanticMismatch,
                "语义不一致",
                StatusCodes.Status422UnprocessableEntity,
                $"调用方声明选项 '{request.OptionId}' 的语义为 {semantic}，"
                + $"但服务端解析为 {option.Semantic}。请按 TaskView.options 的语义重新提交。");
        }

        if (option.RequiresComment && string.IsNullOrWhiteSpace(request.Comment))
        {
            throw new SubmitDecisionException(
                ErrorCodes.DecisionCommentRequired,
                "需要填写审批意见",
                StatusCodes.Status422UnprocessableEntity,
                $"选项 '{request.OptionId}'（{option.Label}）要求填写审批意见。");
        }

        if (option.RequiresTargetNode && string.IsNullOrWhiteSpace(request.TargetNodeId))
        {
            throw new SubmitDecisionException(
                ErrorCodes.DecisionTargetNodeRequired,
                "需要指定目标节点",
                StatusCodes.Status422UnprocessableEntity,
                $"选项 '{request.OptionId}'（{option.Label}）要求指定目标节点。");
        }

        if (option.AllowedTargetNodes is { Count: > 0 }
            && request.TargetNodeId is { Length: > 0 } targetNodeId
            && !option.AllowedTargetNodes.Contains(targetNodeId, StringComparer.Ordinal))
        {
            throw new SubmitDecisionException(
                ErrorCodes.DecisionTargetNodeInvalid,
                "目标节点不在允许范围",
                StatusCodes.Status422UnprocessableEntity,
                $"目标节点 '{targetNodeId}' 不在选项 '{request.OptionId}' 允许的范围（"
                + string.Join(" / ", option.AllowedTargetNodes) + "）。");
        }
    }

    /// <summary>
    /// 组装要写进引擎的流程变量：审批结论（<c>result</c>/<c>comment</c>）+ 本次参数（镜像裸名）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 参数先过写侧授权（<c>writableBy</c>）：审批环节只认 <c>APPROVER</c>，
    /// <b>未声明 <c>writableBy</c> 的参数在提交时一律只读</b>——这正是
    /// <see cref="ParameterDefinition.WritableBy"/> 的原始约定（「发起时可写、之后只读」）。
    /// </para>
    /// <para>
    /// 读不到参数规范时按「无 schema 放行」：目录不可用不该让审批提交不了，
    /// 那时唯一能守的只有下面的保留名冲突检查。
    /// </para>
    /// <para>
    /// <c>targetNodeId</c> 不进流程变量：它是<b>改道指令</b>而不是业务数据，由
    /// <see cref="MoveToTargetNodeAsync"/> 在任务完成后执行引擎跳转（完成前改道会让任务被「跳转」
    /// 删掉而不是「审批完成」，结论落不下来）。
    /// </para>
    /// <para>
    /// <c>formValues</c> 不进流程变量：提交后由 <see cref="IApprovalSnapshot"/> 连同当时的 schema
    /// 一起冻进 <c>nb_approval_snapshot</c>，时间线条目只带 <c>formSnapshotTaskId</c> 引用它。
    /// </para>
    /// </remarks>
    private async ValueTask<Dictionary<string, object?>> BuildVariablesAsync(
        TaskView view,
        SubmitTaskRequest request,
        RequestContext context,
        CancellationToken ct)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [BaseModelVariables.Result] = request.OptionId,
        };

        if (!string.IsNullOrWhiteSpace(request.Comment))
        {
            variables[BaseModelVariables.Comment] = request.Comment;
        }

        if (request.Parameters is not { Count: > 0 } submitted)
        {
            return variables;
        }

        var definitions = (await TryLoadSchemaAsync(view.ProcessKey, context, ct).ConfigureAwait(false))?.Parameters
            ?? [];
        var callerIsAdmin = PlatformIdentity.HasAdminPerspective(context);

        foreach (var (name, value) in submitted)
        {
            // 对外键 param.{name} 镜像回引擎裸名（与发起 / 网关条件同一套变量名）
            var bareName = name.StartsWith("param.", StringComparison.Ordinal)
                ? name["param.".Length..]
                : name;

            if (BaseModelVariables.All.Contains(bareName, StringComparer.Ordinal))
            {
                throw new SubmitDecisionException(
                    ErrorCodes.ParameterConflictWithReserved,
                    "参数名与保留字段冲突",
                    StatusCodes.Status400BadRequest,
                    $"参数 '{name}' 与审批元数据字段 '{bareName}' 冲突。");
            }

            var definition = ParameterWritePolicy.Find(definitions, name, bareName);

            if (definition is not null && value is not null)
            {
                EnsureWritable(definition, name, callerIsAdmin);
            }

            variables[bareName] = value;
        }

        return variables;
    }

    /// <summary>审批环节不接受「写了但不让写」的参数（<c>writableBy</c> 不含 <c>APPROVER</c>）。</summary>
    /// <remarks>
    /// 422 而不是 403：这是<b>字段级</b>语义冲突（这个字段在这一环节不可写），
    /// 不是「你没有办这张单的资格」——后者已经在前面按 404 处理过了。
    /// </remarks>
    private static void EnsureWritable(ParameterDefinition definition, string name, bool callerIsAdmin)
    {
        if (ParameterWritePolicy.IsWritable(definition, ParameterWriteStage.Approve, callerIsAdmin))
        {
            return;
        }

        throw new SubmitDecisionException(
            ErrorCodes.ParameterReadOnly,
            "参数在审批环节不可写",
            StatusCodes.Status422UnprocessableEntity,
            $"参数 '{ParameterWritePolicy.DisplayName(definition, name)}' 不允许审批人写入"
            + $"（可写角色：{ParameterWritePolicy.DescribeWriters(definition, ParameterWriteStage.Approve)}）。");
    }

    /// <summary>取参数规范；读不到返回 <see langword="null"/>（由调用方按「无 schema 放行」降级）。</summary>
    private async ValueTask<ParameterSchema?> TryLoadSchemaAsync(
        string? processKey,
        RequestContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(processKey))
        {
            return null;
        }

        try
        {
            return await _processCatalog.GetParameterSchemaAsync(processKey, context, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "读流程 {ProcessKey} 的参数规范失败：本次不做写侧授权校验。", processKey);

            return null;
        }
    }

    /// <summary>把实例修订号（契约用 <see cref="long"/>）转成引擎的 <see cref="int"/> 乐观锁版本。</summary>
    private static int? ToExpectedRevision(long? expected)
    {
        if (expected is not { } value)
        {
            return null;
        }

        if (value is < 0 or > int.MaxValue)
        {
            throw new SubmitDecisionException(
                ErrorCodes.RequestMalformed,
                "修订号不合法",
                StatusCodes.Status400BadRequest,
                $"expectedRevision 必须在 [0, {int.MaxValue}] 内，实际为 {value}。");
        }

        return (int)value;
    }

    /// <summary>
    /// 组装提交结果：实例视图（单一出口）+ 新待办 + 本次时间线增量 + 事件类型。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 时间线条目<b>在业务完成之后才写</b>：引擎已经办结任务，这里失败只会少一条审计记录，
    /// 不能让调用方以为提交失败（<see cref="IApprovalTimeline.AppendAsync"/> 内部已按此取舍）。
    /// </para>
    /// <para>
    /// <see cref="SubmitTaskResult.TimelineDelta"/> 只带<b>本次操作新增</b>的条目，
    /// 调用方直接用 <c>?afterSequence=</c> 接着拉即可，不需要自己算序号。
    /// </para>
    /// </remarks>
    private async ValueTask<SubmitTaskResult> BuildResultAsync(
        TaskView view,
        ApprovalOption option,
        SubmitTaskRequest request,
        bool jumpedToTarget,
        RequestContext context,
        CancellationToken ct)
    {
        // 表单快照要早于时间线落：时间线条目里的 formSnapshotTaskId 是指向它的钥匙，
        // 先有键再有引用，避免出现「时间线指向一份不存在的快照」。
        // （反过来不成立：快照写不进去也不该挡住审批，见 IApprovalSnapshot 的取舍。）
        var snapshotCaptured = await _snapshots
            .CaptureAsync(view, request, request.OptionId, context, ct)
            .ConfigureAwait(false);

        var delta = new List<TimelineEntry>(2);

        // 本次审批决定：引擎不认识审批意见，只有在这里写下来，历史页才看得到「谁为什么批的」
        var completed = await _timeline.AppendAsync(
            view.InstanceId,
            new TimelineEntry(
                Sequence: 0,
                InstanceId: view.InstanceId,
                Kind: TimelineEntryKind.TaskCompleted,
                ActivityId: view.ActivityId,
                ActivityName: view.ActivityName,
                TaskId: view.TaskId,
                ActorId: context.SubjectId,
                ActorCallerId: null,
                OptionId: request.OptionId,
                Semantic: option.Semantic,
                Comment: request.Comment,
                TargetNodeId: request.TargetNodeId,
                FormSnapshotTaskId: snapshotCaptured ? view.TaskId : null,
                OccurredAt: DateTimeOffset.UtcNow),
            context,
            ct).ConfigureAwait(false);

        if (completed is not null)
        {
            delta.Add(completed);
        }

        // 驳回是审批流上最容易「查无对证」的一类动作：目标节点单独成条，
        // 历史页才能回答「这张单为什么又回到了填写环节」。跳转失败时不写这条（流程没动过）。
        if (jumpedToTarget)
        {
            var jumped = await _timeline.AppendAsync(
                view.InstanceId,
                new TimelineEntry(
                    Sequence: 0,
                    InstanceId: view.InstanceId,
                    Kind: TimelineEntryKind.InstanceJumped,
                    ActivityId: null,
                    ActivityName: null,
                    TaskId: null,
                    ActorId: context.SubjectId,
                    ActorCallerId: null,
                    OptionId: request.OptionId,
                    Semantic: option.Semantic,
                    Comment: request.Comment,
                    TargetNodeId: request.TargetNodeId,
                    FormSnapshotTaskId: null,
                    OccurredAt: DateTimeOffset.UtcNow),
                context,
                ct).ConfigureAwait(false);

            if (jumped is not null)
            {
                delta.Add(jumped);
            }
        }

        var instance = await _instanceCatalog.FindAsync(view.InstanceId, context, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"流程实例 '{view.InstanceId}' 在提交后查不到（引擎历史也没有）。");

        var finished = instance.Status is InstanceStatus.Completed or InstanceStatus.Terminated;

        var events = new List<string>(2) { "task.completed" };

        if (jumpedToTarget)
        {
            events.Add("instance.jumped");
        }

        if (finished)
        {
            events.Add("instance.completed");

            var terminal = await _timeline.AppendAsync(
                instance.InstanceId,
                new TimelineEntry(
                    Sequence: 0,
                    InstanceId: instance.InstanceId,
                    Kind: instance.Status == InstanceStatus.Completed
                        ? TimelineEntryKind.InstanceCompleted
                        : TimelineEntryKind.InstanceTerminated,
                    ActivityId: null,
                    ActivityName: null,
                    TaskId: null,
                    ActorId: null,
                    ActorCallerId: null,
                    OptionId: null,
                    Semantic: instance.Conclusion?.Semantic,
                    Comment: null,
                    TargetNodeId: null,
                    FormSnapshotTaskId: null,
                    OccurredAt: instance.EndedAt ?? DateTimeOffset.UtcNow),
                context,
                ct).ConfigureAwait(false);

            if (terminal is not null)
            {
                delta.Add(terminal);
            }
        }

        return new SubmitTaskResult(
            Instance: instance,
            NextTasks: instance.Tasks,
            TimelineDelta: delta,
            Events: events);
    }
}

/// <summary>
/// 提交审批的可预期拒绝（校验失败 / 归属不符 / 并发冲突），携带稳定错误码与状态码。
/// </summary>
/// <remarks>由端点捕获后转 <see cref="PlatformProblems.Create"/>，不经过全局异常处理器的旧错误体。</remarks>
internal sealed class SubmitDecisionException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="code">稳定错误码（见 <see cref="ErrorCodes"/>）。</param>
    /// <param name="title">短标题。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <param name="detail">具体说明。</param>
    public SubmitDecisionException(string code, string title, int status, string? detail = null)
        : base(title)
    {
        Code = code;
        Title = title;
        Status = status;
        Detail = detail;
    }

    /// <summary>稳定错误码。</summary>
    public string Code { get; }

    /// <summary>短标题。</summary>
    public string Title { get; }

    /// <summary>HTTP 状态码。</summary>
    public int Status { get; }

    /// <summary>具体说明。</summary>
    public string? Detail { get; }
}

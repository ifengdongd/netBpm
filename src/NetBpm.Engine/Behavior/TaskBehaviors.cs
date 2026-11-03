using System.Globalization;
using System.Text.RegularExpressions;
using NetBpm.Bpmn.Model;
using NetBpm.El;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Script;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 手工任务行为。对应 Activiti 的 <c>ManualTaskActivityBehavior</c> ——
/// 它继承自 <c>TaskActivityBehavior</c>，语义即默认行为：不产生待办，直接离开。
/// </summary>
public sealed class ManualTaskActivityBehavior : FlowNodeActivityBehavior
{
    public ManualTaskActivityBehavior(IEngineServices services)
        : base(services)
    {
    }
}

/// <summary>
/// 用户任务行为（P7 全生命周期）。对应 Activiti 的 <c>UserTaskActivityBehavior</c>。
/// <para><b>执行语义（逐条照搬）</b>：</para>
/// <list type="number">
///   <item><description>建任务：<c>TASK_DEF_KEY_ = userTask/@id</c>，<c>BUSINESS_KEY_</c> 取流程实例的；</description></item>
///   <item><description><c>name</c> 按表达式求值，<b>求值失败回退为原文</b>（Activiti 捕获异常后取字面值）；</description></item>
///   <item><description><c>dueDate</c> / <c>priority</c> / <c>category</c> / <c>formKey</c> 依次求值并做类型校验；</description></item>
///   <item><description><b>先插入任务、再处理办理关系</b>（分配需要任务 id）；</description></item>
///   <item><description>候选关系顺序为<b>先候选组、后候选用户</b>（直接影响身份关系行的 id 顺序）；</description></item>
///   <item><description>随后<b>停在节点上等待</b> —— 用户任务是本子集唯一的等待型节点。</description></item>
/// </list>
/// <para>
/// <b>触发语义</b>（<see cref="TriggerAsync"/>）：执行上若还有未删除的任务则抛错
/// （<c>UserTask should not be signalled before complete</c>），否则离开节点。
/// </para>
/// <para>
/// ⚠️ 已登记的不足：<c>activiti:businessCalendarName</c> 不在 §1.2 的属性清单内，
/// 因此 <c>dueDate</c> 只按 ISO-8601 日期串解析，其它取值一律响亮失败（不猜）。
/// </para>
/// </summary>
public sealed class UserTaskActivityBehavior : FlowNodeActivityBehavior
{
    /// <summary>Activiti 的候选值切分正则：<c>str.split("[\\s]*,[\\s]*")</c>。</summary>
    private static readonly Regex CandidateSeparator = new(@"[\s]*,[\s]*", RegexOptions.Compiled);

    public UserTaskActivityBehavior(IEngineServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var userTask = execution.CurrentFlowElement as UserTask
            ?? throw new EngineException(
                "Programmatic error: user task behaviour can only be applied to a UserTask instance, " +
                $"but got an instance of {execution.CurrentFlowElement}");

        var taskManager = RequireTaskManager();
        var executionEntity = execution.AsEntity();

        var task = taskManager.CreateTask();
        task.TaskDefinitionKey = userTask.Id;
        task.BusinessKey = await FindBusinessKeyAsync(executionEntity, ct).ConfigureAwait(false);

        task.Name = await ResolveTextAsync(userTask.Name, execution, ct).ConfigureAwait(false);
        task.DueDate = await ResolveDueDateAsync(userTask.DueDate, execution, ct).ConfigureAwait(false);
        task.Priority = await ResolvePriorityAsync(userTask.Priority, execution, ct).ConfigureAwait(false);
        task.Category = await ResolveTextAsync(userTask.Category, execution, ct).ConfigureAwait(false);
        task.FormKey = await ResolveTextAsync(userTask.FormKey, execution, ct).ConfigureAwait(false);

        // documentation 走历史行专用载体（见 TaskEntity.Documentation 的说明），
        // 与 name / category 等一样支持表达式求值（Activiti 对文档文本同样求值）。
        task.Documentation = await ResolveTextAsync(userTask.Documentation, execution, ct).ConfigureAwait(false);

        // 办理关系需要任务 id，因此必须「先插入、后分配」
        await taskManager.InsertTaskAsync(task, executionEntity, ct).ConfigureAwait(false);

        // 任务监听器（activiti:taskListener，event="create"）+ 流程级 TASK_CREATED 事件
        await ListenerInvoker
            .NotifyTaskListenersAsync(
                Services, task, execution, userTask.TaskListeners, ListenerInvoker.CreateEvent, ct)
            .ConfigureAwait(false);
        await EngineEventDispatcher
            .DispatchAsync(Services, execution, EngineEvents.TaskCreated, task.Id, ct)
            .ConfigureAwait(false);

        if (await ShouldSkipAsync(userTask, execution, ct).ConfigureAwait(false))
        {
            await taskManager.DeleteTaskAsync(task, null, ct).ConfigureAwait(false);
            await LeaveAsync(execution, ct).ConfigureAwait(false);
            return;
        }

        await HandleAssignmentsAsync(userTask, task, taskManager, execution, ct).ConfigureAwait(false);

        // 任务监听器（activiti:taskListener，event="assignment"）：仅在实际指派了办理人时触发（同 Activiti）
        if (!string.IsNullOrEmpty(task.Assignee))
        {
            await ListenerInvoker
                .NotifyTaskListenersAsync(
                    Services, task, execution, userTask.TaskListeners, ListenerInvoker.AssignmentEvent, ct)
                .ConfigureAwait(false);
        }

        // 定时边界事件：任务创建时挂作业（到点由宿主轮询触发，取消任务并转入边界分支）
        await ScheduleBoundaryTimersAsync(userTask, execution, ct).ConfigureAwait(false);

        // 用户任务不离开节点：等 CompleteTaskCommand 删除任务后由 TriggerExecutionOperation 推进
    }

    /// <summary>
    /// 为该用户任务挂上全部定时边界事件。
    /// <para>
    /// 只在<b>任务真正进入等待</b>后创建（skipExpression 跳过时不创建），
    /// 每个边界事件一条作业；任务完成 / 跳转 / 实例结束时由对应命令删除。
    /// </para>
    /// </summary>
    private async ValueTask ScheduleBoundaryTimersAsync(
        UserTask userTask,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        var timerJobs = Services.TimerJobManager;
        var resolver = Services.ProcessDefinitionResolver;

        if (timerJobs is null || resolver is null || string.IsNullOrEmpty(userTask.Id))
        {
            return;
        }

        var process = await resolver
            .ResolveProcessModelAsync(execution.ProcessDefinitionId, ct)
            .ConfigureAwait(false);

        if (process is null)
        {
            return;
        }

        var boundaries = process
            .FindFlowElementsOfType<BoundaryEvent>()
            .Where(boundary =>
                boundary.Timer is not null
                && string.Equals(boundary.AttachedToRef, userTask.Id, StringComparison.Ordinal))
            .ToArray();

        if (boundaries.Length == 0)
        {
            return;
        }

        var executionEntity = execution.AsEntity();
        var now = Services.Clock.UtcNow;

        foreach (var boundary in boundaries)
        {
            // 非中断型部署期已拒绝；这里再守一道，避免绕过校验直接塞模型
            if (!boundary.CancelActivity)
            {
                continue;
            }

            var (due, error) = await TimerDueDate
                .ComputeAsync(boundary.Timer!, Services.ExpressionManager, execution, now, ct)
                .ConfigureAwait(false);

            if (error is not null)
            {
                throw new EngineException($"边界事件 '{boundary.Id}' 的定时器无法创建：{error}");
            }

            await timerJobs.InsertAsync(
                new TimerJobEntity
                {
                    Id = Services.IdGenerator.NextId(),
                    ExecutionId = executionEntity.Id,
                    ProcessInstanceId = execution.ProcessInstanceId ?? executionEntity.Id,
                    ProcessDefinitionId = execution.ProcessDefinitionId,
                    ActivityId = userTask.Id,
                    BoundaryEventId = boundary.Id ?? string.Empty,
                    DueDate = due,
                    TenantId = executionEntity.TenantId,
                    CreateTime = now,
                },
                ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async ValueTask TriggerAsync(
        IDelegateExecution execution,
        string? signalName,
        object? signalData,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var taskManager = RequireTaskManager();

        var tasks = await taskManager.FindTasksByExecutionIdAsync(execution.Id, ct).ConfigureAwait(false);

        if (tasks.Count > 0)
        {
            throw new EngineException("UserTask should not be signalled before complete");
        }

        await LeaveAsync(execution, ct).ConfigureAwait(false);
    }

    // ─────────────────────────── 属性求值 ───────────────────────────

    /// <summary>
    /// 文本类属性求值（name / category / formKey）。
    /// <para>Activiti 对 name 的求值做了 try/catch：表达式不成立时回退为原文。</para>
    /// </summary>
    private async ValueTask<string?> ResolveTextAsync(
        string? rawValue,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return null;
        }

        try
        {
            var value = await Services.ExpressionManager
                .CreateExpression(rawValue)
                .GetValueAsync(execution, ct)
                .ConfigureAwait(false);

            return value?.ToString();
        }
        catch (ElNotSupportedException)
        {
            // 与 Activiti 一致：表达式不可求值时退回原文
            return rawValue;
        }
    }

    /// <summary>到期时间求值。对应 Activiti 的 dueDate 处理（业务日历只支持 ISO-8601 日期串）。</summary>
    private async ValueTask<DateTime?> ResolveDueDateAsync(
        string? rawValue,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return null;
        }

        var value = await Services.ExpressionManager
            .CreateExpression(rawValue)
            .GetValueAsync(execution, ct)
            .ConfigureAwait(false);

        switch (value)
        {
            case null:
                return null;

            case DateTime dateTime:
                return dateTime;

            case string text when DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed):
                return parsed;

            default:
                throw new EngineException(
                    $"Due date expression does not resolve to a Date or Date string: {rawValue}" +
                    "（本子集不支持业务日历，只接受 ISO-8601 日期串）");
        }
    }

    /// <summary>优先级求值。对应 Activiti 的 priority 处理（字符串可转整数，或直接是数字）。</summary>
    private async ValueTask<int> ResolvePriorityAsync(
        string? rawValue,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return TaskEntity.DefaultPriority;
        }

        var value = await Services.ExpressionManager
            .CreateExpression(rawValue)
            .GetValueAsync(execution, ct)
            .ConfigureAwait(false);

        switch (value)
        {
            case null:
                return TaskEntity.DefaultPriority;

            case string text:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : throw new EngineException($"Priority does not resolve to a number: {text}");

            case IConvertible convertible:
                return convertible.ToInt32(CultureInfo.InvariantCulture);

            default:
                throw new EngineException($"Priority expression does not resolve to a number: {rawValue}");
        }
    }

    /// <summary>跳过表达式。对应 Activiti 的 <c>SkipExpressionUtil</c>（无变量开关，只看表达式）。</summary>
    private async ValueTask<bool> ShouldSkipAsync(
        UserTask userTask,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userTask.SkipExpression))
        {
            return false;
        }

        var value = await Services.ExpressionManager
            .CreateExpression(userTask.SkipExpression)
            .GetValueAsync(execution, ct)
            .ConfigureAwait(false);

        return value is true;
    }

    // ─────────────────────────── 办理关系 / 审批链 ───────────────────────────

    /// <summary>
    /// 审批链预处理：把节点解析出的办理人集合交给可选的
    /// <see cref="IApproverResolver"/> 做最终整形 / 动态补人。
    /// </summary>
    /// <returns>最终办理人（空集合 ⇒ 自动跳过）；未配置钩子时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>不配置钩子时返回 <see langword="null"/>，行为与改造前完全一致</b> ——
    /// 这是这次改动的底线：审批链整形属于产品策略，不应由引擎替业务做决定。
    /// </remarks>
    private async ValueTask<IReadOnlyList<string>?> PlanApprovalChainAsync(
        UserTask userTask,
        IDelegateExecution execution,
        string? startUserId,
        IReadOnlyList<string> approvers,
        CancellationToken ct)
    {
        var resolver = Services.ApproverResolver;

        if (resolver is null)
        {
            return null;
        }

        var entity = execution.AsEntity();

        // 动态审批人来源：把 businessKey + 流程变量快照一起交给解析器，
        // 让它能「按业务归属（部门）+ 节点（职位）」查外部系统定审批人。
        var businessKey = await FindBusinessKeyAsync(entity, ct).ConfigureAwait(false);
        var variables = await entity.GetVariablesAsync(ct).ConfigureAwait(false);

        var context = new ApprovalChainContext(
            entity.ProcessDefinitionId ?? string.Empty,
            userTask.Id ?? string.Empty,
            startUserId,
            entity.TenantId,
            businessKey,
            new Dictionary<string, object?>(variables, StringComparer.Ordinal));

        return await resolver.PostProcessAsync(context, approvers, ct).ConfigureAwait(false);
    }

    /// <summary>处理 assignee / owner / candidateGroups / candidateUsers。顺序与 Activiti 一致。</summary>
    private async ValueTask HandleAssignmentsAsync(
        UserTask userTask,
        TaskEntity task,
        ITaskManager taskManager,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(userTask.Assignee))
        {
            var assignee = await EvaluateToStringAsync(userTask.Assignee, execution, ct).ConfigureAwait(false);

            if (assignee is not null)
            {
                // 审批链预处理：发起人本人跳过 / 连续相同合并 / 空审批人转管理员
                var entity = execution.AsEntity();
                var plan = await PlanApprovalChainAsync(
                    userTask,
                    execution,
                    startUserId: entity.StartUserId,
                    approvers: [assignee],
                    ct: ct).ConfigureAwait(false);

                if (plan is not null)
                {
                    assignee = plan.Count > 0 ? plan[0] : null;
                }
            }

            if (assignee is not null)
            {
                await taskManager.ChangeTaskAssigneeAsync(task, assignee, ct).ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrEmpty(userTask.Owner))
        {
            var owner = await EvaluateToStringAsync(userTask.Owner, execution, ct).ConfigureAwait(false);
            await taskManager.ChangeTaskOwnerAsync(task, owner, ct).ConfigureAwait(false);
        }

        if (userTask.CandidateGroups.Count > 0)
        {
            foreach (var candidateGroup in userTask.CandidateGroups)
            {
                var value = await Services.ExpressionManager
                    .CreateExpression(candidateGroup)
                    .GetValueAsync(execution, ct)
                    .ConfigureAwait(false);

                if (value is not string text)
                {
                    throw new EngineException("Expression did not resolve to a string or collection of strings");
                }

                await taskManager
                    .AddCandidateGroupsAsync(task, SplitCandidates(text), ct)
                    .ConfigureAwait(false);
            }
        }

        if (userTask.CandidateUsers.Count > 0)
        {
            foreach (var candidateUser in userTask.CandidateUsers)
            {
                var value = await Services.ExpressionManager
                    .CreateExpression(candidateUser)
                    .GetValueAsync(execution, ct)
                    .ConfigureAwait(false);

                if (value is not string text)
                {
                    throw new EngineException("Expression did not resolve to a string or collection of strings");
                }

                await taskManager
                    .AddCandidateUsersAsync(task, SplitCandidates(text), ct)
                    .ConfigureAwait(false);
            }
        }

        // 动态审批人来源：节点没静态指定任何办理人（无 assignee / owner / 候选组 / 候选用户），
        // 仍把空集合交给 IApproverResolver —— 它可按 businessKey + 部门变量 + 节点（职位）查外部系统定人。
        // 返回非空则写入候选用户（进「我的待办」候选池）；空集保持原样（节点按无审批人处理）。
        if (string.IsNullOrEmpty(task.Assignee)
            && string.IsNullOrEmpty(task.Owner)
            && userTask.CandidateGroups.Count == 0
            && userTask.CandidateUsers.Count == 0)
        {
            var entity = execution.AsEntity();
            var dynamicApprovers = await PlanApprovalChainAsync(
                userTask,
                execution,
                startUserId: entity.StartUserId,
                approvers: [],
                ct: ct).ConfigureAwait(false);

            if (dynamicApprovers is { Count: > 0 })
            {
                await taskManager
                    .AddCandidateUsersAsync(task, dynamicApprovers, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<string?> EvaluateToStringAsync(
        string expressionText,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        var value = await Services.ExpressionManager
            .CreateExpression(expressionText)
            .GetValueAsync(execution, ct)
            .ConfigureAwait(false);

        return value?.ToString();
    }

    /// <summary>按 Activiti 的正则切分候选值。</summary>
    private static List<string> SplitCandidates(string text) => [.. CandidateSeparator.Split(text)];

    // ─────────────────────────── 辅助 ───────────────────────────

    private ITaskManager RequireTaskManager() =>
        Services.TaskManager
        ?? throw new EngineNotImplementedException(
            "任务管理器（ITaskManager）",
            "P7",
            "宿主必须注册 InMemoryTaskManager（内存宿主）或 DapperTaskManager（数据库宿主）");

    /// <summary>
    /// 取流程实例的业务主键。对应 Activiti 的 <c>getProcessInstanceBusinessKey()</c>（沿父执行上溯）。
    /// </summary>
    private async ValueTask<string?> FindBusinessKeyAsync(IExecutionEntity execution, CancellationToken ct)
    {
        IExecutionEntity? current = execution;

        // 执行树深度有限，这里加个上限只是防御性的（避免脏数据造成死循环）
        for (var depth = 0; current is not null && depth < 64; depth++)
        {
            if (!string.IsNullOrEmpty(current.BusinessKey))
            {
                return current.BusinessKey;
            }

            if (current.ParentId is null)
            {
                return null;
            }

            current = current.Parent
                ?? await Services.ExecutionManager.FindByIdAsync(current.ParentId, ct).ConfigureAwait(false);
        }

        return null;
    }
}

/// <summary>
/// 脚本任务行为。对应 Activiti 的 <c>ScriptTaskActivityBehavior</c>（JS 引擎 → 本方案 Roslyn）。
/// <para>
/// 只支持 <c>scriptFormat="csharp"</c>；脚本顶层可直接用 <c>execution</c>（当前执行）与
/// <c>vars</c>（流程变量快照），语义详见 <see cref="Script.ScriptTaskGlobals"/>。
/// 与委托相同的约束：脚本与流程同处一个命令事务（§5.3），内部不得 await 外部系统 IO。
/// </para>
/// </summary>
public sealed class ScriptTaskActivityBehavior : FlowNodeActivityBehavior
{
    public ScriptTaskActivityBehavior(IEngineServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(
        IDelegateExecution execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var scriptTask = execution.CurrentFlowElement as ScriptTask
            ?? throw new EngineException(
                "Programmatic error: script task behaviour can only be applied to a ScriptTask instance, " +
                $"but got an instance of {execution.CurrentFlowElement}");

        await ScriptTaskEngine
            .ExecuteAsync(scriptTask, execution, ct)
            .ConfigureAwait(false);

        await LeaveAsync(execution, ct).ConfigureAwait(false);
    }
}

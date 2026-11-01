using Microsoft.Extensions.Logging;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 实例读模型（<c>/workflow/v1/instances/{id}</c>）——把引擎的执行树 + 历史 + 待办组装成
/// 调用方能直接渲染实例详情的 <see cref="InstanceView"/>。
/// </summary>
/// <remarks>
/// <para>
/// 这是「一个流程实例长什么样」的<b>单一出口</b>：实例详情端点与提交审批的返回体
/// （<see cref="SubmitTaskResult.Instance"/>）都从这里组装，保证同一个实例在两个响应里长得一样，
/// 而不是各自再拼一份（口径分叉的来源）。
/// </para>
/// <para>
/// 数据级授权（租户边界 + 流程白名单）在结果层过滤，与目录 / 待办读模型同口径：
/// 未授权按「不存在」处理（返回 <see langword="null"/>），不泄露实例归属。
/// </para>
/// </remarks>
public interface IInstanceCatalog
{
    /// <summary>查单条实例视图；不存在、未授权或不属于当前租户都返回 <see langword="null"/>。</summary>
    /// <param name="instanceId">流程实例 id（= 根执行 id）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<InstanceView?> FindAsync(
        string instanceId,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 查实例的参数面板：<b>参数声明 + 当前值</b>（<c>GET /instances/{id}/parameters</c>）。
    /// </summary>
    /// <remarks>
    /// 放在实例读模型里而不是另起一个服务，是因为 <c>visibleTo</c> 的判定
    /// （视角角色 → 隐藏集合）只在这里有一份实现。参数面板若自己再算一遍，
    /// 迟早会漏掉「同一个变量有两种叫法」这类细节，于是换个名字就能看见。
    /// 不存在 / 未授权返回 <see langword="null"/>，与 <see cref="FindAsync"/> 同口径。
    /// </remarks>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<InstanceParameterView?> GetParametersAsync(
        string instanceId,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 查实例的参数修订历史（<c>GET /instances/{id}/parameters/history</c>），修订号倒序。
    /// </summary>
    /// <remarks>
    /// 回答的是「谁在第几步把金额改成了多少」：引擎变量只有<b>当前值</b>，
    /// 而审计要看的是过程。历史由 <c>nb_parameter_snapshot</c> 提供（append-only）。
    /// 值同样过 <c>visibleTo</c>：不给看的人连历史里的那一栏都不该出现。
    /// 不存在 / 未授权返回 <see langword="null"/>，与 <see cref="FindAsync"/> 同口径。
    /// </remarks>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="limit">最多返回多少版（最近的在前）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ParameterSnapshot>?> GetParameterHistoryAsync(
        string instanceId,
        int limit,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class InstanceCatalog : IInstanceCatalog
{
    /// <summary>对外参数命名空间前缀（与目录派生同口径：引擎裸名 → <c>param.{name}</c>）。</summary>
    private const string ParameterApiPrefix = "param.";

    /// <summary>
    /// 参数可见角色：发起人（BPMN 里写 APPLICANT）。
    /// 管理视角（BPMN 里常见的 <c>ADMIN</c>）不需要在这里列：持管理 scope 的调用方
    /// 一律不受 <c>visibleTo</c> 限制，写不写都一样。
    /// </summary>
    private const string ParameterRoleApplicant = "APPLICANT";

    /// <summary>参数可见角色：办理人 / 候选人 / 参与人（BPMN 里写 APPROVER）。</summary>
    private const string ParameterRoleApprover = "APPROVER";

    private readonly IProcessEngine _engine;
    private readonly IExecutionManager _executionManager;
    private readonly IProcessDefinitionStore _definitionStore;
    private readonly ITaskCatalog _taskCatalog;
    private readonly IProcessCatalog _processCatalog;
    private readonly IInstanceParticipation _participation;
    private readonly IParameterSnapshotStore _parameterSnapshots;
    private readonly IApprovalTimelineStore _timeline;
    private readonly INodeOutcomeAggregator _outcomes;
    private readonly ILogger<InstanceCatalog> _logger;

    /// <summary>构造。</summary>
    /// <param name="engine">引擎门面（历史 / 变量 / 待办真源）。</param>
    /// <param name="executionManager">执行树管理器（运行态 / 修订号 / 挂起状态）。</param>
    /// <param name="definitionStore">流程定义仓储（实例 → 流程 key / 名称 / 版本）。</param>
    /// <param name="taskCatalog">待办读模型（实例上的待办视图单一出口）。</param>
    /// <param name="processCatalog">流程目录（参数规范：<c>visibleTo</c> 的依据）。</param>
    /// <param name="participation">参与者索引（判定这位真人是申请人还是审批人）。</param>
    /// <param name="parameterSnapshots">参数修订历史（<c>nb_parameter_snapshot</c>：回答「谁在第几步改了什么」）。</param>
    /// <param name="timeline">审批时间线（环节聚合的输入：谁在哪一步投了什么票）。</param>
    /// <param name="outcomes">环节聚合（会签 / 或签口径与审批记录页共用同一份实现）。</param>
    /// <param name="logger">日志。</param>
    public InstanceCatalog(
        IProcessEngine engine,
        IExecutionManager executionManager,
        IProcessDefinitionStore definitionStore,
        ITaskCatalog taskCatalog,
        IProcessCatalog processCatalog,
        IInstanceParticipation participation,
        IParameterSnapshotStore parameterSnapshots,
        IApprovalTimelineStore timeline,
        INodeOutcomeAggregator outcomes,
        ILogger<InstanceCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(executionManager);
        ArgumentNullException.ThrowIfNull(definitionStore);
        ArgumentNullException.ThrowIfNull(taskCatalog);
        ArgumentNullException.ThrowIfNull(processCatalog);
        ArgumentNullException.ThrowIfNull(participation);
        ArgumentNullException.ThrowIfNull(parameterSnapshots);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _executionManager = executionManager;
        _definitionStore = definitionStore;
        _taskCatalog = taskCatalog;
        _processCatalog = processCatalog;
        _participation = participation;
        _parameterSnapshots = parameterSnapshots;
        _timeline = timeline;
        _outcomes = outcomes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<InstanceView?> FindAsync(
        string instanceId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(context);

        // 存在性双查：运行态执行（实例结束前一直在）+ 历史实例（结束后运行态行删除、历史仍在）。
        // 历史被禁用（EnableHistory=false）时，结束实例查不到——那正是「没开历史」应有的行为，
        // 运行中的实例仍靠运行态执行回答。
        var execution = await _executionManager.FindByIdAsync(instanceId, ct).ConfigureAwait(false);
        var historic = await _engine.FindHistoricProcessInstanceAsync(instanceId, ct).ConfigureAwait(false);

        if (execution is null && historic is null)
        {
            return null;
        }

        var processDefinitionId = execution?.ProcessDefinitionId ?? historic!.ProcessDefinitionId;
        var definition = await ResolveDefinitionAsync(processDefinitionId, ct).ConfigureAwait(false);
        var processKey = definition?.Key ?? InstanceReadRules.ParseKey(processDefinitionId);

        // 客户端白名单：未授权流程的实例按「不存在」处理（404），不泄露归属
        if (!context.CanUseProcess(processKey))
        {
            return null;
        }

        // 租户边界：无租户部署（TENANT_ID_ = ''）视为全局共享；实例带租户时必须与调用方一致。
        var tenantId = execution?.TenantId ?? historic?.TenantId;

        if (!string.IsNullOrEmpty(tenantId)
            && context.EffectiveTenantId is not null
            && !string.Equals(tenantId, context.EffectiveTenantId, StringComparison.Ordinal))
        {
            return null;
        }

        var status = InstanceReadRules.ResolveStatus(execution, historic);
        var tasks = await _taskCatalog.BuildViewsForInstanceAsync(instanceId, context, ct).ConfigureAwait(false);
        var variables = await LoadVariablesAsync(instanceId, execution, ct).ConfigureAwait(false);

        var revision = execution?.Revision ?? 0;
        var startedBy = execution?.StartUserId ?? historic?.StartUserId;

        return new InstanceView(
            InstanceId: instanceId,
            ProcessKey: processKey,
            ProcessDefinitionId: processDefinitionId,
            DefinitionVersion: definition?.Version ?? 0,
            BusinessKey: execution?.BusinessKey ?? historic?.BusinessKey,
            Title: null,
            Status: status,
            StartedAt: InstanceReadRules.AsUtcOffset(execution?.StartTime ?? historic?.StartTime),
            StartedBy: startedBy,
            EndedAt: InstanceReadRules.AsUtcOffset(historic?.EndTime),
            CurrentActivityIds: [.. tasks
                .Where(task => !string.IsNullOrWhiteSpace(task.ActivityId))
                .Select(task => task.ActivityId!)
                .Distinct(StringComparer.Ordinal)],
            Revision: revision,
            Tasks: tasks,
            Parameters: await BuildParameterSnapshotAsync(
                processKey,
                instanceId,
                definition?.Version,
                revision,
                variables,
                startedBy,
                context,
                ct).ConfigureAwait(false),
            // 环节聚合读整单时间线（不受分页影响），与审批记录页同一份口径
            NodeOutcomes: await _outcomes
                .ComputeAsync(
                    instanceId,
                    processDefinitionId,
                    await _timeline.ListAsync(instanceId, 0, NodeOutcomeAggregator.MaxEntries, ct).ConfigureAwait(false),
                    tasks,
                    ct)
                .ConfigureAwait(false),
            Conclusion: InstanceReadRules.ResolveConclusion(status, variables));
    }

    /// <inheritdoc />
    public async ValueTask<InstanceParameterView?> GetParametersAsync(
        string instanceId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(context);

        var view = await FindAsync(instanceId, context, ct).ConfigureAwait(false);

        if (view is null)
        {
            return null;
        }

        // 按「实例停在的那一版定义」取参数声明：流程升级后 v1 的单据不该显示 v3 新加的字段
        var declared = await TryLoadSchemaAsync(
            view.ProcessKey,
            view.DefinitionVersion,
            context,
            ct).ConfigureAwait(false);
        var roles = await ResolveViewerRolesAsync(instanceId, view.StartedBy, context, ct).ConfigureAwait(false);
        var hidden = declared is null ? [] : ComputeHidden(declared, roles);

        // 值已经过一次 visibleTo 过滤（在 BuildParameterSnapshotAsync 里），这里只负责
        // 「把声明接上去」以及「补上没声明但确实存在的变量」
        var values = view.Parameters.Values;
        var rows = new List<InstanceParameter>(values.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in declared?.Parameters ?? [])
        {
            var variableKey = string.IsNullOrWhiteSpace(definition.VariableName)
                ? null
                : ParameterApiPrefix + definition.VariableName;

            if (hidden.Contains(definition.ApiName) || (variableKey is not null && hidden.Contains(variableKey)))
            {
                continue;
            }

            string? value = null;

            if (variableKey is not null && values.TryGetValue(variableKey, out var byVariable))
            {
                value = byVariable;
                emitted.Add(variableKey);
            }
            else if (values.TryGetValue(definition.ApiName, out var byApiName))
            {
                value = byApiName;
            }

            emitted.Add(definition.ApiName);

            // 声明了但还没填的参数也要出现：调用方要据此渲染一个空输入框，
            // 而不是等它被写过之后才在面板上冒出来。
            rows.Add(new InstanceParameter(
                ApiName: definition.ApiName,
                VariableName: definition.VariableName,
                DisplayName: definition.DisplayName,
                Type: definition.Type,
                Required: definition.Required,
                Value: value,
                Description: definition.Description,
                Constraints: definition.Constraints,
                WritableBy: definition.WritableBy,
                SortOrder: definition.SortOrder,
                Declared: true));
        }

        foreach (var (name, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (emitted.Contains(name))
            {
                continue;
            }

            rows.Add(new InstanceParameter(
                ApiName: name,
                VariableName: name.StartsWith(ParameterApiPrefix, StringComparison.Ordinal)
                    ? name[ParameterApiPrefix.Length..]
                    : name,
                DisplayName: null,
                Type: ParameterType.String,
                Required: false,
                Value: value,
                Description: null,
                Constraints: null,
                WritableBy: null,
                SortOrder: int.MaxValue,
                Declared: false));
        }

        return new InstanceParameterView(
            InstanceId: instanceId,
            ProcessKey: view.ProcessKey,
            ProcessDefinitionId: view.ProcessDefinitionId,
            SchemaVersion: declared?.Version ?? 0,
            Revision: view.Revision,
            Parameters: rows);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ParameterSnapshot>?> GetParameterHistoryAsync(
        string instanceId,
        int limit,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(context);

        // 存在性 / 可见性复用详情读模型：能看详情才能看它的参数历史，两处判定不能各写一套
        var view = await FindAsync(instanceId, context, ct).ConfigureAwait(false);

        if (view is null)
        {
            return null;
        }

        var declared = await TryLoadSchemaAsync(
            view.ProcessKey,
            view.DefinitionVersion,
            context,
            ct).ConfigureAwait(false);
        var roles = await ResolveViewerRolesAsync(instanceId, view.StartedBy, context, ct).ConfigureAwait(false);
        var hidden = declared is null ? [] : ComputeHidden(declared, roles);

        IReadOnlyList<ParameterSnapshotRecord> records;

        try
        {
            records = await _parameterSnapshots.ListAsync(instanceId, limit, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 老库可能还没跑补表脚本：历史读不出来就返回空，而不是让整张审批页 500
            _logger.LogWarning(
                exception,
                "参数修订历史不可读（{InstanceId}）：本次返回空历史。"
                + "请执行 db/{Dialect}/netbpm_init.sql 补齐 nb_parameter_snapshot。",
                instanceId,
                "mysql|sqlserver");

            return [];
        }

        return [.. records.Select(record => new ParameterSnapshot(
            InstanceId: record.InstanceId,
            Revision: record.Revision,
            Values: FilterHidden(record.Values, hidden),
            UpdatedAt: record.UpdatedAt,
            UpdatedBy: record.UpdatedBy))];
    }

    /// <summary>把 <c>visibleTo</c> 不满足的参数从历史值里摘掉（声明里的两种叫法都要摘）。</summary>
    private static Dictionary<string, string?> FilterHidden(
        IReadOnlyDictionary<string, string?> values,
        HashSet<string> hidden)
    {
        var result = new Dictionary<string, string?>(values.Count, StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            if (hidden.Contains(name))
            {
                continue;
            }

            result[name] = value;
        }

        return result;
    }

    /// <summary>解析流程定义（拿 key / 名称 / 版本）；失败时降级为按定义 id 解析 key。</summary>
    private async ValueTask<ProcessDefinition?> ResolveDefinitionAsync(
        string processDefinitionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
        {
            return null;
        }

        try
        {
            return await _definitionStore.FindByIdAsync(processDefinitionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "解析实例定义失败（{ProcessDefinitionId}）。",
                processDefinitionId);

            return null;
        }
    }

    /// <summary>
    /// 合并「运行变量 + 历史变量」得到实例的当前变量快照。
    /// </summary>
    /// <remarks>
    /// 历史变量只增不删、记录最后一次的值，因此它既能兜住「实例已结束、运行变量已删」，
    /// 也能提供运行变量之外的历史值。运行变量优先（它是当前值）。
    /// 任务局部变量（<c>TaskId</c> 非空）不属于实例快照，排除。
    /// </remarks>
    private async ValueTask<Dictionary<string, object?>> LoadVariablesAsync(
        string instanceId,
        IExecutionEntity? execution,
        CancellationToken ct)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var variable in await _engine.FindHistoricVariablesAsync(instanceId, ct).ConfigureAwait(false))
        {
            if (variable.TaskId is null)
            {
                variables[variable.Name] = variable.Value;
            }
        }

        if (execution is not null)
        {
            foreach (var (name, value) in await _engine.GetVariablesAsync(instanceId, ct).ConfigureAwait(false))
            {
                variables[name] = value;
            }
        }

        return variables;
    }

    /// <summary>
    /// 组装参数快照：引擎里除审批元数据（<see cref="BaseModelVariables"/>）外的变量都是业务参数，
    /// 对外键加 <c>param.</c> 前缀（与参数 schema 的 <c>apiName</c> 同口径）。
    /// </summary>
    /// <remarks>
    /// 独立部署下业务参数以裸名镜像进引擎变量（§4.3），因此这里从变量快照直接派生。
    /// <c>revision</c> 沿用实例修订号（参数本身没有独立计数器）；<c>UpdatedBy</c> 本轮不追溯。
    /// </remarks>
    /// <param name="processKey">流程 key。</param>
    /// <param name="instanceId">实例 id。</param>
    /// <param name="definitionVersion">实例停在的定义版本（决定用哪一版参数声明做可见性过滤）。</param>
    /// <param name="revision">实例修订号。</param>
    /// <param name="variables">引擎变量快照。</param>
    /// <param name="startedBy">发起人（判定 APPLICANT 角色）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask<ParameterSnapshot> BuildParameterSnapshotAsync(
        string processKey,
        string instanceId,
        int? definitionVersion,
        long revision,
        Dictionary<string, object?> variables,
        string? startedBy,
        RequestContext context,
        CancellationToken ct)
    {
        var hidden = await LoadHiddenParametersAsync(
            processKey,
            definitionVersion,
            instanceId,
            variables,
            startedBy,
            context,
            ct).ConfigureAwait(false);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            if (BaseModelVariables.All.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var apiName = ParameterApiPrefix + name;

            if (hidden.Contains(apiName))
            {
                continue;
            }

            values[apiName] = VariableValues.AsString(value);
        }

        return new ParameterSnapshot(
            InstanceId: instanceId,
            Revision: revision,
            Values: values,
            UpdatedAt: null,
            UpdatedBy: null);
    }

    /// <summary>
    /// 按参数声明里的 <c>visibleTo</c> 算出「本次看不到哪些参数」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意的是「字段<b>消失</b>而不是返回 null」：返回 null 会让调用方分不清
    /// 「这个字段还没填」与「这个字段你不该看」，而后者被当成前者泄露出去只是时间问题。
    /// </para>
    /// <para>
    /// 没有声明 <c>visibleTo</c> 的参数对所有人可见（兼容既有 BPMN：它们从没写过这个属性）。
    /// 这一点也是 §4.3 里「不声明就不限制」的同一条规矩。
    /// </para>
    /// <para>
    /// 取不到 schema（流程目录不可用 / 该流程没声明参数）时不隐藏任何东西：
    /// 宁可多给，不可把正常字段藏起来让人以为单据出问题了。
    /// </para>
    /// </remarks>
    private async ValueTask<HashSet<string>> LoadHiddenParametersAsync(
        string processKey,
        int? definitionVersion,
        string instanceId,
        Dictionary<string, object?> variables,
        string? startedBy,
        RequestContext context,
        CancellationToken ct)
    {
        var declared = await TryLoadSchemaAsync(processKey, definitionVersion, context, ct).ConfigureAwait(false);

        if (declared is null || declared.Parameters.Count == 0 || variables.Count == 0)
        {
            return [];
        }

        var roles = await ResolveViewerRolesAsync(instanceId, startedBy, context, ct).ConfigureAwait(false);

        return ComputeHidden(declared, roles);
    }

    /// <summary>
    /// 按 <c>visibleTo</c> 与视角角色算出「本次看不到哪些参数」。
    /// </summary>
    /// <remarks>
    /// 抽成静态方法是因为它有<b>两个</b>调用点：实例详情（过滤值）与参数面板
    /// （<c>GET /instances/{id}/parameters</c>，连声明一起过滤）。口径只能有一份——
    /// 两处各算一遍，迟早会有一处忘记处理「同一个变量有两种叫法」这个别名问题。
    /// </remarks>
    /// <param name="declared">参数规范。</param>
    /// <param name="roles">视角角色集合；<see langword="null"/> 表示不受限（管理视角 / 纯服务令牌）。</param>
    private static HashSet<string> ComputeHidden(ParameterSchema declared, HashSet<string>? roles)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        // null = 不受限制（管理视角 / 纯服务令牌）：它们的可见性不是「角色里有没有这一条」的问题
        if (roles is null)
        {
            return hidden;
        }

        foreach (var parameter in declared.Parameters)
        {
            if (parameter.VisibleTo is not { Count: > 0 })
            {
                continue;
            }

            if (parameter.VisibleTo.Any(role => roles.Contains(role)))
            {
                continue;
            }

            hidden.Add(parameter.ApiName);

            // 声明里给了 apiName 时，同一个变量有两种叫法；两个键都要藏住，
            // 否则「换了个名字就能看见」会变成一条绕过 visibleTo 的捷径。
            if (!string.IsNullOrWhiteSpace(parameter.VariableName))
            {
                hidden.Add(ParameterApiPrefix + parameter.VariableName);
            }
        }

        return hidden;
    }

    /// <summary>取参数规范；读不到一律返回 null（由调用方决定降级，这里不抛）。</summary>
    private async ValueTask<ParameterSchema?> TryLoadSchemaAsync(
        string processKey,
        int? definitionVersion,
        RequestContext context,
        CancellationToken ct)
    {
        try
        {
            return await _processCatalog
                .GetParameterSchemaAsync(processKey, definitionVersion, context, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "读流程 {ProcessKey} 的参数规范失败：本次不做可见性过滤。", processKey);

            return null;
        }
    }

    /// <summary>
    /// 判定这位调用方相对本实例拥有哪些参数可见角色（<c>APPLICANT</c> / <c>APPROVER</c> / <c>ADMIN</c>）。
    /// </summary>
    /// <remarks>
    /// 角色的取值与 BPMN 里 <c>visibleTo</c> 写的字符串逐字对应（大小写不敏感）：
    /// 让调用方去理解「APPLICANT = 发起人」是一回事，让<b>写 BPMN 的人</b>理解同一件事是另一回事，
    /// 后者只能靠名字本身就是那句话。
    /// </remarks>
    private async ValueTask<HashSet<string>?> ResolveViewerRolesAsync(
        string instanceId,
        string? startedBy,
        RequestContext context,
        CancellationToken ct)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ① 管理视角：不受参数可见性限制（它们本来就走得动实例列表的跨人权限）。
        //    这里返回 null 而不是「加上 ADMIN 角色」——后者会让 visibleTo="APPLICANT" 的字段
        //    反而对管理员不可见，那是把一条安全规则解释成了它的反面。
        if (PlatformIdentity.HasAdminPerspective(context))
        {
            return null;
        }

        // ② 纯服务令牌：它代表系统而不是某个真人，谈不上「可见角色」——维持既有行为，不收窄
        if (context.SubjectId is not { Length: > 0 } subject)
        {
            return null;
        }

        // ③ 申请人
        if (string.Equals(startedBy, subject, StringComparison.Ordinal))
        {
            roles.Add(ParameterRoleApplicant);
        }

        // ④ 审批人 / 候选人：走参与者索引（它已经把历史与运行态三个来源合好了）
        var relationship = await _participation
            .DescribeAsync(instanceId, subject, [.. context.Groups], ct)
            .ConfigureAwait(false);

        foreach (var role in relationship.Roles)
        {
            if (role is ParticipationRole.Approver or ParticipationRole.Candidate
                or ParticipationRole.Participant)
            {
                roles.Add(ParameterRoleApprover);
            }
        }

        return roles;
    }
}

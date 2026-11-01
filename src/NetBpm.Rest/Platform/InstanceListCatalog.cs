using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetBpm.Persistence.Deployment;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using EngineTaskQuery = NetBpm.Runtime.TaskQuery;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 实例列表读模型（<c>GET /workflow/v1/instances</c>）——把「我发起的 / 全租户的单据」
/// 组装成分页的 <see cref="InstanceSummary"/>。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IInstanceCatalog"/>（单条视图）的分工：单条视图要「一次拿全」（待办 + 参数 +
/// 环节聚合 + 结论），列表只要「能渲染一行」——标题、状态、当前环节、当前办理人、结论。
/// 列表<b>不复用</b>单条视图：每条都组装 options 与参数快照，20 条就是 20 次多余的重活。
/// 但<b>判定规则</b>（状态、结论）必须共用 <see cref="InstanceReadRules"/>，
/// 否则会出现「列表显示已通过、点进去显示已办结」这种没人会怀疑的实现分叉。
/// </para>
/// <para>
/// 数据源是<b>两侧合并</b>：运行态（<c>ACT_RU_EXECUTION</c> 根执行）与历史（<c>ACT_HI_PROCINST</c>）。
/// 只用历史会在「历史关闭」时返回空；只用运行态会漏掉所有已结束的单据——
/// 而「我的单据」列表里绝大多数是已结束的。
/// </para>
/// </remarks>
public interface IInstanceListCatalog
{
    /// <summary>分页查实例摘要（已按租户、流程白名单、人员范围过滤）。</summary>
    /// <param name="query">查询条件。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="FormatException">游标格式不正确。</exception>
    /// <exception cref="InstanceListRejectedException">条件不被接受（越权按别人的名义查、不支持的排序方向）。</exception>
    ValueTask<CursorPage<InstanceSummary>> ListAsync(
        InstanceQuery query,
        RequestContext context,
        CancellationToken ct = default);
}

/// <summary>
/// 实例列表的可预期拒绝（越权 / 不支持的查询）。
/// </summary>
/// <remarks>
/// 与 <c>SubmitDecisionException</c> 同一套路：服务层给出「对外的错误码 + 状态码 + 文案」，
/// 端点层只负责转成 RFC 9457，不重复判断业务。
/// </remarks>
public sealed class InstanceListRejectedException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="code">对外错误码（见 <see cref="ErrorCodes"/>）。</param>
    /// <param name="title">问题标题。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <param name="detail">问题详情。</param>
    public InstanceListRejectedException(string code, string title, int status, string detail)
        : base(detail)
    {
        Code = code;
        Title = title;
        Status = status;
        Detail = detail;
    }

    /// <summary>对外错误码。</summary>
    public string Code { get; }

    /// <summary>问题标题。</summary>
    public string Title { get; }

    /// <summary>HTTP 状态码。</summary>
    public int Status { get; }

    /// <summary>问题详情。</summary>
    public string Detail { get; }
}

/// <inheritdoc />
public sealed class InstanceListCatalog : IInstanceListCatalog
{
    /// <summary>单页默认条数。</summary>
    private const int DefaultLimit = 20;

    /// <summary>单页上限。</summary>
    /// <remarks>
    /// 比时间线的 200 小得多：每一条都要补「当前办理人」与「结论」（各一次往返），
    /// 100 条已经是 200 次查询。分页是给翻页用的，不是给导数据用的。
    /// </remarks>
    private const int MaxLimit = 100;

    /// <summary>补详情时的并发上限。</summary>
    /// <remarks>
    /// 单页最多 100 条，全串行会让一次翻页变成上百次 RTT 的串行叠加；
    /// 但也不能无限并发——数据库连接池就那么大，打满之后排队反而更慢。
    /// </remarks>
    private const int MaxConcurrency = 4;

    private readonly IProcessEngine _engine;
    private readonly IProcessDefinitionStore _definitionStore;
    private readonly IInstanceParticipation _participation;
    private readonly ILogger<InstanceListCatalog> _logger;

    /// <summary>构造。</summary>
    /// <param name="engine">引擎门面（实例 / 历史 / 待办 / 变量真源）。</param>
    /// <param name="definitionStore">流程定义仓储（定义 id → 流程 key，供白名单判定）。</param>
    /// <param name="participation">参与者索引（按「我参与过」过滤时的数据源）。</param>
    /// <param name="logger">日志。</param>
    public InstanceListCatalog(
        IProcessEngine engine,
        IProcessDefinitionStore definitionStore,
        IInstanceParticipation participation,
        ILogger<InstanceListCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(definitionStore);
        ArgumentNullException.ThrowIfNull(participation);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _definitionStore = definitionStore;
        _participation = participation;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<CursorPage<InstanceSummary>> ListAsync(
        InstanceQuery query,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        // 两个方向都支持：排序与时间边界一起下推给存储层（`order by START_TIME_, ID_` 的 asc/desc），
        // 平台侧只负责游标语义与 tie-break，不靠「全量拉回来再反向切片」。
        var descending = query.Direction != SortDirection.Ascending;
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
        var cursor = DecodeCursor(query.Cursor);
        var startedBy = ResolveStartedBy(query.StartedBy, context);

        // 「我参与过的单据」：一次算出「这个人 → 实例 id → 角色」，既做过滤也用来回填 myRoles
        var involvement = await LoadInvolvementAsync(query.InvolvedUser, context, ct).ConfigureAwait(false);

        var candidates = await LoadCandidatesAsync(query, startedBy, cursor, descending, limit, involvement is null, context, ct)
            .ConfigureAwait(false);

        var filtered = new List<Candidate>(candidates.Count);

        foreach (var candidate in candidates.Values)
        {
            // 参与过滤放在最前面：命中不了就完全不必再判定状态与游标位置
            if (involvement is not null && !involvement.IsInvolved(candidate.InstanceId))
            {
                continue;
            }

            if (!MatchesStatus(candidate, query.Status))
            {
                continue;
            }

            if (cursor is { } position && IsBeforeCursor(candidate, position, descending))
            {
                continue;
            }

            filtered.Add(candidate);
        }

        // 排序：时间为主键、实例 id 为 tie-break（id 恒升序，两个方向都不变）。
        // 保持这个口径是因为游标的「同刻怎么排」必须与排序完全一致，否则翻页会在边界重复或漏行。
        filtered.Sort((left, right) =>
        {
            var byTime = (left.StartTime ?? DateTime.MinValue)
                .CompareTo(right.StartTime ?? DateTime.MinValue);

            if (byTime != 0)
            {
                return descending ? -byTime : byTime;
            }

            return string.CompareOrdinal(left.InstanceId, right.InstanceId);
        });

        var page = filtered.Count > limit ? filtered.GetRange(0, limit) : filtered;
        var hasMore = filtered.Count > limit;

        var items = await BuildSummariesAsync(page, context, involvement, ct).ConfigureAwait(false);

        return new CursorPage<InstanceSummary>(
            Items: items,
            NextCursor: hasMore && page.Count > 0 ? EncodeCursor(page[^1]) : null,
            HasMore: hasMore);
    }

    /// <summary>
    /// 决定「查谁的单据」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里是<b>数据授权</b>的落点，不是 scope 判定。规则按优先级：
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   管理员（<c>netbpm.admin</c> / <c>workflow:admin:instance</c> / <c>workflow:history:read:all</c>）：
    ///   可指定任意发起人，也可不指定（全租户）。
    ///   </description></item>
    ///   <item><description>
    ///   有真人主体（<c>X-Subject-Id</c> / JWT <c>sub</c>）：<b>默认收窄到本人</b>，显式指定别人 → 403。
    ///   「不传就返回全租户单据」是这类列表最典型的越权漏洞，而且不会有任何人报 bug。
    ///   </description></item>
    ///   <item><description>
    ///   纯服务令牌 / 未启用鉴权：没有「人」这一层，按条件查（仍受租户与流程白名单约束）。
    ///   </description></item>
    /// </list>
    /// </remarks>
    /// <param name="requested">请求里声明的发起人。</param>
    /// <param name="context">调用方身份。</param>
    private static string? ResolveStartedBy(string? requested, RequestContext context)
    {
        var canSeeEveryone = PlatformIdentity.HasAdminPerspective(context);

        if (canSeeEveryone)
        {
            return string.IsNullOrWhiteSpace(requested) ? null : requested;
        }

        if (context.SubjectId is not { Length: > 0 } subject)
        {
            // 无主体：服务令牌代表系统自己，按条件查（不额外收窄）
            return string.IsNullOrWhiteSpace(requested) ? null : requested;
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            return subject;
        }

        if (!string.Equals(requested, subject, StringComparison.Ordinal))
        {
            throw new InstanceListRejectedException(
                ErrorCodes.AuthInsufficientScope,
                "不能查看他人的单据",
                StatusCodes.Status403Forbidden,
                $"发起人过滤只能是自己（'{subject}'）；跨人查看需要 netbpm.admin 或 workflow:history:read:all。");
        }

        return subject;
    }

    /// <summary>
    /// 取「我参与过的单据」的候选集合；未查询参与关系时返回 <see langword="null"/>（表示不过滤）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返回 <see langword="null"/> 而不是空映射是刻意的：空映射等于「这个人跟任何单据都无关」，
    /// 会让列表<b>静默返回空</b>——一旦参与者索引因为历史关闭而查不到东西，
    /// 「我的单据」就整体消失，且没有任何报错。null 才是「本次没有这个过滤条件」。
    /// </para>
    /// <para>
    /// 人员范围与 <see cref="ResolveStartedBy"/> 同规则（服务端决定，越权 403）：
    /// 「我参与过」的对象是<b>数据归属</b>，不能由调用方传谁就是谁。
    /// </para>
    /// </remarks>
    /// <param name="requested">请求里声明的参与人。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask<InstanceInvolvement?> LoadInvolvementAsync(
        string? requested,
        RequestContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        var involvedUser = ResolveInvolvedUser(requested, context);

        return await _participation
            .FindAsync(involvedUser, [.. context.Groups], ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 决定「查谁的参与关系」：与管理可见性同约束（<c>netbpm.admin</c> /
    /// <c>workflow:admin:instance</c> / <c>workflow:history:read:all</c>）。
    /// </summary>
    private static string ResolveInvolvedUser(string requested, RequestContext context)
    {
        var canSeeEveryone = PlatformIdentity.HasAdminPerspective(context);

        if (canSeeEveryone)
        {
            return requested;
        }

        if (context.SubjectId is not { Length: > 0 } subject)
        {
            // 纯服务令牌：它代表系统，本来就没有「我」这层关系，但也不禁止按人查
            // （调用方还得过 scope 与租户两关，这里不额外收窄）
            return requested;
        }
        if (!string.Equals(requested, subject, StringComparison.Ordinal))
        {
            throw new InstanceListRejectedException(
                ErrorCodes.AuthInsufficientScope,
                "不能查看他人的单据",
                StatusCodes.Status403Forbidden,
                $"参与人过滤只能是自己（'{subject}'）；跨人查看需要 netbpm.admin 或 workflow:history:read:all。");
        }

        return subject;
    }

    /// <summary>
    /// 从运行态与历史两侧取候选，按实例 id 合并且去重。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么两侧都要查</b>：历史开启时 <c>ACT_HI_PROCINST</c> 同时含运行中与已结束的实例，
    /// 理论上单靠历史就够；但历史可以关闭（<c>EnableHistory=false</c>），那时它恒为空，
    /// 只有运行态能回答「这个实例在不在」。反过来只用运行态会漏掉全部已结束的单据。
    /// </para>
    /// <para>
    /// <b>合并规则</b>：历史提供结束时间与删除原因（状态判定必需），运行态提供挂起状态与
    /// 更及时的业务主键 / 发起人（历史这几列在实例运行期间可能为空）。
    /// </para>
    /// </remarks>
    private async ValueTask<Dictionary<string, Candidate>> LoadCandidatesAsync(
        InstanceQuery query,
        string? startedBy,
        CursorPosition? cursor,
        bool descending,
        int limit,
        bool canLimit,
        RequestContext context,
        CancellationToken ct)
    {
        var merged = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        // 时间边界要跟着方向走：倒序时游标是「上一页最旧的一条」→ 上界；
        // 升序时它是「上一页最新的一条」→ 下界。唯一的约束是边界必须「含等号」，
        // 同刻的多条由内存按实例 id 精确剔除（否则会整批漏掉或整批重复）。
        DateTime? fromBound = query.StartedFrom?.UtcDateTime;
        DateTime? toBound = query.StartedTo?.UtcDateTime;

        var startedAfter = descending ? fromBound : cursor?.StartTime ?? fromBound;
        var startedBefore = descending ? cursor?.StartTime ?? toBound : toBound;

        // ── 把「租户边界」与「流程白名单」也下推 ──
        // 不下推时这两层是在 BuildSummaries 里按行过滤的：也就是先把这个租户（或所有租户）在窗口内的
        // 全部实例拉回来、逐条补详情，再丢掉不属于自己的那些。实例上万之后，这一步就是翻页慢的主因。
        var tenantIds = context.EffectiveTenantId is { } tenantId
            ? new[] { tenantId, string.Empty }
            : null;

        var definitionIds = await ResolveAllowedDefinitionIdsAsync(context, ct).ConfigureAwait(false);

        if (definitionIds is { Count: 0 })
        {
            // 白名单里的 key 一个已部署版本都没有：语义是「零匹配」，绝不能退化成「不加过滤」
            return merged;
        }

        // 每源多取一条判断 hasMore。两个源的排序口径（时间 + id 升序 tie-break）与内存合并完全一致，
        // 所以「各取前 N 条再合并」与「取全量再排序」得到的前 N 条相同。
        int? maxResults = canLimit ? limit + 1 : null;

        if (NeedsHistoric(query.Status))
        {
            var historicQuery = new HistoricProcessInstanceQuery(
                ProcessDefinitionKey: NullIfBlank(query.ProcessKey),
                ProcessDefinitionIds: definitionIds,
                BusinessKey: NullIfBlank(query.BusinessKey),
                StartUserId: startedBy,
                // Status 明确指向「已结束」时由引擎侧筛掉运行中的行：
                // 这一条能把历史表里绝大部分在办实例挡在门外，是最划算的下推。
                Finished: query.Status is InstanceStatus.Completed or InstanceStatus.Terminated ? true : null,
                StartedAfter: startedAfter,
                StartedBefore: startedBefore,
                MaxResults: maxResults,
                TenantIds: tenantIds,
                Descending: descending);

            foreach (var historic in await _engine
                .FindHistoricProcessInstancesAsync(historicQuery, ct)
                .ConfigureAwait(false))
            {
                merged[historic.Id] = new Candidate(
                    InstanceId: historic.Id,
                    ProcessDefinitionId: historic.ProcessDefinitionId,
                    BusinessKey: historic.BusinessKey,
                    StartTime: historic.StartTime,
                    StartUserId: historic.StartUserId,
                    EndTime: historic.EndTime,
                    DeleteReason: historic.DeleteReason,
                    TenantId: historic.TenantId,
                    HasExecution: false,
                    SuspensionState: null);
            }
        }

        if (NeedsExecution(query.Status))
        {
            var executionQuery = new ProcessInstanceQuery(
                ProcessDefinitionKey: NullIfBlank(query.ProcessKey),
                ProcessDefinitionIds: definitionIds,
                BusinessKey: NullIfBlank(query.BusinessKey),
                StartUserId: startedBy,
                StartedAfter: startedAfter,
                StartedBefore: startedBefore,
                MaxResults: maxResults,
                TenantIds: tenantIds,
                Descending: descending);

            foreach (var execution in await _engine
                .FindProcessInstancesAsync(executionQuery, ct)
                .ConfigureAwait(false))
            {
                // 运行态优先：实例在跑，业务主键 / 发起人以运行态为准（历史那几列可能还没写）
                merged[execution.Id] = merged.TryGetValue(execution.Id, out var existing)
                    ? existing with
                    {
                        BusinessKey = execution.BusinessKey ?? existing.BusinessKey,
                        StartTime = execution.StartTime ?? existing.StartTime,
                        StartUserId = execution.StartUserId ?? existing.StartUserId,
                        HasExecution = true,
                        SuspensionState = execution.SuspensionState,
                    }
                    : new Candidate(
                        InstanceId: execution.Id,
                        ProcessDefinitionId: execution.ProcessDefinitionId ?? string.Empty,
                        BusinessKey: execution.BusinessKey,
                        StartTime: execution.StartTime,
                        StartUserId: execution.StartUserId,
                        EndTime: null,
                        DeleteReason: null,
                        TenantId: execution.TenantId,
                        HasExecution: true,
                        SuspensionState: execution.SuspensionState);
            }
        }

        return merged;
    }

    /// <summary>该状态是否需要查历史侧。</summary>
    private static bool NeedsHistoric(InstanceStatus? status) =>
        status is null or InstanceStatus.Completed or InstanceStatus.Terminated;

    /// <summary>
    /// 把客户端的「可用流程 key 白名单」展开成定义 id 列表（用于下推）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQL 只能按 <c>PROC_DEF_ID_</c> 过滤，白名单却是按 key 授权的：同一个 key 有多个版本，
    /// 而版本 id 只有定义表知道。定义表每版本一行（很少），且仓储自带缓存，
    /// 一次 <c>FindAll</c> 远比「把窗口内的实例全拉回来再逐条解析 key」便宜。
    /// </para>
    /// <para>
    /// 返回 <see langword="null"/> = 不限制（凭据没有白名单）；
    /// 返回空列表 = 白名单里的 key 一个版本都没部署 → 调用方必须直接返回空页，
    /// 不能让它退化成「不加过滤」（那是越权，不是性能问题）。
    /// </para>
    /// </remarks>
    private async ValueTask<IReadOnlyCollection<string>?> ResolveAllowedDefinitionIdsAsync(
        RequestContext context,
        CancellationToken ct)
    {
        if (context.AllowedProcessKeys.Count == 0)
        {
            return null;
        }

        // null 租户 = 跨租户取全部定义：客户端看到的实例里既有本租户部署的，也可能有全局共享的
        var definitions = await _definitionStore.FindAllAsync(null, ct).ConfigureAwait(false);

        return
        [
            .. definitions
                .Where(definition => context.AllowedProcessKeys.Contains(definition.Key))
                .Select(definition => definition.Id),
        ];
    }

    /// <summary>该状态是否需要查运行态。</summary>
    /// <remarks>
    /// <see cref="InstanceStatus.Failed"/> 也走运行态：引擎不产出该状态，查一次确认「没有」比
    /// 直接返回空更诚实（将来引擎真的产出异常挂起时不用改这里）。
    /// </remarks>
    private static bool NeedsExecution(InstanceStatus? status) =>
        status is null or InstanceStatus.Running or InstanceStatus.Suspended or InstanceStatus.Failed;

    /// <summary>状态精筛（下推只能按「运行 / 结束」粗筛，挂起要看运行态标量）。</summary>
    private static bool MatchesStatus(Candidate candidate, InstanceStatus? status)
    {
        if (status is null)
        {
            return true;
        }

        if (status == InstanceStatus.Failed)
        {
            return false;
        }

        var actual = InstanceReadRules.ResolveStatus(
            candidate.HasExecution,
            candidate.SuspensionState,
            candidate.DeleteReason);

        return actual == status;
    }

    /// <summary>组装本页摘要：逐条补「当前办理人」与「结论」，并做租户 / 流程白名单过滤。</summary>
    private async ValueTask<IReadOnlyList<InstanceSummary>> BuildSummariesAsync(
        List<Candidate> page,
        RequestContext context,
        InstanceInvolvement? involvement,
        CancellationToken ct)
    {
        var results = new InstanceSummary?[page.Count];
        var definitionKeys = new Dictionary<string, string?>(StringComparer.Ordinal);

        // 并发上限由 MaxDegreeOfParallelism 控制（不再叠一层信号量：两层限流只会让排查更绕）
        await Parallel.ForEachAsync(
            Enumerable.Range(0, page.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct },
            async (index, token) =>
            {
                results[index] = await BuildSummaryAsync(
                    page[index],
                    context,
                    definitionKeys,
                    involvement?.RolesFor(page[index].InstanceId),
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return [.. results.Where(summary => summary is not null).Select(summary => summary!)];
    }

    /// <summary>组装一条摘要；未授权（租户不符 / 流程不在白名单）返回 <see langword="null"/>。</summary>
    private async ValueTask<InstanceSummary?> BuildSummaryAsync(
        Candidate candidate,
        RequestContext context,
        Dictionary<string, string?> definitionKeys,
        IReadOnlyList<ParticipationRole>? myRoles,
        CancellationToken ct)
    {
        string processKey;

        try
        {
            processKey = await ResolveProcessKeyAsync(
                candidate.ProcessDefinitionId,
                definitionKeys,
                ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 定义解析失败不该让整页失败：降级用定义 id 里的 key，仍然可以做白名单判定
            _logger.LogWarning(
                exception,
                "实例列表解析流程定义失败（{ProcessDefinitionId}）。",
                candidate.ProcessDefinitionId);

            processKey = InstanceReadRules.ParseKey(candidate.ProcessDefinitionId);
        }

        // 客户端白名单：未授权流程的实例按「不存在」处理（不进列表，不泄露归属）
        if (!context.CanUseProcess(processKey))
        {
            return null;
        }

        // 租户边界：无租户部署（TENANT_ID_ = ''）视为全局共享；实例带租户时必须与调用方一致
        if (!string.IsNullOrEmpty(candidate.TenantId)
            && context.EffectiveTenantId is not null
            && !string.Equals(candidate.TenantId, context.EffectiveTenantId, StringComparison.Ordinal))
        {
            return null;
        }

        var status = InstanceReadRules.ResolveStatus(
            candidate.HasExecution,
            candidate.SuspensionState,
            candidate.DeleteReason);

        var (activityIds, assignees) = await LoadPendingAsync(candidate, status, ct).ConfigureAwait(false);
        var conclusion = await LoadConclusionAsync(candidate, status, ct).ConfigureAwait(false);

        return new InstanceSummary(
            InstanceId: candidate.InstanceId,
            ProcessKey: processKey,
            BusinessKey: candidate.BusinessKey,
            Title: null,
            Status: status,
            StartedAt: InstanceReadRules.AsUtcOffset(candidate.StartTime),
            StartedBy: candidate.StartUserId,
            EndedAt: InstanceReadRules.AsUtcOffset(candidate.EndTime),
            CurrentActivityIds: activityIds,
            CurrentAssignees: assignees,
            Conclusion: conclusion,
            MyRoles: myRoles);
    }

    /// <summary>
    /// 取「当前停在哪、在谁手上」。
    /// </summary>
    /// <remarks>
    /// 只对运行中 / 已挂起的实例查待办：已结束的实例不可能有待办，白查一次没意义。
    /// <see cref="InstanceSummary.CurrentAssignees"/> 只收<b>已认领</b>待办的办理人；
    /// 候选组未认领时它为空——「这单在等谁认领」用 <see cref="InstanceSummary.CurrentActivityIds"/> 判断，
    /// 把候选人 / 候选组塞进同一个「人」列表只会让调用方分不清哪个是人哪个是组。
    /// </remarks>
    private async ValueTask<(IReadOnlyList<string> ActivityIds, IReadOnlyList<string> Assignees)> LoadPendingAsync(
        Candidate candidate,
        InstanceStatus status,
        CancellationToken ct)
    {
        if (status is not (InstanceStatus.Running or InstanceStatus.Suspended))
        {
            return ([], []);
        }

        var tasks = await _engine
            .FindTasksAsync(new EngineTaskQuery(ProcessInstanceId: candidate.InstanceId), ct)
            .ConfigureAwait(false);

        var activityIds = tasks
            .Where(task => task.TaskDefinitionKey is { Length: > 0 })
            .Select(task => task.TaskDefinitionKey!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var assignees = tasks
            .Where(task => task.Assignee is { Length: > 0 })
            .Select(task => task.Assignee!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return (activityIds, assignees);
    }

    /// <summary>
    /// 推导已结束实例的结论。
    /// </summary>
    /// <remarks>
    /// 运行中的实例结论恒为 <see langword="null"/>（<see cref="InstanceReadRules.ResolveConclusion"/> 的约定），
    /// 所以这里<b>不查变量</b>——列表里在办单据通常占少数但也不能白付一次往返。
    /// 已结束的才读历史变量（运行变量在实例结束后已随执行删除）。
    /// </remarks>
    private async ValueTask<InstanceConclusion?> LoadConclusionAsync(
        Candidate candidate,
        InstanceStatus status,
        CancellationToken ct)
    {
        if (status is not (InstanceStatus.Completed or InstanceStatus.Terminated))
        {
            return null;
        }

        try
        {
            var variables = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var variable in await _engine
                .FindHistoricVariablesAsync(candidate.InstanceId, ct)
                .ConfigureAwait(false))
            {
                // 任务局部变量不属于实例快照（与单条视图同口径）
                if (variable.TaskId is null)
                {
                    variables[variable.Name] = variable.Value;
                }
            }

            return InstanceReadRules.ResolveConclusion(status, variables);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 结论解析失败不该让一条单据从列表里消失：降级为「没有结论」，
            // 点进详情还能拿到（详情侧会重试）。
            _logger.LogWarning(
                exception,
                "实例列表推导结论失败（{InstanceId}）。",
                candidate.InstanceId);

            return null;
        }
    }

    /// <summary>解析定义 id → 流程 key（同批实例共享缓存，避免每条都查一次定义表）。</summary>
    private async ValueTask<string> ResolveProcessKeyAsync(
        string processDefinitionId,
        Dictionary<string, string?> cache,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
        {
            return string.Empty;
        }

        string? key;

        lock (cache)
        {
            if (cache.TryGetValue(processDefinitionId, out var cached))
            {
                return cached ?? InstanceReadRules.ParseKey(processDefinitionId);
            }
        }

        key = (await _definitionStore
            .FindByIdAsync(processDefinitionId, ct)
            .ConfigureAwait(false))?.Key;

        lock (cache)
        {
            cache[processDefinitionId] = key;
        }

        return key ?? InstanceReadRules.ParseKey(processDefinitionId);
    }

    /// <summary>
    /// 这条候选是否「排在游标之前」（已翻过），需要跳过。
    /// </summary>
    /// <remarks>
    /// 排序键是 <c>(开始时间, 实例 id)</c>：时间比游标更晚的在倒序里已经翻过、在升序里还没到；
    /// 同刻的多条按实例 id 升序排，因此倒序要跳过 id 更大的（<c>&gt;=</c> 含游标自身），
    /// 升序反过来。这条规则必须与 <see cref="ListAsync"/> 里的排序逐字对应。
    /// </remarks>
    private static bool IsBeforeCursor(Candidate candidate, CursorPosition position, bool descending)
    {
        var byTime = (candidate.StartTime ?? DateTime.MinValue)
            .CompareTo(position.StartTime ?? DateTime.MinValue);

        if (byTime != 0)
        {
            return descending ? byTime > 0 : byTime < 0;
        }

        var byId = string.CompareOrdinal(candidate.InstanceId, position.InstanceId);

        return descending ? byId >= 0 : byId <= 0;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    // ─────────────────────────── 游标 ───────────────────────────

    /// <summary>游标位置：排序键（开始时间）+ 实例 id（同刻的 tie-break）。</summary>
    private readonly record struct CursorPosition(DateTime? StartTime, string InstanceId);

    private static string EncodeCursor(Candidate candidate) =>
        Base64UrlEncode(
            $"{(candidate.StartTime ?? DateTime.MinValue).Ticks.ToString(CultureInfo.InvariantCulture)}"
            + $"\u001f{candidate.InstanceId}");

    /// <summary>
    /// 解析游标。
    /// </summary>
    /// <remarks>
    /// 用 <c>(开始时间, 实例 id)</c> 而不是偏移量：审批数据是持续写入的，offset 分页在并发写入下
    /// 会漏行或重复行。同刻多条用实例 id 做 tie-break，保证翻页边界唯一。
    /// </remarks>
    private static CursorPosition? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        var raw = Base64UrlDecode(cursor);
        var separator = raw.IndexOf('\u001f', StringComparison.Ordinal);

        if (separator <= 0
            || !long.TryParse(raw[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            throw new FormatException("分页游标格式不正确：请原样回传响应里的 nextCursor。");
        }

        var instanceId = raw[(separator + 1)..];

        return string.IsNullOrEmpty(instanceId)
            ? throw new FormatException("分页游标格式不正确：缺少实例 id。")
            : new CursorPosition(new DateTime(ticks, DateTimeKind.Utc), instanceId);
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("分页游标格式不正确。"),
        };

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    /// <summary>两侧合并后的候选实例（只为排序 / 判定存在，不对外）。</summary>
    private sealed record Candidate(
        string InstanceId,
        string ProcessDefinitionId,
        string? BusinessKey,
        DateTime? StartTime,
        string? StartUserId,
        DateTime? EndTime,
        string? DeleteReason,
        string? TenantId,
        bool HasExecution,
        int? SuspensionState);
}

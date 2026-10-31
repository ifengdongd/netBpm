using Microsoft.Extensions.Logging;
using NetBpm.Engine.History;
using NetBpm.Identity;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;
using EngineTaskQuery = NetBpm.Runtime.TaskQuery;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 参与者索引（回答「这个人跟哪些单据有关、什么关系」）。
/// </summary>
/// <remarks>
/// <para>
/// 「我的已办」与「我参与过的单据」看起来只是两个列表条件，但它们没有对应的单一字段：
/// 发起人要看 <c>ACT_HI_PROCINST.START_USER_ID_</c>，审批人要看历史任务的
/// <c>ASSIGNEE_</c>，候选人要走 <c>ACT_HI_IDENTITYLINK</c>。
/// <b>让调用方自己去拼这三个来源是不可能的</b>（它甚至不知道有这三张表），
/// 因此这里做成平台的一个独立出口：一次调用带回
/// 「实例 id → <see cref="ParticipationRole"/> 列表」的映射。
/// </para>
/// <para>
/// 为什么必须读<b>历史</b>身份关系：运行态的 <c>ACT_RU_IDENTITYLINK</c> 在任务完成、
/// 实例结束时被级联清理，而已办清单<b>全部</b>是已结束的任务——
/// 只查运行态会得到「我没审过任何单」这个安静的错误答案。
/// </para>
/// </remarks>
public interface IInstanceParticipation
{
    /// <summary>
    /// 查某人的参与关系。
    /// </summary>
    /// <param name="userId">用户 id（真人主体）。</param>
    /// <param name="groupIds">该用户所属的组（候选组命中要靠它；可以为空）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实例 id → 角色集合；没有任何关系时为空映射。</returns>
    ValueTask<InstanceInvolvement> FindAsync(
        string userId,
        IReadOnlyCollection<string>? groupIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// 判定某人与<b>指定实例</b>的关系（单实例判定）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="FindAsync"/> 的答案等价，但代价不同：那边问的是「这个人名下的全部单据」，
    /// 这里只问「这一张单据」。按实例看审批记录、按实例做可见性判定时用这个——
    /// 让一次单实例读取去扫描某人名下的全部历史任务，是典型的读放大。
    /// </para>
    /// <para>
    /// 同样遵循「任一来源失败只降级」：单实例判定发生在读路径上，
    /// 一个来源抖动不该让整张单据打不开。
    /// </para>
    /// <para>
    /// <b>不含发起人</b>：发起人由调用方比 <c>InstanceView.StartedBy</c> 得出——
    /// 那个值读实例时已经在手边，为它再查一次历史表不划算。
    /// </para>
    /// </remarks>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="userId">用户 id（真人主体）。</param>
    /// <param name="groupIds">该用户所属的组（候选组命中要靠它）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<InstanceRelationship> DescribeAsync(
        string instanceId,
        string userId,
        IReadOnlyCollection<string>? groupIds = null,
        CancellationToken ct = default);
}

/// <summary>
/// 一个人的参与关系集合（<see cref="IInstanceParticipation.FindAsync"/> 的结果）。
/// </summary>
/// <remarks>
/// 设计成不可变只读视图而不是直接返回 <see cref="Dictionary{TKey,TValue}"/>：
/// 调用方（列表分页）只需要「判断在不在」与「取角色」两件事，
/// 返回可变字典等于允许误改，而误改造成的缺行很难查。
/// </remarks>
public sealed class InstanceInvolvement
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ParticipationRole>> _roles;

    internal InstanceInvolvement(IReadOnlyDictionary<string, IReadOnlyList<ParticipationRole>> roles) =>
        _roles = roles;

    /// <summary>没有任何参与关系时的空结果。</summary>
    public static InstanceInvolvement Empty { get; } =
        new(new Dictionary<string, IReadOnlyList<ParticipationRole>>(StringComparer.Ordinal));

    /// <summary>参与的实例个数。</summary>
    public int Count => _roles.Count;

    /// <summary>这个人是否与指定实例有关。</summary>
    /// <param name="instanceId">实例 id。</param>
    public bool IsInvolved(string instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) && _roles.ContainsKey(instanceId);

    /// <summary>取这个人相对指定实例的角色；无关时返回空集合。</summary>
    /// <param name="instanceId">实例 id。</param>
    public IReadOnlyList<ParticipationRole> RolesFor(string instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId)
        && _roles.TryGetValue(instanceId, out var roles)
            ? roles
            : [];

    /// <summary>全部参与的实例 id。</summary>
    public IEnumerable<string> InstanceIds => _roles.Keys;
}

/// <summary>
/// 一个人与<b>单个实例</b>的关系（<see cref="IInstanceParticipation.DescribeAsync"/> 的结果）。
/// </summary>
/// <param name="Involved">是否有关（<see cref="Roles"/> 非空与之恒等，留这个字段是为了让调用点读起来像句子）。</param>
/// <param name="Roles">角色集合（去重升序）；无关时为空。</param>
public sealed record InstanceRelationship(
    bool Involved,
    IReadOnlyList<ParticipationRole> Roles)
{
    /// <summary>无关时的结果。</summary>
    public static InstanceRelationship None { get; } = new(false, []);
}

/// <inheritdoc />
public sealed class InstanceParticipation : IInstanceParticipation
{
    private readonly IProcessEngine _engine;
    private readonly ILogger<InstanceParticipation> _logger;

    /// <summary>构造。</summary>
    /// <param name="engine">引擎门面（历史与运行态的数据源）。</param>
    /// <param name="logger">日志。</param>
    public InstanceParticipation(IProcessEngine engine, ILogger<InstanceParticipation> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<InstanceInvolvement> FindAsync(
        string userId,
        IReadOnlyCollection<string>? groupIds = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var groups = groupIds?.Where(group => !string.IsNullOrWhiteSpace(group)).ToList() ?? [];
        var builder = new Map();

        // ① 发起人：历史 + 运行态（历史开了一样要查运行态——它是「开了历史之后才写的」）
        await CollectAsync(
            $"按发起人查历史实例（{userId}）",
            async () =>
            {
                foreach (var instance in await _engine
                    .FindHistoricProcessInstancesAsync(
                        new HistoricProcessInstanceQuery(StartUserId: userId), ct)
                    .ConfigureAwait(false))
                {
                    builder.AddStarter(instance.Id);
                }
            }).ConfigureAwait(false);

        await CollectAsync(
            $"按发起人查运行实例（{userId}）",
            async () =>
            {
                foreach (var instance in await _engine
                    .FindProcessInstancesAsync(new ProcessInstanceQuery(StartUserId: userId), ct)
                    .ConfigureAwait(false))
                {
                    builder.AddStarter(instance.Id);
                }
            }).ConfigureAwait(false);

        // ② 审批人：历史任务的办理人（这是唯一能回答「已办」的来源）
        await CollectAsync(
            $"按办理人查历史任务（{userId}）",
            async () =>
            {
                foreach (var task in await _engine
                    .FindHistoricTasksAsync(new HistoricTaskQuery(Assignee: userId), ct)
                    .ConfigureAwait(false))
                {
                    builder.AddApprover(task.ProcessInstanceId);
                }
            }).ConfigureAwait(false);

        // 运行态兜底：历史关闭的宿主（或开了历史之前就办过的任务）只有运行态能回答
        await CollectAsync(
            $"按办理人查运行待办（{userId}）",
            async () =>
            {
                foreach (var task in await _engine
                    .FindTasksAsync(new EngineTaskQuery(Assignee: userId, AllUsers: true), ct)
                    .ConfigureAwait(false))
                {
                    builder.AddApprover(task.ProcessInstanceId);
                }
            }).ConfigureAwait(false);

        // ③ 候选关系：历史身份关系一次下推（用户 OR 组），这是「参与者索引」的正主
        await CollectAsync(
            $"按用户 / 组查历史身份关系（{userId}，{groups.Count} 个组）",
            async () =>
            {
                foreach (var link in await _engine
                    .FindHistoricIdentityLinksAsync(
                        new HistoricIdentityLinkQuery(UserId: userId, GroupIds: groups), ct)
                    .ConfigureAwait(false))
                {
                    builder.AddIdentityLink(link.Type, link.ProcessInstanceId);
                }
            }).ConfigureAwait(false);

        // 运行态兜底：候选用户 / 候选组（GroupIds 逐个查，组通常只有个位数）
        await CollectAsync(
            $"按候选用户查运行待办（{userId}）",
            async () =>
            {
                foreach (var task in await _engine
                    .FindTasksAsync(new EngineTaskQuery(CandidateUser: userId, AllUsers: true), ct)
                    .ConfigureAwait(false))
                {
                    builder.AddIdentityLink(IdentityLinkType.Candidate, task.ProcessInstanceId);
                }
            }).ConfigureAwait(false);

        foreach (var group in groups)
        {
            var groupId = group;

            await CollectAsync(
                $"按候选组查运行待办（{groupId}）",
                async () =>
                {
                    foreach (var task in await _engine
                        .FindTasksAsync(new EngineTaskQuery(CandidateGroup: groupId, AllUsers: true), ct)
                        .ConfigureAwait(false))
                    {
                        builder.AddIdentityLink(IdentityLinkType.Candidate, task.ProcessInstanceId);
                    }
                }).ConfigureAwait(false);
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public async ValueTask<InstanceRelationship> DescribeAsync(
        string instanceId,
        string userId,
        IReadOnlyCollection<string>? groupIds = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var groups = groupIds?.Where(group => !string.IsNullOrWhiteSpace(group)).ToList() ?? [];
        var roles = new SortedSet<ParticipationRole>();

        // ① 候选人 / 参与者：历史身份关系一次下推（用户 OR 组 OR 实例，SQL 侧已经在三个维度上过滤了）
        await CollectAsync(
            $"判定 {userId} 与实例 {instanceId} 的历史身份关系",
            async () =>
            {
                if (await _engine
                    .FindHistoricIdentityLinksAsync(
                        new HistoricIdentityLinkQuery(
                            UserId: userId,
                            GroupIds: groups,
                            ProcessInstanceId: instanceId,
                            MaxResults: 1),
                        ct)
                    .ConfigureAwait(false) is { Count: > 0 } links)
                {
                    // 候选取代参与者（与全文索引同一口径：候选是更具体的那句）
                    roles.Add(links[0].Type is { Length: > 0 } type
                        && string.Equals(type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                            ? ParticipationRole.Candidate
                            : ParticipationRole.Participant);
                }
            }).ConfigureAwait(false);

        // ② 审批人：历史任务优先（已办），没有再退回运行待办（在办还没落到历史里）
        if (!roles.Contains(ParticipationRole.Approver))
        {
            await CollectAsync(
                $"判定 {userId} 是否办过实例 {instanceId} 的任务",
                async () =>
                {
                    if (await _engine
                        .FindHistoricTasksAsync(
                            new HistoricTaskQuery(
                                ProcessInstanceId: instanceId,
                                Assignee: userId,
                                MaxResults: 1),
                            ct)
                        .ConfigureAwait(false) is { Count: > 0 })
                    {
                        roles.Add(ParticipationRole.Approver);
                    }
                }).ConfigureAwait(false);
        }

        if (!roles.Contains(ParticipationRole.Approver))
        {
            await CollectAsync(
                $"判定 {userId} 是否持有实例 {instanceId} 的在办任务",
                async () =>
                {
                    if (await _engine
                        .FindTasksAsync(
                            new EngineTaskQuery(
                                Assignee: userId,
                                ProcessInstanceId: instanceId,
                                AllUsers: true,
                                MaxResults: 1),
                            ct)
                        .ConfigureAwait(false) is { Count: > 0 })
                    {
                        roles.Add(ParticipationRole.Approver);
                    }
                }).ConfigureAwait(false);
        }

        // ③ 运行态候选兜底（历史关闭的宿主）：已由上面的流程确认未命中时才查，避免每次多扫
        if (roles.Count == 0)
        {
            await CollectAsync(
                $"判定 {userId} 是否为实例 {instanceId} 的运行态候选人",
                async () =>
                {
                    if (await _engine
                        .FindTasksAsync(
                            new EngineTaskQuery(
                                CandidateUser: userId,
                                ProcessInstanceId: instanceId,
                                AllUsers: true,
                                MaxResults: 1),
                            ct)
                        .ConfigureAwait(false) is { Count: > 0 })
                    {
                        roles.Add(ParticipationRole.Candidate);
                    }
                }).ConfigureAwait(false);

            foreach (var group in groups)
            {
                var groupId = group;

                if (roles.Contains(ParticipationRole.Candidate))
                {
                    break;
                }

                await CollectAsync(
                    $"判定组 {groupId} 是否为实例 {instanceId} 的运行态候选组",
                    async () =>
                    {
                        if (await _engine
                            .FindTasksAsync(
                                new EngineTaskQuery(
                                    CandidateGroup: groupId,
                                    ProcessInstanceId: instanceId,
                                    AllUsers: true,
                                    MaxResults: 1),
                                ct)
                            .ConfigureAwait(false) is { Count: > 0 })
                        {
                            roles.Add(ParticipationRole.Candidate);
                        }
                    }).ConfigureAwait(false);
            }
        }

        return roles.Count == 0 ? InstanceRelationship.None : new InstanceRelationship(true, [.. roles]);
    }

    /// <summary>
    /// 单取一个来源：任一路失败只告警，不让「我参与过的单据」整页 500。
    /// </summary>
    /// <remarks>
    /// 六个来源之间是<b>并集</b>关系，少一路的结果是「少几条单据」（降级），
    /// 而不是「答案错了」；反过来让整页失败才是真的事故——
    /// 「我的单据列表打不开」比「列表少一两条」严重得多。
    /// </remarks>
    private async ValueTask CollectAsync(string what, Func<ValueTask> collect)
    {
        try
        {
            await collect().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "参与者索引：{What} 失败，本次结果可能不完整。", what);
        }
    }

    /// <summary>
    /// 把「三个来源」合成一个角色映射（内部累加器）。
    /// </summary>
    private sealed class Map
    {
        private readonly Dictionary<string, SortedSet<ParticipationRole>> _roles =
            new(StringComparer.Ordinal);

        public void AddStarter(string? instanceId) => Add(instanceId, ParticipationRole.Starter);

        public void AddApprover(string? instanceId) => Add(instanceId, ParticipationRole.Approver);

        /// <summary>
        /// 按身份关系的类型落角色；未知类型按参与者处理（保守：它同样证明了「有关」）。
        /// </summary>
        /// <remarks>
        /// 候选命中会<b>吃掉</b>参与者：引擎在登记候选用户时会顺手写一条实例级
        /// <c>participant</c> 行（Activiti 的 <c>involveUser</c> 行为），
        /// 于是同一个人身上同时有候选与参与两种关系。两个都给会让调用方分不清该看哪个，
        /// 而「候选人」是更具体的那句——「只是被拉进来看看」才是 <c>participant</c>。
        /// </remarks>
        public void AddIdentityLink(string? type, string? instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return;
            }

            if (string.Equals(type, IdentityLinkType.Candidate, StringComparison.Ordinal))
            {
                Add(instanceId, ParticipationRole.Candidate);
                _roles[instanceId].Remove(ParticipationRole.Participant);

                return;
            }

            if (Roles(instanceId).Contains(ParticipationRole.Candidate))
            {
                return;
            }

            Add(instanceId, ParticipationRole.Participant);
        }

        public InstanceInvolvement Build()
        {
            var result = new Dictionary<string, IReadOnlyList<ParticipationRole>>(
                _roles.Count,
                StringComparer.Ordinal);

            foreach (var pair in _roles)
            {
                result[pair.Key] = [.. pair.Value];
            }

            return new InstanceInvolvement(result);
        }

        private SortedSet<ParticipationRole> Roles(string instanceId)
        {
            if (!_roles.TryGetValue(instanceId, out var roles))
            {
                roles = [];
                _roles[instanceId] = roles;
            }

            return roles;
        }

        private void Add(string? instanceId, ParticipationRole role)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return;
            }

            Roles(instanceId).Add(role);
        }
    }
}

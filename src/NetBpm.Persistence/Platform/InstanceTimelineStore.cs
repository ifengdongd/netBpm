using System.Collections.Concurrent;
using Dapper;
using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 审批记录时间线的存储（表 <c>nb_instance_timeline</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>append-only</b>：接口刻意只有「追加」与「读」，没有 upsert / delete。
/// 一旦历史可以被改写，它就不再是证据。
/// </para>
/// <para>
/// <see cref="AppendAsync"/> 的 <c>Sequence</c> 由存储层分配（读当前最大值 +1，撞唯一键就重读重试），
/// 调用方传进来的 <see cref="TimelineEntry.Sequence"/> 会被忽略——序号是并发安全的产物，不该由业务决定。
/// </para>
/// </remarks>
public interface IApprovalTimelineStore
{
    /// <summary>追加一条时间线条目，返回带真实序号的那一条。</summary>
    /// <param name="tenantId">租户（平台表一律带租户，便于归档 / 清理）。</param>
    /// <param name="entry">条目（<c>Sequence</c> 会被忽略，由存储层分配）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<TimelineEntry> AppendAsync(
        string tenantId,
        TimelineEntry entry,
        CancellationToken ct = default);

    /// <summary>按实例读条目（升序）。</summary>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="afterSequence">只取序号大于它的条目（增量拉取游标；0 表示从头）。</param>
    /// <param name="limit">最多取多少条。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<TimelineEntry>> ListAsync(
        string instanceId,
        long afterSequence,
        int limit,
        CancellationToken ct = default);

    /// <summary>该实例已有多少条目（判断是否需要从引擎历史回填）。</summary>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<int> CountAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// 按任务 id 批量取「审批定稿」条目（只取 <c>TASK_COMPLETED</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是「我的已办」能带回<b>我当时的结论</b>的唯一来源：选项 id / 语义 / 审批意见
    /// 都不在引擎的任务表里（引擎不认识审批意见这个业务概念），只在写时定稿的时间线上。
    /// </para>
    /// <para>
    /// 刻意<b>不</b>做「按 actor 查全部实例」：那样等于给人建一张跨实例的二级索引，
    /// 而调用方（已办列表）手里的钥匙本来就是 taskId 集合——
    /// 按已知 taskId 精确定位，比按人扫一张大表便宜得多，也不用为一致性买单。
    /// </para>
    /// </remarks>
    /// <param name="taskIds">任务 id 集合（去重后传入；空集合返回空列表，不查库）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<TimelineEntry>> ListDecisionsByTaskIdsAsync(
        IReadOnlyList<string> taskIds,
        CancellationToken ct = default);
}

/// <summary>
/// 数据库版时间线存储。
/// </summary>
/// <remarks>
/// 列名下划线、属性驼峰，Dapper 不自动映射，SQL 里逐列 <c>as</c> 别名（本仓规矩）。
/// 序号分配走「读 max + 插入 + 唯一键冲突重试」：单条插入的开销已经够小，
/// 换成数据库序列反而要给两种方言各写一套 DDL。
/// </remarks>
public sealed class DapperApprovalTimelineStore : IApprovalTimelineStore
{
    private const string Columns =
        "process_instance_id as InstanceId, seq as Seq, kind as KindText, activity_id as ActivityId, "
        + "activity_name as ActivityName, task_id as TaskId, actor_id as ActorId, "
        + "actor_caller_id as ActorCallerId, option_id as OptionId, semantic as SemanticText, "
        + "comment_text as Comment, target_node_id as TargetNodeId, "
        + "form_snapshot_task_id as FormSnapshotTaskId, round_no as RoundNo, occurred_at as OccurredAt";

    private const string InsertSql =
        "insert into nb_instance_timeline "
        + "(process_instance_id, seq, tenant_id, kind, activity_id, activity_name, task_id, "
        + "actor_id, actor_caller_id, option_id, semantic, comment_text, target_node_id, "
        + "form_snapshot_task_id, round_no, payload_json, occurred_at) "
        + "values (@instanceId, @seq, @tenantId, @kind, @activityId, @activityName, @taskId, "
        + "@actorId, @actorCallerId, @optionId, @semantic, @comment, @targetNodeId, "
        + "@formSnapshotTaskId, @roundNo, null, @occurredAt)";

    /// <summary>
    /// 一次取齐「下一个序号」与「该环节上一轮的位置 / 轮次」，用来定轮次。
    /// </summary>
    /// <remarks>
    /// 三个标量放同一条 select：它们本来就都要读这张表，分成三条语句只会多两次往返，
    /// 还会让三次读取之间出现「别人刚插了一条」的窗口（序号与轮次对不上）。
    /// </remarks>
    private const string NextPositionSql =
        "select "
        + "coalesce((select max(seq) from nb_instance_timeline where process_instance_id = @instanceId), 0) as MaxSeq, "
        + "coalesce((select max(seq) from nb_instance_timeline where process_instance_id = @instanceId "
        + "and activity_id = @activityId), 0) as LastSeq, "
        + "coalesce((select max(round_no) from nb_instance_timeline where process_instance_id = @instanceId "
        + "and activity_id = @activityId), 0) as LastRound";

    /// <summary>唯一键冲突时的重试次数（并发追加同一实例）。</summary>
    private const int MaxAppendAttempts = 5;

    private readonly IDbConnectionFactory _connections;

    /// <summary>构造。</summary>
    /// <param name="connections">连接工厂。</param>
    public DapperApprovalTimelineStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<TimelineEntry> AppendAsync(
        string tenantId,
        TimelineEntry entry,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = _connections.CreateConnection();

        // 跳转类条目自己没有 activity_id（进的是 target_node_id），但「即将重新进入哪个环节」
        // 正是定轮次需要的信息，否则跳回同一节点会被算成同一轮
        var activityId = TimelineRoundRules.EffectiveActivity(entry);

        for (var attempt = 1; ; attempt++)
        {
            var position = await connection
                .QuerySingleAsync<Position>(new CommandDefinition(
                    NextPositionSql,
                    new { instanceId = entry.InstanceId, activityId },
                    cancellationToken: ct))
                .ConfigureAwait(false);

            var next = entry with
            {
                Sequence = position.MaxSeq + 1,
                Round = TimelineRoundRules.Resolve(
                    activityId,
                    entry.Kind,
                    position.MaxSeq,
                    position.LastSeq,
                    position.LastRound),
            };

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    InsertSql,
                    new
                    {
                        instanceId = next.InstanceId,
                        seq = next.Sequence,
                        tenantId,
                        kind = TimelineKindText.ToText(next.Kind),
                        activityId = next.ActivityId,
                        activityName = next.ActivityName,
                        taskId = next.TaskId,
                        actorId = next.ActorId,
                        actorCallerId = next.ActorCallerId,
                        optionId = next.OptionId,
                        semantic = next.Semantic is { } semantic ? SemanticText.ToText(semantic) : null,
                        comment = next.Comment,
                        targetNodeId = next.TargetNodeId,
                        formSnapshotTaskId = next.FormSnapshotTaskId,
                        roundNo = next.Round,
                        // 时间列统一按 UTC 写入（datetime(3) 不存时区）
                        occurredAt = next.OccurredAt.UtcDateTime,
                    },
                    cancellationToken: ct))
                    .ConfigureAwait(false);

                return next;
            }
            catch (Exception exception) when (attempt < MaxAppendAttempts && DatabaseErrors.IsUniqueViolation(exception))
            {
                // 另一个写入者抢先占了这个序号：重读 max 再来一次
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TimelineEntry>> ListAsync(
        string instanceId,
        long afterSequence,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = _connections.CreateConnection();

        var parameters = new DynamicParameters();
        parameters.Add("instanceId", instanceId);
        parameters.Add("after", afterSequence);

        var paging = SqlPaging.Build(_connections.DatabaseType, 0, limit, parameters);

        var rows = await connection
            .QueryAsync<Row>(new CommandDefinition(
                $"select {Columns} from nb_instance_timeline "
                + "where process_instance_id = @instanceId and seq > @after "
                + $"order by seq{paging}",
                parameters,
                cancellationToken: ct))
            .ConfigureAwait(false);

        return [.. rows.Select(ToEntry)];
    }

    /// <inheritdoc />
    public async ValueTask<int> CountAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await using var connection = _connections.CreateConnection();

        return await connection
            .ExecuteScalarAsync<int>(new CommandDefinition(
                "select count(*) from nb_instance_timeline where process_instance_id = @instanceId",
                new { instanceId },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TimelineEntry>> ListDecisionsByTaskIdsAsync(
        IReadOnlyList<string> taskIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);

        var ids = taskIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = _connections.CreateConnection();

        var rows = await connection
            .QueryAsync<Row>(new CommandDefinition(
                $"select {Columns} from nb_instance_timeline "
                + "where kind = @kind and task_id in @taskIds order by seq asc",
                new { kind = TimelineKindText.ToText(TimelineEntryKind.TaskCompleted), taskIds = ids },
                cancellationToken: ct))
            .ConfigureAwait(false);

        return [.. rows.Select(ToEntry)];
    }

    private static TimelineEntry ToEntry(Row row) =>
        new(
            Sequence: row.Seq,
            InstanceId: row.InstanceId,
            Kind: TimelineKindText.TryParse(row.KindText, out var kind) ? kind : TimelineEntryKind.Comment,
            ActivityId: row.ActivityId,
            ActivityName: row.ActivityName,
            TaskId: row.TaskId,
            ActorId: row.ActorId,
            ActorCallerId: row.ActorCallerId,
            OptionId: row.OptionId,
            Semantic: SemanticText.TryParse(row.SemanticText, out var semantic) ? semantic : null,
            Comment: row.Comment,
            TargetNodeId: row.TargetNodeId,
            FormSnapshotTaskId: row.FormSnapshotTaskId,
            OccurredAt: PlatformTimestamps.ToOffset(row.OccurredAt),
            Round: row.RoundNo);

    /// <summary>Dapper 行（属性名与 SQL 别名一致）。</summary>
    private sealed class Row
    {
        public string InstanceId { get; set; } = string.Empty;

        public long Seq { get; set; }

        public string KindText { get; set; } = string.Empty;

        public string? ActivityId { get; set; }

        public string? ActivityName { get; set; }

        public string? TaskId { get; set; }

        public string? ActorId { get; set; }

        public string? ActorCallerId { get; set; }

        public string? OptionId { get; set; }

        public string? SemanticText { get; set; }

        public string? Comment { get; set; }

        public string? TargetNodeId { get; set; }

        public string? FormSnapshotTaskId { get; set; }

        public int RoundNo { get; set; }

        public DateTime OccurredAt { get; set; }
    }

    /// <summary>下一次追加的位置信息（见 <see cref="NextPositionSql"/>）。</summary>
    private sealed class Position
    {
        /// <summary>该实例当前最大序号（0 = 还没有任何条目）。</summary>
        public long MaxSeq { get; set; }

        /// <summary>目标环节上一条条目的序号（0 = 这个环节还没出现过）。</summary>
        public long LastSeq { get; set; }

        /// <summary>目标环节已有的最大轮次（0 = 老数据 / 还没出现过）。</summary>
        public int LastRound { get; set; }
    }
}

/// <summary>
/// 内存版时间线存储（无库宿主 / 单元测试）。
/// </summary>
/// <remarks>
/// 与数据库版同样是 append-only，序号分配在锁内完成——单进程内不需要重试。
/// </remarks>
public sealed class InMemoryApprovalTimelineStore : IApprovalTimelineStore
{
    private readonly Dictionary<string, List<TimelineEntry>> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask<TimelineEntry> AppendAsync(
        string tenantId,
        TimelineEntry entry,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.InstanceId, out var list))
            {
                list = [];
                _entries[entry.InstanceId] = list;
            }

            var activityId = TimelineRoundRules.EffectiveActivity(entry);
            var lastSeq = 0L;
            var lastRound = 0;

            if (activityId is { Length: > 0 })
            {
                // 与数据库版同口径：取该环节上一条的序号与最大轮次
                for (var index = list.Count - 1; index >= 0; index--)
                {
                    if (!string.Equals(list[index].ActivityId, activityId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    lastSeq = list[index].Sequence;
                    lastRound = list[index].Round;
                    break;
                }

                // 最大轮次可能与「最后一条」不同（轮次单调递增，但中间可能有老数据行），取两者最大值
                foreach (var item in list)
                {
                    if (string.Equals(item.ActivityId, activityId, StringComparison.Ordinal))
                    {
                        lastRound = Math.Max(lastRound, item.Round);
                    }
                }
            }

            var maxSeq = list.Count == 0 ? 0 : list[^1].Sequence;

            var appended = entry with
            {
                Sequence = maxSeq + 1,
                Round = TimelineRoundRules.Resolve(activityId, entry.Kind, maxSeq, lastSeq, lastRound),
            };

            list.Add(appended);

            return ValueTask.FromResult(appended);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TimelineEntry>> ListAsync(
        string instanceId,
        long afterSequence,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (_gate)
        {
            IReadOnlyList<TimelineEntry> result = _entries.TryGetValue(instanceId, out var list)
                ? [.. list.Where(item => item.Sequence > afterSequence).Take(limit)]
                : [];

            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CountAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        lock (_gate)
        {
            return ValueTask.FromResult(_entries.TryGetValue(instanceId, out var list) ? list.Count : 0);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TimelineEntry>> ListDecisionsByTaskIdsAsync(
        IReadOnlyList<string> taskIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);

        if (taskIds.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<TimelineEntry>>([]);
        }

        var wanted = new HashSet<string>(taskIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);

        lock (_gate)
        {
            IReadOnlyList<TimelineEntry> result = [.. _entries.Values
                .SelectMany(list => list)
                .Where(item => item.Kind == TimelineEntryKind.TaskCompleted
                    && item.TaskId is { Length: > 0 } taskId
                    && wanted.Contains(taskId))
                .OrderBy(item => item.Sequence)];

            return ValueTask.FromResult(result);
        }
    }
}

/// <summary>
/// 时间线「轮次」的推导规则（写入时定稿，读取端不再猜）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须写时定</b>：读端只能看条目连不连续。正常退回是「A、B、A」——断开可判；
/// 但管理干预「跳回当前节点」是「A、JUMPED、A」，在只看完成条目的视角里 A、A 是<b>连着的</b>，
/// 必然把两轮并成一轮，于是「第 1 轮 1 人同意 + 第 2 轮 2 人同意」被算成「3 人同意」。
/// </para>
/// <para>
/// 判定规则（针对「本条目标环节」这一维度）：
/// <list type="number">
///   <item><description>该环节从没出现过 → 第 1 轮；</description></item>
///   <item><description>本条是<b>重新进入标记</b>（<c>TASK_CREATED</c> / <c>INSTANCE_JUMPED</c>）
///     → 上一轮 +1；</description></item>
///   <item><description>该环节上一条之后又插入了别的条目（走过别的节点才回到这里）→ 上一轮 +1；</description></item>
///   <item><description>其余情况（同一轮里的第二、三个人）→ 沿用上一轮。</description></item>
/// </list>
/// 第 3 条正是「一个会签环节 3 个人各批一次」与「退回后重批」的区分点：
/// 前者条目之间没有别人的记录，后者一定夹着退回动作或别的节点。
/// </para>
/// <para>
/// 老数据（本能力上线前写入）的轮次是 0，规则里统一按「沿用」处理：不猜、不补写，
/// 由读端认出 0 之后走连续段近似（见 <c>NodeOutcomeAggregator</c>）。
/// </para>
/// </remarks>
internal static class TimelineRoundRules
{
    /// <summary>本条目的「目标环节」：跳转类没有 <c>activity_id</c>，它的目标是 <c>target_node_id</c>。</summary>
    internal static string? EffectiveActivity(TimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return string.IsNullOrEmpty(entry.ActivityId) && entry.Kind == TimelineEntryKind.InstanceJumped
            ? entry.TargetNodeId
            : entry.ActivityId;
    }

    /// <summary>算出本条条目应落的轮次（<c>0</c> = 与环节无关的条目，如发起 / 结束）。</summary>
    /// <param name="activityId">目标环节（<see cref="EffectiveActivity"/> 的结果）。</param>
    /// <param name="kind">条目类型。</param>
    /// <param name="maxSeq">该实例当前最大序号。</param>
    /// <param name="lastSeq">目标环节上一条条目的序号（0 = 还没出现过）。</param>
    /// <param name="lastRound">目标环节已有的最大轮次（0 = 老数据 / 还没出现过）。</param>
    internal static int Resolve(
        string? activityId,
        TimelineEntryKind kind,
        long maxSeq,
        long lastSeq,
        int lastRound)
    {
        if (string.IsNullOrEmpty(activityId))
        {
            return 0;
        }

        if (lastSeq == 0)
        {
            return 1;
        }

        if (kind is TimelineEntryKind.TaskCreated or TimelineEntryKind.InstanceJumped)
        {
            return lastRound + 1;
        }

        return maxSeq > lastSeq ? lastRound + 1 : lastRound;
    }
}

/// <summary>时间线条目类型的文本词汇（与对外 JSON 词汇一致，不存枚举序号）。</summary>
internal static class TimelineKindText
{
    internal static string ToText(TimelineEntryKind kind) => kind switch
    {
        TimelineEntryKind.InstanceStarted => "INSTANCE_STARTED",
        TimelineEntryKind.TaskCreated => "TASK_CREATED",
        TimelineEntryKind.TaskCompleted => "TASK_COMPLETED",
        TimelineEntryKind.TaskClaimed => "TASK_CLAIMED",
        TimelineEntryKind.TaskUnclaimed => "TASK_UNCLAIMED",
        TimelineEntryKind.TaskTransferred => "TASK_TRANSFERRED",
        TimelineEntryKind.InstanceSuspended => "INSTANCE_SUSPENDED",
        TimelineEntryKind.InstanceResumed => "INSTANCE_RESUMED",
        TimelineEntryKind.InstanceTerminated => "INSTANCE_TERMINATED",
        TimelineEntryKind.InstanceCompleted => "INSTANCE_COMPLETED",
        TimelineEntryKind.Comment => "COMMENT",
        TimelineEntryKind.ParametersUpdated => "PARAMETERS_UPDATED",
        TimelineEntryKind.InstanceJumped => "INSTANCE_JUMPED",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的时间线条目类型。"),
    };

    internal static bool TryParse(string? text, out TimelineEntryKind kind)
    {
        kind = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Enum.TryParse(text.Trim().Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out kind);
    }
}

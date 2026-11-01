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
        + "form_snapshot_task_id as FormSnapshotTaskId, occurred_at as OccurredAt";

    private const string InsertSql =
        "insert into nb_instance_timeline "
        + "(process_instance_id, seq, tenant_id, kind, activity_id, activity_name, task_id, "
        + "actor_id, actor_caller_id, option_id, semantic, comment_text, target_node_id, "
        + "form_snapshot_task_id, payload_json, occurred_at) "
        + "values (@instanceId, @seq, @tenantId, @kind, @activityId, @activityName, @taskId, "
        + "@actorId, @actorCallerId, @optionId, @semantic, @comment, @targetNodeId, "
        + "@formSnapshotTaskId, null, @occurredAt)";

    private const string MaxSeqSql =
        "select coalesce(max(seq), 0) from nb_instance_timeline where process_instance_id = @instanceId";

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

        for (var attempt = 1; ; attempt++)
        {
            var seq = await connection
                .ExecuteScalarAsync<long>(new CommandDefinition(
                    MaxSeqSql,
                    new { instanceId = entry.InstanceId },
                    cancellationToken: ct))
                .ConfigureAwait(false);

            var next = new TimelineEntry(
                Sequence: seq + 1,
                InstanceId: entry.InstanceId,
                Kind: entry.Kind,
                ActivityId: entry.ActivityId,
                ActivityName: entry.ActivityName,
                TaskId: entry.TaskId,
                ActorId: entry.ActorId,
                ActorCallerId: entry.ActorCallerId,
                OptionId: entry.OptionId,
                Semantic: entry.Semantic,
                Comment: entry.Comment,
                TargetNodeId: entry.TargetNodeId,
                FormSnapshotTaskId: entry.FormSnapshotTaskId,
                OccurredAt: entry.OccurredAt);

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
            OccurredAt: PlatformTimestamps.ToOffset(row.OccurredAt));

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

        public DateTime OccurredAt { get; set; }
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

            var appended = entry with { Sequence = list.Count == 0 ? 1 : list[^1].Sequence + 1 };
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

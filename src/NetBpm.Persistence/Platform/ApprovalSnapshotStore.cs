using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 表单快照的存储（表 <c>nb_approval_snapshot</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要在提交时把<b>字段定义</b>一起冻住：流程定义会升级，实例却停在旧版本上。
/// 只存表单值是不够的——值离开定义就没有类型、没有下拉项、没有必填规则，
/// 历史页要么显示一堆裸字符串，要么偷偷按新定义渲染出「当时根本不存在」的字段。
/// </para>
/// <para>
/// 因此这里是「<b>值 + 定义</b>」的一起落库，键是任务 id：
/// 它是「这一次提交」的唯一锚点，也正是时间线条目里的
/// <c>formSnapshotTaskId</c>（教科书里的那句：历史带着它的解释走）。
/// </para>
/// <para>
/// 与主流程的关系：<c>写失败不影响审批</c>。任务已经被引擎办结了，
/// 这时候抛错只会让调用方以为没提交成功——缺一份快照是遗憾，撒谎才是事故。
/// </para>
/// </remarks>
public interface IApprovalSnapshotStore
{
    /// <summary>按任务 id 取快照；不存在返回 <see langword="null"/>。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ApprovalSnapshotRecord?> FindAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// 落一份快照；已存在时<b>保留先写的那份</b>并返回 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// 主键冲突不覆盖：同一任务被并发重复提交时谁是「原始记录」由时间决定，
    /// 后来的请求也没有资格改写已经写进去的那一份。
    /// </remarks>
    /// <param name="record">快照。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<bool> AppendAsync(ApprovalSnapshotRecord record, CancellationToken ct = default);

    /// <summary>按实例列出全部快照（历史详情的多屏渲染 / 导出）。</summary>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ApprovalSnapshotRecord>> ListByInstanceAsync(
        string instanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 删除定稿时间早于 <paramref name="before"/> 的快照，返回删除条数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 保留期<b>与历史归档共用同一个配置</b>（<c>NetBpm:Admin:ArchiveRetentionDays</c>，
    /// 见 <c>AdminArchiveCleanupService</c>）：快照的作用是「解释这张单当时怎么填的」，
    /// 而它解释的那段历史一旦被归档清理掉，快照就只剩一份无法定位的孤儿数据。
    /// 两者同进同退，不会出现「历史没了、快照还在」或反之的错位。
    /// </para>
    /// <para>
    /// ⚠️ 与「实例被删除」是两件事：显式删除实例<b>不</b>级联删快照（本表刻意没有外键），
    /// 快照仍是那张单存在过的唯一解释；只有按保留期清理才会真正抹掉它。
    /// 因此保留期应当 ≥ 审计/合规要求的年限，<c>0</c>（默认）表示永不清理。
    /// </para>
    /// </remarks>
    /// <param name="before">截止时间（UTC）：早于它的快照被删除。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default);
}

/// <summary>
/// 一份表单快照（持久化视图，与对外的 <c>FormSnapshot</c> 同语义、多几个 MQ 字段）。
/// </summary>
/// <param name="TaskId">任务 id（主键）。</param>
/// <param name="InstanceId">流程实例 id。</param>
/// <param name="TenantId">租户。</param>
/// <param name="ProcessKey">流程 key（写入时定稿）。</param>
/// <param name="ProcessDefinitionId">流程定义 id（引擎内部标识，用于反查原始 BPMN）。</param>
/// <param name="DefinitionVersion">提交时的定义版本。</param>
/// <param name="ActivityId">环节 id。</param>
/// <param name="ActivityName">环节名（写入时定稿）。</param>
/// <param name="ActorId">提交人（真人主体）。</param>
/// <param name="ActorCallerId">提交该动作的调用系统。</param>
/// <param name="OptionId">本次审批选项 id。</param>
/// <param name="Fields">当时的字段定义。</param>
/// <param name="Values">当时的表单值（键 = 字段 id，值一律字符串）。</param>
/// <param name="Attachments">当时的附件引用（只存引用）。</param>
/// <param name="CapturedAt">定稿时间（UTC）。</param>
public sealed record ApprovalSnapshotRecord(
    string TaskId,
    string InstanceId,
    string TenantId,
    string? ProcessKey,
    string? ProcessDefinitionId,
    int DefinitionVersion,
    string? ActivityId,
    string? ActivityName,
    string? ActorId,
    string? ActorCallerId,
    string? OptionId,
    IReadOnlyList<FormField> Fields,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<AttachmentRef>? Attachments,
    DateTimeOffset CapturedAt);

/// <summary>快照里 JSON 列的读写口径（键一律 camelCase）。</summary>
internal static class ApprovalSnapshotJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    internal static string? Write<T>(T? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Options);

    internal static T? Read<T>(string? json)
        where T : class =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>
/// 数据库版表单快照存储。
/// </summary>
/// <remarks>
/// 列名下划线、属性驼峰，Dapper 不自动映射，SQL 里逐列 <c>as</c> 别名（本仓规矩）。
/// </remarks>
public sealed class DapperApprovalSnapshotStore : IApprovalSnapshotStore
{
    private const string Columns =
        "task_id as TaskId, process_instance_id as InstanceId, tenant_id as TenantId, "
        + "process_key as ProcessKey, process_definition_id as ProcessDefinitionId, "
        + "definition_version as DefinitionVersion, activity_id as ActivityId, "
        + "activity_name as ActivityName, actor_id as ActorId, actor_caller_id as ActorCallerId, "
        + "option_id as OptionId, schema_json as SchemaJson, values_json as ValuesJson, "
        + "attachments_json as AttachmentsJson, captured_at as CapturedAt";

    private readonly IDbConnectionFactory _connections;

    /// <summary>构造。</summary>
    /// <param name="connections">连接工厂。</param>
    public DapperApprovalSnapshotStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalSnapshotRecord?> FindAsync(
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        await using var connection = _connections.CreateConnection();

        var row = await connection
            .QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
                $"select {Columns} from nb_approval_snapshot where task_id = @taskId",
                new { taskId },
                cancellationToken: ct))
            .ConfigureAwait(false);

        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public async ValueTask<bool> AppendAsync(
        ApprovalSnapshotRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = _connections.CreateConnection();

        // 「有则不写」有两种写法：先查后插（两次往返 + 竞争窗口）与方言原语。
        // 后者各写一套（MySQL 的 on duplicate / SQL Server 的 merge 都得小心触发唯一索引之外的行为），
        // 这里用最不容易出错的一种：直接插，撞主键就是「已经有人先写了」。
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "insert into nb_approval_snapshot "
                + "(task_id, process_instance_id, tenant_id, process_key, process_definition_id, "
                + "definition_version, activity_id, activity_name, actor_id, actor_caller_id, option_id, "
                + "schema_json, values_json, attachments_json, captured_at) "
                + "values (@taskId, @instanceId, @tenantId, @processKey, @processDefinitionId, "
                + "@definitionVersion, @activityId, @activityName, @actorId, @actorCallerId, @optionId, "
                + "@schema, @values, @attachments, @capturedAt)",
                new
                {
                    taskId = record.TaskId,
                    instanceId = record.InstanceId,
                    tenantId = string.IsNullOrWhiteSpace(record.TenantId) ? "default" : record.TenantId,
                    processKey = record.ProcessKey,
                    processDefinitionId = record.ProcessDefinitionId,
                    definitionVersion = record.DefinitionVersion,
                    activityId = record.ActivityId,
                    activityName = record.ActivityName,
                    actorId = record.ActorId,
                    actorCallerId = record.ActorCallerId,
                    optionId = record.OptionId,
                    schema = ApprovalSnapshotJson.Write(record.Fields),
                    values = ApprovalSnapshotJson.Write(record.Values),
                    attachments = ApprovalSnapshotJson.Write(record.Attachments),
                    capturedAt = record.CapturedAt.UtcDateTime,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ApprovalSnapshotRecord>> ListByInstanceAsync(
        string instanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await using var connection = _connections.CreateConnection();

        var rows = await connection
            .QueryAsync<Row>(new CommandDefinition(
                $"select {Columns} from nb_approval_snapshot "
                + "where process_instance_id = @instanceId order by captured_at asc, task_id asc",
                new { instanceId },
                cancellationToken: ct))
            .ConfigureAwait(false);

        return [.. rows.Select(ToRecord)];
    }

    /// <inheritdoc />
    public async ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        // 走 ix_nb_approval_snapshot_captured：这张表只增不减，全表扫着删会随数据量线性恶化
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from nb_approval_snapshot where captured_at < @before",
            new { before },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static ApprovalSnapshotRecord ToRecord(Row row) =>
        new(
            TaskId: row.TaskId,
            InstanceId: row.InstanceId,
            TenantId: row.TenantId,
            ProcessKey: row.ProcessKey,
            ProcessDefinitionId: row.ProcessDefinitionId,
            DefinitionVersion: row.DefinitionVersion,
            ActivityId: row.ActivityId,
            ActivityName: row.ActivityName,
            ActorId: row.ActorId,
            ActorCallerId: row.ActorCallerId,
            OptionId: row.OptionId,
            Fields: ApprovalSnapshotJson.Read<List<FormField>>(row.SchemaJson) ?? [],
            Values: ApprovalSnapshotJson.Read<Dictionary<string, string?>>(row.ValuesJson)
                ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            Attachments: ApprovalSnapshotJson.Read<List<AttachmentRef>>(row.AttachmentsJson),
            CapturedAt: PlatformTimestamps.ToOffset(row.CapturedAt));

    /// <summary>Dapper 行（属性名与 SQL 别名一致）。</summary>
    private sealed class Row
    {
        public string TaskId { get; set; } = string.Empty;

        public string InstanceId { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        public string? ProcessKey { get; set; }

        public string? ProcessDefinitionId { get; set; }

        public int DefinitionVersion { get; set; }

        public string? ActivityId { get; set; }

        public string? ActivityName { get; set; }

        public string? ActorId { get; set; }

        public string? ActorCallerId { get; set; }

        public string? OptionId { get; set; }

        public string? SchemaJson { get; set; }

        public string? ValuesJson { get; set; }

        public string? AttachmentsJson { get; set; }

        public DateTime CapturedAt { get; set; }
    }
}

/// <summary>
/// 内存版表单快照存储（无库宿主 / 单元测试）。
/// </summary>
public sealed class InMemoryApprovalSnapshotStore : IApprovalSnapshotStore
{
    private readonly Dictionary<string, ApprovalSnapshotRecord> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask<ApprovalSnapshotRecord?> FindAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        lock (_gate)
        {
            return ValueTask.FromResult(
                _records.TryGetValue(taskId, out var record) ? record : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> AppendAsync(ApprovalSnapshotRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            if (_records.ContainsKey(record.TaskId))
            {
                return ValueTask.FromResult(false);
            }

            _records[record.TaskId] = record;

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ApprovalSnapshotRecord>> ListByInstanceAsync(
        string instanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        lock (_gate)
        {
            IReadOnlyList<ApprovalSnapshotRecord> result = [.. _records.Values
                .Where(item => string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal))
                .OrderBy(item => item.CapturedAt)
                .ThenBy(item => item.TaskId, StringComparer.Ordinal)];

            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var expired = _records
                .Where(item => item.Value.CapturedAt.UtcDateTime < before)
                .Select(item => item.Key)
                .ToArray();

            foreach (var taskId in expired)
            {
                _records.Remove(taskId);
            }

            return ValueTask.FromResult(expired.Length);
        }
    }
}

namespace NetBpm.Engine.Migration;

/// <summary>
/// 一次流程实例迁移的审计记录（<c>ACT_HI_MIGRATION</c>）。
/// <para>
/// 迁移不改写历史：<c>ACT_HI_PROCINST.PROC_DEF_ID_</c> 仍记录实例<b>启动时</b>的定义，
/// 「什么时候从哪个版本迁到哪个版本、搬了哪些节点」由本记录回答。
/// </para>
/// </summary>
/// <param name="Id">记录 id。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="SourceProcessDefinitionId">迁移前的流程定义 id。</param>
/// <param name="TargetProcessDefinitionId">迁移后的流程定义 id。</param>
/// <param name="ActivityMappings">活动映射（源节点 id → 目标节点 id）；未显式映射的按同 id 保留。</param>
/// <param name="Reason">迁移原因（运维说明）。</param>
/// <param name="UserId">操作人。</param>
/// <param name="MigratedAt">迁移时间（UTC）。</param>
public sealed record MigrationRecord(
    string Id,
    string ProcessInstanceId,
    string SourceProcessDefinitionId,
    string TargetProcessDefinitionId,
    IReadOnlyDictionary<string, string> ActivityMappings,
    string? Reason,
    string? UserId,
    DateTime MigratedAt);

/// <summary>迁移审计存储端口。实现落在 <c>NetBpm.Persistence</c>（Dapper）与内存版。</summary>
public interface IMigrationLog
{
    ValueTask RecordAsync(MigrationRecord record, CancellationToken ct = default);

    /// <summary>按流程实例查迁移记录（按迁移时间倒序）。</summary>
    ValueTask<IReadOnlyList<MigrationRecord>> FindByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default);
}

/// <summary>内存版迁移审计（单元测试 / 无数据库宿主）。</summary>
public sealed class InMemoryMigrationLog : IMigrationLog
{
    private readonly List<MigrationRecord> _records = [];
    private readonly Lock _syncRoot = new();

    public ValueTask RecordAsync(MigrationRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_syncRoot)
        {
            _records.Add(record);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<MigrationRecord>> FindByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        lock (_syncRoot)
        {
            IReadOnlyList<MigrationRecord> result =
            [
                .. _records
                    .Where(record => string.Equals(
                        record.ProcessInstanceId,
                        processInstanceId,
                        StringComparison.Ordinal))
                    .OrderByDescending(record => record.MigratedAt)
                    .ThenByDescending(record => record.Id, StringComparer.Ordinal),
            ];

            return ValueTask.FromResult(result);
        }
    }
}

/// <summary>
/// 流程实例迁移请求。
/// <para>
/// <see cref="ActivityMappings"/> 只需列出<b>节点 id 发生变化</b>的活动；
/// 目标定义里存在同 id 且类型兼容的节点时默认按同 id 迁移。
/// </para>
/// </summary>
/// <param name="ProcessInstanceId">运行中的流程实例 id。</param>
/// <param name="TargetProcessDefinitionId">目标流程定义 id（必须与当前定义同 key / 同租户）。</param>
/// <param name="ActivityMappings">活动映射（源节点 id → 目标节点 id）；可为空。</param>
/// <param name="Reason">迁移原因（写入任务删除原因与审计记录）。</param>
/// <param name="UserId">操作人（审计记录）。</param>
public sealed record ProcessInstanceMigrationRequest(
    string ProcessInstanceId,
    string TargetProcessDefinitionId,
    IReadOnlyDictionary<string, string>? ActivityMappings = null,
    string? Reason = null,
    string? UserId = null)
{
    /// <summary>归一化后的映射（去空白、序数比较）。</summary>
    public IReadOnlyDictionary<string, string> NormalizedMappings()
    {
        if (ActivityMappings is null || ActivityMappings.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(ActivityMappings.Count, StringComparer.Ordinal);

        foreach (var (from, to) in ActivityMappings)
        {
            result[from.Trim()] = to.Trim();
        }

        return result;
    }
}

/// <summary>单个活动的迁移计划。</summary>
/// <param name="FromActivityId">源节点 id。</param>
/// <param name="ToActivityId">目标节点 id。</param>
/// <param name="ExecutionCount">受影响的执行数（并行分支 / 会签会大于 1）。</param>
/// <param name="RecreateTask">是否重建待办（用户任务为 true，网关 Join 为 false）。</param>
public sealed record ActivityMigration(
    string FromActivityId,
    string ToActivityId,
    int ExecutionCount,
    bool RecreateTask);

/// <summary>
/// 迁移计划（预演结果）。
/// <para>预演只读：不写执行树、不改任务、不记审计。执行时会在命令内重新计算同一份计划。</para>
/// </summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="SourceProcessDefinitionId">迁移前定义 id。</param>
/// <param name="TargetProcessDefinitionId">目标定义 id。</param>
/// <param name="Activities">活动映射明细（按源节点 id 排序）。</param>
/// <param name="TasksToCancel">将被取消的待办数。</param>
/// <param name="Warnings">不阻断迁移、但需要操作人确认的提示。</param>
public sealed record MigrationPlan(
    string ProcessInstanceId,
    string SourceProcessDefinitionId,
    string TargetProcessDefinitionId,
    IReadOnlyList<ActivityMigration> Activities,
    int TasksToCancel,
    IReadOnlyList<string> Warnings);

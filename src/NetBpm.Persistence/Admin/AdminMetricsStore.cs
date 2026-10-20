using Dapper;

namespace NetBpm.Persistence.Admin;

/// <summary>
/// 待办侧指标。门面的 <c>TaskQuery</c> 刻意不允许无条件扫描，
/// 管理指标改由数据库聚合查询提供（内存宿主持有 <see cref="UnsupportedAdminMetricsStore"/>）。
/// </summary>
/// <param name="Pending">当前待办总数。</param>
/// <param name="Overdue">已逾期（到期时间早于 now）。</param>
/// <param name="DueToday">今天到期（按调用方时区的自然日）。</param>
/// <param name="ByProcessDefinitionId">按流程定义 id 分组的待办数（上层再按流程 key 合并）。</param>
public sealed record TaskMetrics(
    int Pending,
    int Overdue,
    int DueToday,
    IReadOnlyDictionary<string, int> ByProcessDefinitionId);

/// <summary>管理指标存储：只承载无法从引擎门面直接取到的聚合口径。</summary>
public interface IAdminMetricsStore
{
    /// <summary>
    /// 取待办指标；宿主不支持时返回 <see langword="null"/>（指标接口会如实返回空值，而不是伪造 0）。
    /// </summary>
    /// <param name="tenantId">租户过滤（<see langword="null"/> 不限）。</param>
    /// <param name="utcNow">当前 UTC 时间。</param>
    /// <param name="timeZoneOffsetMinutes">调用方时区偏移（用于「今天到期」的自然日换算）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<TaskMetrics?> GetTaskMetricsAsync(
        string? tenantId,
        DateTime utcNow,
        int timeZoneOffsetMinutes,
        CancellationToken ct = default);
}

/// <summary>内存宿主的指标存储：没有任务表可聚合，返回 <see langword="null"/>。</summary>
public sealed class UnsupportedAdminMetricsStore : IAdminMetricsStore
{
    /// <inheritdoc />
    public ValueTask<TaskMetrics?> GetTaskMetricsAsync(
        string? tenantId,
        DateTime utcNow,
        int timeZoneOffsetMinutes,
        CancellationToken ct = default) => ValueTask.FromResult<TaskMetrics?>(null);
}

/// <summary>数据库指标存储（聚合 <c>ACT_RU_TASK</c>）。</summary>
public sealed class DapperAdminMetricsStore : IAdminMetricsStore
{
    private readonly IDbConnectionFactory _connections;

    public DapperAdminMetricsStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<TaskMetrics?> GetTaskMetricsAsync(
        string? tenantId,
        DateTime utcNow,
        int timeZoneOffsetMinutes,
        CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : " where TENANT_ID_ = @tenantId";

        await using var connection = _connections.CreateConnection();

        // 「今天到期」按调用方时区计算自然日（默认 UTC），再换算回 UTC 区间比较
        var offset = timeZoneOffsetMinutes;
        var localStart = utcNow.AddMinutes(offset).Date;
        var todayStart = localStart.AddMinutes(-offset);
        var tomorrowStart = todayStart.AddDays(1);

        var parameters = new { tenantId, now = utcNow, todayStart, tomorrowStart };

        var pending = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_RU_TASK{filter}",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        var overdue = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_RU_TASK{filter}{And(filter)}DUE_DATE_ is not null and DUE_DATE_ < @now",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        var dueToday = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_RU_TASK{filter}{And(filter)}" +
            "DUE_DATE_ is not null and DUE_DATE_ >= @todayStart and DUE_DATE_ < @tomorrowStart",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DefinitionCountRow>(new CommandDefinition(
            $"select PROC_DEF_ID_ as ProcessDefinitionId, count(*) as Count from ACT_RU_TASK{filter} " +
            "group by PROC_DEF_ID_",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        var byDefinition = rows
            .Where(row => !string.IsNullOrEmpty(row.ProcessDefinitionId))
            .ToDictionary(row => row.ProcessDefinitionId, row => row.Count, StringComparer.Ordinal);

        return new TaskMetrics(pending, overdue, dueToday, byDefinition);
    }

    private static string And(string filter) => filter.Length == 0 ? " where " : " and ";

    private sealed class DefinitionCountRow
    {
        public string ProcessDefinitionId { get; set; } = string.Empty;

        public int Count { get; set; }
    }
}

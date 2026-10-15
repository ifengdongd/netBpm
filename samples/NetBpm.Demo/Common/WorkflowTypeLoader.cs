using Dapper;
using NetBpm.Persistence;

namespace NetBpm.Demo.Common;

/// <summary>
/// 从 MySQL workflow_type 表（0008）加载流程信息，灌进 <see cref="ProcessInfoEnum"/>。
/// </summary>
/// <remarks>
/// 系统启动时调用 <see cref="LoadAsync"/>：<b>新增流程 = 表里插一行</b>，不用改代码。
/// 表不存在 / 查询失败（如 JSON 演示模式没跑过 0008）→ 返回 <see langword="null"/>，
/// 调用方回退 <see cref="ProcessInfoEnum.Defaults"/>，宿主永远起得来。
/// </remarks>
public sealed class WorkflowTypeLoader
{
    private readonly IDbConnectionFactory _connections;
    private readonly ILogger _logger;
    private readonly long _tenantId;

    /// <summary>构造加载器。</summary>
    public WorkflowTypeLoader(IDbConnectionFactory connections, IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _logger = logger;
        _tenantId = configuration.GetValue(ConstField.WorkflowTenantConfigKey, 1L);
    }

    /// <summary>加载启用的流程类型（按 sort_order 排序）；表不可用时返回 null。</summary>
    public async ValueTask<IReadOnlyList<ProcessInfo>?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = _connections.CreateConnection();

            var rows = await connection.QueryAsync<WorkflowTypeRow>(new CommandDefinition(
                """
                select id          as Id,
                       name        as Name,
                       `describe`  as `Describe`,
                       view_url    as ViewUrl,
                       approve_url as ApproveUrl
                  from workflow_type
                 where tenant_id = @TenantId
                   and enabled = 1
                 order by sort_order asc, id asc
                """,
                new { TenantId = _tenantId },
                cancellationToken: ct)).ConfigureAwait(false);

            var items = rows
                .Select(row => new ProcessInfo(row.Id, row.Name, row.Describe, row.ViewUrl, row.ApproveUrl))
                .ToArray();

            _logger.LogInformation(
                "已从 workflow_type 表加载 {Count} 条流程信息：{Names}",
                items.Length,
                string.Join(" / ", items.Select(item => item.Name)));

            return items;
        }
        catch (Exception exception)
        {
            // 表不存在 / 连接问题：回退内置默认，不让宿主起不来
            _logger.LogWarning(
                exception,
                "workflow_type 表加载失败（表不存在或查询出错），流程信息枚举回退内置默认清单。");
            return null;
        }
    }

    private sealed class WorkflowTypeRow
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Describe { get; set; } = string.Empty;

        public string ViewUrl { get; set; } = string.Empty;

        public string ApproveUrl { get; set; } = string.Empty;
    }
}

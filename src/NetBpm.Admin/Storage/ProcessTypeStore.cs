using Dapper;
using NetBpm.Persistence;

namespace NetBpm.Admin.Storage;

/// <summary>
/// 流程类型（workflow_type 表）的 Dapper 存取：流程注册表的增删改查。
/// </summary>
/// <remarks>方言差异只有 <c>describe</c> 保留字的引用方式（MySQL 反引号 / SQL Server 方括号）。</remarks>
public sealed class ProcessTypeStore
{
    private readonly IDbConnectionFactory _connections;
    private readonly long _tenantId;
    private readonly string _describe;
    private readonly string _selectProjection;

    public ProcessTypeStore(IDbConnectionFactory connections, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(configuration);

        _connections = connections;
        _tenantId = configuration.GetValue(AdminStorageDefaults.TenantConfigKey, AdminStorageDefaults.DefaultTenantId);
        _describe = connections.DatabaseType == DatabaseType.SqlServer ? "[describe]" : "`describe`";

        // List / Get 共用的列投影（describe 是 MySQL/SQL Server 的保留字：列名与别名都必须按方言加引号）
        _selectProjection = $"""
            id          as Id,
            name        as Name,
            {_describe} as {_describe},
            view_url    as ViewUrl,
            approve_url as ApproveUrl,
            sort_order  as SortOrder,
            enabled     as Enabled,
            create_time as CreateTime,
            modify_time as ModifyTime
            """;
    }

    /// <summary>流程类型列表（按 sort_order 升序）。</summary>
    public async Task<IReadOnlyList<ProcessType>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<ProcessTypeRow>(new CommandDefinition(
            $"""
             select {_selectProjection}
               from workflow_type
              where tenant_id = @TenantId
              order by sort_order asc, id asc
             """,
            new { TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(ToDto).ToArray();
    }

    /// <summary>按 id 取流程类型；不存在返回 null。</summary>
    public async Task<ProcessType?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var row = await connection.QuerySingleOrDefaultAsync<ProcessTypeRow>(new CommandDefinition(
            $"""
             select {_selectProjection}
               from workflow_type
              where id = @Id and tenant_id = @TenantId
             """,
            new { Id = id, TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : ToDto(row);
    }

    /// <summary>创建流程类型（id 由后端生成）；返回新行。</summary>
    public async Task<ProcessType> CreateAsync(SaveProcessTypeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        await using var connection = _connections.CreateConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             insert into workflow_type
                 (tenant_id, id, name, {_describe}, view_url, approve_url, sort_order, enabled, create_time)
             values
                 (@TenantId, @Id, @Name, @Describe, @ViewUrl, @ApproveUrl, @SortOrder, @Enabled, @Now)
             """,
            new
            {
                TenantId = _tenantId,
                Id = id,
                request.Name,
                request.Describe,
                request.ViewUrl,
                request.ApproveUrl,
                request.SortOrder,
                Enabled = request.Enabled ? (byte)1 : (byte)0,
                Now = now,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return new ProcessType
        {
            Id = id,
            Name = request.Name,
            Describe = request.Describe,
            ViewUrl = request.ViewUrl,
            ApproveUrl = request.ApproveUrl,
            SortOrder = request.SortOrder,
            Enabled = request.Enabled,
            CreateTime = now,
            ModifyTime = null,
        };
    }

    /// <summary>按 id 更新；返回是否存在该行。</summary>
    public async Task<bool> UpdateAsync(string id, SaveProcessTypeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
             update workflow_type
                set name         = @Name,
                    {_describe}  = @Describe,
                    view_url     = @ViewUrl,
                    approve_url  = @ApproveUrl,
                    sort_order   = @SortOrder,
                    enabled      = @Enabled,
                    modify_time  = @Now
              where id = @Id and tenant_id = @TenantId
             """,
            new
            {
                Id = id,
                TenantId = _tenantId,
                request.Name,
                request.Describe,
                request.ViewUrl,
                request.ApproveUrl,
                request.SortOrder,
                Enabled = request.Enabled ? (byte)1 : (byte)0,
                Now = DateTime.UtcNow,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>按 id 删除；返回是否真的删掉了一行。</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "delete from workflow_type where id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    private static ProcessType ToDto(ProcessTypeRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Describe = row.Describe,
        ViewUrl = row.ViewUrl,
        ApproveUrl = row.ApproveUrl,
        SortOrder = row.SortOrder,
        Enabled = row.Enabled != 0,
        CreateTime = row.CreateTime,
        ModifyTime = row.ModifyTime,
    };

    private sealed class ProcessTypeRow
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Describe { get; set; } = string.Empty;

        public string ViewUrl { get; set; } = string.Empty;

        public string ApproveUrl { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public byte Enabled { get; set; }

        public DateTime CreateTime { get; set; }

        public DateTime? ModifyTime { get; set; }
    }
}

using Dapper;
using NetBpm.Persistence;

namespace NetBpm.Admin.Storage;

/// <summary>
/// 流程模型（bpmn_model 表）的 Dapper 存取。BPMN XML 的编辑源，与部署结果（ACT_RE_PROCDEF）分离。
/// </summary>
/// <remarks>SQL 双方言一致（无保留字列、无分页方言差异）；upsert 是唯一的方言差异。</remarks>
public sealed class BpmnModelStore
{
    private readonly IDbConnectionFactory _connections;
    private readonly long _tenantId;

    public BpmnModelStore(IDbConnectionFactory connections, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(configuration);

        _connections = connections;
        _tenantId = configuration.GetValue("NetBpm:AdminTenantId", 1L);
    }

    /// <summary>模型列表（不含 XML/SVG 正文，按最近修改倒序）。</summary>
    public async Task<IReadOnlyList<BpmnModelSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<BpmnModelSummary>(new CommandDefinition(
            """
            select id          as Id,
                   name        as Name,
                   process_key as ProcessKey,
                   description as Description,
                   modify_time as ModifyTime
              from bpmn_model
             where tenant_id = @TenantId
             order by modify_time desc, id asc
            """,
            new { TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToArray();
    }

    /// <summary>按 id 取模型详情；不存在返回 null。</summary>
    public async Task<BpmnModelDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        return await connection.QuerySingleOrDefaultAsync<BpmnModelDetail>(new CommandDefinition(
            """
            select id          as Id,
                   name        as Name,
                   process_key as ProcessKey,
                   description as Description,
                   bpmn_xml    as BpmnXml,
                   svg         as Svg,
                   create_time as CreateTime,
                   modify_time as ModifyTime
              from bpmn_model
             where id = @Id and tenant_id = @TenantId
            """,
            new { Id = id, TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>按 id upsert 保存模型。</summary>
    public async Task SaveAsync(string id, SaveBpmnModelRequest model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await using var connection = _connections.CreateConnection();

        var sql = _connections.DatabaseType == DatabaseType.SqlServer ? SqlServerUpsert : MySqlUpsert;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                TenantId = _tenantId,
                model.Name,
                model.ProcessKey,
                model.Description,
                model.BpmnXml,
                model.Svg,
                Now = DateTime.UtcNow,
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>按 id 删除；返回是否真的删掉了一行。</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "delete from bpmn_model where id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    private const string MySqlUpsert = """
        insert into bpmn_model
            (id, tenant_id, name, process_key, description, bpmn_xml, svg, create_time, modify_time)
        values
            (@Id, @TenantId, @Name, @ProcessKey, @Description, @BpmnXml, @Svg, @Now, @Now)
        on duplicate key update
            name        = values(name),
            process_key = values(process_key),
            description = values(description),
            bpmn_xml    = values(bpmn_xml),
            svg         = values(svg),
            modify_time = values(modify_time)
        """;

    private const string SqlServerUpsert = """
        if exists (select 1 from bpmn_model where id = @Id and tenant_id = @TenantId)
            update bpmn_model
               set name        = @Name,
                   process_key = @ProcessKey,
                   description = @Description,
                   bpmn_xml    = @BpmnXml,
                   svg         = @Svg,
                   modify_time = @Now
             where id = @Id and tenant_id = @TenantId;
        else
            insert into bpmn_model
                (id, tenant_id, name, process_key, description, bpmn_xml, svg, create_time, modify_time)
            values
                (@Id, @TenantId, @Name, @ProcessKey, @Description, @BpmnXml, @Svg, @Now, @Now);
        """;
}

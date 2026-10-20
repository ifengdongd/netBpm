using Dapper;
using NetBpm.Persistence;

namespace NetBpm.Admin.Storage;

/// <summary>
/// 流程模型（bpmn_model 表）的 Dapper 存取。BPMN XML 的编辑源，与部署结果（ACT_RE_PROCDEF）分离。
/// </summary>
/// <remarks>
/// <para>
/// 每次保存都会在 <c>bpmn_model_history</c> 追加一版快照（内容为本次保存的 XML），
/// 因此可以查看历史、回滚到任意版本；保存与历史写入在同一事务内，不会出现「模型更新了、历史没记」。
/// </para>
/// <para>SQL 双方言一致（无保留字列、无分页方言差异）；upsert 是唯一的方言差异。</para>
/// </remarks>
public sealed class BpmnModelStore
{
    private readonly IDbConnectionFactory _connections;
    private readonly long _tenantId;

    public BpmnModelStore(IDbConnectionFactory connections, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(configuration);

        _connections = connections;
        _tenantId = configuration.GetValue(AdminStorageDefaults.TenantConfigKey, AdminStorageDefaults.DefaultTenantId);
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

    /// <summary>
    /// 按 id upsert 保存模型，并在同一事务内追加版本快照。
    /// <para>
    /// revision 由 <c>max+1</c> 计算得出，并发保存同一模型时会撞唯一索引
    /// <c>ux_bpmn_model_history_rev (model_id, revision)</c> —— 数据库保证不会写入重复版本，
    /// 这里捕获冲突后重读重算，最多重试 3 次。
    /// </para>
    /// </summary>
    /// <param name="id">模型 id。</param>
    /// <param name="model">模型内容（调用方已完成部署期校验）。</param>
    /// <param name="operatorId">操作人（写入历史）。</param>
    /// <param name="action">动作：<c>save</c> / <c>rollback</c>。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次写入的版本号（同一模型内从 1 递增）。</returns>
    public async Task<int> SaveAsync(
        string id,
        SaveBpmnModelRequest model,
        string? operatorId = null,
        string action = "save",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(model);

        const int maxAttempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SaveOnceAsync(id, model, operatorId, action, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < maxAttempts && DatabaseErrors.IsUniqueViolation(exception))
            {
                // 撞唯一索引：另一个并发保存已抢先占用了版本号，重读 revision 再试
            }
        }
    }

    /// <summary>单次保存尝试（含事务；失败时事务随 dispose 回滚）。</summary>
    private async Task<int> SaveOnceAsync(
        string id,
        SaveBpmnModelRequest model,
        string? operatorId,
        string action,
        CancellationToken ct)
    {
        await using var connection = _connections.CreateConnection();
        using var transaction = connection.BeginTransaction();

        var revision = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            select coalesce(max(revision), 0) + 1
              from bpmn_model_history
             where model_id = @Id and tenant_id = @TenantId
            """,
            new { Id = id, TenantId = _tenantId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var now = DateTime.UtcNow;

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into bpmn_model_history
                (id, tenant_id, model_id, revision, name, process_key, description,
                 bpmn_xml, svg, action, operator, created_at)
            values
                (@HistoryId, @TenantId, @ModelId, @Revision, @Name, @ProcessKey, @Description,
                 @BpmnXml, @Svg, @Action, @Operator, @CreatedAt)
            """,
            new
            {
                HistoryId = Guid.NewGuid().ToString("N"),
                TenantId = _tenantId,
                ModelId = id,
                Revision = revision,
                model.Name,
                model.ProcessKey,
                model.Description,
                model.BpmnXml,
                model.Svg,
                Action = action,
                Operator = Truncate(operatorId, 255),
                CreatedAt = now,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            _connections.DatabaseType == DatabaseType.SqlServer ? SqlServerUpsert : MySqlUpsert,
            new
            {
                Id = id,
                TenantId = _tenantId,
                model.Name,
                model.ProcessKey,
                model.Description,
                model.BpmnXml,
                model.Svg,
                Now = now,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        transaction.Commit();

        return revision;
    }

    /// <summary>版本历史（不含 XML/SVG 正文，按版本号倒序）。</summary>
    public async Task<IReadOnlyList<BpmnModelRevisionSummary>> ListHistoryAsync(
        string id,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<BpmnModelRevisionSummary>(new CommandDefinition(
            """
            select id          as Id,
                   model_id    as ModelId,
                   revision    as Revision,
                   name        as Name,
                   process_key as ProcessKey,
                   description as Description,
                   action      as Action,
                   operator    as Operator,
                   created_at  as CreatedAt
              from bpmn_model_history
             where model_id = @Id and tenant_id = @TenantId
             order by revision desc
            """,
            new { Id = id, TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToArray();
    }

    /// <summary>单个版本快照（含 XML/SVG）；不存在返回 null。</summary>
    public async Task<BpmnModelRevisionDetail?> GetRevisionAsync(
        string id,
        string revisionId,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        return await connection.QuerySingleOrDefaultAsync<BpmnModelRevisionDetail>(
            new CommandDefinition(
                """
                select id          as Id,
                       model_id    as ModelId,
                       revision    as Revision,
                       name        as Name,
                       process_key as ProcessKey,
                       description as Description,
                       bpmn_xml    as BpmnXml,
                       svg         as Svg,
                       action      as Action,
                       operator    as Operator,
                       created_at  as CreatedAt
                  from bpmn_model_history
                 where id = @RevisionId and model_id = @Id and tenant_id = @TenantId
                """,
                new { RevisionId = revisionId, Id = id, TenantId = _tenantId },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// 回滚到指定版本：把该版本内容作为<b>新版本</b>写入（历史只增不删）。
    /// </summary>
    /// <returns>回滚后的模型详情；版本不存在返回 null。</returns>
    public async Task<BpmnModelDetail?> RollbackAsync(
        string id,
        string revisionId,
        string? operatorId = null,
        CancellationToken ct = default)
    {
        var revision = await GetRevisionAsync(id, revisionId, ct).ConfigureAwait(false);

        if (revision is null)
        {
            return null;
        }

        var request = new SaveBpmnModelRequest
        {
            Name = revision.Name,
            ProcessKey = revision.ProcessKey,
            Description = revision.Description,
            BpmnXml = revision.BpmnXml,
            Svg = revision.Svg,
        };

        await SaveAsync(id, request, operatorId, action: "rollback", ct).ConfigureAwait(false);

        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>按 id 删除（连同版本历史）；返回是否真的删掉了一行。</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from bpmn_model_history where model_id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "delete from bpmn_model where id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        transaction.Commit();

        return affected > 0;
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

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

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
/// <para>
/// 保存 / 回滚共用「读版本号 → 写历史 → upsert 主表」的事务骨架；
/// 回滚在同一事务内完成读快照与写入，中途失败整体回滚，不会出现半套状态。
/// </para>
/// <para>方言差异只有两处：<c>describe</c> 无关的分页子句与 upsert 语句。</para>
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

    private const string SummaryColumns = """
        id          as Id,
        name        as Name,
        process_key as ProcessKey,
        description as Description,
        modify_time as ModifyTime
        """;

    private const string RevisionDetailColumns = """
        id          as Id,
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
        """;

    /// <summary>版本摘要列（不含 XML/SVG 正文）。</summary>
    private const string RevisionSummaryColumns = """
        id          as Id,
        model_id    as ModelId,
        revision    as Revision,
        name        as Name,
        process_key as ProcessKey,
        description as Description,
        action      as Action,
        operator    as Operator,
        created_at  as CreatedAt
        """;

    /// <summary>
    /// 模型分页列表（不含 XML/SVG 正文，按最近修改倒序）。
    /// <para><paramref name="keyword"/> 非空时按名称 / 流程 Key / 描述模糊过滤。</para>
    /// </summary>
    public async Task<BpmnModelPage> ListAsync(
        int firstResult,
        int maxResults,
        string? keyword = null,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        // 关键字过滤走参数化 like，不拼接用户输入
        var trimmedKeyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var keywordFilter = trimmedKeyword is null
            ? string.Empty
            : " and (name like @Keyword or process_key like @Keyword or description like @Keyword)";
        var where = $" where tenant_id = @TenantId{keywordFilter}";

        var parameters = new DynamicParameters();
        parameters.Add("TenantId", _tenantId);

        if (trimmedKeyword is not null)
        {
            parameters.Add("Keyword", $"%{trimmedKeyword}%");
        }

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from bpmn_model{where}",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        // 分页子句是方言差异：MySQL limit/offset，SQL Server offset/fetch
        var listSql = _connections.DatabaseType == DatabaseType.SqlServer
            ? $"select {SummaryColumns} from bpmn_model{where} order by modify_time desc, id asc offset @Skip rows fetch next @Take rows only"
            : $"select {SummaryColumns} from bpmn_model{where} order by modify_time desc, id asc limit @Take offset @Skip";

        parameters.Add("Skip", firstResult);
        parameters.Add("Take", maxResults);

        var rows = await connection.QueryAsync<BpmnModelSummary>(new CommandDefinition(
            listSql,
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        return new BpmnModelPage(rows.ToArray(), total);
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

        var revision = await NextRevisionAsync(connection, transaction, id, ct).ConfigureAwait(false);

        await AppendRevisionAsync(connection, transaction, id, revision, model, operatorId, action, ct)
            .ConfigureAwait(false);

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
            $"""
             select {RevisionSummaryColumns}
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
                $"""
                 select {RevisionDetailColumns}
                   from bpmn_model_history
                  where id = @RevisionId and model_id = @Id and tenant_id = @TenantId
                 """,
                new { RevisionId = revisionId, Id = id, TenantId = _tenantId },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// 回滚到指定版本：把该版本内容作为<b>新版本</b>写入（历史只增不删）。
    /// <para>
    /// 读快照、写历史、upsert 主表在<b>同一事务</b>内完成——中途失败整体回滚，
    /// 不会出现「历史多了 rollback 快照但主表未更新」的半套状态。
    /// </para>
    /// </summary>
    /// <returns>回滚后的模型详情；版本不存在返回 null。</returns>
    public async Task<BpmnModelDetail?> RollbackAsync(
        string id,
        string revisionId,
        string? operatorId = null,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();
        using var transaction = connection.BeginTransaction();

        var revision = await connection.QuerySingleOrDefaultAsync<BpmnModelRevisionDetail>(
            new CommandDefinition(
                $"""
                 select {RevisionDetailColumns}
                   from bpmn_model_history
                  where id = @RevisionId and model_id = @Id and tenant_id = @TenantId
                 """,
                new { RevisionId = revisionId, Id = id, TenantId = _tenantId },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

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

        var nextRevision = await NextRevisionAsync(connection, transaction, id, ct).ConfigureAwait(false);

        await AppendRevisionAsync(connection, transaction, id, nextRevision, request, operatorId, "rollback", ct)
            .ConfigureAwait(false);

        transaction.Commit();

        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 清理模型历史：只保留最近 <paramref name="keepLatest"/> 个版本快照（最新版本永远保留）。
    /// <para>删除范围（cutoff）与删除在同一事务内计算，并发保存的新版本不受影响。</para>
    /// </summary>
    /// <returns>删除的快照数；模型没有历史（或无需清理）返回 0。</returns>
    public async Task<int> PruneHistoryAsync(string id, int keepLatest, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();
        using var transaction = connection.BeginTransaction();

        var maxRevision = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "select max(revision) from bpmn_model_history where model_id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (maxRevision is null)
        {
            return 0;
        }

        var cutoff = maxRevision.Value - keepLatest;

        if (cutoff < 1)
        {
            return 0;
        }

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from bpmn_model_history
             where model_id = @Id and tenant_id = @TenantId and revision <= @Cutoff
            """,
            new { Id = id, TenantId = _tenantId, Cutoff = cutoff },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        transaction.Commit();

        return affected;
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

    /// <summary>计算下一个版本号（同一事务内调用；并发冲突由唯一索引 + 重试兜底）。</summary>
    private async Task<int> NextRevisionAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        string id,
        CancellationToken ct)
    {
        var revision = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            select max(revision) + 1
              from bpmn_model_history
             where model_id = @Id and tenant_id = @TenantId
            """,
            new { Id = id, TenantId = _tenantId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return revision ?? 1;
    }

    /// <summary>在同一事务内追加版本快照并 upsert 主表。</summary>
    private async Task AppendRevisionAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        string id,
        int revision,
        SaveBpmnModelRequest model,
        string? operatorId,
        string action,
        CancellationToken ct)
    {
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

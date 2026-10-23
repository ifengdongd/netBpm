using Dapper;
using NetBpm.Engine;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

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
    private readonly IDbSessionProvider _sessions;
    private readonly long _tenantId;

    public BpmnModelStore(
        IDbConnectionFactory connections,
        IDbSessionProvider sessions,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(configuration);

        _connections = connections;
        _sessions = sessions;
        _tenantId = configuration.GetValue(AdminStorageDefaults.TenantConfigKey, AdminStorageDefaults.DefaultTenantId);
    }

    private const string SummaryColumns = """
        id          as Id,
        name        as Name,
        process_key as ProcessKey,
        description as Description,
        modify_time as ModifyTime,
        (select max(h.revision)
           from bpmn_model_history h
          where h.model_id = bpmn_model.id and h.tenant_id = bpmn_model.tenant_id) as Revision
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
                   modify_time as ModifyTime,
                   (select max(h.revision)
                      from bpmn_model_history h
                     where h.model_id = bpmn_model.id and h.tenant_id = bpmn_model.tenant_id) as Revision
              from bpmn_model
             where id = @Id and tenant_id = @TenantId
            """,
            new { Id = id, TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>模型的最新版本号；模型不存在或没有历史时返回 <see langword="null"/>。</summary>
    public async Task<int?> GetLatestRevisionAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = _connections.CreateConnection();

        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            select max(revision)
              from bpmn_model_history
             where model_id = @Id and tenant_id = @TenantId
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
    /// <param name="expectedRevision">
    /// 调用方看到的版本号（HTTP <c>If-Match</c>）。给出时做<b>事务内条件写入</b>：
    /// 当前版本不等于它即抛 <see cref="OptimisticLockingException"/>（映射为 409）。
    /// 端点里的「先查后比」只是快速失败，权威判定必须在这里，否则两个请求同时通过检查后仍会互相覆盖。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次写入的版本号（同一模型内从 1 递增）。</returns>
    public async Task<int> SaveAsync(
        string id,
        SaveBpmnModelRequest model,
        string? operatorId = null,
        string action = "save",
        int? expectedRevision = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(model);

        const int maxAttempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SaveOnceAsync(id, model, operatorId, action, expectedRevision, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (DatabaseErrors.IsUniqueViolation(exception))
            {
                // 撞唯一索引：另一个并发保存已抢先占用了版本号，重读 revision 再试。
                // 重试用尽仍然冲突 ⇒ 并发度超出预期，转成 409 而不是把 DbException 漏成 500。
                if (attempt >= maxAttempts)
                {
                    throw new OptimisticLockingException(
                        $"模型 '{id}' 的保存连续冲突 {maxAttempts} 次（并发写入过于频繁），请刷新后重试。");
                }
            }
        }
    }

    /// <summary>新建模型（id 由后端生成），初始版本 1；返回创建后的详情。</summary>
    public async Task<BpmnModelDetail> CreateAsync(
        SaveBpmnModelRequest model,
        string? operatorId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var id = Guid.NewGuid().ToString("N");

        await SaveAsync(id, model, operatorId, "save", expectedRevision: null, ct: ct).ConfigureAwait(false);

        return await GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"模型 '{id}' 创建后读取失败。");
    }

    /// <summary>单次保存尝试（含事务；失败时事务随 dispose 回滚）。</summary>
    private async Task<int> SaveOnceAsync(
        string id,
        SaveBpmnModelRequest model,
        string? operatorId,
        string action,
        int? expectedRevision,
        CancellationToken ct)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;
        await using var unitOfWork = lease.BeginUnitOfWork();

        var revision = await NextRevisionAsync(session, id, ct).ConfigureAwait(false);

        // 🔑 条件写入（compare-and-set）：版本校验与写入在同一事务、同一把锁内，
        // 不存在「检查通过 → 别人提交 → 我覆盖」的窗口。
        if (expectedRevision is { } expected && revision != expected + 1)
        {
            throw new OptimisticLockingException(
                $"模型 '{id}' 已被其他人修改（服务端版本 {revision - 1}，本次提交基于版本 {expected}），请刷新后重试。");
        }

        await AppendRevisionAsync(session, id, revision, model, operatorId, action, ct)
            .ConfigureAwait(false);

        await unitOfWork.CommitAsync(ct).ConfigureAwait(false);

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

    /// <summary>
    /// 分页版本历史（不含 XML/SVG 正文，按版本号倒序）。
    /// <para>
    /// 版本多的老模型历史可能上百条，列表页只需要第一页；<c>Total</c> 用于分页器。
    /// </para>
    /// </summary>
    public async Task<BpmnModelHistoryPage> PageHistoryAsync(
        string id,
        int firstResult,
        int maxResults,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from bpmn_model_history where model_id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        if (total == 0)
        {
            return new BpmnModelHistoryPage([], 0);
        }

        var pageSql = _connections.DatabaseType == DatabaseType.SqlServer
            ? $"""
               select {RevisionSummaryColumns}
                 from bpmn_model_history
                where model_id = @Id and tenant_id = @TenantId
                order by revision desc
               offset @Skip rows fetch next @Take rows only
               """
            : $"""
               select {RevisionSummaryColumns}
                 from bpmn_model_history
                where model_id = @Id and tenant_id = @TenantId
                order by revision desc
                limit @Take offset @Skip
               """;

        var rows = await connection.QueryAsync<BpmnModelRevisionSummary>(new CommandDefinition(
            pageSql,
            new
            {
                Id = id,
                TenantId = _tenantId,
                Skip = Math.Max(0, firstResult),
                Take = Math.Max(1, maxResults),
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return new BpmnModelHistoryPage(rows.ToArray(), total);
    }

    /// <summary>
    /// 同租户下是否已有别的模型占用该 <c>process_key</c>；占用时返回其 id。
    /// <para>
    /// 唯一索引是兜底，这里提前给出可读冲突：同一 key 下混入两个模型来源，
    /// 发布后会在同一 key 下产生来源不明的版本，回滚 / 发布历史也对不上。
    /// </para>
    /// </summary>
    public async Task<string?> FindIdByProcessKeyAsync(
        string processKey,
        string? excludeId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        await using var connection = _connections.CreateConnection();

        var where = """
            where tenant_id = @TenantId
              and process_key = @ProcessKey
              and (@ExcludeId is null or id <> @ExcludeId)
            """;

        var sql = _connections.DatabaseType == DatabaseType.SqlServer
            ? $"select top 1 id from bpmn_model {where}"
            : $"select id from bpmn_model {where} limit 1";

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            sql,
            new { TenantId = _tenantId, ProcessKey = processKey.Trim(), ExcludeId = excludeId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量读取模型的版本水位：最新版本号 + 最近一次 <c>publish</c> 快照版本号。
    /// <para>
    /// 用于列表/详情的「线上版本 / 是否有未发布修改」，一次查询覆盖整页模型，
    /// 不给每个模型单独发一次 count。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, BpmnModelRevisionStamp>> GetRevisionStampsAsync(
        IReadOnlyCollection<string> modelIds,
        CancellationToken ct = default)
    {
        if (modelIds.Count == 0)
        {
            return new Dictionary<string, BpmnModelRevisionStamp>(StringComparer.Ordinal);
        }

        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<RevisionStampRow>(new CommandDefinition(
            """
            select model_id as ModelId,
                   max(revision) as LatestRevision,
                   max(case when action = 'publish' then revision end) as PublishedRevision
              from bpmn_model_history
             where tenant_id = @TenantId and model_id in @ModelIds
             group by model_id
            """,
            new { TenantId = _tenantId, ModelIds = modelIds },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToDictionary(
            row => row.ModelId,
            row => new BpmnModelRevisionStamp(row.LatestRevision, row.PublishedRevision),
            StringComparer.Ordinal);
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
        using var lease = _sessions.Acquire();
        var session = lease.Session;
        await using var unitOfWork = lease.BeginUnitOfWork();

        var revision = await session.Connection.QuerySingleOrDefaultAsync<BpmnModelRevisionDetail>(
            new CommandDefinition(
                $"""
                 select {RevisionDetailColumns}
                   from bpmn_model_history
                  where id = @RevisionId and model_id = @Id and tenant_id = @TenantId
                 """,
                new { RevisionId = revisionId, Id = id, TenantId = _tenantId },
                session.Transaction,
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

        var nextRevision = await NextRevisionAsync(session, id, ct).ConfigureAwait(false);

        await AppendRevisionAsync(session, id, nextRevision, request, operatorId, "rollback", ct)
            .ConfigureAwait(false);

        await unitOfWork.CommitAsync(ct).ConfigureAwait(false);

        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 清理模型历史：只保留最近 <paramref name="keepLatest"/> 个版本快照（最新版本永远保留）。
    /// <para>删除范围（cutoff）与删除在同一事务内计算，并发保存的新版本不受影响。</para>
    /// </summary>
    /// <returns>删除的快照数；模型没有历史（或无需清理）返回 0。</returns>
    public async Task<int> PruneHistoryAsync(string id, int keepLatest, CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;
        await using var unitOfWork = lease.BeginUnitOfWork();

        var maxRevision = await session.Connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "select max(revision) from bpmn_model_history where model_id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            session.Transaction,
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

        var affected = await session.Connection.ExecuteAsync(new CommandDefinition(
            """
            delete from bpmn_model_history
             where model_id = @Id and tenant_id = @TenantId and revision <= @Cutoff
            """,
            new { Id = id, TenantId = _tenantId, Cutoff = cutoff },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await unitOfWork.CommitAsync(ct).ConfigureAwait(false);

        return affected;
    }

    /// <summary>按 id 删除（连同版本历史）；返回是否真的删掉了一行。</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;
        await using var unitOfWork = lease.BeginUnitOfWork();

        await session.Connection.ExecuteAsync(new CommandDefinition(
            "delete from bpmn_model_history where model_id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var affected = await session.Connection.ExecuteAsync(new CommandDefinition(
            "delete from bpmn_model where id = @Id and tenant_id = @TenantId",
            new { Id = id, TenantId = _tenantId },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await unitOfWork.CommitAsync(ct).ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>
    /// 计算下一个版本号（同一事务内调用），并<b>对最新版本行加锁</b>。
    /// <para>
    /// 为什么不是 <c>select max(revision) + 1</c>：聚合查询无法加行锁，两个事务会读到同一个版本号，
    /// 只能靠唯一索引把它们撞成「重试」。重试能保证不重号，却会掩盖 <c>If-Match</c> 语义
    /// （条件判断读到的是旧值）。加锁读把「读版本 → 写新版本」变成串行临界区。
    /// </para>
    /// </summary>
    private async Task<int> NextRevisionAsync(
        IDbSession session,
        string id,
        CancellationToken ct)
    {
        var sql = _connections.DatabaseType == DatabaseType.SqlServer
            ? """
              select top 1 revision
                from bpmn_model_history with (updlock, holdlock)
               where model_id = @Id and tenant_id = @TenantId
               order by revision desc
              """
            : """
              select revision
                from bpmn_model_history
               where model_id = @Id and tenant_id = @TenantId
               order by revision desc
               limit 1
               for update
              """;

        var current = await session.Connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            sql,
            new { Id = id, TenantId = _tenantId },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return (current ?? 0) + 1;
    }

    /// <summary>在同一事务内追加版本快照并 upsert 主表。</summary>
    private async Task AppendRevisionAsync(
        IDbSession session,
        string id,
        int revision,
        SaveBpmnModelRequest model,
        string? operatorId,
        string action,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await session.Connection.ExecuteAsync(new CommandDefinition(
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
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await session.Connection.ExecuteAsync(new CommandDefinition(
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
            session.Transaction,
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

    /// <summary>版本水位：最新版本号与最近一次发布快照版本号。</summary>
    /// <param name="LatestRevision">模型的最新版本号。</param>
    /// <param name="PublishedRevision">最近一次 publish 快照的版本号；从未发布为 null。</param>
    public sealed record BpmnModelRevisionStamp(int LatestRevision, int? PublishedRevision);

    /// <summary>分页的版本历史（当前页 + 总数）。</summary>
    public sealed record BpmnModelHistoryPage(IReadOnlyList<BpmnModelRevisionSummary> Items, int Total);

    private sealed class RevisionStampRow
    {
        public string ModelId { get; set; } = string.Empty;

        public int LatestRevision { get; set; }

        public int? PublishedRevision { get; set; }
    }
}

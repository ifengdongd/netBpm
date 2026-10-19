using System.Text;
using Dapper;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence.Deployment;

/// <summary>
/// 流程定义仓储的 Dapper 实现（P5）。
/// <para>
/// 表：<c>ACT_RE_DEPLOYMENT</c>（部署）+ <c>ACT_GE_BYTEARRAY</c>（BPMN XML 资源）
/// + <c>ACT_RE_PROCDEF</c>（流程定义）。插入顺序受外键约束
/// （<c>ACT_GE_BYTEARRAY.DEPLOYMENT_ID_ → ACT_RE_DEPLOYMENT.ID_</c>）限制。
/// </para>
/// <para>
/// 为什么 DB 模式下这一步是必需的：<c>ACT_RU_EXECUTION.PROC_DEF_ID_</c> 有指向
/// <c>ACT_RE_PROCDEF</c> 的外键，流程定义必须先落库，否则启动实例时 INSERT 会被数据库直接拒绝。
/// </para>
/// </summary>
public sealed class DapperProcessDefinitionStore : IProcessDefinitionStore
{
    /// <summary>
    /// 已解析模型与租户 id 的进程内缓存容量。
    /// 流程定义一旦部署即不可变（重部署生成新 id），因此按 id 缓存是安全的；
    /// 用 LRU 限容只是防止长期运行 + 频繁重部署导致内存线性增长。
    /// </summary>
    private const int DefaultCacheCapacity = 256;

    private readonly IDbSessionProvider _sessions;
    private readonly IIdGenerator _idGenerator;
    private readonly IBpmnXmlConverter _xmlConverter;
    private readonly BoundedCache<string, BpmnModel> _models;
    private readonly BoundedCache<string, string?> _tenants;

    public DapperProcessDefinitionStore(
        IDbSessionProvider sessions,
        IIdGenerator idGenerator,
        IBpmnXmlConverter xmlConverter,
        int cacheCapacity = DefaultCacheCapacity)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(xmlConverter);

        _sessions = sessions;
        _idGenerator = idGenerator;
        _xmlConverter = xmlConverter;
        _models = new BoundedCache<string, BpmnModel>(cacheCapacity, StringComparer.Ordinal);
        _tenants = new BoundedCache<string, string?>(cacheCapacity, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async ValueTask<ProcessDefinition> DeployAsync(
        BpmnModel bpmnModel,
        string resourceName,
        string? deploymentName = null,
        byte[]? rawResourceBytes = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bpmnModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var process = bpmnModel.GetMainProcess()
            ?? throw new EngineException("部署失败：BPMN 模型中没有流程定义。");

        var key = process.Id;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new EngineException("部署失败：流程缺少 id。");
        }

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var connection = session.Connection;
        var transaction = session.Transaction;
        var tenant = tenantId ?? string.Empty;

        // VERSION_ = 同一 (KEY_, TENANT_ID_) 下的 max(VERSION_) + 1（见 §7.4；Activiti 同口径）
        var version = await connection.ExecuteScalarAsync<int>(
            NextVersionSql,
            new { key, tenantId = tenant },
            transaction).ConfigureAwait(false) + 1;

        var deploymentId = _idGenerator.NextId();
        var definitionId = ProcessDefinition.BuildId(key, version, _idGenerator.NextId());
        var engineVersion = EngineVersion;
        var deployTime = DateTime.UtcNow;
        // B2.5：调用方给原始字节就存原始字节（与 Activiti 一致）；内部路径退回重新序列化
        var xmlBytes = rawResourceBytes is { Length: > 0 }
            ? rawResourceBytes
            : Encoding.UTF8.GetBytes(Serialize(bpmnModel));

        // 1) 部署
        // ⚠️ KEY_ / CATEGORY_ 必须为 null：Activiti 的部署默认不带 key（DeploymentBuilder.key 需显式设置）、
        // category 也不从 targetNamespace 取 —— 真值比对（2026-10-12）里这两列全线差异，皆因我们画蛇添足。
        await connection.ExecuteAsync(
            InsertDeploymentSql,
            new
            {
                deploymentId,
                name = deploymentName,
                category = (string?)null,
                key = (string?)null,
                tenantId = tenant,
                deployTime,
                engineVersion,
            },
            transaction).ConfigureAwait(false);

        // 2) 资源（BPMN XML）
        await connection.ExecuteAsync(
            InsertByteArraySql,
            new { id = _idGenerator.NextId(), name = resourceName, deploymentId, bytes = xmlBytes },
            transaction).ConfigureAwait(false);

        // 3) 流程定义
        var hasGraphicalNotation = bpmnModel.Shapes.Count > 0 || bpmnModel.Edges.Count > 0;

        await connection.ExecuteAsync(
            InsertProcessDefinitionSql,
            new
            {
                id = definitionId,
                category = bpmnModel.TargetNamespace,
                name = process.Name,
                key,
                version,
                deploymentId,
                resourceName,
                hasGraphicalNotation = hasGraphicalNotation ? 1 : 0,
                engineVersion,
                tenantId = tenant,
            },
            transaction).ConfigureAwait(false);

        return new ProcessDefinition(definitionId, key, process.Name, version, deploymentId, resourceName);
    }

    /// <inheritdoc />
    public async ValueTask<ProcessDefinition?> FindByIdAsync(
        string processDefinitionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(
                SelectByIdSql,
                new { processDefinitionId },
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        foreach (IDictionary<string, object> row in rows)
        {
            return new ProcessDefinition(
                Text(row, "ID_")!,
                Text(row, "KEY_")!,
                Text(row, "NAME_"),
                Number(row, "VERSION_"),
                Text(row, "DEPLOYMENT_ID_")!,
                Text(row, "RESOURCE_NAME_")!);
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<ProcessDefinition?> FindLatestByKeyAsync(
        string processDefinitionKey,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionKey);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 用「VERSION_ = (select max(...))」而不是 order by + limit：
        // 后者在 SQL Server / MySQL 语法不同，本项目两种方言共用同一套 SQL
        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(
                SelectLatestByKeySql,
                new { key = processDefinitionKey, tenantId = tenantId ?? string.Empty },
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        foreach (IDictionary<string, object> row in rows)
        {
            return new ProcessDefinition(
                Text(row, "ID_")!,
                Text(row, "KEY_")!,
                Text(row, "NAME_"),
                Number(row, "VERSION_"),
                Text(row, "DEPLOYMENT_ID_")!,
                Text(row, "RESOURCE_NAME_")!);
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindAllAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(
                tenantId is null ? SelectAllSql : SelectAllByTenantSql,
                new { tenantId },
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<DeployedProcessDefinition>();

        foreach (IDictionary<string, object> row in rows)
        {
            DateTime? deployTime = row.TryGetValue("DEPLOY_TIME_", out var time) && time is not DBNull
                ? DateTime.SpecifyKind(Convert.ToDateTime(time, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)
                : null;

            result.Add(new DeployedProcessDefinition(
                Text(row, "ID_")!,
                Text(row, "KEY_")!,
                Text(row, "NAME_"),
                Number(row, "VERSION_"),
                Text(row, "DEPLOYMENT_ID_")!,
                deployTime,
                Text(row, "TENANT_ID_"),
                Number(row, "SUSPENSION_STATE_") == SuspendedState,
                Text(row, "RESOURCE_NAME_"),
                Text(row, "DEPLOYMENT_NAME_")));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetResourceXmlAsync(
        string processDefinitionId,
        CancellationToken ct = default)
    {
        var bytes = await QueryResourceBytesAsync(processDefinitionId, ct).ConfigureAwait(false);

        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <inheritdoc />
    public async ValueTask<BpmnModel?> GetBpmnModelAsync(
        string processDefinitionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        // 每次启动实例都重新查 BYTES_ 并解析 XML 是启动路径上最大的一笔固定开销
        // （Activiti 同样用 DeploymentCache 缓存解析结果）。定义不可变 ⇒ 缓存安全。
        if (_models.TryGet(processDefinitionId, out var cached))
        {
            return cached;
        }

        var bytes = await QueryResourceBytesAsync(processDefinitionId, ct).ConfigureAwait(false);

        if (bytes is null)
        {
            return null;
        }

        var model = _xmlConverter.ConvertToBpmnModel(new MemoryStream(bytes));
        _models.Set(processDefinitionId, model);

        return model;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetTenantIdAsync(
        string processDefinitionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        if (_tenants.TryGet(processDefinitionId, out var cached))
        {
            return cached;
        }

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var tenantId = await session.Connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                SelectTenantSql,
                new { processDefinitionId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        _tenants.Set(processDefinitionId, tenantId);

        return tenantId;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetSuspensionStateAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 与 Activiti 的 updateProcessDefinitionSuspensionState 同口径：只动 SUSPENSION_STATE_ + REV_
        var affected = await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateSuspensionStateSql,
                new { processDefinitionId, state = suspended ? SuspendedState : ActiveState },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsSuspendedAsync(string processDefinitionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        // 刻意不缓存：挂起状态是唯一可变列（模型 / 租户都不可变），
        // 多实例部署下任何进程内缓存都无法及时失效，代价只是启动路径上的一次主键查询
        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var state = await session.Connection.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                SelectSuspensionStateSql,
                new { processDefinitionId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        return state == SuspendedState;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ProcessDefinition>> FindByDeploymentIdAsync(
        string deploymentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(
                SelectByDeploymentIdSql,
                new { deploymentId },
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<ProcessDefinition>();

        foreach (IDictionary<string, object> row in rows)
        {
            result.Add(new ProcessDefinition(
                Text(row, "ID_")!,
                Text(row, "KEY_")!,
                Text(row, "NAME_"),
                Number(row, "VERSION_"),
                Text(row, "DEPLOYMENT_ID_")!,
                Text(row, "RESOURCE_NAME_")!));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteDeploymentAsync(string deploymentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 删定义前先记住 id，用于清进程内缓存（Dapper 版有模型 / 租户 LRU；
        // 若不清，删除后按 id 启动仍会命中缓存模型，最后撞 ACT_RU_EXECUTION.PROC_DEF_ID_ 外键）
        var definitionIds = (await session.Connection.QueryAsync<string>(
            new CommandDefinition(
                SelectDefinitionIdsByDeploymentIdSql,
                new { deploymentId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false)).ToList();

        // 顺序受外键约束：ACT_RE_PROCDEF / ACT_GE_BYTEARRAY 都指向 ACT_RE_DEPLOYMENT
        await session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteDefinitionsByDeploymentIdSql,
            new { deploymentId },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteByteArraysByDeploymentIdSql,
            new { deploymentId },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var affected = await session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteDeploymentSql,
            new { deploymentId },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (affected == 0)
        {
            return false;
        }

        foreach (var definitionId in definitionIds)
        {
            _models.Remove(definitionId);
            _tenants.Remove(definitionId);
        }

        return true;
    }

    /// <summary>直接取 XML 字节，避免「字节 → string → 字节」的两次编码转换。</summary>
    private async ValueTask<byte[]?> QueryResourceBytesAsync(string processDefinitionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        return await session.Connection
            .ExecuteScalarAsync<byte[]>(new CommandDefinition(
                SelectResourceSql,
                new { processDefinitionId },
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    private string Serialize(BpmnModel bpmnModel)
    {
        using var buffer = new MemoryStream();
        _xmlConverter.ConvertToXml(bpmnModel, buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// 写入 <c>ENGINE_VERSION_</c> 的标识串。
    /// <para>⚠️ 该列与 Activiti 的 <c>"9.0.0"</c> 必然不同，属<b>已登记的有意差异</b>：
    /// 对拍时它应列入忽略列（见 tests/NetBpm.DifferentialTests 的默认忽略列）。</para>
    /// </summary>
    private static string EngineVersion =>
        $"netbpm-{typeof(DapperProcessDefinitionStore).Assembly.GetName().Version?.ToString() ?? "0.0.0"}";

    private static string? Text(IDictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value as string : null;

    private static int Number(IDictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

    // ─────────────────────────── SQL ───────────────────────────

    /// <summary><c>ACT_RE_PROCDEF.SUSPENSION_STATE_</c> 的取值（与 Activiti <c>SuspensionState</c> 一致）。</summary>
    private const int ActiveState = 1;

    /// <summary>挂起态。</summary>
    private const int SuspendedState = 2;

    private const string UpdateSuspensionStateSql = """
        update ACT_RE_PROCDEF
        set REV_ = REV_ + 1, SUSPENSION_STATE_ = @state
        where ID_ = @processDefinitionId
        """;

    private const string SelectSuspensionStateSql =
        "select SUSPENSION_STATE_ from ACT_RE_PROCDEF where ID_ = @processDefinitionId";

    private const string SelectByDeploymentIdSql = """
        select ID_, KEY_, NAME_, VERSION_, DEPLOYMENT_ID_, RESOURCE_NAME_
        from ACT_RE_PROCDEF where DEPLOYMENT_ID_ = @deploymentId
        """;

    private const string SelectDefinitionIdsByDeploymentIdSql =
        "select ID_ from ACT_RE_PROCDEF where DEPLOYMENT_ID_ = @deploymentId";

    private const string DeleteDefinitionsByDeploymentIdSql =
        "delete from ACT_RE_PROCDEF where DEPLOYMENT_ID_ = @deploymentId";

    private const string DeleteByteArraysByDeploymentIdSql =
        "delete from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ = @deploymentId";

    private const string DeleteDeploymentSql =
        "delete from ACT_RE_DEPLOYMENT where ID_ = @deploymentId";

    private const string NextVersionSql =
        "select coalesce(max(VERSION_), 0) from ACT_RE_PROCDEF where KEY_ = @key and TENANT_ID_ = @tenantId";

    private const string InsertDeploymentSql = """
        insert into ACT_RE_DEPLOYMENT (
            ID_, NAME_, CATEGORY_, KEY_, TENANT_ID_, DEPLOY_TIME_, ENGINE_VERSION_, VERSION_, PROJECT_RELEASE_VERSION_)
        values (
            @deploymentId, @name, @category, @key, @tenantId, @deployTime, @engineVersion, 1, null)
        """;

    private const string InsertByteArraySql = """
        insert into ACT_GE_BYTEARRAY (ID_, REV_, NAME_, DEPLOYMENT_ID_, BYTES_, GENERATED_)
        values (@id, 1, @name, @deploymentId, @bytes, 0)
        """;

    private const string InsertProcessDefinitionSql = """
        insert into ACT_RE_PROCDEF (
            ID_, REV_, CATEGORY_, NAME_, KEY_, VERSION_, DEPLOYMENT_ID_, RESOURCE_NAME_,
            DGRM_RESOURCE_NAME_, DESCRIPTION_, HAS_START_FORM_KEY_, HAS_GRAPHICAL_NOTATION_,
            SUSPENSION_STATE_, TENANT_ID_, ENGINE_VERSION_, APP_VERSION_)
        values (
            @id, 1, @category, @name, @key, @version, @deploymentId, @resourceName,
            null, null, 0, @hasGraphicalNotation,
            1, @tenantId, @engineVersion, null)
        """;

    private const string SelectByIdSql = """
        select ID_, KEY_, NAME_, VERSION_, DEPLOYMENT_ID_, RESOURCE_NAME_
        from ACT_RE_PROCDEF where ID_ = @processDefinitionId
        """;

    private const string SelectLatestByKeySql = """
        select ID_, KEY_, NAME_, VERSION_, DEPLOYMENT_ID_, RESOURCE_NAME_
        from ACT_RE_PROCDEF
        where KEY_ = @key and TENANT_ID_ = @tenantId
          and VERSION_ = (
              select max(VERSION_) from ACT_RE_PROCDEF where KEY_ = @key and TENANT_ID_ = @tenantId)
        """;

    private const string SelectAllSql = """
        select P.ID_, P.KEY_, P.NAME_, P.VERSION_, P.DEPLOYMENT_ID_, D.DEPLOY_TIME_, P.TENANT_ID_,
               P.SUSPENSION_STATE_, P.RESOURCE_NAME_, D.NAME_ as DEPLOYMENT_NAME_
        from ACT_RE_PROCDEF P
        inner join ACT_RE_DEPLOYMENT D on D.ID_ = P.DEPLOYMENT_ID_
        order by D.DEPLOY_TIME_ desc, P.KEY_, P.VERSION_ desc
        """;

    private const string SelectAllByTenantSql = """
        select P.ID_, P.KEY_, P.NAME_, P.VERSION_, P.DEPLOYMENT_ID_, D.DEPLOY_TIME_, P.TENANT_ID_,
               P.SUSPENSION_STATE_, P.RESOURCE_NAME_, D.NAME_ as DEPLOYMENT_NAME_
        from ACT_RE_PROCDEF P
        inner join ACT_RE_DEPLOYMENT D on D.ID_ = P.DEPLOYMENT_ID_
        where P.TENANT_ID_ = @tenantId
        order by D.DEPLOY_TIME_ desc, P.KEY_, P.VERSION_ desc
        """;

    private const string SelectResourceSql = """
        select B.BYTES_
        from ACT_GE_BYTEARRAY B
        inner join ACT_RE_PROCDEF P on P.DEPLOYMENT_ID_ = B.DEPLOYMENT_ID_
        where P.ID_ = @processDefinitionId and B.NAME_ = P.RESOURCE_NAME_
        """;

    private const string SelectTenantSql =
        "select TENANT_ID_ from ACT_RE_PROCDEF where ID_ = @processDefinitionId";
}

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
    private readonly IDbSessionProvider _sessions;
    private readonly IIdGenerator _idGenerator;
    private readonly IBpmnXmlConverter _xmlConverter;

    public DapperProcessDefinitionStore(
        IDbSessionProvider sessions,
        IIdGenerator idGenerator,
        IBpmnXmlConverter xmlConverter)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(xmlConverter);

        _sessions = sessions;
        _idGenerator = idGenerator;
        _xmlConverter = xmlConverter;
    }

    /// <inheritdoc />
    public async ValueTask<ProcessDefinition> DeployAsync(
        BpmnModel bpmnModel,
        string resourceName,
        string? deploymentName = null,
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

        // VERSION_ = 同一 KEY_ 下的 max(VERSION_) + 1（见 §7.4）
        var version = await connection.ExecuteScalarAsync<int>(
            NextVersionSql,
            new { key },
            transaction).ConfigureAwait(false) + 1;

        var deploymentId = _idGenerator.NextId();
        var definitionId = ProcessDefinition.BuildId(key, version, _idGenerator.NextId());
        var engineVersion = EngineVersion;
        var deployTime = DateTime.UtcNow;
        var xmlBytes = Encoding.UTF8.GetBytes(Serialize(bpmnModel));

        // 1) 部署
        await connection.ExecuteAsync(
            InsertDeploymentSql,
            new
            {
                deploymentId,
                name = deploymentName,
                category = bpmnModel.TargetNamespace,
                key,
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
    public async ValueTask<string?> GetResourceXmlAsync(
        string processDefinitionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var bytes = await session.Connection
            .ExecuteScalarAsync<byte[]>(new CommandDefinition(
                SelectResourceSql,
                new { processDefinitionId },
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <inheritdoc />
    public async ValueTask<BpmnModel?> GetBpmnModelAsync(
        string processDefinitionId,
        CancellationToken ct = default)
    {
        var xml = await GetResourceXmlAsync(processDefinitionId, ct).ConfigureAwait(false);

        return xml is null
            ? null
            : _xmlConverter.ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
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

    private const string NextVersionSql =
        "select coalesce(max(VERSION_), 0) from ACT_RE_PROCDEF where KEY_ = @key";

    private const string InsertDeploymentSql = """
        insert into ACT_RE_DEPLOYMENT (
            ID_, NAME_, CATEGORY_, KEY_, TENANT_ID_, DEPLOY_TIME_, ENGINE_VERSION_, VERSION_, PROJECT_RELEASE_VERSION_)
        values (
            @deploymentId, @name, @category, @key, '', @deployTime, @engineVersion, 1, null)
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
            1, '', @engineVersion, null)
        """;

    private const string SelectByIdSql = """
        select ID_, KEY_, NAME_, VERSION_, DEPLOYMENT_ID_, RESOURCE_NAME_
        from ACT_RE_PROCDEF where ID_ = @processDefinitionId
        """;

    private const string SelectResourceSql = """
        select B.BYTES_
        from ACT_GE_BYTEARRAY B
        inner join ACT_RE_PROCDEF P on P.DEPLOYMENT_ID_ = B.DEPLOYMENT_ID_
        where P.ID_ = @processDefinitionId and B.NAME_ = P.RESOURCE_NAME_
        """;
}

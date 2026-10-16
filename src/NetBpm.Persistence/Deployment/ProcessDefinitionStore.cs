using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;

namespace NetBpm.Persistence.Deployment;

/// <summary>已部署的流程定义。对应 <c>ACT_RE_PROCDEF</c> 的一行。</summary>
/// <param name="Id">流程定义 id，形如 <c>{key}:{version}:{idGeneratorId}</c>（见 §7.4）。</param>
/// <param name="Key">流程 key（XML 中的 <c>process/@id</c>）。</param>
/// <param name="Name">流程名。</param>
/// <param name="Version">同一 key 下 <c>max(VERSION_) + 1</c>。</param>
/// <param name="DeploymentId">部署 id。</param>
/// <param name="ResourceName">资源名（如 <c>designer.bpmn20.xml</c>）。</param>
public sealed record ProcessDefinition(
    string Id,
    string Key,
    string? Name,
    int Version,
    string DeploymentId,
    string ResourceName)
{
    /// <summary>由 <c>key:version:id</c> 生成流程定义 id（格式必须与 Activiti 一致，见 §7.4）。</summary>
    public static string BuildId(string key, int version, string idGeneratorId) => $"{key}:{version}:{idGeneratorId}";
}

/// <summary>
/// 已部署流程定义的列表视图（含部署时间）。
/// <para>对应 <c>ACT_RE_PROCDEF</c> join <c>ACT_RE_DEPLOYMENT</c> 的一行。</para>
/// </summary>
/// <param name="Id">流程定义 id。</param>
/// <param name="Key">流程 key。</param>
/// <param name="Name">流程名。</param>
/// <param name="Version">版本号。</param>
/// <param name="DeploymentId">部署 id。</param>
/// <param name="DeployTime">部署时间（UTC）。</param>
public sealed record DeployedProcessDefinition(
    string Id,
    string Key,
    string? Name,
    int Version,
    string DeploymentId,
    DateTime? DeployTime,
    string? TenantId = null);

/// <summary>
/// 流程定义仓储（部署 / 回显）。
/// <para>Activiti 侧对应 <c>ACT_RE_DEPLOYMENT</c> + <c>ACT_GE_BYTEARRAY</c> + <c>ACT_RE_PROCDEF</c>。</para>
/// </summary>
public interface IProcessDefinitionStore
{
    /// <summary>
    /// 部署流程定义。
    /// <para>
    /// <paramref name="rawResourceBytes"/> 是<b>调用方提供的原始 XML 字节</b>（B2.5）：
    /// 传入时 <c>ACT_GE_BYTEARRAY.BYTES_</c> 存原始字节（与 Activiti 一致），而不是重新序列化的 XML；
    /// 为 <see langword="null"/> 时退回重新序列化（内存仓储 / 内部部署路径）。
    /// </para>
    /// <para>
    /// <paramref name="tenantId"/> 为部署租户（B2.3），落到 <c>ACT_RE_DEPLOYMENT</c> 与
    /// <c>ACT_RE_PROCDEF.TENANT_ID_</c>；为 <see langword="null"/> 时落 <c>''</c>（无租户，与 Activiti 一致）。
    /// </para>
    /// </summary>
    ValueTask<ProcessDefinition> DeployAsync(
        BpmnModel bpmnModel,
        string resourceName,
        string? deploymentName = null,
        byte[]? rawResourceBytes = null,
        string? tenantId = null,
        CancellationToken ct = default);

    ValueTask<ProcessDefinition?> FindByIdAsync(string processDefinitionId, CancellationToken ct = default);

    /// <summary>查全部已部署的流程定义（每个部署一行，按部署时间倒序）；<paramref name="tenantId"/> 过滤可选。</summary>
    ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindAllAsync(
        string? tenantId = null,
        CancellationToken ct = default);

    ValueTask<BpmnModel?> GetBpmnModelAsync(string processDefinitionId, CancellationToken ct = default);

    ValueTask<string?> GetResourceXmlAsync(string processDefinitionId, CancellationToken ct = default);

    /// <summary>查流程定义的租户（B2.3，启动实例时继承）；定义不存在返回 <see langword="null"/>。</summary>
    ValueTask<string?> GetTenantIdAsync(string processDefinitionId, CancellationToken ct = default);
}

/// <summary>
/// 内存版流程定义仓储。
/// <para>用于单元测试与「无数据库宿主」（POC 脚手架）。生产必须使用
/// <see cref="DapperProcessDefinitionStore"/>。</para>
/// </summary>
public sealed class InMemoryProcessDefinitionStore : IProcessDefinitionStore
{
    private readonly IIdGenerator _idGenerator;
    private readonly IBpmnXmlConverter _xmlConverter;
    private readonly Dictionary<string, ProcessDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BpmnModel> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _deployTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> Tenants = new(StringComparer.Ordinal);
    private readonly Lock _syncRoot = new();

    public InMemoryProcessDefinitionStore(IIdGenerator idGenerator, IBpmnXmlConverter xmlConverter)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(xmlConverter);

        _idGenerator = idGenerator;
        _xmlConverter = xmlConverter;
    }

    /// <inheritdoc />
    public ValueTask<ProcessDefinition> DeployAsync(
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

        if (string.IsNullOrWhiteSpace(process.Id))
        {
            throw new EngineException("部署失败：流程缺少 id。");
        }

        lock (_syncRoot)
        {
            var version = _definitions.Values
                .Where(definition => string.Equals(definition.Key, process.Id, StringComparison.Ordinal)
                    && string.Equals(Tenants.GetValueOrDefault(definition.Id), tenantId, StringComparison.Ordinal))
                .Select(definition => definition.Version)
                .DefaultIfEmpty(0)
                .Max() + 1;

            var definitionId = ProcessDefinition.BuildId(process.Id!, version, _idGenerator.NextId());
            var deploymentId = _idGenerator.NextId();

            var definition = new ProcessDefinition(
                definitionId,
                process.Id!,
                process.Name,
                version,
                deploymentId,
                resourceName);

            _definitions[definitionId] = definition;
            _models[definitionId] = bpmnModel;
            _resources[definitionId] = rawResourceBytes is { Length: > 0 } raw
                ? System.Text.Encoding.UTF8.GetString(raw)
                : Serialize(bpmnModel);
            _deployTimes[deploymentId] = DateTime.UtcNow;
            Tenants[definitionId] = tenantId;

            return ValueTask.FromResult(definition);
        }
    }

    /// <inheritdoc />
    public ValueTask<ProcessDefinition?> FindByIdAsync(string processDefinitionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult(_definitions.GetValueOrDefault(processDefinitionId));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindAllAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            var result = _definitions.Values
                .Where(definition => tenantId is null
                    || string.Equals(Tenants.GetValueOrDefault(definition.Id), tenantId, StringComparison.Ordinal))
                .Select(definition => new DeployedProcessDefinition(
                    definition.Id,
                    definition.Key,
                    definition.Name,
                    definition.Version,
                    definition.DeploymentId,
                    _deployTimes.GetValueOrDefault(definition.DeploymentId),
                    Tenants.GetValueOrDefault(definition.Id)))
                .OrderByDescending(definition => definition.DeployTime ?? DateTime.MinValue)
                .ToList();

            return ValueTask.FromResult<IReadOnlyList<DeployedProcessDefinition>>(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<BpmnModel?> GetBpmnModelAsync(string processDefinitionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult(_models.GetValueOrDefault(processDefinitionId));
        }
    }

    /// <inheritdoc />
    public ValueTask<string?> GetResourceXmlAsync(string processDefinitionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult(_resources.GetValueOrDefault(processDefinitionId));
        }
    }

    /// <inheritdoc />
    public ValueTask<string?> GetTenantIdAsync(string processDefinitionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult(Tenants.GetValueOrDefault(processDefinitionId));
        }
    }

    private string Serialize(BpmnModel bpmnModel)
    {
        using var buffer = new MemoryStream();
        _xmlConverter.ConvertToXml(bpmnModel, buffer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

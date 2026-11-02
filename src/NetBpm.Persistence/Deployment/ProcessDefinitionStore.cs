using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;

namespace NetBpm.Persistence.Deployment;

/// <summary>已部署的流程定义。对应 <c>ACT_RE_PROCDEF</c> 的一行。</summary>
/// <param name="Id">流程定义 id，形如 <c>{key}:{version}:{idGeneratorId}</c>（见 §7.4）。</param>
/// <param name="Key">流程 key（XML 中的 <c>process/@id</c>）。</param>
/// <param name="Name">流程名。</param>
/// <param name="Description">
/// 流程文档（<c>ACT_RE_PROCDEF.DESCRIPTION_</c>，来自 <c>&lt;process&gt;&lt;documentation&gt;</c>）。
/// Activiti 落的就是这一列；对拍显示缺了它会造成 13 条 <c>DESCRIPTION_</c> 差异。
/// </param>
/// <param name="Version">同一 key 下 <c>max(VERSION_) + 1</c>。</param>
/// <param name="DeploymentId">部署 id。</param>
/// <param name="ResourceName">资源名（如 <c>designer.bpmn20.xml</c>）。</param>
public sealed record ProcessDefinition(
    string Id,
    string Key,
    string? Name,
    string? Description,
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
/// <param name="TenantId">租户 id（无租户为 <see langword="null"/>）。</param>
/// <param name="IsSuspended">
/// 是否已挂起（<c>ACT_RE_PROCDEF.SUSPENSION_STATE_ = 2</c>）。
/// 挂起后不能再启动新实例；运行中的实例是否也挂起，取决于挂起时传的
/// <c>suspendProcessInstances</c>（见 <see cref="IProcessDefinitionStore.SetSuspensionStateAsync"/>）。
/// </param>
public sealed record DeployedProcessDefinition(
    string Id,
    string Key,
    string? Name,
    int Version,
    string DeploymentId,
    DateTime? DeployTime,
    string? TenantId = null,
    bool IsSuspended = false,
    string? ResourceName = null,
    string? DeploymentName = null);

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

    /// <summary>
    /// 按 key 查<b>最新版本</b>的流程定义（同一 <c>KEY_</c> + <c>TENANT_ID_</c> 下 <c>VERSION_</c> 最大者）。
    /// <para>
    /// 对应 Activiti <c>runtimeService.startProcessInstanceByKey(key)</c> 的「key → 最新版本」解析。
    /// </para>
    /// <para>
    /// <paramref name="tenantId"/> 为 <see langword="null"/> 表示<b>无租户</b>（<c>TENANT_ID_ = ''</c>），
    /// 不是「跨租户」—— 与启动接口的租户口径一致。
    /// </para>
    /// </summary>
    ValueTask<ProcessDefinition?> FindLatestByKeyAsync(
        string processDefinitionKey,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>查全部已部署的流程定义（每个部署一行，按部署时间倒序）；<paramref name="tenantId"/> 过滤可选。</summary>
    ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindAllAsync(
        string? tenantId = null,
        CancellationToken ct = default);

    ValueTask<BpmnModel?> GetBpmnModelAsync(string processDefinitionId, CancellationToken ct = default);

    ValueTask<string?> GetResourceXmlAsync(string processDefinitionId, CancellationToken ct = default);

    /// <summary>查流程定义的租户（B2.3，启动实例时继承）；定义不存在返回 <see langword="null"/>。</summary>
    ValueTask<string?> GetTenantIdAsync(string processDefinitionId, CancellationToken ct = default);

    /// <summary>
    /// 挂起 / 激活流程定义（<c>ACT_RE_PROCDEF.SUSPENSION_STATE_</c> 1=激活、2=挂起）。
    /// <para>
    /// 对应 Activiti <c>repositoryService.suspendProcessDefinitionById</c> /
    /// <c>activateProcessDefinitionById</c> 的<b>定义级</b>语义：挂起后不能再启动新实例。
    /// </para>
    /// <para>
    /// 至于「在跑的实例是否一起挂起」，由调用方用
    /// <c>suspendProcessInstances</c> 决定（对应 Activiti 的第二个布尔参数）：
    /// 需要额外调用 <see cref="Engine.Execution.IExecutionManager.UpdateSuspensionStateByProcessDefinitionIdAsync"/>
    /// 与 <see cref="Engine.Tasks.ITaskManager.UpdateSuspensionStateByProcessDefinitionIdAsync"/>。
    /// </para>
    /// </summary>
    /// <returns>定义是否存在（不存在返回 <see langword="false"/>，不做任何修改）。</returns>
    ValueTask<bool> SetSuspensionStateAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default);

    /// <summary>流程定义是否已挂起；定义不存在返回 <see langword="false"/>（存在性由调用方另行判定）。</summary>
    ValueTask<bool> IsSuspendedAsync(string processDefinitionId, CancellationToken ct = default);

    /// <summary>查某次部署下的全部流程定义（一个部署可以带多个 <c>process</c>）。</summary>
    ValueTask<IReadOnlyList<ProcessDefinition>> FindByDeploymentIdAsync(
        string deploymentId,
        CancellationToken ct = default);

    /// <summary>
    /// 删除部署及其流程定义与资源（<c>ACT_RE_DEPLOYMENT</c> + <c>ACT_RE_PROCDEF</c> +
    /// <c>ACT_GE_BYTEARRAY</c>，对应 Activiti <c>deleteDeployment</c>）。
    /// <para>
    /// ⚠️ <b>本方法不做级联</b>：调用方必须先清掉该部署下的运行实例
    /// （有 <c>PROC_DEF_ID_</c> 外键约束，且删定义会让运行实例变成孤儿）。
    /// 运行态清理见 <c>IProcessEngine.DeleteDeploymentAsync(cascade)</c>。
    /// </para>
    /// <para>
    /// ⚠️ <b>历史保留</b>：<c>ACT_HI_*</c> 的表行不删（本项目的账本只增不删），
    /// 这与 Activiti 的 <c>deleteDeployment(cascade=true)</c> 会连历史一起删<b>有意不同</b>。
    /// </para>
    /// </summary>
    /// <returns>部署是否存在（不存在返回 <see langword="false"/>）。</returns>
    ValueTask<bool> DeleteDeploymentAsync(string deploymentId, CancellationToken ct = default);
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
    private readonly Dictionary<string, string?> _deploymentNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> Tenants = new(StringComparer.Ordinal);
    private readonly HashSet<string> _suspended = new(StringComparer.Ordinal);
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
                process.Documentation,
                version,
                deploymentId,
                resourceName);

            _definitions[definitionId] = definition;
            _models[definitionId] = bpmnModel;
            _resources[definitionId] = rawResourceBytes is { Length: > 0 } raw
                ? System.Text.Encoding.UTF8.GetString(raw)
                : Serialize(bpmnModel);
            _deployTimes[deploymentId] = DateTime.UtcNow;
            _deploymentNames[deploymentId] = deploymentName;
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
    public ValueTask<ProcessDefinition?> FindLatestByKeyAsync(
        string processDefinitionKey,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionKey);

        lock (_syncRoot)
        {
            var latest = _definitions.Values
                .Where(definition => string.Equals(definition.Key, processDefinitionKey, StringComparison.Ordinal)
                    && string.Equals(Tenants.GetValueOrDefault(definition.Id), tenantId, StringComparison.Ordinal))
                .OrderByDescending(definition => definition.Version)
                .FirstOrDefault();

            return ValueTask.FromResult(latest);
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
                    Tenants.GetValueOrDefault(definition.Id),
                    _suspended.Contains(definition.Id),
                    definition.ResourceName,
                    _deploymentNames.GetValueOrDefault(definition.DeploymentId)))
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

    /// <inheritdoc />
    public ValueTask<bool> SetSuspensionStateAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        lock (_syncRoot)
        {
            if (!_definitions.ContainsKey(processDefinitionId))
            {
                return ValueTask.FromResult(false);
            }

            if (suspended)
            {
                _suspended.Add(processDefinitionId);
            }
            else
            {
                _suspended.Remove(processDefinitionId);
            }

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> IsSuspendedAsync(string processDefinitionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        lock (_syncRoot)
        {
            return ValueTask.FromResult(_suspended.Contains(processDefinitionId));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ProcessDefinition>> FindByDeploymentIdAsync(
        string deploymentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        lock (_syncRoot)
        {
            IReadOnlyList<ProcessDefinition> definitions =
            [
                .. _definitions.Values
                    .Where(definition => string.Equals(definition.DeploymentId, deploymentId, StringComparison.Ordinal)),
            ];

            return ValueTask.FromResult(definitions);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteDeploymentAsync(string deploymentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        lock (_syncRoot)
        {
            if (!_deployTimes.ContainsKey(deploymentId))
            {
                return ValueTask.FromResult(false);
            }

            var definitionIds = _definitions.Values
                .Where(definition => string.Equals(definition.DeploymentId, deploymentId, StringComparison.Ordinal))
                .Select(definition => definition.Id)
                .ToList();

            foreach (var definitionId in definitionIds)
            {
                _definitions.Remove(definitionId);
                _models.Remove(definitionId);
                _resources.Remove(definitionId);
                _suspended.Remove(definitionId);
                Tenants.Remove(definitionId);
            }

            _deployTimes.Remove(deploymentId);

            return ValueTask.FromResult(true);
        }
    }

    private string Serialize(BpmnModel bpmnModel)
    {
        using var buffer = new MemoryStream();
        _xmlConverter.ConvertToXml(bpmnModel, buffer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

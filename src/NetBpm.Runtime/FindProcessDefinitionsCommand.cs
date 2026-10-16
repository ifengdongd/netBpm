using NetBpm.Engine.Command;
using NetBpm.Persistence.Deployment;

namespace NetBpm.Runtime;

/// <summary>
/// 查询全部已部署的流程定义（流程部署列表数据源，只读，无需写事务）。
/// </summary>
internal sealed class FindProcessDefinitionsCommand : ICommand<IReadOnlyList<DeployedProcessDefinition>>
{
    private readonly IProcessDefinitionStore _definitionStore;
    private readonly string? _tenantId;

    public FindProcessDefinitionsCommand(IProcessDefinitionStore definitionStore, string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(definitionStore);

        _definitionStore = definitionStore;
        _tenantId = tenantId;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DeployedProcessDefinition>> ExecuteAsync(
        ICommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _definitionStore.FindAllAsync(_tenantId, ct);
    }
}

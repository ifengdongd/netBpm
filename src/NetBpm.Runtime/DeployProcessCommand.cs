using NetBpm.Bpmn.Model;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Deployment;

namespace NetBpm.Runtime;

/// <summary>
/// 部署流程定义（<c>ACT_RE_DEPLOYMENT</c> + <c>ACT_GE_BYTEARRAY</c> + <c>ACT_RE_PROCDEF</c> 三张表的写入）。
/// <para>
/// 🔑 <b>必须作为命令执行</b>：三张表的写入要落在一个事务里。
/// 否则部署中途失败会留下**孤儿部署行**（实测过：第一次部署因重复 id 失败后，
/// 残留的 id='0' 行让后续每次部署都撞主键）。
/// </para>
/// <para>XML 解析与校验在命令外完成（纯计算，不需要事务）。</para>
/// </summary>
internal sealed class DeployProcessCommand : ICommand<ProcessDefinition>
{
    private readonly IProcessDefinitionStore _definitionStore;
    private readonly BpmnModel _bpmnModel;
    private readonly string _resourceName;
    private readonly string? _deploymentName;

    public DeployProcessCommand(
        IProcessDefinitionStore definitionStore,
        BpmnModel bpmnModel,
        string resourceName,
        string? deploymentName)
    {
        ArgumentNullException.ThrowIfNull(definitionStore);
        ArgumentNullException.ThrowIfNull(bpmnModel);

        _definitionStore = definitionStore;
        _bpmnModel = bpmnModel;
        _resourceName = resourceName;
        _deploymentName = deploymentName;
    }

    /// <inheritdoc />
    public async ValueTask<ProcessDefinition> ExecuteAsync(
        ICommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await _definitionStore
            .DeployAsync(_bpmnModel, _resourceName, _deploymentName, ct)
            .ConfigureAwait(false);
    }
}

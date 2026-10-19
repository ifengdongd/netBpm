using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Deployment;

namespace NetBpm.Runtime;

/// <summary>
/// 删除部署。对应 Activiti 的 <c>repositoryService.deleteDeployment(deploymentId, cascade)</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>级联语义</b>：<c>cascade = true</c> 时先删除该部署下所有流程定义的<b>运行实例</b>
/// （待办、变量、执行树按「删除流程实例」的同一路径收口，历史行写入
/// <c>DELETE_REASON_ = 删除部署</c>），再删定义、资源与部署行。
/// </para>
/// <para>
/// <c>cascade = false</c> 时若仍有运行实例，<b>明确拒绝</b>（Activiti 同样会抛异常），
/// 而不是悄悄留下指向已删定义的孤儿实例。
/// </para>
/// <para>
/// <b>与 Activiti 的有意差异</b>：<c>ACT_HI_*</c> 的历史行<b>不删</b>。
/// 本项目的账本只增不删（删除实例、跳转等都只收口历史），删部署同样保留历史用于审计；
/// 需要清历史属于独立的运维动作。
/// </para>
/// <para>
/// 另一个副作用：定义删除后会清掉进程内的模型 / 租户缓存，否则「按已删定义 id 启动」
/// 会先命中缓存、最后撞 <c>ACT_RU_EXECUTION.PROC_DEF_ID_</c> 外键，报错信息毫无指向性。
/// </para>
/// </remarks>
internal sealed class DeleteDeploymentCommand : ICommand<Unit>
{
    /// <summary>删除部署时写进历史的原因（与 Activiti 的 "deployment deleted" 对应）。</summary>
    private const string DeleteReason = "删除部署";

    private readonly IProcessDefinitionStore _definitionStore;
    private readonly string _deploymentId;
    private readonly bool _cascade;

    public DeleteDeploymentCommand(
        IProcessDefinitionStore definitionStore,
        string deploymentId,
        bool cascade)
    {
        ArgumentNullException.ThrowIfNull(definitionStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        _definitionStore = definitionStore;
        _deploymentId = deploymentId;
        _cascade = cascade;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var executionManager = context.Services.ExecutionManager;

        var definitions = await _definitionStore
            .FindByDeploymentIdAsync(_deploymentId, ct)
            .ConfigureAwait(false);

        if (definitions.Count == 0)
        {
            // 部署可能只带资源、没有可执行流程：存在性交给仓储判定
            if (!await _definitionStore.DeleteDeploymentAsync(_deploymentId, ct).ConfigureAwait(false))
            {
                throw new EngineObjectNotFoundException($"部署 '{_deploymentId}' 不存在。");
            }

            return Unit.Value;
        }

        var instanceIds = new List<string>();

        foreach (var definition in definitions)
        {
            instanceIds.AddRange(
                await executionManager
                    .FindProcessInstanceIdsByProcessDefinitionIdAsync(definition.Id, ct)
                    .ConfigureAwait(false));
        }

        if (instanceIds.Count > 0 && !_cascade)
        {
            throw new EngineConflictException(
                $"部署 '{_deploymentId}' 下还有 {instanceIds.Count} 个运行中的流程实例，不能删除" +
                "（会留下指向已删定义的孤儿实例）；需要连同实例一起删除请传 cascade=true。");
        }

        foreach (var instanceId in instanceIds)
        {
            await executionManager
                .DeleteProcessInstanceExecutionEntityAsync(
                    instanceId,
                    currentFlowElementId: null,
                    deleteReason: DeleteReason,
                    cancel: true,
                    ct)
                .ConfigureAwait(false);
        }

        if (!await _definitionStore.DeleteDeploymentAsync(_deploymentId, ct).ConfigureAwait(false))
        {
            throw new EngineObjectNotFoundException($"部署 '{_deploymentId}' 不存在。");
        }

        return Unit.Value;
    }
}

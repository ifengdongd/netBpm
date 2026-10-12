using NetBpm.Engine.Agenda;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Operations;

/// <summary>
/// 结束一条执行，并按 BPMN 规则决定流程实例能否结束。
/// 对应 Activiti 的 <c>EndExecutionOperation</c>。
/// <para>
/// 🔑 「合并」的收口判定就在这里（开发计划 §5.5）：<b>流程实例是否结束 = 是否还存在活动的子执行</b>。
/// </para>
/// </summary>
public sealed class EndExecutionOperation : IOperation
{
    private readonly IExecutionEntity _execution;
    private readonly IEngineServices _services;

    public EndExecutionOperation(IExecutionEntity execution, IEngineServices services)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(services);

        _execution = execution;
        _services = services;
    }

    /// <inheritdoc />
    public string Name => "EndExecutionOperation";

    /// <inheritdoc />
    public ValueTask RunAsync(CancellationToken ct = default) =>
        _execution.IsProcessInstanceType
            ? HandleProcessInstanceExecutionAsync(_execution, ct)
            : HandleRegularExecutionAsync(ct);

    /// <summary>对应 Activiti 的 <c>handleProcessInstanceExecution</c>（裁剪掉调用活动 / 子流程部分）。</summary>
    private async ValueTask HandleProcessInstanceExecutionAsync(
        IExecutionEntity processInstanceExecution,
        CancellationToken ct)
    {
        var processInstanceId = processInstanceExecution.Id;

        var activeExecutions = await _services.ExecutionManager
            .CountActiveChildExecutionsForProcessInstanceAsync(processInstanceId, ct)
            .ConfigureAwait(false);

        if (activeExecutions == 0)
        {
            // ⚠️ 元素 id 取自**操作自身的执行**（而非流程实例执行）—— 流程实例根执行的 ACT_ID_ 恒为空，
            // 用它的 id 会让 ACT_HI_PROCINST.END_ACT_ID_ 永远是 null。
            await _services.ExecutionManager
                .DeleteProcessInstanceExecutionEntityAsync(
                    processInstanceId,
                    _execution.CurrentFlowElement?.Id,
                    deleteReason: null,
                    cancel: false,
                    ct)
                .ConfigureAwait(false);
        }

        // 否则：还有并行分支未完成，流程实例继续存活（§5.5）
    }

    /// <summary>对应 Activiti 的 <c>handleRegularExecution</c>（裁剪掉子流程 / 多实例部分）。</summary>
    private async ValueTask HandleRegularExecutionAsync(CancellationToken ct)
    {
        var parentExecutionId = _execution.ParentId
            ?? throw new EngineException($"非流程实例执行 '{_execution.Id}' 缺少 PARENT_ID_。");

        var parentExecution = await _services.ExecutionManager
            .FindByIdAsync(parentExecutionId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineException(
                $"找不到执行 '{_execution.Id}' 的父执行 '{parentExecutionId}'。");

        await _services.ExecutionManager
            .DeleteExecutionAndRelatedDataAsync(_execution, null, ct)
            .ConfigureAwait(false);

        // 普通执行结束时，把当前元素上抛给父执行（Activiti: handleRegularExecutionEnd）
        if (!parentExecution.IsProcessInstanceType)
        {
            parentExecution.CurrentFlowElement = _execution.CurrentFlowElement;
        }

        var childCount = await _services.ExecutionManager
            .CountChildExecutionsForExecutionAsync(parentExecution.Id, ct)
            .ConfigureAwait(false);

        if (childCount != 0)
        {
            return;
        }

        // 父执行再无活动子执行 → 可以继续
        if (parentExecution.IsProcessInstanceType)
        {
            await HandleProcessInstanceExecutionAsync(parentExecution, ct).ConfigureAwait(false);
        }
        else
        {
            _services.Agenda.PlanTakeOutgoingSequenceFlowsOperation(parentExecution, evaluateConditions: true);
        }
    }
}

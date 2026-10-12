using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Operations;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Agenda;

/// <summary>
/// 基于 FIFO 队列的 <see cref="IEngineAgenda"/>。
/// 严格照搬 Activiti 的「规划 → 排空」模型（§5.3）：一次命令内把队列排空，绝不递归推进。
/// </summary>
public sealed class EngineAgenda : IEngineAgenda
{
    private readonly Queue<IOperation> _operations = new();
    private readonly Func<IEngineServices> _servicesAccessor;
    private readonly ILogger<EngineAgenda> _logger;
    private bool _isDraining;

    public EngineAgenda(Func<IEngineServices> servicesAccessor, ILogger<EngineAgenda>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(servicesAccessor);
        _servicesAccessor = servicesAccessor;
        _logger = logger ?? NullLogger<EngineAgenda>.Instance;
    }

    /// <inheritdoc />
    public bool IsEmpty => _operations.Count == 0;

    /// <summary>待执行的操作数（对拍与测试用）。</summary>
    public int PendingOperationCount => _operations.Count;

    /// <inheritdoc />
    public void PlanContinueProcessOperation(IExecutionEntity execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _operations.Enqueue(new ContinueProcessOperation(execution, _servicesAccessor()));
        _logger.LogTrace("规划 ContinueProcessOperation：执行 {ExecutionId}", execution.Id);
    }

    /// <inheritdoc />
    public void PlanTakeOutgoingSequenceFlowsOperation(IExecutionEntity execution, bool evaluateConditions)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _operations.Enqueue(new TakeOutgoingSequenceFlowsOperation(execution, _servicesAccessor(), evaluateConditions));
        _logger.LogTrace(
            "规划 TakeOutgoingSequenceFlowsOperation：执行 {ExecutionId}，条件判定={EvaluateConditions}",
            execution.Id,
            evaluateConditions);
    }

    /// <inheritdoc />
    public void PlanEndExecutionOperation(IExecutionEntity execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _operations.Enqueue(new EndExecutionOperation(execution, _servicesAccessor()));
        _logger.LogTrace("规划 EndExecutionOperation：执行 {ExecutionId}", execution.Id);
    }

    /// <inheritdoc />
    public void PlanTriggerExecutionOperation(
        IExecutionEntity execution,
        string? signalName = null,
        object? signalData = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _operations.Enqueue(new TriggerExecutionOperation(execution, _servicesAccessor(), signalName, signalData));
        _logger.LogTrace("规划 TriggerExecutionOperation：执行 {ExecutionId}", execution.Id);
    }

    /// <inheritdoc />
    public void Clear() => _operations.Clear();

    /// <inheritdoc />
    public async ValueTask DrainAsync(CancellationToken ct = default)
    {
        if (_isDraining)
        {
            // 重入：外层排空循环会继续消费新加入的操作
            return;
        }

        _isDraining = true;
        try
        {
            while (_operations.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var operation = _operations.Dequeue();
                _logger.LogTrace("执行操作 {Operation}", operation.Name);

                // 注意：这里 await 的是引擎内部操作（数据库写由一级缓存 + flush 批量完成），
                // 不允许在操作内部 await 外部系统 IO，否则事务时长不可控（§5.3）。
                await operation.RunAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _isDraining = false;
        }
    }
}

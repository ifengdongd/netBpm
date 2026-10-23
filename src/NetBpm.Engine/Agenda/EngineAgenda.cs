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
    /// <summary>
    /// 单个命令内允许执行的操作数上限（默认 1 万）。
    /// <para>
    /// 🔑 这是<b>正确性护栏</b>，不是性能开关：BPMN 允许环路（回退边 / 恒真条件网关），
    /// 一个坏模型能让「规划 → 排空」在单个命令里无限循环，内存无界增长直到进程被打爆。
    /// 超限时<b>响亮失败</b>（<see cref="EngineException"/>），而不是让宿主表现为 OOM 或请求挂死。
    /// </para>
    /// </summary>
    public const int DefaultMaxOperationsPerCommand = 10_000;

    private readonly Queue<IOperation> _operations = new();
    private readonly Func<IEngineServices> _servicesAccessor;
    private readonly ILogger<EngineAgenda> _logger;
    private readonly int _maxOperations;
    private bool _isDraining;

    /// <param name="servicesAccessor">引擎服务访问器（延迟取用，打破构造循环）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="maxOperations">
    /// 单命令操作预算。超出即判定为「模型存在死循环」并抛错，见 <see cref="DefaultMaxOperationsPerCommand"/>。
    /// </param>
    public EngineAgenda(
        Func<IEngineServices> servicesAccessor,
        ILogger<EngineAgenda>? logger = null,
        int maxOperations = DefaultMaxOperationsPerCommand)
    {
        ArgumentNullException.ThrowIfNull(servicesAccessor);

        if (maxOperations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOperations),
                maxOperations,
                "单命令操作预算必须为正整数。");
        }

        _servicesAccessor = servicesAccessor;
        _logger = logger ?? NullLogger<EngineAgenda>.Instance;
        _maxOperations = maxOperations;
    }

    /// <inheritdoc />
    public bool IsEmpty => _operations.Count == 0;

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
        var executed = 0;

        try
        {
            while (_operations.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var operation = _operations.Dequeue();

                // 环路的唯一可见症状就是「队列永远排不空」：到预算上限立刻响亮失败。
                // 注意预算按「本次排空」计数；重试拦截器调用 ResetForRetry 会新建议程，计数自然归零。
                if (++executed > _maxOperations)
                {
                    var pending = _operations.Count;
                    var last = operation.Name;
                    _operations.Clear();

                    throw new EngineException(
                        $"流程在单个命令内执行了超过 {_maxOperations} 步仍未停止" +
                        $"（最后一个操作：{last}，队列中剩余 {pending} 条）。" +
                        "这通常意味着模型存在死循环：请检查回退边 / 恒真条件网关 / 无条件环路。" +
                        "如确属需要（例如超大并行分支），可调大 NetBpm:MaxOperationsPerCommand。");
                }

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

using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Command;

/// <summary>
/// 启动流程实例。对应 Activiti 的 <c>StartProcessInstanceCmd</c>。
/// <para>
/// 🔑 初始执行树结构必须照搬（<c>ProcessInstanceHelper.createProcessInstanceWithInitialFlowElement</c>）：
/// </para>
/// <list type="number">
///   <item><description>创建<b>流程实例根执行</b>：<c>IsScope = true</c>、<c>ParentId = null</c>、
///     <c>ProcessInstanceId = 自身 Id</c>；</description></item>
///   <item><description>为根执行创建<b>一个子执行</b>（<c>IsScope = false</c>），由它访问全部流程元素。</description></item>
/// </list>
/// <para>
/// 根执行不参与流程推进这一点至关重要：并行网关 Join 时会删除「除当前执行外的全部非活动执行」，
/// 若根执行参与推进，就可能被误删，从而破坏执行树。
/// </para>
/// </summary>
public sealed class StartProcessInstanceCommand : ICommand<string>
{
    private readonly Process _process;
    private readonly string _processDefinitionId;
    private readonly string? _businessKey;
    private readonly string? _startUserId;
    private readonly IDictionary<string, object?>? _variables;

    public StartProcessInstanceCommand(
        Process process,
        string processDefinitionId,
        string? businessKey = null,
        string? startUserId = null,
        IDictionary<string, object?>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        _process = process;
        _processDefinitionId = processDefinitionId;
        _businessKey = businessKey;
        _startUserId = startUserId;
        _variables = variables;
    }

    /// <inheritdoc />
    public async ValueTask<string> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;

        var initialFlowElement = _process.GetInitialFlowElement()
            ?? throw new EngineException(
                $"流程 '{_process.Id}' 没有开始事件，无法启动实例。");

        var processInstance = services.ExecutionManager.CreateProcessInstanceExecution(
            _process,
            _processDefinitionId,
            _businessKey,
            _startUserId);

        await services.ExecutionManager.InsertAsync(processInstance, ct).ConfigureAwait(false);

        // 创建「访问全部流程元素」的第一个子执行
        var execution = services.ExecutionManager.CreateChildExecution(processInstance);
        execution.CurrentFlowElement = initialFlowElement;
        await services.ExecutionManager.InsertAsync(execution, ct).ConfigureAwait(false);

        // ⚠️ 变量必须先写、再记录实例启动：真值比对（2026-10-12 首次跑批）证明 Activiti 的事件顺序是
        //   VARIABLE_CREATED × N → PROCESS_STARTED → SEQUENCEFLOW_TAKEN → ACTIVITY_STARTED …
        // 而本实现原本是"先 PROCESS_STARTED 再写变量"，整条事件序列因此错位一位，
        // 在维度③上造成 2461 条假差异（占总差异的 82%）。顺序即契约。
        //
        // 发起人变量（activiti:initiator）在最前面：对应 Activiti 的 StartProcessInstanceCmd ——
        // 实例一创建就把认证用户写进该变量（认证用户为 null 时变量仍会创建，null 类型），
        // 随后 ${initiator} 才能在 humanPerformer 等表达式里引用。
        if (initialFlowElement is StartEvent { Initiator: { Length: > 0 } initiatorVariableName })
        {
            await processInstance.SetVariableAsync(initiatorVariableName, _startUserId, ct).ConfigureAwait(false);
        }

        if (_variables is { Count: > 0 })
        {
            await processInstance.SetVariablesAsync(new Dictionary<string, object?>(_variables, StringComparer.Ordinal), ct)
                .ConfigureAwait(false);
        }

        // 历史：流程实例启动 —— START_ACT_ID_ 取开始事件 id
        await services.HistoryManager
            .RecordProcessInstanceStartAsync(processInstance, initialFlowElement.Id, ct)
            .ConfigureAwait(false);

        // 流程级 PROCESS_STARTED 事件分发（activiti:eventListener）
        await EngineEventDispatcher
            .DispatchAsync(services, processInstance, EngineEvents.ProcessStarted, processInstance.Id, ct)
            .ConfigureAwait(false);

        services.Agenda.PlanContinueProcessOperation(execution);

        return processInstance.Id;
    }
}

using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 规则 1：节点必须有出流（结束事件除外）。
/// 对应 Activiti 的 <c>FlowElementValidator</c> 中「无出流节点」红线（§1.1 L2 校验红线）。
/// </summary>
public sealed class NodeHasOutgoingFlowRule : IProcessValidationRule
{
    public string Name => "node-has-outgoing-flow";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var node in process.FindFlowElementsOfType<FlowNode>())
        {
            if (node is EndEvent || node.OutgoingFlows.Count > 0)
            {
                continue;
            }

            yield return new ValidationError(
                Name,
                node.Id,
                node.GetType().Name,
                "节点没有任何出流，流程会在此处非法终止（结束事件除外）。");
        }
    }
}

/// <summary>
/// 规则 2：排他网关必须能选出流。
/// <para>
/// 依 Activiti 语义（§5.4 之一）无需「条件全覆盖」检查 —— 只要「存在默认流」或「存在无条件流」即可，
/// 否则运行期必然抛 <c>No outgoing sequence flow ... could be selected</c>。
/// </para>
/// </summary>
public sealed class ExclusiveGatewayHasSelectableFlowRule : IProcessValidationRule
{
    public string Name => "exclusive-gateway-has-selectable-flow";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var gateway in process.FindFlowElementsOfType<ExclusiveGateway>())
        {
            if (gateway.DefaultFlow is not null)
            {
                continue;
            }

            var hasUnconditionalFlow = gateway.OutgoingFlows
                .Any(flow => string.IsNullOrEmpty(flow.ConditionExpression));

            if (!hasUnconditionalFlow)
            {
                yield return new ValidationError(
                    Name,
                    gateway.Id,
                    nameof(ExclusiveGateway),
                    "排他网关的每条出流都带条件且未设置默认流，条件全为假时无法选出流。");
            }
        }
    }
}

/// <summary>规则 3：不允许孤立节点（非开始事件必须有入流）。</summary>
public sealed class NoOrphanNodeRule : IProcessValidationRule
{
    public string Name => "no-orphan-node";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var node in process.FindFlowElementsOfType<FlowNode>())
        {
            if (node is StartEvent || node.IncomingFlows.Count > 0)
            {
                continue;
            }

            yield return new ValidationError(
                Name,
                node.Id,
                node.GetType().Name,
                "节点没有任何入流，属于孤立节点。");
        }
    }
}

/// <summary>规则 4：元素 id 必须唯一且非空。</summary>
public sealed class UniqueElementIdRule : IProcessValidationRule
{
    public string Name => "unique-element-id";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in process.FlowElements)
        {
            if (string.IsNullOrWhiteSpace(element.Id))
            {
                yield return new ValidationError(
                    Name,
                    element.Id,
                    element.GetType().Name,
                    "元素缺少 id。");

                continue;
            }

            if (!seen.Add(element.Id))
            {
                yield return new ValidationError(
                    Name,
                    element.Id,
                    element.GetType().Name,
                    "元素 id 重复。");
            }
        }
    }
}

/// <summary>
/// 规则 5：并行网关的入流 / 出流条数必须与结构匹配（≥1），否则 Join 永不满足或 Fork 无意义。
/// </summary>
public sealed class ParallelGatewayFlowCountRule : IProcessValidationRule
{
    public string Name => "parallel-gateway-flow-count";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var gateway in process.FindFlowElementsOfType<ParallelGateway>())
        {
            if (gateway.IncomingFlows.Count == 0 || gateway.OutgoingFlows.Count == 0)
            {
                yield return new ValidationError(
                    Name,
                    gateway.Id,
                    nameof(ParallelGateway),
                    $"并行网关必须有至少一条入流与一条出流（当前入流 {gateway.IncomingFlows.Count} 条、" +
                    $"出流 {gateway.OutgoingFlows.Count} 条）；否则 Join 期望值不可能达成。");
            }

            if (gateway.DefaultFlow is not null)
            {
                yield return new ValidationError(
                    Name,
                    gateway.Id,
                    nameof(ParallelGateway),
                    "并行网关不支持默认流 —— 其出口不校验条件（见 §5.4 之二）。");
            }
        }
    }
}

/// <summary>规则 6：流程必须恰好有一个开始事件，且至少有一个结束事件。</summary>
public sealed class StartAndEndEventRule : IProcessValidationRule
{
    public string Name => "start-and-end-event";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var startEvents = process.FindFlowElementsOfType<StartEvent>().ToList();
        var endEvents = process.FindFlowElementsOfType<EndEvent>().ToList();

        if (startEvents.Count == 0)
        {
            yield return new ValidationError(Name, process.Id, nameof(Process), "流程没有开始事件。");
        }
        else if (startEvents.Count > 1)
        {
            // 多开始事件需要「按 id 启动」的额外语义，本子集不支持
            foreach (var startEvent in startEvents.Skip(1))
            {
                yield return new ValidationError(
                    Name,
                    startEvent.Id,
                    nameof(StartEvent),
                    "本子集只支持一个开始事件。");
            }
        }

        if (endEvents.Count == 0)
        {
            yield return new ValidationError(Name, process.Id, nameof(Process), "流程没有结束事件。");
        }
    }
}

/// <summary>规则 7：顺序流的 sourceRef / targetRef 必须能解析到节点。</summary>
public sealed class SequenceFlowReferenceRule : IProcessValidationRule
{
    public string Name => "sequence-flow-reference";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var flow in process.FindFlowElementsOfType<SequenceFlow>())
        {
            if (flow.SourceFlowNode is null)
            {
                yield return new ValidationError(
                    Name,
                    flow.Id,
                    nameof(SequenceFlow),
                    $"sourceRef '{flow.SourceRef}' 无法解析到流程节点。");
            }

            if (flow.TargetFlowNode is null)
            {
                yield return new ValidationError(
                    Name,
                    flow.Id,
                    nameof(SequenceFlow),
                    $"targetRef '{flow.TargetRef}' 无法解析到流程节点。");
            }
        }
    }
}

/// <summary>
/// 规则 9：本子集未实现的能力必须<b>显式拒绝</b>，不能静默忽略。
/// <para>
/// 覆盖三类：执行监听器（<c>activiti:executionListener</c>）、任务监听器（<c>activiti:taskListener</c>）、
/// 服务任务字段注入（<c>activiti:field</c>）—— §1.2 把它们列为「可选能力」，
/// 若放行则运行期会被无声跳过，属于最危险的一类差异（模型写了、引擎没做）。
/// </para>
/// </summary>
public sealed class UnsupportedCapabilityRule : IProcessValidationRule
{
    public string Name => "unsupported-capability";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var element in process.FlowElements.OfType<BaseElement>())
        {
            if (element.ExecutionListeners.Count > 0)
            {
                yield return new ValidationError(
                    Name,
                    element.Id,
                    element.GetType().Name,
                    "执行监听器（activiti:executionListener）尚未实现，为避免运行期静默跳过，这里拒绝部署。");
            }

            if (element is UserTask { TaskListeners.Count: > 0 } userTask)
            {
                yield return new ValidationError(
                    Name,
                    userTask.Id,
                    nameof(UserTask),
                    "任务监听器（activiti:taskListener）尚未实现，为避免运行期静默跳过，这里拒绝部署。");
            }

            if (element is ServiceTask { Fields.Count: > 0 } serviceTask)
            {
                yield return new ValidationError(
                    Name,
                    serviceTask.Id,
                    nameof(ServiceTask),
                    "服务任务字段注入（activiti:field）尚未实现，为避免运行期静默跳过，这里拒绝部署。");
            }
        }
    }
}

/// <summary>规则 8：必填属性（服务任务必须有实现方式）。</summary>
/// <remarks>
/// <para>
/// ⚠️ <b>曾经还要求「用户任务必须指定办理人或候选人/候选组」，现已移除</b>：
/// Activiti 允许无人办理的用户任务（任务照常创建，只是没人认领），因此那条要求是<b>过度限制</b> ——
/// 它会把 Activiti 能跑的流程在我们这里拒之门外（P10 对拍会直接暴露为"部署行为不一致"）。
/// </para>
/// <para>
/// 它当初的理由是"无人办理的任务按办理人查不出来"，而本子集已提供
/// <c>TaskQuery.processInstanceId</c>（见 <c>ITaskManager.FindTasksByProcessInstanceIdAsync</c>），
/// 该理由不再成立。服务任务那条则保留：Activiti 在运行期同样会因缺少实现方式而抛异常
/// （"One of the attributes 'class', 'delegateExpression', or 'expression' is mandatory"），
/// 我们只是把它提前到部署期。
/// </para>
/// </remarks>
public sealed class RequiredAttributesRule : IProcessValidationRule
{
    public string Name => "required-attributes";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var serviceTask in process.FindFlowElementsOfType<ServiceTask>())
        {
            if (serviceTask.ImplementationType == ServiceTaskImplementationType.None)
            {
                yield return new ValidationError(
                    Name,
                    serviceTask.Id,
                    nameof(ServiceTask),
                    "服务任务必须指定 activiti:class / activiti:delegateExpression / activiti:expression 之一。");
            }
        }
    }
}

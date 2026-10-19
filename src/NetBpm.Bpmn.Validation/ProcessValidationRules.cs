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

/// <summary>
/// 规则 3：不允许孤立节点（非开始事件必须有入流）。
/// <para>
/// <b>提示级</b>：孤立节点永远不会被到达，但<b>不影响</b>其余路径的执行 ——
/// Activiti 也照常部署这种模型。因此这里只提示"可能画漏了连线"，不阻断。
/// </para>
/// </summary>
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
                "节点没有任何入流，属于孤立节点（永远不会被执行）。",
                ValidationSeverity.Warning);
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
/// 规则 5b：包含网关必须至少有一条入流与一条出流，且「默认流或无条件出流」至少满足其一。
/// <para>
/// Fork 语义与排他网关同构（条件全假时无流可走），但 Join 期望值依赖入流数，因此入流为 0 也要拦。
/// </para>
/// </summary>
public sealed class InclusiveGatewayRule : IProcessValidationRule
{
    public string Name => "inclusive-gateway";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var gateway in process.FindFlowElementsOfType<InclusiveGateway>())
        {
            if (gateway.IncomingFlows.Count == 0 || gateway.OutgoingFlows.Count == 0)
            {
                yield return new ValidationError(
                    Name,
                    gateway.Id,
                    nameof(InclusiveGateway),
                    $"包含网关必须有至少一条入流与一条出流（当前入流 {gateway.IncomingFlows.Count} 条、" +
                    $"出流 {gateway.OutgoingFlows.Count} 条）。");
            }

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
                    nameof(InclusiveGateway),
                    "包含网关的每条出流都带条件且未设置默认流，条件全为假时无法选出流。");
            }
        }
    }
}

/// <summary>
/// 规则 5c：事件网关不被支持（需要中间捕获事件：定时/消息/信号）。
/// <para>在<b>部署期</b>显式报错，而不是等到运行期才发现引擎没有对应行为。</para>
/// </summary>
public sealed class EventGatewayUnsupportedRule : IProcessValidationRule
{
    public string Name => "event-gateway-unsupported";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var gateway in process.FindFlowElementsOfType<EventGateway>())
        {
            yield return new ValidationError(
                Name,
                gateway.Id,
                nameof(EventGateway),
                "事件网关的后续必须是中间捕获事件（定时/消息/信号），本子集未实现；请改用单一网关或包含网关。");
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
                // 提示级：Activiti 会解析出 default 属性但并行网关取道时根本不看条件，
                // 因此这条 default 是"写了也不生效"的冗余配置 —— 不影响执行，不阻断部署
                yield return new ValidationError(
                    Name,
                    gateway.Id,
                    nameof(ParallelGateway),
                    "并行网关不支持默认流 —— 其出口不校验条件，该 default 属性不会生效（见 §5.4 之二）。",
                    ValidationSeverity.Warning);
            }
        }
    }
}

/// <summary>
/// 规则 6：流程必须恰好有一个开始事件（阻断），且至少有一个结束事件（提示）。
/// <para>
/// 结束事件的检查降为提示：Activiti 允许没有结束事件的流程（实例只能靠删除收口），
/// 真正会「跑不下去」的情形（终端节点没有出流）已由 <see cref="NodeHasOutgoingFlowRule"/> 阻断。
/// </para>
/// </summary>
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
            yield return new ValidationError(
                Name,
                process.Id,
                nameof(Process),
                "流程没有结束事件：实例不会自然结束（Activiti 允许这种模型，此处只提示）。",
                ValidationSeverity.Warning);
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
/// 覆盖：服务任务字段注入（<c>activiti:field</c>）—— 执行/任务/事件监听器已由引擎执行
/// （见引擎侧 <c>ListenerInvoker</c> / <c>EngineEventDispatcher</c>），从本规则移出；
/// 它们的形状校验见 <see cref="ListenerConfigurationRule"/>。
/// </para>
/// </summary>
public sealed class UnsupportedCapabilityRule : IProcessValidationRule
{
    public string Name => "unsupported-capability";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // 服务任务的字段注入已支持（B2.1）；监听器的字段注入仍属未实现 ——
        // 解析进模型但不执行，静默忽略是最危险的一类差异，这里在部署期显式拒绝
        // （监听器可挂在任意元素上，逐元素扫执行监听器）
        foreach (var element in process.FindFlowElementsOfType<FlowElement>())
        {
            foreach (var listener in element.ExecutionListeners)
            {
                if (listener.Fields.Count > 0)
                {
                    yield return new ValidationError(
                        Name,
                        element.Id,
                        element.GetType().Name,
                        $"执行监听器 '{Describe(listener)}' 的字段注入（activiti:field）尚未实现，拒绝部署。");
                }
            }
        }

        foreach (var userTask in process.FindFlowElementsOfType<UserTask>())
        {
            foreach (var listener in userTask.TaskListeners)
            {
                if (listener.Fields.Count > 0)
                {
                    yield return new ValidationError(
                        Name,
                        userTask.Id,
                        nameof(UserTask),
                        $"任务监听器 '{Describe(listener)}' 的字段注入（activiti:field）尚未实现，拒绝部署。");
                }
            }
        }
    }

    private static string Describe(NetBpm.Bpmn.Model.ExecutionListener listener) =>
        listener.Expression ?? listener.DelegateExpression ?? listener.ClassName ?? "(未配置实现)";
}

/// <summary>
/// 规则 11：多实例（会签 / 串签）只允许配在用户任务上，且实例来源必须明确。
/// <para>
/// 原因：本子集的多实例语义按「审批会签」设计（<c>MultiInstanceUserTaskBehavior</c>）——
/// 服务任务等多实例可以改用「委托循环 / 多条并行流」表达，部署期显式拒绝而不是静默按单实例执行。
/// </para>
/// </summary>
public sealed class MultiInstanceRule : IProcessValidationRule
{
    public string Name => "multi-instance";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var serviceTask in process.FindFlowElementsOfType<ServiceTask>())
        {
            if (serviceTask.LoopCharacteristics is not null)
            {
                yield return new ValidationError(
                    Name,
                    serviceTask.Id,
                    nameof(ServiceTask),
                    "多实例（multiInstanceLoopCharacteristics）只支持用户任务。");
            }
        }

        foreach (var scriptTask in process.FindFlowElementsOfType<ScriptTask>())
        {
            if (scriptTask.LoopCharacteristics is not null)
            {
                yield return new ValidationError(
                    Name,
                    scriptTask.Id,
                    nameof(ScriptTask),
                    "多实例（multiInstanceLoopCharacteristics）只支持用户任务。");
            }
        }

        foreach (var manualTask in process.FindFlowElementsOfType<ManualTask>())
        {
            if (manualTask.LoopCharacteristics is not null)
            {
                yield return new ValidationError(
                    Name,
                    manualTask.Id,
                    nameof(ManualTask),
                    "多实例（multiInstanceLoopCharacteristics）只支持用户任务。");
            }
        }

        foreach (var userTask in process.FindFlowElementsOfType<UserTask>())
        {
            var mi = userTask.LoopCharacteristics;

            if (mi is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(mi.LoopCardinality) && string.IsNullOrWhiteSpace(mi.Collection))
            {
                yield return new ValidationError(
                    Name,
                    userTask.Id,
                    nameof(UserTask),
                    "多实例必须指定实例数来源：<loopCardinality> 或 activiti:collection 之一。");
            }

            if (!string.IsNullOrWhiteSpace(userTask.SkipExpression))
            {
                // skipExpression 会在单实例行为里「删任务 + 直接离开」，与多实例的实例计数冲突
                yield return new ValidationError(
                    Name,
                    userTask.Id,
                    nameof(UserTask),
                    "多实例用户任务不支持 activiti:skipExpression（两者语义冲突），请去掉其一。");
            }
        }
    }
}

/// <summary>
/// 规则 10：脚本任务必须有脚本内容，且只支持 csharp 脚本格式（Roslyn Scripting）。
/// <para>
/// Activiti 对缺省 <c>scriptFormat</c> 回退到 JavaScript —— 本子集不内置 JS 引擎，
/// 因此「缺省 / 其它格式」在<b>部署期</b>显式拒绝，而不是等运行期才发现跑不了。
/// </para>
/// </summary>
public sealed class ScriptTaskRule : IProcessValidationRule
{
    public string Name => "script-task";

    /// <summary>接受的大小写不敏感格式清单，与引擎侧 <c>ScriptTaskEngine.AcceptedFormats</c> 同源。</summary>
    private static readonly string[] AcceptedFormats = ["csharp", "c#"];

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var scriptTask in process.FindFlowElementsOfType<ScriptTask>())
        {
            if (string.IsNullOrWhiteSpace(scriptTask.Script))
            {
                yield return new ValidationError(
                    Name,
                    scriptTask.Id,
                    nameof(ScriptTask),
                    "脚本任务缺少 <script> 脚本内容。");

                continue;
            }

            if (scriptTask.ScriptFormat is null ||
                !AcceptedFormats.Contains(scriptTask.ScriptFormat.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                yield return new ValidationError(
                    Name,
                    scriptTask.Id,
                    nameof(ScriptTask),
                    $"脚本任务只支持 scriptFormat=\"csharp\"（实际：'{scriptTask.ScriptFormat ?? "(未指定)"}'）。");
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

/// <summary>
/// 规则 12：用户任务没有任何办理人来源（<b>提示级</b>）。
/// <para>
/// 与已移除的「用户任务必须指定办理人」硬性规则不同：Activiti 允许无人办理的任务
/// （任务照常创建、只是没人认领），本引擎同样能执行，因此这里<b>只提示</b>。
/// </para>
/// <para>
/// 提示的价值在于可发现性：没有 assignee / candidateUsers / candidateGroups 时，
/// 任务只能通过 <c>TaskQuery.processInstanceId</c> 查到 —— 按办理人 / 候选人查询永远查不到，
/// 这通常是设计时漏配，而不是有意为之。
/// </para>
/// <para>
/// 多实例任务不检查：实例的办理人由 <c>collection</c> / <c>loopCardinality</c> 决定（见 <c>MultiInstanceRule</c>）。
/// </para>
/// </summary>
public sealed class UserTaskAssignmentRule : IProcessValidationRule
{
    public string Name => "user-task-assignment";

    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var userTask in process.FindFlowElementsOfType<UserTask>())
        {
            if (userTask.LoopCharacteristics is not null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(userTask.Assignee)
                || userTask.CandidateUsers.Count > 0
                || userTask.CandidateGroups.Count > 0)
            {
                continue;
            }

            yield return new ValidationError(
                Name,
                userTask.Id,
                nameof(UserTask),
                "用户任务没有指定办理人（activiti:assignee）或候选人 / 候选组：" +
                "任务仍会创建（Activiti 同语义），但按办理人 / 候选人查不到，只能按流程实例查。",
                ValidationSeverity.Warning);
        }
    }
}

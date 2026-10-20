using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 规则：流程 id（key）必须非空；不符合标识符习惯时给出提示。
/// <para>
/// 空 id 会导致落库/启动直接失败，属于阻断级。含冒号等字符的 id 在 Activiti 里是合法的
/// （对拍语料里就有 <c>myProcess{0}</c> 这类模板 id），因此只提示、不阻断 —— 但流程定义 id
/// 形如 <c>{key}:{version}:{id}</c>，key 里带冒号会让人工排查时分段困难，设计器也按更严的
/// 标识符口径提示。
/// </para>
/// </summary>
public sealed class ProcessIdRule : IProcessValidationRule
{
    /// <inheritdoc />
    public string Name => "process-id";

    /// <inheritdoc />
    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (string.IsNullOrWhiteSpace(process.Id))
        {
            yield return new ValidationError(
                Name,
                process.Id,
                nameof(Process),
                "流程 id（key）不能为空：启动实例与部署都按它定位流程。");

            yield break;
        }

        if (!IsValidKey(process.Id))
        {
            yield return new ValidationError(
                Name,
                process.Id,
                nameof(Process),
                $"流程 id（key）'{process.Id}' 不符合标识符习惯：建议以字母或下划线开头，只含字母、数字、下划线与短横" +
                "（流程定义 id 形如 {key}:{version}:{id}，key 里带冒号会让排查与脚本处理变困难）。",
                ValidationSeverity.Warning);
        }
    }

    /// <summary>与设计器 <c>validateElementId</c> 同一口径（不使用正则，避免分析器告警）。</summary>
    internal static bool IsValidKey(string value)
    {
        if (value.Length == 0 || (!char.IsAsciiLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 规则：<c>default</c> 必须指向本元素的<b>出流</b>，且默认流上不应写条件。
/// <para>
/// 过去只判断 <c>DefaultFlow is not null</c>：写错 id、指向别的节点的流都能部署通过，
/// 运行期才发现「默认流永远不被选中」。条件写在默认流上是 Activiti 明确忽略的配置，
/// 属于「写了也不生效」，这里给提示。
/// </para>
/// </summary>
public sealed class DefaultFlowRule : IProcessValidationRule
{
    /// <inheritdoc />
    public string Name => "default-flow";

    /// <inheritdoc />
    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var gateway in process.FindFlowElementsOfType<Gateway>())
        {
            // 并行/事件网关的 default 由各自规则处理（并行：不生效；事件：整类拒绝）
            var warnOnCondition = gateway is ExclusiveGateway or InclusiveGateway;
            foreach (var error in ValidateDefault(gateway.Id, nameof(Gateway), gateway.DefaultFlow, gateway.OutgoingFlows, warnOnCondition))
            {
                yield return error;
            }
        }

        foreach (var activity in process.FindFlowElementsOfType<Activity>())
        {
            foreach (var error in ValidateDefault(activity.Id, activity.GetType().Name, activity.DefaultFlow, activity.OutgoingFlows, warnOnCondition: true))
            {
                yield return error;
            }
        }
    }

    private IEnumerable<ValidationError> ValidateDefault(
        string? elementId,
        string elementType,
        string? defaultFlowId,
        IList<SequenceFlow> outgoingFlows,
        bool warnOnCondition)
    {
        if (string.IsNullOrWhiteSpace(defaultFlowId))
        {
            yield break;
        }

        var defaultFlow = outgoingFlows.FirstOrDefault(
            flow => string.Equals(flow.Id, defaultFlowId, StringComparison.Ordinal));

        if (defaultFlow is null)
        {
            yield return new ValidationError(
                Name,
                elementId,
                elementType,
                $"默认流 '{defaultFlowId}' 不是该节点的出流（或 id 拼写错误）：运行期永远不会选中它。");

            yield break;
        }

        if (warnOnCondition && !string.IsNullOrWhiteSpace(defaultFlow.ConditionExpression))
        {
            yield return new ValidationError(
                Name,
                elementId,
                elementType,
                $"默认流 '{defaultFlowId}' 上写了条件：Activiti 语义下默认流不参与条件判定，该条件不会生效。",
                ValidationSeverity.Warning);
        }
    }
}

/// <summary>
/// 规则：事件定义必须在支持清单内。
/// <para>
/// 支持：结束事件的 <c>terminateEventDefinition</c>、边界事件的 <c>timerEventDefinition</c>。
/// 其余（定时/消息启动、信号、错误、补偿……）解析得进来，但部署期显式拒绝 ——
/// 绝不出现「部署成功但语义丢失」。
/// </para>
/// </summary>
public sealed class UnsupportedEventDefinitionRule : IProcessValidationRule
{
    /// <inheritdoc />
    public string Name => "unsupported-event-definition";

    /// <inheritdoc />
    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var startEvent in process.FindFlowElementsOfType<StartEvent>())
        {
            foreach (var definition in startEvent.EventDefinitions)
            {
                yield return Error(startEvent.Id, nameof(StartEvent), definition);
            }
        }

        foreach (var endEvent in process.FindFlowElementsOfType<EndEvent>())
        {
            foreach (var definition in endEvent.EventDefinitions)
            {
                // 终止结束事件已支持；其余仍拒绝
                if (string.Equals(definition, BpmnConstants.ElementTerminateEventDefinition, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return Error(endEvent.Id, nameof(EndEvent), definition);
            }
        }

        foreach (var boundaryEvent in process.FindFlowElementsOfType<BoundaryEvent>())
        {
            foreach (var definition in boundaryEvent.EventDefinitions)
            {
                if (string.Equals(definition, BpmnConstants.ElementTimerEventDefinition, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return Error(boundaryEvent.Id, nameof(BoundaryEvent), definition);
            }

            // 一个边界事件挂多个定时器会让「哪个先到点」变得不可预测
            if (boundaryEvent.HasMultipleEventDefinitions)
            {
                yield return new ValidationError(
                    Name,
                    boundaryEvent.Id,
                    nameof(BoundaryEvent),
                    "一个边界事件只能有一个事件定义（多个定时器请拆成多个边界事件）。");
            }
        }
    }

    private ValidationError Error(string? elementId, string elementType, string definition) =>
        new(
            Name,
            elementId,
            elementType,
            $"事件定义 <{definition}> 不在本子集支持范围内：只支持结束事件的 terminateEventDefinition " +
            "与边界事件的 timerEventDefinition（定时、消息、信号、错误等语义不会被引擎执行）。");
}

/// <summary>
/// 规则：不认识的本子集 <c>activiti:*</c> 属性给出提示，而不是静默忽略。
/// <para>
/// 典型例子：<c>activiti:asyncBefore</c> / <c>activiti:asyncAfter</c> / <c>activiti:exclusive</c>。
/// 引擎按同步执行，写这些属性不会报错也不会生效 —— 属于最危险的一类「配置了但没做」。
/// 这里不阻断（Activiti 模型可能带无关属性），但必须让建模者看见。
/// </para>
/// </summary>
public sealed class UnknownActivitiAttributeRule : IProcessValidationRule
{
    /// <summary>Activiti 扩展命名空间（与转换器 <c>BpmnConstants.ActivitiNamespace</c> 同值）。</summary>
    private const string ActivitiNamespace = "http://activiti.org/bpmn";

    /// <inheritdoc />
    public string Name => "unknown-activiti-attribute";

    /// <inheritdoc />
    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        foreach (var error in Check(process.Id, nameof(Process), process.Attributes))
        {
            yield return error;
        }

        foreach (var element in process.FlowElements.OfType<BaseElement>())
        {
            foreach (var error in Check(element.Id, element.GetType().Name, element.Attributes))
            {
                yield return error;
            }
        }
    }

    private IEnumerable<ValidationError> Check(
        string? elementId,
        string elementType,
        IDictionary<string, IList<ExtensionAttribute>> attributes)
    {
        foreach (var attribute in attributes.Values.SelectMany(list => list))
        {
            if (!string.Equals(attribute.NamespaceUri, ActivitiNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new ValidationError(
                Name,
                elementId,
                elementType,
                $"属性 activiti:{attribute.Name}=\"{attribute.Value}\" 不在本子集支持范围，引擎会忽略它（不会报错也不会生效）。",
                ValidationSeverity.Warning);
        }
    }
}

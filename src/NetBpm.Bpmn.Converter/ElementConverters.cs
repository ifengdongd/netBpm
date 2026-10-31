using System.Xml;
using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Converter;

/// <summary>开始事件（<c>startEvent</c>）转换器。对应 Activiti 的 <c>StartEventXMLConverter</c>。</summary>
public sealed class StartEventXmlConverter : BaseBpmnXmlConverter<StartEvent>
{
    private static readonly HashSet<string> KnownAttributes = new(StringComparer.Ordinal) { "initiator" };

    public override string ElementName => "startEvent";

    protected override IReadOnlySet<string> KnownActivitiAttributes => KnownAttributes;

    protected override StartEvent CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, StartEvent element, Process process)
    {
        // 发起人变量名（activiti:initiator）—— 实例启动时引擎把认证用户写进该变量
        element.Initiator = Attr(reader, "initiator", BpmnConstants.ActivitiNamespace);

        // 定时/消息等启动事件仍不支持：记录下来，由校验层显式拒绝，
        // 而不是只认属性、把 <timerEventDefinition/> 静默丢掉。
        ReadEventDefinitions(reader, element);
    }

    protected override void ConvertElementToXml(XmlWriter writer, StartEvent element)
    {
        writer.WriteStartElement("startEvent", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteActivitiAttribute(writer, "initiator", element.Initiator);
        WriteExtensionElements(writer, element);
        WriteEventDefinitions(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>结束事件（<c>endEvent</c>）转换器。对应 Activiti 的 <c>EndEventXMLConverter</c>。</summary>
public sealed class EndEventXmlConverter : BaseBpmnXmlConverter<EndEvent>
{
    public override string ElementName => "endEvent";

    protected override EndEvent CreateElement() => new();

    // 终止结束事件（terminateEventDefinition）已支持；其它事件定义记录下来交给校验层拒绝
    protected override void ConvertXmlToElement(XmlReader reader, EndEvent element, Process process) =>
        ReadEventDefinitions(reader, element);

    protected override void ConvertElementToXml(XmlWriter writer, EndEvent element)
    {
        writer.WriteStartElement("endEvent", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteExtensionElements(writer, element);
        WriteEventDefinitions(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>
/// 边界事件（<c>boundaryEvent</c>）转换器。
/// <para>
/// 只支持挂在用户任务上的定时边界事件；解析时把 <c>attachedToRef</c> 原样记下，
/// 由 <c>BpmnXmlConverter</c> 在解析末期与 <c>BpmnModel</c> 接线（与顺序流同一时机）。
/// </para>
/// </summary>
public sealed class BoundaryEventXmlConverter : BaseBpmnXmlConverter<BoundaryEvent>
{
    public override string ElementName => BpmnConstants.ElementBoundaryEvent;

    protected override BoundaryEvent CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, BoundaryEvent element, Process process)
    {
        element.AttachedToRef = Attr(reader, BpmnConstants.AttributeAttachedToRef);

        // cancelActivity 缺省为 true（BPMN 默认值）
        element.CancelActivity = !string.Equals(
            Attr(reader, BpmnConstants.AttributeCancelActivity),
            "false",
            StringComparison.OrdinalIgnoreCase);

        ReadEventDefinitions(reader, element);
    }

    protected override void ConvertElementToXml(XmlWriter writer, BoundaryEvent element)
    {
        writer.WriteStartElement(BpmnConstants.ElementBoundaryEvent, BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeAttachedToRef, element.AttachedToRef);
        writer.WriteAttributeString(
            BpmnConstants.AttributeCancelActivity,
            element.CancelActivity ? "true" : "false");
        WriteExtensionElements(writer, element);
        WriteEventDefinitions(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>排他网关（<c>exclusiveGateway</c>）转换器。对应 Activiti 的 <c>ExclusiveGatewayXMLConverter</c>。</summary>
public sealed class ExclusiveGatewayXmlConverter : BaseBpmnXmlConverter<ExclusiveGateway>
{
    public override string ElementName => "exclusiveGateway";

    protected override ExclusiveGateway CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, ExclusiveGateway element, Process process)
    {
        element.DefaultFlow = Attr(reader, "default");
        ReadChildren(reader, element, _ => { });
    }

    protected override void ConvertElementToXml(XmlWriter writer, ExclusiveGateway element)
    {
        writer.WriteStartElement("exclusiveGateway", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteAttributeIfPresent(writer, "default", element.DefaultFlow);
        WriteExtensionElements(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>并行网关（<c>parallelGateway</c>）转换器。对应 Activiti 的 <c>ParallelGatewayXMLConverter</c>。</summary>
public sealed class ParallelGatewayXmlConverter : BaseBpmnXmlConverter<ParallelGateway>
{
    public override string ElementName => "parallelGateway";

    protected override ParallelGateway CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, ParallelGateway element, Process process)
    {
        element.DefaultFlow = Attr(reader, "default");
        ReadChildren(reader, element, _ => { });
    }

    protected override void ConvertElementToXml(XmlWriter writer, ParallelGateway element)
    {
        writer.WriteStartElement("parallelGateway", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteAttributeIfPresent(writer, "default", element.DefaultFlow);
        WriteExtensionElements(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>包含网关（<c>inclusiveGateway</c>）转换器。对应 Activiti 的 <c>InclusiveGatewayXMLConverter</c>。</summary>
public sealed class InclusiveGatewayXmlConverter : BaseBpmnXmlConverter<InclusiveGateway>
{
    public override string ElementName => "inclusiveGateway";

    protected override InclusiveGateway CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, InclusiveGateway element, Process process)
    {
        element.DefaultFlow = Attr(reader, "default");
        ReadChildren(reader, element, _ => { });
    }

    protected override void ConvertElementToXml(XmlWriter writer, InclusiveGateway element)
    {
        writer.WriteStartElement("inclusiveGateway", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteAttributeIfPresent(writer, "default", element.DefaultFlow);
        WriteExtensionElements(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>
/// 事件网关（<c>eventBasedGateway</c>）转换器。对应 Activiti 的 <c>EventGatewayXMLConverter</c>。
/// <para>只做往返解析；执行不被支持（见 <see cref="InclusiveGateway"/> 同级的模型注释与校验规则）。</para>
/// </summary>
public sealed class EventGatewayXmlConverter : BaseBpmnXmlConverter<EventGateway>
{
    public override string ElementName => "eventBasedGateway";

    protected override EventGateway CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, EventGateway element, Process process) =>
        ReadChildren(reader, element, _ => { });

    protected override void ConvertElementToXml(XmlWriter writer, EventGateway element)
    {
        writer.WriteStartElement("eventBasedGateway", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteExtensionElements(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>顺序流（<c>sequenceFlow</c>）转换器。对应 Activiti 的 <c>SequenceFlowXMLConverter</c>。</summary>
public sealed class SequenceFlowXmlConverter : BaseBpmnXmlConverter<SequenceFlow>
{
    public override string ElementName => "sequenceFlow";

    protected override SequenceFlow CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, SequenceFlow element, Process process)
    {
        element.SourceRef = Attr(reader, "sourceRef");
        element.TargetRef = Attr(reader, "targetRef");

        ReadChildren(reader, element, child =>
        {
            if (child.LocalName == "conditionExpression")
            {
                element.ConditionExpression = ReadElementText(child);
            }
        });
    }

    protected override void ConvertElementToXml(XmlWriter writer, SequenceFlow element)
    {
        writer.WriteStartElement("sequenceFlow", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteAttributeIfPresent(writer, "sourceRef", element.SourceRef);
        WriteAttributeIfPresent(writer, "targetRef", element.TargetRef);
        WriteExtensionElements(writer, element);

        if (!string.IsNullOrEmpty(element.ConditionExpression))
        {
            writer.WriteStartElement("conditionExpression", BpmnConstants.BpmnNamespace);
            writer.WriteAttributeString("xsi", "type", BpmnConstants.XsiNamespace, "tFormalExpression");
            writer.WriteString(element.ConditionExpression);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }
}

/// <summary>用户任务（<c>userTask</c>）转换器。对应 Activiti 的 <c>UserTaskXMLConverter</c>。</summary>
public sealed class UserTaskXmlConverter : BaseBpmnXmlConverter<UserTask>
{
    private static readonly HashSet<string> KnownAttributes = new(StringComparer.Ordinal)
    {
        "assignee",
        "owner",
        "candidateUsers",
        "candidateGroups",
        "formKey",
        "dueDate",
        "priority",
        "category",
        "skipExpression",
        "asyncBefore",
        "asyncAfter",
        "exclusive",
    };

    public override string ElementName => "userTask";

    protected override IReadOnlySet<string> KnownActivitiAttributes => KnownAttributes;

    protected override UserTask CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, UserTask element, Process process)
    {
        element.Assignee = Attr(reader, BpmnConstants.AttributeAssignee, BpmnConstants.ActivitiNamespace);
        element.Owner = Attr(reader, "owner", BpmnConstants.ActivitiNamespace);
        element.FormKey = Attr(reader, BpmnConstants.AttributeFormKey, BpmnConstants.ActivitiNamespace);
        element.DueDate = Attr(reader, BpmnConstants.AttributeDueDate, BpmnConstants.ActivitiNamespace);
        element.Priority = Attr(reader, BpmnConstants.AttributePriority, BpmnConstants.ActivitiNamespace);
        element.Category = Attr(reader, "category", BpmnConstants.ActivitiNamespace);
        element.SkipExpression = Attr(reader, BpmnConstants.AttributeSkipExpression, BpmnConstants.ActivitiNamespace);
        ReadActivityAttributes(reader, element);

        FillCsv(element.CandidateUsers, Attr(reader, BpmnConstants.AttributeCandidateUsers, BpmnConstants.ActivitiNamespace));
        FillCsv(element.CandidateGroups, Attr(reader, BpmnConstants.AttributeCandidateGroups, BpmnConstants.ActivitiNamespace));

        // extensionElements（任务监听器 / 字段注入）由基类的 ReadChildren 集中处理；
        // 办理关系还有 BPMN 标准写法（<humanPerformer> / <potentialOwner>，见下）
        ReadChildren(reader, element, child =>
        {
            switch (child.LocalName)
            {
                case "multiInstanceLoopCharacteristics":
                    element.LoopCharacteristics = ReadMultiInstanceLoopCharacteristics(child);
                    break;

                case "humanPerformer":
                    {
                        // 对应 UserTaskXMLConverter.HumanPerformerParser：
                        // <humanPerformer><resourceAssignmentExpression><formalExpression>fozzie</formalExpression>
                        var expression = ReadFormalExpression(child);

                        if (!string.IsNullOrEmpty(expression))
                        {
                            element.Assignee = expression;
                        }

                        break;
                    }

                case "potentialOwner":
                    {
                        // 对应 PotentialOwnerParser：按逗号拆，user(x) ⇒ 候选用户，
                        // group(y) 与**裸名** ⇒ 候选组（Activiti 把无法识别的都归候选组）
                        var expression = ReadFormalExpression(child);

                        if (string.IsNullOrEmpty(expression))
                        {
                            break;
                        }

                        foreach (var assignment in expression.Split(','))
                        {
                            var value = assignment.Trim();

                            if (value.Length == 0)
                            {
                                continue;
                            }

                            if (value.StartsWith("user(", StringComparison.Ordinal) && value.EndsWith(')'))
                            {
                                element.CandidateUsers.Add(value[5..^1].Trim());
                            }
                            else if (value.StartsWith("group(", StringComparison.Ordinal) && value.EndsWith(')'))
                            {
                                element.CandidateGroups.Add(value[6..^1].Trim());
                            }
                            else
                            {
                                element.CandidateGroups.Add(value);
                            }
                        }

                        break;
                    }
            }
        });
    }

    /// <summary>
    /// 读 <c>resourceAssignmentExpression/formalExpression</c> 的文本。
    /// 对应 Activiti 的 <c>XMLStreamReaderUtil.moveDown</c> 两跳。
    /// </summary>
    private static string? ReadFormalExpression(XmlReader reader)
    {
        if (!reader.ReadToDescendant("resourceAssignmentExpression"))
        {
            return null;
        }

        if (!reader.ReadToDescendant("formalExpression"))
        {
            return null;
        }

        var text = reader.ReadElementContentAsString();

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    protected override void ConvertElementToXml(XmlWriter writer, UserTask element)
    {
        writer.WriteStartElement("userTask", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteActivityAttributes(writer, element);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeAssignee, element.Assignee);
        WriteActivitiAttribute(writer, "owner", element.Owner);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeCandidateUsers, string.Join(',', element.CandidateUsers));
        WriteActivitiAttribute(writer, BpmnConstants.AttributeCandidateGroups, string.Join(',', element.CandidateGroups));
        WriteActivitiAttribute(writer, BpmnConstants.AttributeFormKey, element.FormKey);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeDueDate, element.DueDate);
        WriteActivitiAttribute(writer, BpmnConstants.AttributePriority, element.Priority);
        WriteActivitiAttribute(writer, "category", element.Category);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeSkipExpression, element.SkipExpression);
        WriteExtensionElements(writer, element);
        WriteMultiInstanceLoopCharacteristics(writer, element.LoopCharacteristics);
        writer.WriteEndElement();
    }

    private static void FillCsv(IList<string> target, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return;
        }

        foreach (var item in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            target.Add(item);
        }
    }
}

/// <summary>服务任务（<c>serviceTask</c>）转换器。对应 Activiti 的 <c>ServiceTaskXMLConverter</c>。</summary>
public sealed class ServiceTaskXmlConverter : BaseBpmnXmlConverter<ServiceTask>
{
    private static readonly HashSet<string> KnownAttributes = new(StringComparer.Ordinal)
    {
        "class",
        "delegateExpression",
        "expression",
        "resultVariableName",
        "topic",
        "asyncBefore",
        "asyncAfter",
        "exclusive",
    };

    public override string ElementName => "serviceTask";

    protected override IReadOnlySet<string> KnownActivitiAttributes => KnownAttributes;

    protected override ServiceTask CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, ServiceTask element, Process process)
    {
        element.ClassName = Attr(reader, BpmnConstants.AttributeClass, BpmnConstants.ActivitiNamespace);
        element.DelegateExpression = Attr(reader, BpmnConstants.AttributeDelegateExpression, BpmnConstants.ActivitiNamespace);
        element.Expression = Attr(reader, BpmnConstants.AttributeExpression, BpmnConstants.ActivitiNamespace);
        element.ResultVariableName = Attr(reader, BpmnConstants.AttributeResultVariableName, BpmnConstants.ActivitiNamespace);
        element.Topic = Attr(reader, BpmnConstants.AttributeTopic, BpmnConstants.ActivitiNamespace);
        ReadActivityAttributes(reader, element);

        // 推导顺序即优先级；同时写了两种（如 class + topic）由 RequiredAttributesRule 拒绝部署，
        // 这里只保证「单一实现」时能落到正确的分支
        element.ImplementationType = element.ClassName is not null
            ? ServiceTaskImplementationType.Class
            : element.DelegateExpression is not null
                ? ServiceTaskImplementationType.DelegateExpression
                : element.Expression is not null
                    ? ServiceTaskImplementationType.Expression
                    : element.Topic is not null
                        ? ServiceTaskImplementationType.ExternalTopic
                        : ServiceTaskImplementationType.None;

        // extensionElements（任务监听器 / 字段注入）由基类的 ReadChildren 集中处理
        ReadChildren(reader, element, child =>
        {
            // 多实例解析进模型但引擎不支持 —— 由 MultiInstanceRule 部署期拒绝
            if (child.LocalName == "multiInstanceLoopCharacteristics")
            {
                element.LoopCharacteristics = ReadMultiInstanceLoopCharacteristics(child);
            }
        });
    }

    protected override void ConvertElementToXml(XmlWriter writer, ServiceTask element)
    {
        writer.WriteStartElement("serviceTask", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteActivityAttributes(writer, element);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeClass, element.ClassName);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeDelegateExpression, element.DelegateExpression);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeExpression, element.Expression);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeResultVariableName, element.ResultVariableName);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeTopic, element.Topic);
        WriteExtensionElements(writer, element);
        WriteMultiInstanceLoopCharacteristics(writer, element.LoopCharacteristics);
        writer.WriteEndElement();
    }
}

/// <summary>手工任务（<c>manualTask</c>）转换器。</summary>
public sealed class ManualTaskXmlConverter : BaseBpmnXmlConverter<ManualTask>
{
    private static readonly HashSet<string> KnownAttributes = new(StringComparer.Ordinal)
    {
        "asyncBefore",
        "asyncAfter",
        "exclusive",
    };

    public override string ElementName => "manualTask";

    protected override IReadOnlySet<string> KnownActivitiAttributes => KnownAttributes;

    protected override ManualTask CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, ManualTask element, Process process)
    {
        ReadActivityAttributes(reader, element);

        ReadChildren(reader, element, child =>
        {
            if (child.LocalName == "multiInstanceLoopCharacteristics")
            {
                element.LoopCharacteristics = ReadMultiInstanceLoopCharacteristics(child);
            }
        });
    }

    protected override void ConvertElementToXml(XmlWriter writer, ManualTask element)
    {
        writer.WriteStartElement("manualTask", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteActivityAttributes(writer, element);
        WriteExtensionElements(writer, element);
        WriteMultiInstanceLoopCharacteristics(writer, element.LoopCharacteristics);
        writer.WriteEndElement();
    }
}

/// <summary>脚本任务（<c>scriptTask</c>）转换器。执行实现属二期（见 §1.2）。</summary>
public sealed class ScriptTaskXmlConverter : BaseBpmnXmlConverter<ScriptTask>
{
    private static readonly HashSet<string> KnownAttributes = new(StringComparer.Ordinal)
    {
        "resultVariable",
        "autoStoreVariables",
        "asyncBefore",
        "asyncAfter",
        "exclusive",
    };

    public override string ElementName => "scriptTask";

    protected override IReadOnlySet<string> KnownActivitiAttributes => KnownAttributes;

    protected override ScriptTask CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, ScriptTask element, Process process)
    {
        element.ScriptFormat = Attr(reader, "scriptFormat");
        ReadActivityAttributes(reader, element);
        element.ResultVariable = Attr(reader, "resultVariable", BpmnConstants.ActivitiNamespace);
        element.AutoStoreVariables = string.Equals(
            Attr(reader, "autoStoreVariables", BpmnConstants.ActivitiNamespace),
            "true",
            StringComparison.OrdinalIgnoreCase);

        ReadChildren(reader, element, child =>
        {
            if (child.LocalName == "multiInstanceLoopCharacteristics")
            {
                element.LoopCharacteristics = ReadMultiInstanceLoopCharacteristics(child);
                return;
            }

            if (child.LocalName == "script")
            {
                element.Script = ReadElementText(child);
            }
        });
    }

    protected override void ConvertElementToXml(XmlWriter writer, ScriptTask element)
    {
        writer.WriteStartElement("scriptTask", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteAttributeIfPresent(writer, "scriptFormat", element.ScriptFormat);
        WriteActivityAttributes(writer, element);
        WriteActivitiAttribute(writer, "resultVariable", element.ResultVariable);
        WriteActivitiAttribute(writer, "autoStoreVariables", element.AutoStoreVariables ? "true" : null);
        WriteExtensionElements(writer, element);
        WriteMultiInstanceLoopCharacteristics(writer, element.LoopCharacteristics);
        writer.WriteStartElement("script", BpmnConstants.BpmnNamespace);
        writer.WriteString(element.Script ?? string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }
}

using System.Xml;
using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Converter;

/// <summary>开始事件（<c>startEvent</c>）转换器。对应 Activiti 的 <c>StartEventXMLConverter</c>。</summary>
public sealed class StartEventXmlConverter : BaseBpmnXmlConverter<StartEvent>
{
    public override string ElementName => "startEvent";

    protected override StartEvent CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, StartEvent element, Process process)
    {
        // 发起人变量名（activiti:initiator）—— 实例启动时引擎把认证用户写进该变量
        element.Initiator = Attr(reader, "initiator", BpmnConstants.ActivitiNamespace);

        // 子集内不支持任何事件定义（见 §1.3 第 3 条），出现即由校验层报错。
        ReadChildren(reader, element, _ => { });
    }

    protected override void ConvertElementToXml(XmlWriter writer, StartEvent element)
    {
        writer.WriteStartElement("startEvent", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteActivitiAttribute(writer, "initiator", element.Initiator);
        writer.WriteEndElement();
    }
}

/// <summary>结束事件（<c>endEvent</c>）转换器。对应 Activiti 的 <c>EndEventXMLConverter</c>。</summary>
public sealed class EndEventXmlConverter : BaseBpmnXmlConverter<EndEvent>
{
    public override string ElementName => "endEvent";

    protected override EndEvent CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, EndEvent element, Process process) =>
        ReadChildren(reader, element, _ => { });

    protected override void ConvertElementToXml(XmlWriter writer, EndEvent element)
    {
        writer.WriteStartElement("endEvent", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
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
    public override string ElementName => "userTask";

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
        element.DefaultFlow = Attr(reader, "default");

        FillCsv(element.CandidateUsers, Attr(reader, BpmnConstants.AttributeCandidateUsers, BpmnConstants.ActivitiNamespace));
        FillCsv(element.CandidateGroups, Attr(reader, BpmnConstants.AttributeCandidateGroups, BpmnConstants.ActivitiNamespace));

        // extensionElements（任务监听器 / 字段注入）由基类的 ReadChildren 集中处理；
        // 办理关系还有 BPMN 标准写法（<humanPerformer> / <potentialOwner>，见下）
        ReadChildren(reader, element, child =>
        {
            switch (child.LocalName)
            {
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
        WriteAttributeIfPresent(writer, "default", element.DefaultFlow);
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
    public override string ElementName => "serviceTask";

    protected override ServiceTask CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, ServiceTask element, Process process)
    {
        element.ClassName = Attr(reader, BpmnConstants.AttributeClass, BpmnConstants.ActivitiNamespace);
        element.DelegateExpression = Attr(reader, BpmnConstants.AttributeDelegateExpression, BpmnConstants.ActivitiNamespace);
        element.Expression = Attr(reader, BpmnConstants.AttributeExpression, BpmnConstants.ActivitiNamespace);
        element.ResultVariableName = Attr(reader, BpmnConstants.AttributeResultVariableName, BpmnConstants.ActivitiNamespace);
        element.DefaultFlow = Attr(reader, "default");

        element.ImplementationType = element.ClassName is not null
            ? ServiceTaskImplementationType.Class
            : element.DelegateExpression is not null
                ? ServiceTaskImplementationType.DelegateExpression
                : element.Expression is not null
                    ? ServiceTaskImplementationType.Expression
                    : ServiceTaskImplementationType.None;

        // extensionElements（任务监听器 / 字段注入）由基类的 ReadChildren 集中处理
        ReadChildren(reader, element, _ => { });
    }

    protected override void ConvertElementToXml(XmlWriter writer, ServiceTask element)
    {
        writer.WriteStartElement("serviceTask", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        WriteAttributeIfPresent(writer, "default", element.DefaultFlow);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeClass, element.ClassName);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeDelegateExpression, element.DelegateExpression);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeExpression, element.Expression);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeResultVariableName, element.ResultVariableName);
        WriteExtensionElements(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>手工任务（<c>manualTask</c>）转换器。</summary>
public sealed class ManualTaskXmlConverter : BaseBpmnXmlConverter<ManualTask>
{
    public override string ElementName => "manualTask";

    protected override ManualTask CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, ManualTask element, Process process) =>
        ReadChildren(reader, element, _ => { });

    protected override void ConvertElementToXml(XmlWriter writer, ManualTask element)
    {
        writer.WriteStartElement("manualTask", BpmnConstants.BpmnNamespace);
        WriteFlowElementAttributes(writer, element);
        writer.WriteEndElement();
    }
}

/// <summary>脚本任务（<c>scriptTask</c>）转换器。执行实现属二期（见 §1.2）。</summary>
public sealed class ScriptTaskXmlConverter : BaseBpmnXmlConverter<ScriptTask>
{
    public override string ElementName => "scriptTask";

    protected override ScriptTask CreateElement() => new();

    protected override void ConvertXmlToElement(XmlReader reader, ScriptTask element, Process process)
    {
        element.ScriptFormat = Attr(reader, "scriptFormat");
        element.ResultVariable = Attr(reader, "resultVariable", BpmnConstants.ActivitiNamespace);
        element.AutoStoreVariables = string.Equals(
            Attr(reader, "autoStoreVariables", BpmnConstants.ActivitiNamespace),
            "true",
            StringComparison.OrdinalIgnoreCase);

        ReadChildren(reader, element, child =>
        {
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
        WriteActivitiAttribute(writer, "resultVariable", element.ResultVariable);
        WriteActivitiAttribute(writer, "autoStoreVariables", element.AutoStoreVariables ? "true" : null);
        writer.WriteStartElement("script", BpmnConstants.BpmnNamespace);
        writer.WriteString(element.Script ?? string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }
}

using System.Xml;
using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Converter;

/// <summary>注册表使用的非泛型视图。</summary>
internal interface IBpmnElementXmlConverter
{
    string ElementName { get; }

    Type ElementType { get; }

    FlowElement Read(XmlReader reader, Process process);

    void Write(XmlWriter writer, FlowElement element);
}

/// <summary>
/// 单个 BPMN 元素的转换器基类。
/// <para>
/// 🔑 这是开发计划 §1.1 L2 要求的<b>「每个元素一个 Converter」分层</b>的落点 ——
/// 与 Activiti 的 <c>BaseBpmnXMLConverter</c> 一一对应，便于逐元素比对与补实现。
/// </para>
/// </summary>
/// <typeparam name="TElement">该转换器负责的元素类型。</typeparam>
public abstract class BaseBpmnXmlConverter<TElement> : IBpmnElementXmlConverter
    where TElement : FlowElement
{
    /// <summary>XML 本地元素名（如 <c>userTask</c>）。</summary>
    public abstract string ElementName { get; }

    /// <summary>创建空的元素实例。</summary>
    protected abstract TElement CreateElement();

    /// <summary>
    /// 解析元素自身的属性与子元素。
    /// <para>约定：进入时 <c>reader</c> 位于该元素的起始标签；返回时位于对应的结束标签
    /// （若为自闭合元素则不读取）。</para>
    /// </summary>
    protected abstract void ConvertXmlToElement(XmlReader reader, TElement element, Process process);

    /// <summary>把元素写回 XML。</summary>
    protected abstract void ConvertElementToXml(XmlWriter writer, TElement element);

    /// <summary>
    /// 本元素<b>实际实现</b>的 <c>activiti:*</c> 属性名。
    /// <para>
    /// 未列入的属性会被记到 <see cref="BaseElement.Attributes"/>，由
    /// <c>unknown-activiti-attribute</c> 规则给提示 —— 典型是 <c>asyncBefore</c> / <c>asyncAfter</c>
    /// 这类「引擎按同步执行、写了也不生效」的配置。不在这里直接报错，是因为模型校验只依赖
    /// 模型对象，拒绝与否统一由校验层裁定。
    /// </para>
    /// </summary>
    protected virtual IReadOnlySet<string> KnownActivitiAttributes => NoActivitiAttributes;

    private static readonly HashSet<string> NoActivitiAttributes = new(StringComparer.Ordinal);

    /// <inheritdoc />
    Type IBpmnElementXmlConverter.ElementType => typeof(TElement);

    /// <summary>供注册表调用的读入口。</summary>
    FlowElement IBpmnElementXmlConverter.Read(XmlReader reader, Process process)
    {
        var element = CreateElement();
        element.Id = reader.GetAttribute("id");
        element.Name = reader.GetAttribute("name");

        RecordUnknownActivitiAttributes(reader, element);

        ConvertXmlToElement(reader, element, process);
        return element;
    }

    /// <summary>
    /// 在读取自身属性之前，把本元素上未被任何转换器消费的 <c>activiti:*</c> 属性记进模型。
    /// <para>必须在 <see cref="ConvertXmlToElement"/> 之前调用：那时 reader 还停在起始标签上。</para>
    /// </summary>
    private void RecordUnknownActivitiAttributes(XmlReader reader, TElement element)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return;
        }

        do
        {
            if (string.Equals(reader.NamespaceURI, BpmnConstants.ActivitiNamespace, StringComparison.Ordinal)
                && !KnownActivitiAttributes.Contains(reader.LocalName))
            {
                element.AddAttribute(BpmnConstants.ActivitiNamespace, reader.LocalName, reader.Value);
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
    }

    /// <summary>供注册表调用的写入口。</summary>
    void IBpmnElementXmlConverter.Write(XmlWriter writer, FlowElement element) =>
        ConvertElementToXml(writer, (TElement)element);

    // ─────────────────────────── 通用读取工具 ───────────────────────────

    /// <summary>读取属性（含命名空间限定名）。</summary>
    protected static string? Attr(XmlReader reader, string localName, string? namespaceUri = null) =>
        namespaceUri is null ? reader.GetAttribute(localName) : reader.GetAttribute(localName, namespaceUri);

    /// <summary>
    /// 子元素扫描循环，并<b>集中处理 <c>extensionElements</c></b>。
    /// <para>
    /// 🔑 必须集中处理：执行监听器、任务监听器、字段注入都挂在 <c>extensionElements</c> 下，
    /// 而 BPMN 里<b>任何元素都可以带它</b>。若只让部分转换器处理，其它元素上的监听器会被静默丢弃 ——
    /// 那是最危险的一类差异（模型写了、引擎没做）。
    /// </para>
    /// </summary>
    protected static void ReadChildren(XmlReader reader, BaseElement element, Action<XmlReader> onChild)
    {
        ArgumentNullException.ThrowIfNull(element);

        ReadChildren(reader, child =>
        {
            if (string.Equals(child.LocalName, "extensionElements", StringComparison.Ordinal))
            {
                ReadExtensionElements(child, element);
                return;
            }

            onChild(child);
        });
    }

    /// <summary>
    /// 通用的子元素扫描循环：一直读到本元素的结束标签，期间把每个子元素交给
    /// <paramref name="onChild"/> 处理。<paramref name="onChild"/> 只需读取该子元素起始标签上的
    /// 属性；若需要读取文本内容，须自行消费到该子元素的结束标签。
    /// </summary>
    protected static void ReadChildren(XmlReader reader, Action<XmlReader> onChild)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var localName = reader.LocalName;
        var depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth && reader.LocalName == localName)
            {
                return;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                onChild(reader);
            }
        }
    }

    /// <summary>
    /// 读取事件上的<b>事件定义</b>子元素（定时 / 终止 / 消息 / 信号 / 错误……）。
    /// <para>
    /// 必须把它们<b>记下来</b>：整棵忽略会让「定时启动」的流程部署成功、语义却静默丢失。
    /// 记录后由校验层逐类裁定支持与否（本子集支持 <c>terminateEventDefinition</c> 与
    /// 边界事件上的 <c>timerEventDefinition</c>）。
    /// </para>
    /// </summary>
    protected static void ReadEventDefinitions(XmlReader reader, Event element)
    {
        ReadChildren(reader, element, child =>
        {
            if (!EventDefinitionElements.Contains(child.LocalName))
            {
                return;
            }

            if (!element.EventDefinitions.Contains(child.LocalName, StringComparer.Ordinal))
            {
                element.EventDefinitions.Add(child.LocalName);
            }

            if (string.Equals(child.LocalName, BpmnConstants.ElementTimerEventDefinition, StringComparison.Ordinal))
            {
                element.Timer = ReadTimerEventDefinition(child);
            }
        });

        element.HasEventDefinition = element.EventDefinitions.Count > 0;
        element.HasMultipleEventDefinitions = element.EventDefinitions.Count > 1;
    }

    /// <summary>读取 <c>timerEventDefinition</c> 的 timeDate / timeDuration / timeCycle 文本。</summary>
    private static TimerEventDefinition ReadTimerEventDefinition(XmlReader reader)
    {
        var timer = new TimerEventDefinition();

        ReadChildren(reader, child =>
        {
            switch (child.LocalName)
            {
                case BpmnConstants.ElementTimeDate:
                    timer.TimeDate = ReadElementText(child);
                    break;

                case BpmnConstants.ElementTimeDuration:
                    timer.TimeDuration = ReadElementText(child);
                    break;

                case BpmnConstants.ElementTimeCycle:
                    timer.TimeCycle = ReadElementText(child);
                    break;
            }
        });

        return timer;
    }

    /// <summary>
    /// 把事件定义写回 XML（往返保真）。
    /// <para>
    /// 过去只在读侧记录、写侧完全缺失 —— 设计器保存一次就会把 <c>terminateEventDefinition</c>
    /// 与定时配置永久丢掉。
    /// </para>
    /// </summary>
    protected static void WriteEventDefinitions(XmlWriter writer, Event element)
    {
        foreach (var definition in element.EventDefinitions)
        {
            writer.WriteStartElement(definition, BpmnConstants.BpmnNamespace);

            if (string.Equals(definition, BpmnConstants.ElementTimerEventDefinition, StringComparison.Ordinal)
                && element.Timer is { } timer)
            {
                WriteTextElementIfPresent(writer, BpmnConstants.ElementTimeDate, timer.TimeDate);
                WriteTextElementIfPresent(writer, BpmnConstants.ElementTimeDuration, timer.TimeDuration);
                WriteTextElementIfPresent(writer, BpmnConstants.ElementTimeCycle, timer.TimeCycle);
            }

            writer.WriteEndElement();
        }
    }

    private static void WriteTextElementIfPresent(XmlWriter writer, string localName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        writer.WriteStartElement(localName, BpmnConstants.BpmnNamespace);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static readonly HashSet<string> EventDefinitionElements = new(StringComparer.Ordinal)
    {
        "timerEventDefinition",
        "messageEventDefinition",
        "signalEventDefinition",
        "errorEventDefinition",
        "terminateEventDefinition",
        "conditionalEventDefinition",
        "escalationEventDefinition",
        "compensateEventDefinition",
        "linkEventDefinition",
        "cancelEventDefinition",
    };

    /// <summary>读取元素的文本内容（要求 <c>reader</c> 位于起始标签）。</summary>
    protected static string? ReadElementText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return null;
        }

        var localName = reader.LocalName;
        var depth = reader.Depth;
        string? text = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
            {
                text = (text ?? string.Empty) + reader.Value;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth && reader.LocalName == localName)
            {
                return text;
            }
        }

        return text;
    }

    /// <summary>解析 <c>extensionElements</c>（执行监听器 / 任务监听器 / 字段注入 / 表单属性）。</summary>
    /// <remarks><c>internal</c> 供 <see cref="BpmnXmlConverter"/> 读取流程级 extensionElements（记到 Process 上）。</remarks>
    internal static void ReadExtensionElements(XmlReader reader, BaseElement element)
    {
        if (element is UserTask userTask)
        {
            ReadExtensionElementsInto(reader, element, userTask.TaskListeners, userTask.FormProperties);
            return;
        }

        ReadExtensionElementsInto(reader, element, taskListeners: null, formProperties: null);
    }

    private static void ReadExtensionElementsInto(
        XmlReader reader,
        BaseElement element,
        IList<TaskListener>? taskListeners,
        IList<FormProperty>? formProperties)
    {
        ReadChildren(reader, child =>
        {
            var name = child.LocalName;

            if (name == BpmnConstants.ElementExecutionListener)
            {
                element.ExecutionListeners.Add(ReadListener(new ExecutionListener(), child));
            }
            else if (name == BpmnConstants.ElementTaskListener && taskListeners is not null)
            {
                taskListeners.Add((TaskListener)ReadListener(new TaskListener(), child));
            }
            else if (name == BpmnConstants.ElementField && element is ServiceTask serviceTask)
            {
                serviceTask.Fields.Add(ReadField(child));
            }
            else if (name == BpmnConstants.ElementFormProperty && formProperties is not null)
            {
                formProperties.Add(ReadFormProperty(child));
            }
            else if (name == BpmnConstants.ElementEventListener && element is Process process)
            {
                // 流程级事件监听器：记到 Process 上，运行期由引擎事件分发器逐个通知
                process.EventListeners.Add(ReadEventListener(child));
            }
        });
    }

    private static FormProperty ReadFormProperty(XmlReader reader)
    {
        var property = new FormProperty
        {
            Id = Attr(reader, "id"),
            Name = Attr(reader, "name"),
            Type = Attr(reader, "type"),
            Expression = Attr(reader, BpmnConstants.AttributeExpression),
            Variable = Attr(reader, "variable"),
            Default = Attr(reader, "default"),
            DatePattern = Attr(reader, "datePattern"),
            Required = OptionalBool(reader, "required"),
            Readable = OptionalBool(reader, "readable"),
            Writable = OptionalBool(reader, "writable"),
        };

        if (!reader.IsEmptyElement)
        {
            // 枚举选项 <activiti:value id name />
            ReadChildren(reader, child =>
            {
                if (child.LocalName == BpmnConstants.ElementFormPropertyValue)
                {
                    property.Values.Add(new FormPropertyValue
                    {
                        Id = Attr(child, "id"),
                        Name = Attr(child, "name"),
                    });
                }
            });
        }

        return property;
    }

    private static bool? OptionalBool(XmlReader reader, string attributeName)
    {
        var raw = Attr(reader, attributeName);

        return bool.TryParse(raw, out var value) ? value : null;
    }

    /// <summary>解析流程级 <c>activiti:eventListener</c>（属性无前缀，见 <see cref="ReadActivitiAttribute"/>）。</summary>
    private static EventListener ReadEventListener(XmlReader reader) =>
        new()
        {
            Events = ReadActivitiAttribute(reader, "events"),
            EntityType = ReadActivitiAttribute(reader, "entityType"),
            ClassName = ReadActivitiAttribute(reader, BpmnConstants.AttributeClass),
            Expression = ReadActivitiAttribute(reader, BpmnConstants.AttributeExpression),
            DelegateExpression = ReadActivitiAttribute(reader, BpmnConstants.AttributeDelegateExpression),
            ThrowOnFailure = OptionalBool(reader, "throwOnFailure"),
        };

    /// <summary>写出流程级 <c>activiti:eventListener</c>（属性无前缀，见 <see cref="WriteListener"/> 的说明）。</summary>
    internal static void WriteEventListener(XmlWriter writer, EventListener listener)
    {
        writer.WriteStartElement(BpmnConstants.ActivitiPrefix, BpmnConstants.ElementEventListener, BpmnConstants.ActivitiNamespace);
        WriteAttributeIfPresent(writer, "events", listener.Events);
        WriteAttributeIfPresent(writer, "entityType", listener.EntityType);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeClass, listener.ClassName);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeExpression, listener.Expression);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeDelegateExpression, listener.DelegateExpression);
        WriteBoolAttributeIfPresent(writer, "throwOnFailure", listener.ThrowOnFailure);
        writer.WriteEndElement();
    }

    private static ExecutionListener ReadListener(ExecutionListener listener, XmlReader reader)
    {
        // ⚠️ activiti:* 扩展元素自身的属性是**无前缀**的（Activiti 标准格式、bpmn-js 的写出形式）：
        // <activiti:executionListener event="start" class="A" />。
        // 历史落库 XML 里本转换器旧版曾写出 activiti:event 前缀形式，读不到时回退限定名。
        listener.Event = ReadActivitiAttribute(reader, "event");
        listener.ClassName = ReadActivitiAttribute(reader, BpmnConstants.AttributeClass);
        listener.Expression = ReadActivitiAttribute(reader, BpmnConstants.AttributeExpression);
        listener.DelegateExpression = ReadActivitiAttribute(reader, BpmnConstants.AttributeDelegateExpression);
        listener.ImplementationType = ReadActivitiAttribute(reader, "type");

        if (!reader.IsEmptyElement)
        {
            // 监听器内部只可能有 <activiti:field>；其文本内容由主循环跳过
            ReadChildren(reader, child =>
            {
                if (child.LocalName == BpmnConstants.ElementField)
                {
                    listener.Fields.Add(ReadField(child));
                }
            });
        }

        return listener;
    }

    /// <summary>
    /// 读 <c>activiti:*</c> 扩展元素的属性：优先无前缀形式（Activiti 标准），
    /// 回退 <c>activiti:</c> 限定形式（历史落库 XML）。
    /// </summary>
    private static string? ReadActivitiAttribute(XmlReader reader, string localName) =>
        Attr(reader, localName) ?? Attr(reader, localName, BpmnConstants.ActivitiNamespace);

    private static FieldExtension ReadField(XmlReader reader)
    {
        var field = new FieldExtension
        {
            FieldName = Attr(reader, "name"),
            StringValue = Attr(reader, "stringValue"),
            Expression = ReadActivitiAttribute(reader, BpmnConstants.AttributeExpression),
        };

        if (!reader.IsEmptyElement)
        {
            ReadChildren(reader, child =>
            {
                if (child.LocalName == "string" && !child.IsEmptyElement)
                {
                    var text = ReadElementText(child);
                    if (!string.IsNullOrEmpty(text))
                    {
                        field.StringValue = text;
                    }
                }
            });
        }

        return field;
    }

    // ─────────────────────────── 多实例（会签 / 串签，B1.2）───────────────────────────

    /// <summary>
    /// 读 <c>multiInstanceLoopCharacteristics</c> 子元素（任务转换器共用）。
    /// <para>
    /// 解析进模型（而非忽略）是为了「配置了但不支持」不被静默丢弃 —— 由
    /// <c>MultiInstanceRule</c> 在部署期裁定支持与否。
    /// </para>
    /// </summary>
    protected static MultiInstanceLoopCharacteristics? ReadMultiInstanceLoopCharacteristics(XmlReader reader)
    {
        var mi = new MultiInstanceLoopCharacteristics
        {
            IsSequential = string.Equals(Attr(reader, "isSequential"), "true", StringComparison.OrdinalIgnoreCase),
            Collection = Attr(reader, BpmnConstants.AttributeCollection, BpmnConstants.ActivitiNamespace),
            ElementVariable = Attr(reader, BpmnConstants.AttributeElementVariable, BpmnConstants.ActivitiNamespace),
        };

        ReadChildren(reader, child =>
        {
            switch (child.LocalName)
            {
                case "loopCardinality":
                    mi.LoopCardinality = ReadElementText(child);
                    break;

                case "completionCondition":
                    mi.CompletionCondition = ReadElementText(child);
                    break;
            }
        });

        return mi;
    }

    /// <summary>把 <c>multiInstanceLoopCharacteristics</c> 写回 XML（往返保真）。</summary>
    protected static void WriteMultiInstanceLoopCharacteristics(XmlWriter writer, MultiInstanceLoopCharacteristics? mi)
    {
        if (mi is null)
        {
            return;
        }

        writer.WriteStartElement("multiInstanceLoopCharacteristics", BpmnConstants.BpmnNamespace);
        writer.WriteAttributeString("isSequential", mi.IsSequential ? "true" : "false");
        WriteActivitiAttribute(writer, BpmnConstants.AttributeCollection, mi.Collection);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeElementVariable, mi.ElementVariable);

        if (!string.IsNullOrEmpty(mi.LoopCardinality))
        {
            writer.WriteStartElement("loopCardinality", BpmnConstants.BpmnNamespace);
            writer.WriteString(mi.LoopCardinality);
            writer.WriteEndElement();
        }

        if (!string.IsNullOrEmpty(mi.CompletionCondition))
        {
            writer.WriteStartElement("completionCondition", BpmnConstants.BpmnNamespace);
            writer.WriteString(mi.CompletionCondition);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    // ─────────────────────────── 通用写入工具 ───────────────────────────

    protected static void WriteFlowElementAttributes(XmlWriter writer, FlowElement element)
    {
        if (!string.IsNullOrEmpty(element.Id))
        {
            writer.WriteAttributeString("id", element.Id);
        }

        if (!string.IsNullOrEmpty(element.Name))
        {
            writer.WriteAttributeString("name", element.Name);
        }
    }

    protected static void WriteExtensionElements(XmlWriter writer, BaseElement element)
    {
        var taskListeners = (element as UserTask)?.TaskListeners;
        var hasTaskListeners = taskListeners is { Count: > 0 };
        var fields = (element as ServiceTask)?.Fields;
        var hasFields = fields is { Count: > 0 };
        var formProperties = (element as UserTask)?.FormProperties;
        var hasFormProperties = formProperties is { Count: > 0 };

        if (element.ExtensionElements.Count == 0
            && element.ExecutionListeners.Count == 0
            && !hasTaskListeners
            && !hasFields
            && !hasFormProperties)
        {
            return;
        }

        writer.WriteStartElement("extensionElements", BpmnConstants.BpmnNamespace);

        foreach (var listener in element.ExecutionListeners)
        {
            WriteListener(writer, BpmnConstants.ElementExecutionListener, listener);
        }

        if (hasTaskListeners)
        {
            foreach (var listener in taskListeners!)
            {
                WriteListener(writer, BpmnConstants.ElementTaskListener, listener);
            }
        }

        if (hasFields)
        {
            foreach (var field in fields!)
            {
                WriteField(writer, field);
            }
        }

        if (hasFormProperties)
        {
            foreach (var property in formProperties!)
            {
                WriteFormProperty(writer, property);
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteFormProperty(XmlWriter writer, FormProperty property)
    {
        writer.WriteStartElement(BpmnConstants.ActivitiPrefix, BpmnConstants.ElementFormProperty, BpmnConstants.ActivitiNamespace);
        WriteAttributeIfPresent(writer, "id", property.Id);
        WriteAttributeIfPresent(writer, "name", property.Name);
        WriteAttributeIfPresent(writer, "type", property.Type);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeExpression, property.Expression);
        WriteAttributeIfPresent(writer, "variable", property.Variable);
        WriteAttributeIfPresent(writer, "default", property.Default);
        WriteAttributeIfPresent(writer, "datePattern", property.DatePattern);
        WriteBoolAttributeIfPresent(writer, "required", property.Required);
        WriteBoolAttributeIfPresent(writer, "readable", property.Readable);
        WriteBoolAttributeIfPresent(writer, "writable", property.Writable);

        foreach (var value in property.Values)
        {
            writer.WriteStartElement(BpmnConstants.ActivitiPrefix, BpmnConstants.ElementFormPropertyValue, BpmnConstants.ActivitiNamespace);
            WriteAttributeIfPresent(writer, "id", value.Id);
            WriteAttributeIfPresent(writer, "name", value.Name);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>写入布尔属性（true/false）；值为 null（XML 未写出该属性）时跳过。</summary>
    private static void WriteBoolAttributeIfPresent(XmlWriter writer, string name, bool? value)
    {
        if (value is not null)
        {
            writer.WriteAttributeString(name, value.Value ? "true" : "false");
        }
    }

    private static void WriteField(XmlWriter writer, FieldExtension field)
    {
        writer.WriteStartElement(BpmnConstants.ActivitiPrefix, BpmnConstants.ElementField, BpmnConstants.ActivitiNamespace);
        WriteAttributeIfPresent(writer, "name", field.FieldName);
        WriteAttributeIfPresent(writer, "stringValue", field.StringValue);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeExpression, field.Expression);
        writer.WriteEndElement();
    }

    /// <summary>写入无命名空间限定的属性；值为空时跳过。</summary>
    protected static void WriteAttributeIfPresent(XmlWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteAttributeString(name, value);
        }
    }

    /// <summary>
    /// 写单个执行监听器（<c>activiti:executionListener</c>）。
    /// 供 BpmnXMLConverter 写流程级监听器（Process.ExecutionListeners）使用。
    /// </summary>
    internal static void WriteExecutionListener(XmlWriter writer, ExecutionListener listener) =>
        WriteListener(writer, BpmnConstants.ElementExecutionListener, listener);
    private static void WriteListener(XmlWriter writer, string elementName, ExecutionListener listener)
    {
        // ⚠️ activiti:* 扩展元素自身的属性按 Activiti 标准格式写出（**无前缀**），
        // 与 bpmn-js / Activiti 的解析口径一致 —— 前缀形式 activiti:event 反而读不回来。
        writer.WriteStartElement(BpmnConstants.ActivitiPrefix, elementName, BpmnConstants.ActivitiNamespace);
        WriteAttributeIfPresent(writer, "event", listener.Event);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeClass, listener.ClassName);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeExpression, listener.Expression);
        WriteAttributeIfPresent(writer, BpmnConstants.AttributeDelegateExpression, listener.DelegateExpression);
        writer.WriteEndElement();
    }

    /// <summary>写入 <c>activiti:</c> 命名空间属性；值为空时跳过（避免写出 <c>attr=""</c>）。</summary>
    protected static void WriteActivitiAttribute(XmlWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteAttributeString(BpmnConstants.ActivitiPrefix, name, BpmnConstants.ActivitiNamespace, value);
        }
    }
}

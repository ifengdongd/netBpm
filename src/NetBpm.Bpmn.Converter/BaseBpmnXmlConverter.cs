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

    /// <inheritdoc />
    Type IBpmnElementXmlConverter.ElementType => typeof(TElement);

    /// <summary>供注册表调用的读入口。</summary>
    FlowElement IBpmnElementXmlConverter.Read(XmlReader reader, Process process)
    {
        var element = CreateElement();
        element.Id = reader.GetAttribute("id");
        element.Name = reader.GetAttribute("name");

        ConvertXmlToElement(reader, element, process);
        return element;
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
    protected static void ReadExtensionElements(XmlReader reader, BaseElement element)
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

    private static ExecutionListener ReadListener(ExecutionListener listener, XmlReader reader)
    {
        listener.Event = Attr(reader, "event", BpmnConstants.ActivitiNamespace);
        listener.ClassName = Attr(reader, BpmnConstants.AttributeClass, BpmnConstants.ActivitiNamespace);
        listener.Expression = Attr(reader, BpmnConstants.AttributeExpression, BpmnConstants.ActivitiNamespace);
        listener.DelegateExpression = Attr(reader, BpmnConstants.AttributeDelegateExpression, BpmnConstants.ActivitiNamespace);
        listener.ImplementationType = Attr(reader, "type", BpmnConstants.ActivitiNamespace);

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

    private static FieldExtension ReadField(XmlReader reader)
    {
        var field = new FieldExtension
        {
            FieldName = Attr(reader, "name"),
            StringValue = Attr(reader, "stringValue"),
            Expression = Attr(reader, BpmnConstants.AttributeExpression, BpmnConstants.ActivitiNamespace),
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
        WriteActivitiAttribute(writer, BpmnConstants.AttributeExpression, field.Expression);
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

    private static void WriteListener(XmlWriter writer, string elementName, ExecutionListener listener)
    {
        writer.WriteStartElement(BpmnConstants.ActivitiPrefix, elementName, BpmnConstants.ActivitiNamespace);
        WriteActivitiAttribute(writer, "event", listener.Event);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeClass, listener.ClassName);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeExpression, listener.Expression);
        WriteActivitiAttribute(writer, BpmnConstants.AttributeDelegateExpression, listener.DelegateExpression);
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

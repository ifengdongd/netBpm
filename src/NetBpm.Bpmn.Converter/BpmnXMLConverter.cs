using System.Globalization;
using System.Xml;
using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Converter;

/// <summary>
/// BPMN 2.0 XML 转换器（子集实现）。对应 Activiti 的 <c>BpmnXMLConverter</c>。
/// <para>
/// 保持<b>流式</b>读写：<c>XmlReader</c> 顺序扫描 + <c>XmlWriter</c> 顺序落盘，
/// 不整树加载（开发计划 §3）。
/// </para>
/// <para>
/// 子集外元素默认<b>显式失败</b>，仅对 §1.3 第 8 条要求「容忍并忽略」的元素
/// （泳道、注释、数据对象等）放行。
/// </para>
/// </summary>
public sealed class BpmnXmlConverter : IBpmnXmlConverter
{
    /// <summary>
    /// 按 §1.3 第 8 条在解析时容忍并忽略的元素。
    /// ⚠️ <c>extensionElements</c> 不在此列：它被显式读取（监听器记到宿主元素上，交校验拦截），
    /// 其它容忍元素必须整棵跳过（见 <see cref="SkipSubtree"/>）。
    /// </summary>
    private static readonly HashSet<string> IgnorableElements = new(StringComparer.Ordinal)
    {
        "documentation",
        "laneSet",
        "dataObject",
        "dataObjectReference",
        "dataStoreReference",
        "textAnnotation",
        "association",
        "group",
    };

    private readonly Dictionary<string, IBpmnElementXmlConverter> _elementReaders = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, IBpmnElementXmlConverter> _elementWriters = [];

    /// <summary>构造并注册 §1.2 支持的全部 10 个元素转换器。</summary>
    public BpmnXmlConverter()
    {
        Register(new StartEventXmlConverter());
        Register(new EndEventXmlConverter());
        Register(new SequenceFlowXmlConverter());
        Register(new ExclusiveGatewayXmlConverter());
        Register(new ParallelGatewayXmlConverter());
        Register(new InclusiveGatewayXmlConverter());
        Register(new EventGatewayXmlConverter());
        Register(new UserTaskXmlConverter());
        Register(new ServiceTaskXmlConverter());
        Register(new ManualTaskXmlConverter());
        Register(new ScriptTaskXmlConverter());
    }

    /// <summary>注册一个元素转换器（供扩展与测试注入）。</summary>
    public void Register<TElement>(BaseBpmnXmlConverter<TElement> converter)
        where TElement : FlowElement
    {
        ArgumentNullException.ThrowIfNull(converter);

        var view = (IBpmnElementXmlConverter)converter;
        _elementReaders[converter.ElementName] = view;
        _elementWriters[typeof(TElement)] = view;
    }

    /// <inheritdoc />
    public BpmnModel ConvertToBpmnModel(Stream xmlStream)
    {
        ArgumentNullException.ThrowIfNull(xmlStream);

        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        var model = new BpmnModel();

        try
        {
            using var reader = XmlReader.Create(xmlStream, settings);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "definitions":
                        ReadDefinitions(reader, model);
                        break;

                    case "process":
                        ReadProcess(reader, model);
                        break;

                    case "BPMNDiagram":
                        ReadDiagram(reader, model);
                        break;

                    default:
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new BpmnXmlParseException($"BPMN XML 格式错误：{ex.Message}", ex);
        }

        foreach (var process in model.Processes)
        {
            WireSequenceFlows(process);
        }

        return model;
    }

    /// <inheritdoc />
    public void ConvertToXml(BpmnModel bpmnModel, Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(bpmnModel);
        ArgumentNullException.ThrowIfNull(outputStream);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = false,
        };

        using var writer = XmlWriter.Create(outputStream, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("definitions", BpmnConstants.BpmnNamespace);
        writer.WriteAttributeString("xmlns", BpmnConstants.ActivitiPrefix, null, BpmnConstants.ActivitiNamespace);
        writer.WriteAttributeString("xmlns", "bpmndi", null, BpmnConstants.BpmnDiNamespace);
        writer.WriteAttributeString("xmlns", "dc", null, BpmnConstants.OmgDcNamespace);
        writer.WriteAttributeString("xmlns", "di", null, BpmnConstants.OmgDiNamespace);
        writer.WriteAttributeString("xmlns", "xsi", null, BpmnConstants.XsiNamespace);
        writer.WriteAttributeString("targetNamespace", bpmnModel.TargetNamespace ?? BpmnConstants.BpmnNamespace);

        if (!string.IsNullOrEmpty(bpmnModel.Exporter))
        {
            writer.WriteAttributeString("exporter", bpmnModel.Exporter);
        }

        foreach (var process in bpmnModel.Processes)
        {
            WriteProcess(writer, process);
        }

        if (bpmnModel.Shapes.Count > 0 || bpmnModel.Edges.Count > 0)
        {
            WriteDiagram(writer, bpmnModel);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
    }

    // ─────────────────────────── 读 ───────────────────────────

    private static void ReadDefinitions(XmlReader reader, BpmnModel model)
    {
        model.TargetNamespace = reader.GetAttribute("targetNamespace");
        model.Exporter = reader.GetAttribute("exporter");
        model.ExporterVersion = reader.GetAttribute("exporterVersion");
        model.SchemaLocation = reader.GetAttribute("schemaLocation", BpmnConstants.XsiNamespace);
    }

    private void ReadProcess(XmlReader reader, BpmnModel model)
    {
        var process = new Process
        {
            Id = reader.GetAttribute("id"),
            Name = reader.GetAttribute("name"),
        };

        var isExecutable = reader.GetAttribute("isExecutable");
        process.IsExecutable = isExecutable is null
            || string.Equals(isExecutable, "true", StringComparison.OrdinalIgnoreCase);

        model.AddProcess(process);

        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement
                && reader.Depth == depth
                && reader.LocalName == "process")
            {
                return;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "extensionElements")
            {
                // 流程级 extensionElements（设计器的「执行监听器」组写在 Process 上）：
                // 监听器要记到 Process 上，交给校验规则统一拦截 —— 解析器只负责"读得进来"，
                // 拒绝与否由校验（unsupported-capability）给出准确原因。
                BaseBpmnXmlConverter<FlowElement>.ReadExtensionElements(reader, process);
            }
            else if (_elementReaders.TryGetValue(reader.LocalName, out var converter))
            {
                var element = converter.Read(reader, process);
                process.AddFlowElement((FlowElement)element);
            }
            else if (IgnorableElements.Contains(reader.LocalName))
            {
                // 容忍元素必须**整棵跳过**：流式解析若只略过开始标签，
                // 它的子元素会被本循环当成流程级元素撞上下面的拒绝分支，
                // 报出「元素 <executionListener> 不在本子集支持范围内」这类误导性错误。
                SkipSubtree(reader);
            }
            else
            {
                throw new BpmnXmlParseException(
                    $"流程 '{process.Id}' 中的元素 <{reader.LocalName}> 不在本子集支持范围内。" +
                    "支持的元素见开发计划 §1.2；排除项见 §1.3。");
            }
        }
    }

    private static void ReadDiagram(XmlReader reader, BpmnModel model)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement
                && reader.Depth == depth
                && reader.LocalName == "BPMNDiagram")
            {
                return;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "BPMNShape":
                    ReadShape(reader, model);
                    break;

                case "BPMNEdge":
                    ReadEdge(reader, model);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>把 <paramref name="reader"/> 当前元素（含子树）整体跳过；空元素不读。</summary>
    private static void SkipSubtree(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        var localName = reader.LocalName;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement
                && reader.Depth == depth
                && reader.LocalName == localName)
            {
                return;
            }
        }
    }

    private static void ReadShape(XmlReader reader, BpmnModel model)
    {
        var shape = new BpmnShape
        {
            Id = reader.GetAttribute("id"),
            BpmnElement = reader.GetAttribute("bpmnElement"),
        };

        GraphicInfo? label = null;

        ReadGraphicContainer(reader, (graphicInfo, isLabel) =>
        {
            if (isLabel)
            {
                label = graphicInfo;
            }
            else
            {
                shape.GraphicInfo = graphicInfo;
            }
        });

        if (shape.Id is not null)
        {
            model.Shapes[shape.Id] = shape;
        }

        if (shape.BpmnElement is not null && shape.GraphicInfo is not null)
        {
            model.LocationMap[shape.BpmnElement] = shape.GraphicInfo;
        }

        if (label is not null)
        {
            shape.LabelGraphicInfo = label;

            if (shape.BpmnElement is not null)
            {
                model.LabelLocationMap[shape.BpmnElement] = label;
            }
        }
    }

    private static void ReadEdge(XmlReader reader, BpmnModel model)
    {
        var edge = new BpmnEdge
        {
            Id = reader.GetAttribute("id"),
            BpmnElement = reader.GetAttribute("bpmnElement"),
        };

        ReadGraphicContainer(reader, (graphicInfo, isLabel) =>
        {
            if (isLabel)
            {
                edge.LabelGraphicInfo = graphicInfo;
            }
            else
            {
                // BPMNEdge 下非标签的图形只有 di:waypoint
                edge.Waypoints.Add(graphicInfo);
            }
        });

        if (edge.Id is not null)
        {
            model.Edges[edge.Id] = edge;
        }
    }

    /// <summary>
    /// 读取 <c>dc:Bounds</c> / <c>di:waypoint</c>，并区分它们是否位于 <c>BPMNLabel</c> 之内。
    /// </summary>
    private static void ReadGraphicContainer(XmlReader reader, Action<GraphicInfo, bool> onGraphic)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var localName = reader.LocalName;
        var depth = reader.Depth;
        var labelDepth = -1;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (labelDepth >= 0 && reader.Depth == labelDepth)
                {
                    labelDepth = -1;
                }
                else if (reader.Depth == depth && reader.LocalName == localName)
                {
                    return;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "BPMNLabel")
            {
                labelDepth = reader.Depth;
                continue;
            }

            if (reader.LocalName is "Bounds" or "waypoint")
            {
                onGraphic(ReadGraphicInfo(reader), labelDepth >= 0);
            }
        }
    }

    private static GraphicInfo ReadGraphicInfo(XmlReader reader)
    {
        var graphicInfo = new GraphicInfo
        {
            Element = reader.GetAttribute("bpmnElement"),
        };

        var x = reader.GetAttribute("x");
        var y = reader.GetAttribute("y");
        var width = reader.GetAttribute("width");
        var height = reader.GetAttribute("height");

        if (x is not null)
        {
            graphicInfo.X = double.Parse(x, CultureInfo.InvariantCulture);
        }

        if (y is not null)
        {
            graphicInfo.Y = double.Parse(y, CultureInfo.InvariantCulture);
        }

        if (width is not null)
        {
            graphicInfo.Width = double.Parse(width, CultureInfo.InvariantCulture);
        }

        if (height is not null)
        {
            graphicInfo.Height = double.Parse(height, CultureInfo.InvariantCulture);
        }

        return graphicInfo;
    }

    /// <summary>把顺序流与源/目标节点互相关联（Activiti 在解析末期做同样的事）。</summary>
    private static void WireSequenceFlows(Process process)
    {
        foreach (var sequenceFlow in process.FindFlowElementsOfType<SequenceFlow>())
        {
            if (sequenceFlow.SourceRef is not null
                && process.GetFlowElement(sequenceFlow.SourceRef) is IFlowNode source)
            {
                sequenceFlow.SourceFlowNode = source;
                source.OutgoingFlows.Add(sequenceFlow);
            }

            if (sequenceFlow.TargetRef is not null
                && process.GetFlowElement(sequenceFlow.TargetRef) is IFlowNode target)
            {
                sequenceFlow.TargetFlowNode = target;
                target.IncomingFlows.Add(sequenceFlow);
            }
        }
    }

    // ─────────────────────────── 写 ───────────────────────────

    private void WriteProcess(XmlWriter writer, Process process)
    {
        writer.WriteStartElement("process", BpmnConstants.BpmnNamespace);
        WriteAttributeIfPresent(writer, "id", process.Id);
        WriteAttributeIfPresent(writer, "name", process.Name);
        writer.WriteAttributeString("isExecutable", process.IsExecutable ? "true" : "false");

        if (process.EventListeners.Count > 0)
        {
            writer.WriteStartElement("extensionElements", BpmnConstants.BpmnNamespace);

            foreach (var listener in process.ExecutionListeners)
            {
                BaseBpmnXmlConverter<FlowElement>.WriteExecutionListener(writer, listener);
            }

            foreach (var listener in process.EventListeners)
            {
                BaseBpmnXmlConverter<FlowElement>.WriteEventListener(writer, listener);
            }

            writer.WriteEndElement();
        }

        foreach (var flowElement in process.FlowElements.OfType<FlowElement>())
        {
            if (_elementWriters.TryGetValue(flowElement.GetType(), out var converter))
            {
                converter.Write(writer, flowElement);
                continue;
            }

            throw new BpmnXmlParseException(
                $"流程元素 '{flowElement.Id}'（{flowElement.GetType().Name}）没有对应的写出转换器。");
        }

        writer.WriteEndElement();
    }

    private static void WriteDiagram(XmlWriter writer, BpmnModel model)
    {
        writer.WriteStartElement("bpmndi", "BPMNDiagram", BpmnConstants.BpmnDiNamespace);
        writer.WriteStartElement("bpmndi", "BPMNPlane", BpmnConstants.BpmnDiNamespace);

        var mainProcess = model.GetMainProcess();
        if (mainProcess?.Id is not null)
        {
            writer.WriteAttributeString("bpmnElement", mainProcess.Id);
        }

        foreach (var shape in model.Shapes.Values)
        {
            WriteShape(writer, shape);
        }

        foreach (var edge in model.Edges.Values)
        {
            WriteEdge(writer, edge);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteShape(XmlWriter writer, BpmnShape shape)
    {
        writer.WriteStartElement("bpmndi", "BPMNShape", BpmnConstants.BpmnDiNamespace);
        WriteAttributeIfPresent(writer, "id", shape.Id);
        WriteAttributeIfPresent(writer, "bpmnElement", shape.BpmnElement);

        if (shape.GraphicInfo is not null)
        {
            WriteBounds(writer, shape.GraphicInfo);
        }

        if (shape.LabelGraphicInfo is not null)
        {
            writer.WriteStartElement("bpmndi", "BPMNLabel", BpmnConstants.BpmnDiNamespace);
            WriteBounds(writer, shape.LabelGraphicInfo);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteEdge(XmlWriter writer, BpmnEdge edge)
    {
        writer.WriteStartElement("bpmndi", "BPMNEdge", BpmnConstants.BpmnDiNamespace);
        WriteAttributeIfPresent(writer, "id", edge.Id);
        WriteAttributeIfPresent(writer, "bpmnElement", edge.BpmnElement);

        foreach (var waypoint in edge.Waypoints)
        {
            writer.WriteStartElement("di", "waypoint", BpmnConstants.OmgDiNamespace);
            writer.WriteAttributeString("x", Format(waypoint.X));
            writer.WriteAttributeString("y", Format(waypoint.Y));
            writer.WriteEndElement();
        }

        if (edge.LabelGraphicInfo is not null)
        {
            writer.WriteStartElement("bpmndi", "BPMNLabel", BpmnConstants.BpmnDiNamespace);
            WriteBounds(writer, edge.LabelGraphicInfo);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteBounds(XmlWriter writer, GraphicInfo graphicInfo)
    {
        writer.WriteStartElement("dc", "Bounds", BpmnConstants.OmgDcNamespace);
        writer.WriteAttributeString("x", Format(graphicInfo.X));
        writer.WriteAttributeString("y", Format(graphicInfo.Y));
        writer.WriteAttributeString("width", Format(graphicInfo.Width));
        writer.WriteAttributeString("height", Format(graphicInfo.Height));
        writer.WriteEndElement();
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void WriteAttributeIfPresent(XmlWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteAttributeString(name, value);
        }
    }
}

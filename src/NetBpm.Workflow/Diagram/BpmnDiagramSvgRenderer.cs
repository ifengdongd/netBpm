using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace NetBpm.Workflow;

/// <summary>
/// 流程图的高亮信息：哪些节点已经流转过、哪些节点是「当前正停着」的。
/// </summary>
/// <param name="VisitedActivityIds">走过的节点 id（含当前环节；来自审批历史的 ActivityId）。</param>
/// <param name="ActiveActivityIds">当前正停着的节点 id（历史里 EndedAt 为空的环节）。</param>
public sealed record DiagramHighlight(
    IReadOnlyCollection<string> VisitedActivityIds,
    IReadOnlyCollection<string> ActiveActivityIds)
{
    /// <summary>不高亮（纯流程定义图）。</summary>
    public static DiagramHighlight None { get; } = new([], []);
}

/// <summary>
/// 把<b>已发布</b>（部署）的 BPMN XML 渲染成 SVG 流程图图片。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 图形信息直接取 BPMN 文件里的 <c>bpmndi:BPMNDiagram</c>（设计器导出时就带着）：
/// 节点用 <c>BPMNShape/dc:Bounds</c> 定位，连线用 <c>BPMNEdge/di:waypoint</c> 折线，
/// 不需要任何第三方绘图库 —— 发布流程图后即可渲染出「设计器里画的样子」。
/// </para>
/// <para>
/// 流转状态高亮（红框 / 红线）：走过的节点 → <b>红框</b>（当前正停着的环节加粗红框 + 浅红填充）；
/// 走过的线 → <b>红线</b>（两端的节点都已流转过的顺序流，箭头同样变红）。
/// </para>
/// <para>输出是自包含 SVG（无外部资源），浏览器直接当图片显示、可另存为 .svg 文件。</para>
/// </remarks>
public static class BpmnDiagramSvgRenderer
{
    private const string StrokeDefault = "#374151";
    private const string StrokeVisited = "#dc2626";
    private const string FillVisited = "#fef2f2";

    /// <summary>渲染流程图 SVG。</summary>
    /// <param name="bpmnXml">已部署的 BPMN XML（含 bpmndi 图形信息）。</param>
    /// <param name="highlight">流转状态高亮；<see cref="DiagramHighlight.None"/> = 纯定义图。</param>
    public static string RenderSvg(string bpmnXml, DiagramHighlight? highlight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bpmnXml);

        var document = XDocument.Parse(bpmnXml);
        var root = document.Root ?? throw new InvalidOperationException("BPMN XML 缺少根元素。");

        var model = ParseModel(root);
        var visited = new HashSet<string>(highlight?.VisitedActivityIds ?? [], StringComparer.Ordinal);
        var active = new HashSet<string>(highlight?.ActiveActivityIds ?? [], StringComparer.Ordinal);
        var nodes = model.Nodes.Values.Where(node => node.Bounds is not null).ToList();

        // 没有 bpmndi（或 BPMNShape 全对不上）时没有任何坐标可画，正常流程只会得到
        // 一张 20×20 的白图——调用方看到的是「图挂了」，却不知道该怪定义还是怪接口。
        // 这种情况显式画一张带说明的占位图：说清缺什么、怎么补，比沉默的空白有用。
        if (nodes.Count == 0)
        {
            return RenderUndrawable();
        }

        var (width, height) = Measure(model);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Round(width + 20)}\" height=\"{Round(height + 20)}\" ")
           .Append(CultureInfo.InvariantCulture, $"viewBox=\"0 0 {Round(width + 20)} {Round(height + 20)}\" ")
           .Append("style=\"font-family:'Segoe UI','Microsoft YaHei',system-ui,sans-serif\">");
        svg.Append("<defs>")
           .Append("<marker id=\"arrow\" markerWidth=\"9\" markerHeight=\"8\" refX=\"8\" refY=\"4\" orient=\"auto\">")
           .Append(CultureInfo.InvariantCulture, $"<path d=\"M0,0 L9,4 L0,8 z\" fill=\"{StrokeDefault}\"/></marker>")
           .Append("<marker id=\"arrow-red\" markerWidth=\"9\" markerHeight=\"8\" refX=\"8\" refY=\"4\" orient=\"auto\">")
           .Append(CultureInfo.InvariantCulture, $"<path d=\"M0,0 L9,4 L0,8 z\" fill=\"{StrokeVisited}\"/></marker>")
           .Append("</defs>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"0\" width=\"{Round(width + 20)}\" height=\"{Round(height + 20)}\" fill=\"#ffffff\"/>");

        // 连线先画（节点压在线上面）
        foreach (var flow in model.Flows.Values)
        {
            RenderFlow(svg, flow, visited);
        }

        foreach (var node in nodes)
        {
            RenderNode(svg, node, visited.Contains(node.Id), active.Contains(node.Id));
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    // ───────────────────────── 无法绘制时的占位图 ─────────────────────────

    /// <summary>占位画布：宽度取详情页图片的常见最小宽度，高度够放三行说明。</summary>
    private const double UndrawableWidth = 680;

    /// <inheritdoc cref="UndrawableWidth" />
    private const double UndrawableHeight = 160;

    /// <summary>
    /// 定义里没有图形信息时的占位图：虚线框 + 三行说明。
    /// </summary>
    /// <remarks>
    /// 文案写的是「缺什么、怎么补」，不是「渲染失败」——后者会让人去查平台，
    /// 而真正要改的是那份 BPMN 定义。同时把同样的意思放进 <c>&lt;title&gt;</c>/<c>&lt;desc&gt;</c>，
    /// 让调用方可以程序化识别（图片在页面里也许只是一块灰，日志里要说得清）。
    /// </remarks>
    private static string RenderUndrawable()
    {
        const string title = "当前流程定义缺少图形信息，无法绘制流程图";
        const string reason = "BPMN 里没有 bpmndi:BPMNDiagram 的坐标（BPMNShape / BPMNEdge）。";
        const string action = "请在设计器中重新导出带图形信息的 BPMN，或部署包含 BPMNDiagram 节点的版本。";

        var centerX = Round(UndrawableWidth / 2);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Round(UndrawableWidth)}\" height=\"{Round(UndrawableHeight)}\" ")
           .Append(CultureInfo.InvariantCulture, $"viewBox=\"0 0 {Round(UndrawableWidth)} {Round(UndrawableHeight)}\" ")
           .Append("style=\"font-family:'Segoe UI','Microsoft YaHei',system-ui,sans-serif\">")
           .Append(CultureInfo.InvariantCulture, $"<title>{Escape(title)}</title>")
           .Append(CultureInfo.InvariantCulture, $"<desc>{Escape(reason + action)}</desc>")
           .Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"0\" width=\"{Round(UndrawableWidth)}\" height=\"{Round(UndrawableHeight)}\" fill=\"#ffffff\"/>")
           .Append(CultureInfo.InvariantCulture, $"<rect x=\"20\" y=\"20\" width=\"{Round(UndrawableWidth - 40)}\" height=\"{Round(UndrawableHeight - 40)}\" rx=\"10\" ")
           .Append("fill=\"#fafafa\" stroke=\"#d0d5dd\" stroke-width=\"1.5\" stroke-dasharray=\"6 5\"/>")
           .Append(CultureInfo.InvariantCulture, $"<text x=\"{centerX}\" y=\"76\" font-size=\"14\" font-weight=\"600\" fill=\"{TextColor}\" text-anchor=\"middle\">{Escape(title)}</text>")
           .Append(CultureInfo.InvariantCulture, $"<text x=\"{centerX}\" y=\"100\" font-size=\"12\" fill=\"#7a828c\" text-anchor=\"middle\">{Escape(reason)}</text>")
           .Append(CultureInfo.InvariantCulture, $"<text x=\"{centerX}\" y=\"120\" font-size=\"12\" fill=\"#7a828c\" text-anchor=\"middle\">{Escape(action)}</text>")
           .Append("</svg>");

        return svg.ToString();
    }

    // ───────────────────────── 模型解析 ─────────────────────────

    private sealed record NodeModel(string Id, string? Name, string LocalType, RectModel? Bounds);

    private sealed record RectModel(double X, double Y, double Width, double Height);

    private sealed record FlowModel(string Id, string? Name, string SourceRef, string TargetRef, List<(double X, double Y)> Waypoints);

    private sealed record DiagramModel(
        Dictionary<string, NodeModel> Nodes,
        Dictionary<string, FlowModel> Flows);

    private static DiagramModel ParseModel(XElement root)
    {
        var process = root.Elements().FirstOrDefault(e => e.Name.LocalName == "process")
            ?? throw new InvalidOperationException("BPMN XML 缺少 process 元素。");
        var plane = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "BPMNDiagram")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "BPMNPlane");

        var nodes = new Dictionary<string, NodeModel>(StringComparer.Ordinal);
        var flows = new Dictionary<string, FlowModel>(StringComparer.Ordinal);

        // 节点与顺序流：process 的直接子元素（子流程内部不展开，本仓库的流程都是单层）
        foreach (var element in process.Elements())
        {
            var id = (string?)element.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (element.Name.LocalName == "sequenceFlow")
            {
                flows[id] = new FlowModel(
                    id,
                    (string?)element.Attribute("name"),
                    (string?)element.Attribute("sourceRef") ?? string.Empty,
                    (string?)element.Attribute("targetRef") ?? string.Empty,
                    []);
            }
            else if (element.Name.LocalName is not ("extensionElements" or "laneSet"))
            {
                nodes[id] = new NodeModel(
                    id,
                    (string?)element.Attribute("name"),
                    element.Name.LocalName,
                    null);
            }
        }

        // 图形信息：BPMNShape 的 Bounds 与 BPMNEdge 的 waypoints
        if (plane is not null)
        {
            foreach (var shape in plane.Elements().Where(e => e.Name.LocalName == "BPMNShape"))
            {
                var bpmnElement = (string?)shape.Attribute("bpmnElement");
                if (bpmnElement is null || !nodes.TryGetValue(bpmnElement, out var node))
                {
                    continue;
                }

                var bounds = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Bounds");
                if (bounds is null)
                {
                    continue;
                }

                nodes[bpmnElement] = node with
                {
                    Bounds = new RectModel(
                    (double?)bounds.Attribute("x") ?? 0,
                    (double?)bounds.Attribute("y") ?? 0,
                    (double?)bounds.Attribute("width") ?? 0,
                    (double?)bounds.Attribute("height") ?? 0)
                };
            }

            foreach (var edge in plane.Elements().Where(e => e.Name.LocalName == "BPMNEdge"))
            {
                var bpmnElement = (string?)edge.Attribute("bpmnElement");
                if (bpmnElement is null || !flows.TryGetValue(bpmnElement, out var flow))
                {
                    continue;
                }

                flow.Waypoints.AddRange(edge.Elements()
                    .Where(e => e.Name.LocalName == "waypoint")
                    .Select(e => ((double?)e.Attribute("x") ?? 0, (double?)e.Attribute("y") ?? 0)));
            }
        }

        return new DiagramModel(nodes, flows);
    }

    private static (double Width, double Height) Measure(DiagramModel model)
    {
        double width = 0, height = 0;

        foreach (var bounds in model.Nodes.Values.Select(n => n.Bounds).OfType<RectModel>())
        {
            width = Math.Max(width, bounds.X + bounds.Width);
            height = Math.Max(height, bounds.Y + bounds.Height);
        }

        foreach (var flow in model.Flows.Values)
        {
            foreach (var (x, y) in flow.Waypoints)
            {
                width = Math.Max(width, x);
                height = Math.Max(height, y);
            }
        }

        return (width, height);
    }

    // ───────────────────────── 绘制 ─────────────────────────

    private static void RenderFlow(StringBuilder svg, FlowModel flow, HashSet<string> visited)
    {
        if (flow.Waypoints.Count < 2)
        {
            return;
        }

        // 走过的线：两端的节点都已流转过 → 红线 + 红箭头
        var traversed = visited.Contains(flow.SourceRef) && visited.Contains(flow.TargetRef);
        var stroke = traversed ? StrokeVisited : StrokeDefault;
        var width = traversed ? 2.5 : 1.6;
        var points = string.Join(' ', flow.Waypoints.Select(p => $"{Round(p.X)},{Round(p.Y)}"));

        svg.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{points}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{width}\" ")
           .Append(CultureInfo.InvariantCulture, $"marker-end=\"url(#{(traversed ? "arrow-red" : "arrow")})\"/>");

        // 连线名（如「通过」「拒绝」）：画在第一段折线的中点上方
        if (!string.IsNullOrWhiteSpace(flow.Name))
        {
            var (x, y) = flow.Waypoints[0];
            var (x2, y2) = flow.Waypoints[1];
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{Round((x + x2) / 2)}\" y=\"{Round((y + y2) / 2 - 4)}\" font-size=\"11\" ")
               .Append(CultureInfo.InvariantCulture, $"fill=\"{(traversed ? StrokeVisited : "#7a828c")}\" text-anchor=\"middle\">{Escape(flow.Name)}</text>");
        }
    }

    private static void RenderNode(StringBuilder svg, NodeModel node, bool visited, bool active)
    {
        var bounds = node.Bounds!;
        var label = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;
        var stroke = active || visited ? StrokeVisited : StrokeDefault;
        var strokeWidth = active ? 3.5 : visited ? 2.5 : 1.6;
        var fill = active ? "#fee2e2" : visited ? FillVisited : "#ffffff";
        var highlighted = active || visited;

        switch (node.LocalType)
        {
            case "startEvent":
            case "endEvent":
            case "intermediateCatchEvent":
            case "intermediateThrowEvent":
            case "boundaryEvent":
                {
                    var cx = bounds.X + bounds.Width / 2;
                    var cy = bounds.Y + bounds.Height / 2;
                    var radius = Math.Min(bounds.Width, bounds.Height) / 2;
                    var strokeWidthOfEvent = node.LocalType == "endEvent" ? 4 : 2;
                    svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{Round(cx)}\" cy=\"{Round(cy)}\" r=\"{Round(radius)}\" fill=\"{fill}\" ")
                       .Append(CultureInfo.InvariantCulture, $"stroke=\"{stroke}\" stroke-width=\"{(highlighted ? strokeWidth : strokeWidthOfEvent)}\"/>");
                    if (node.LocalType is "startEvent" or "endEvent")
                    {
                        Label(svg, cx, bounds.Y + bounds.Height + 14, label, highlighted);
                    }

                    break;
                }

            case "exclusiveGateway":
            case "parallelGateway":
            case "inclusiveGateway":
            case "eventGateway":
                {
                    var cx = bounds.X + bounds.Width / 2;
                    var cy = bounds.Y + bounds.Height / 2;
                    var half = Math.Min(bounds.Width, bounds.Height) / 2;
                    svg.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{Round(cx)},{Round(cy - half)} {Round(cx + half)},{Round(cy)} {Round(cx)},{Round(cy + half)} {Round(cx - half)},{Round(cy)}\" ")
                       .Append(CultureInfo.InvariantCulture, $"fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{strokeWidth}\"/>");
                    if (node.LocalType == "exclusiveGateway")
                    {
                        var inner = half * 0.4;
                        svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{Round(cx - inner)},{Round(cy - inner)} L{Round(cx + inner)},{Round(cy + inner)} M{Round(cx - inner)},{Round(cy + inner)} L{Round(cx + inner)},{Round(cy - inner)}\" ")
                           .Append(CultureInfo.InvariantCulture, $"stroke=\"{stroke}\" stroke-width=\"2\" fill=\"none\"/>");
                    }

                    Label(svg, cx, bounds.Y + bounds.Height + 14, label, highlighted);
                    break;
                }

            default:
                {
                    // 任务类（userTask / serviceTask / task / …）与其余节点：圆角矩形
                    svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{Round(bounds.X)}\" y=\"{Round(bounds.Y)}\" width=\"{Round(bounds.Width)}\" height=\"{Round(bounds.Height)}\" rx=\"10\" ")
                       .Append(CultureInfo.InvariantCulture, $"fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{strokeWidth}\"/>");
                    Label(svg, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2 + 4, label, highlighted);
                    break;
                }
        }
    }

    /// <summary>节点名：中心对齐；字数太多按 7 字一行折行（矩形内最多 3 行，超长截断）。</summary>
    private static void Label(StringBuilder svg, double centerX, double centerY, string label, bool highlighted)
    {
        const int maxPerLine = 7;

        var lines = new List<string>();
        for (var offset = 0; offset < label.Length && lines.Count < 3; offset += maxPerLine)
        {
            lines.Add(label[offset..Math.Min(offset + maxPerLine, label.Length)]);
        }

        if (label.Length > maxPerLine * 3)
        {
            lines[2] = lines[2][..^1] + "…";
        }

        var lineHeight = 14;
        var startY = centerY - (lines.Count - 1) * lineHeight / 2.0;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var y = startY + index * lineHeight;
            var weight = highlighted && index == 0 ? "600" : "400";
            var fill = highlighted ? StrokeVisited : TextColor;
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{Round(centerX)}\" y=\"{Round(y)}\" font-size=\"12\" font-weight=\"{weight}\" ")
               .Append(CultureInfo.InvariantCulture, $"fill=\"{fill}\" text-anchor=\"middle\" dominant-baseline=\"middle\">{Escape(line)}</text>");
        }
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static string Round(double value) => Math.Round(value, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private const string TextColor = "#1f2329";
}

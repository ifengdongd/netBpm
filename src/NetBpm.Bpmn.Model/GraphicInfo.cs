namespace NetBpm.Bpmn.Model;

/// <summary>图形坐标（<c>dc:Bounds</c> / <c>di:waypoint</c>）。</summary>
public class GraphicInfo
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>该图形对应的 BPMN 元素 id。</summary>
    public string? Element { get; set; }

    /// <summary>是否为展开（子流程）图形。</summary>
    public bool Expanded { get; set; }
}

/// <summary>图形形状（<c>bpmndi:BPMNShape</c>）。</summary>
public class BpmnShape
{
    /// <summary>BPMN 元素 id（对应 <c>bpmnElement</c>）。</summary>
    public string? BpmnElement { get; set; }

    /// <summary>图形 id（对应 <c>id</c>）。</summary>
    public string? Id { get; set; }

    public GraphicInfo? GraphicInfo { get; set; }

    public GraphicInfo? LabelGraphicInfo { get; set; }
}

/// <summary>图形连线（<c>bpmndi:BPMNEdge</c>）。</summary>
public class BpmnEdge
{
    /// <summary>BPMN 元素 id（对应 <c>bpmnElement</c>）。</summary>
    public string? BpmnElement { get; set; }

    /// <summary>图形 id（对应 <c>id</c>）。</summary>
    public string? Id { get; set; }

    /// <summary>折点集合（<c>di:waypoint</c>）。</summary>
    public IList<GraphicInfo> Waypoints { get; } = [];

    public GraphicInfo? LabelGraphicInfo { get; set; }
}

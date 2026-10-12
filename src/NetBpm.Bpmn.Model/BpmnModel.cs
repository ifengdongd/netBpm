namespace NetBpm.Bpmn.Model;

/// <summary>
/// BPMN 文档根对象（对应 <c>definitions</c>）。对应 Activiti 的 <c>BpmnModel</c>。
/// 同时承载流程图（BPMNDI）信息 —— 设计器往返保真的依据（§6.2 / R5）。
/// </summary>
public class BpmnModel
{
    /// <summary>目标命名空间（<c>definitions/@targetNamespace</c>）。</summary>
    public string? TargetNamespace { get; set; }

    /// <summary>导出者（<c>definitions/@exporter</c>）。</summary>
    public string? Exporter { get; set; }

    /// <summary>导出者版本（<c>definitions/@exporterVersion</c>）。</summary>
    public string? ExporterVersion { get; set; }

    /// <summary>xsi:schemaLocation。</summary>
    public string? SchemaLocation { get; set; }

    /// <summary>流程定义集合。</summary>
    public IList<Process> Processes { get; } = [];

    /// <summary>元素 id → 图形坐标。</summary>
    public IDictionary<string, GraphicInfo> LocationMap { get; } =
        new Dictionary<string, GraphicInfo>(StringComparer.Ordinal);

    /// <summary>元素 id → 标签图形坐标。</summary>
    public IDictionary<string, GraphicInfo> LabelLocationMap { get; } =
        new Dictionary<string, GraphicInfo>(StringComparer.Ordinal);

    /// <summary>形状 id → 形状。</summary>
    public IDictionary<string, BpmnShape> Shapes { get; } =
        new Dictionary<string, BpmnShape>(StringComparer.Ordinal);

    /// <summary>连线 id → 连线。</summary>
    public IDictionary<string, BpmnEdge> Edges { get; } =
        new Dictionary<string, BpmnEdge>(StringComparer.Ordinal);

    public void AddProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        Processes.Add(process);
    }

    public Process? GetProcessById(string processId)
    {
        ArgumentException.ThrowIfNullOrEmpty(processId);

        foreach (var process in Processes)
        {
            if (string.Equals(process.Id, processId, StringComparison.Ordinal))
            {
                return process;
            }
        }

        return null;
    }

    /// <summary>取主流程（子集内只支持单流程；多流程时取第一个 isExecutable 的）。</summary>
    public Process? GetMainProcess()
    {
        foreach (var process in Processes)
        {
            if (process.IsExecutable)
            {
                return process;
            }
        }

        return Processes.Count > 0 ? Processes[0] : null;
    }

    /// <summary>主流程的全部流程元素。</summary>
    public IReadOnlyCollection<IFlowElement> MainProcessElements =>
        GetMainProcess()?.FlowElements.ToArray() ?? [];
}

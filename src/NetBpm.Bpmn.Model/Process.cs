namespace NetBpm.Bpmn.Model;

/// <summary>
/// 流程定义（<c>process</c>）。对应 Activiti 的 <c>Process</c>。
/// </summary>
public class Process : BaseElement, IFlowElementsContainer
{
    /// <summary>是否可执行（<c>isExecutable</c>）。</summary>
    public bool IsExecutable { get; set; } = true;

    public IList<IFlowElement> FlowElements { get; } = [];

    /// <summary>潜在启动者（<c>activiti:potentialStarter</c>）。</summary>
    public PotentialStarter? PotentialStarter { get; set; }

    public void AddFlowElement(FlowElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.ParentContainer = this;
        FlowElements.Add(element);
    }

    /// <summary>按 id 查找元素；未找到返回 <see langword="null"/>。</summary>
    public IFlowElement? GetFlowElement(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        foreach (var element in FlowElements)
        {
            if (string.Equals(element.Id, id, StringComparison.Ordinal))
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>按类型枚举元素。</summary>
    public IEnumerable<T> FindFlowElementsOfType<T>()
        where T : FlowElement =>
        FlowElements.OfType<T>();

    /// <summary>
    /// 初始流程元素（开始事件）。对应 Activiti 的 <c>Process.getInitialFlowElement()</c>。
    /// </summary>
    public FlowElement? GetInitialFlowElement() => FlowElements.OfType<StartEvent>().FirstOrDefault();
}

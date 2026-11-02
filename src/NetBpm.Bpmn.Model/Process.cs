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

    /// <summary>
    /// 流程级事件监听器（<c>activiti:eventListener</c>）。引擎在对应事件发生时逐个通知，
    /// 执行语义见引擎侧的 <c>IEngineEventListener</c> 与事件分发器。
    /// </summary>
    public IList<EventListener> EventListeners { get; } = [];

    /// <summary>
    /// 解析到但<b>本子集不支持</b>的流程元素（如 <c>subProcess</c> / <c>callActivity</c>）。
    /// </summary>
    /// <remarks>
    /// 为什么不直接在解析器里抛异常：那样报出来的是「XML 坏了」形态的解析错误，
    /// 而它其实是「这个能力不在子集内」—— 与其它 20 条规则的 <see cref="ValidationError"/> 形态不一致，
    /// 前端设计器也拿不到结构化提示。记进模型、交给校验层拒绝，才能和其它出界能力同形。
    /// </remarks>
    public IList<UnsupportedElement> UnsupportedElements { get; } = [];

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

/// <summary>
/// 一个「认识但不支持」的 BPMN 元素。对应 Activiti 里被 <c>BpmnParse</c> 跳过的能力。
/// </summary>
/// <param name="ElementName">XML 元素名（如 <c>subProcess</c>）。</param>
/// <param name="Id">元素 id（缺失时为 <see langword="null"/>）。</param>
/// <param name="Capability">能力的中文说明（给建模者看的，不写元素名）。</param>
public sealed record UnsupportedElement(string ElementName, string? Id, string Capability);

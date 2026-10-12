namespace NetBpm.Bpmn.Model;

/// <summary>可被流程包含的元素。</summary>
public interface IFlowElement
{
    string? Id { get; set; }

    string? Name { get; set; }
}

/// <summary>流程节点：拥有入流与出流的元素（网关 / 事件 / 活动）。</summary>
public interface IFlowNode : IFlowElement
{
    IList<SequenceFlow> IncomingFlows { get; }

    IList<SequenceFlow> OutgoingFlows { get; }
}

/// <summary>元素容器（<c>process</c>）。</summary>
public interface IFlowElementsContainer
{
    IList<IFlowElement> FlowElements { get; }
}

/// <summary>流程元素基类。对应 Activiti 的 <c>FlowElement</c>。</summary>
public abstract class FlowElement : BaseElement, IFlowElement
{
    /// <summary>所属流程（由解析器回填）。</summary>
    public Process? ParentContainer { get; set; }
}

/// <summary>流程节点基类。对应 Activiti 的 <c>FlowNode</c>。</summary>
public abstract class FlowNode : FlowElement, IFlowNode
{
    public IList<SequenceFlow> IncomingFlows { get; } = [];

    public IList<SequenceFlow> OutgoingFlows { get; } = [];

    /// <summary>按 id 取入流。</summary>
    public SequenceFlow? GetIncomingFlow(string id) =>
        IncomingFlows.FirstOrDefault(flow => string.Equals(flow.Id, id, StringComparison.Ordinal));

    /// <summary>按 id 取出流。</summary>
    public SequenceFlow? GetOutgoingFlow(string id) =>
        OutgoingFlows.FirstOrDefault(flow => string.Equals(flow.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// 顺序流（<c>sequenceFlow</c>）。
/// 对应 Activiti 的 <c>SequenceFlow</c>；分支判定入口见 <c>ConditionUtil</c>（开发计划 §5.2）。
/// </summary>
public class SequenceFlow : FlowElement
{
    /// <summary>源节点 id（对应 XML 属性 <c>sourceRef</c>）。</summary>
    public string? SourceRef { get; set; }

    /// <summary>目标节点 id（对应 XML 属性 <c>targetRef</c>）。</summary>
    public string? TargetRef { get; set; }

    /// <summary>源节点对象（由解析器回填）。</summary>
    public IFlowNode? SourceFlowNode { get; set; }

    /// <summary>目标节点对象（由解析器回填）。</summary>
    public IFlowNode? TargetFlowNode { get; set; }

    /// <summary>
    /// 条件表达式原文（<c>conditionExpression</c>）。
    /// 为空表示「无条件流」—— 依 Activiti 语义恒为 true（见 §5.2 ConditionUtil）。
    /// </summary>
    public string? ConditionExpression { get; set; }
}

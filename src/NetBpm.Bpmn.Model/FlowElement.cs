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

    /// <summary>
    /// 进入节点前先落一条异步作业（<c>activiti:asyncBefore</c>，旧写法 <c>activiti:async</c>）：
    /// 命令提交后由 JobExecutor 在独立事务里继续推进。
    /// </summary>
    /// <remarks>
    /// 为什么放在 <b>FlowNode</b> 而不是 <c>Activity</c>：异步断点是<b>令牌推进</b>层面的语义，
    /// Activiti 允许它写在网关上（对拍语料 <c>ExclusiveGatewayTest.testAsyncExclusiveGateway</c>
    /// 就在 <c>exclusiveGateway</c> 上写了 <c>activiti:async="true"</c>，Activiti 会真的停在网关前）。
    /// 只挂在 Activity 上时，网关上的异步配置会被静默忽略 —— 属于「配置了但没做」这类最危险的差异。
    /// </remarks>
    public bool AsyncBefore { get; set; }

    /// <summary>
    /// 节点执行完成后不立即离开，而是落一条异步作业（<c>activiti:asyncAfter</c>）。
    /// </summary>
    public bool AsyncAfter { get; set; }

    /// <summary>
    /// 是否独占作业（<c>activiti:exclusive</c>，默认 true）。
    /// <para>本子集只支持独占：并发作业（exclusive=false）会让同一实例的多条令牌并行推进，</para>
    /// <para>与当前「一个命令一棵执行树」的并发模型冲突，由 <c>AsyncJobRule</c> 部署期拒绝。</para>
    /// </summary>
    public bool Exclusive { get; set; } = true;
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

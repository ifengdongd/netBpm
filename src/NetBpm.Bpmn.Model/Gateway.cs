namespace NetBpm.Bpmn.Model;

/// <summary>网关基类。对应 Activiti 的 <c>Gateway</c>。</summary>
public abstract class Gateway : FlowNode
{
    /// <summary>
    /// 默认流 id（对应 XML 属性 <c>default</c>）。
    /// 注意保存的是「顺序流 id」而非对象引用 —— 与 Activiti 行为类中的用法一致（见 §5.4）。
    /// </summary>
    public string? DefaultFlow { get; set; }
}

/// <summary>
/// 排他网关（<c>exclusiveGateway</c>）。异或语义：取「第一个条件为真」的流。
/// 行为实现见 <c>ExclusiveGatewayActivityBehavior</c>（开发计划 §5.4 之一）。
/// </summary>
public class ExclusiveGateway : Gateway;

/// <summary>
/// 并行网关（<c>parallelGateway</c>）。与语义：Fork / Join 双向。
/// 行为实现见 <c>ParallelGatewayActivityBehavior</c>（开发计划 §5.4 之二）。
/// </summary>
public class ParallelGateway : Gateway;

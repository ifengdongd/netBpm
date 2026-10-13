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

/// <summary>
/// 包含网关（<c>inclusiveGateway</c>）。或语义：
/// Fork 取「全部条件为真」的出流（无匹配走默认流）；Join 等待所有「仍可到达本网关」的分支。
/// 行为实现见 <c>InclusiveGatewayActivityBehavior</c>。
/// </summary>
public class InclusiveGateway : Gateway;

/// <summary>
/// 事件网关（<c>eventBasedGateway</c>）。
/// <para>
/// ⚠️ 本子集<b>未实现</b>：它的每条出流必须指向中间捕获事件（定时/消息/信号），而中间事件不在支持范围内。
/// 转换器可解析（保证往返不丢元素），但校验规则 <c>event-gateway-unsupported</c> 会显式拒绝部署 ——
/// 与"宁可报错也不要静默行为不一致"的项目原则一致。
/// </para>
/// </summary>
public class EventGateway : Gateway;

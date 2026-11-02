namespace NetBpm.Engine.Behavior;

/// <summary>
/// 标记「失败即等于事件丢失」的流程级事件监听器实现。
/// </summary>
/// <remarks>
/// <para>
/// 带本特性的实现被部署期自检（<c>ServiceTaskDelegateGuard</c>）视为<b>关键监听器</b>：
/// 它抛异常而 <c>throwOnFailure</c> 未声明为 <c>true</c> 时，引擎会记 warning 并继续推进流程 ——
/// 事件没送达、流程照常走。对「写审批记录 / 推送待办」这类监听器，这等于静默丢数据，
/// 因此要求在 BPMN 上显式声明 <c>activiti:throwOnFailure="true"</c>，否则拒绝部署。
/// </para>
/// <para>
/// 判据挂在<b>实现类型</b>上而不是监听器名字上：同一个实现可能被不同流程以 <c>class</c>
/// 或 <c>delegateExpression</c> 引用，只有类型是稳定的；而「这个监听器失败了算不算丢数据」
/// 是实现的固有属性，不该由某条流程的配置来挑选。
/// </para>
/// <para>
/// 只对流程级事件监听器（<c>activiti:eventListener</c>）生效：执行监听器 / 任务监听器
/// 抛错本来就会让命令失败，没有「吞掉」这条路，也就不需要该声明。
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CriticalEventListenerAttribute : Attribute
{
    /// <summary>
    /// 为什么关键（写进部署期报错信息，告诉配置者该怎么处理）。
    /// </summary>
    public string? Reason { get; init; }
}

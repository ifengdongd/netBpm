using NetBpm.Engine.History;

namespace NetBpm.Runtime;

/// <summary>
/// 流程图数据（P9 交付项「流程图数据」，§6.2 第 5 项只读实例视图的数据源）。
/// <para>
/// 一次请求给全前端着色所需的东西：BPMN XML（含 BPMNDI 坐标）+ 该实例走过的活动序列。
/// 前端把 <c>Activities</c> 按 <c>ActivityId</c> 映射到 XML 里的元素即可上色：
/// <c>EndTime == null</c> ⇒ 正在该节点等待。
/// </para>
/// </summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ProcessDefinitionId">流程定义 id（<c>{key}:{version}:{id}</c>）。</param>
/// <param name="BpmnXml">部署时的 BPMN XML 原文（含 BPMNDI）。</param>
/// <param name="IsRunning">实例是否仍在运行（结束后只能靠历史判定）。</param>
/// <param name="Activities">历史活动序列（按开始时间排序）。</param>
public sealed record DiagramData(
    string ProcessInstanceId,
    string ProcessDefinitionId,
    string? BpmnXml,
    bool IsRunning,
    IReadOnlyList<HistoricActivityInstance> Activities);

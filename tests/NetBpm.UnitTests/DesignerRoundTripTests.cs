using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;

namespace NetBpm.UnitTests;

/// <summary>
/// P8 验收：「设计器画出来 → 导出 XML → 后端解析成功率 100% 且**往返不丢属性**」。
/// <para>
/// 语料刻意按 **bpmn-js 的实际输出形状**写（<c>bpmn:</c> / <c>bpmndi:</c> 前缀、<c>activiti:*</c>
/// 扩展属性、<c>xsi:type</c> 的条件表达式、实体转义），覆盖属性面板**能写出的每一个属性**：
/// 只要面板能填，这条用例就必须证明"后端读得到、写回后还在"。
/// </para>
/// <para>
/// 已知的有意差异（不在断言范围内）：<c>exporter</c> / <c>exporterVersion</c> / <c>xsi:schemaLocation</c>
/// 后端只读不写，但 bpmn-js 每次保存都会重新生成它们，因此对用户不可见。
/// </para>
/// </summary>
public sealed class DesignerRoundTripTests
{
    [Fact]
    public void DesignerOutput_ShouldPreserveEveryPanelEditableAttribute()
    {
        var original = Parse(DesignerOutput());

        AssertPanelAttributes(original);

        // 后端导出 → 再解析：属性零丢失（这是"往返保真"的可执行定义）
        var reparsed = Parse(Write(original));

        AssertPanelAttributes(reparsed);
    }

    [Fact]
    public void DesignerOutput_ShouldPreserveGraphicalInformation()
    {
        var original = Parse(DesignerOutput());

        AssertGraphicalInformation(original);

        // DI 是设计器往返的命门（风险 R5：丢 DI 就等于"图没了"）
        AssertGraphicalInformation(Parse(Write(original)));
    }

    private static void AssertPanelAttributes(BpmnModel model)
    {
        var process = Assert.IsType<Process>(model.GetMainProcess());

        // 流程组
        Assert.Equal("leave-approval", process.Id);
        Assert.Equal("请假申请", process.Name);
        Assert.True(process.IsExecutable);

        // 用户任务：面板暴露的全部属性
        var approve = Assert.IsType<UserTask>(process.GetFlowElement("approve"));

        Assert.Equal("kermit", approve.Assignee);
        Assert.Equal("gonzo", approve.Owner);
        Assert.Equal(["alice", "bob"], approve.CandidateUsers);
        Assert.Equal(["managers", "hr"], approve.CandidateGroups);
        Assert.Equal("approveForm", approve.FormKey);
        Assert.Equal("2030-01-02T03:04:05", approve.DueDate);
        Assert.Equal("80", approve.Priority);
        Assert.Equal("leave", approve.Category);
        Assert.Equal("${skipped}", approve.SkipExpression);

        // 服务任务：实现方式三选一（这里用 class）+ 结果变量名 + 默认流
        var notify = Assert.IsType<ServiceTask>(process.GetFlowElement("notify"));

        Assert.Equal(ServiceTaskImplementationType.Class, notify.ImplementationType);
        Assert.Equal("NetBpm.Demo.NotifyDelegate", notify.ClassName);
        Assert.Null(notify.DelegateExpression);
        Assert.Null(notify.Expression);
        Assert.Equal("notifyResult", notify.ResultVariableName);
        Assert.Equal("flowNotifyFailed", notify.DefaultFlow);

        // 脚本任务
        var script = Assert.IsType<ScriptTask>(process.GetFlowElement("tally"));

        Assert.Equal("javascript", script.ScriptFormat);
        Assert.Equal("tallyResult", script.ResultVariable);
        Assert.True(script.AutoStoreVariables);
        Assert.Contains("amount > 100", script.Script, StringComparison.Ordinal);

        // 手工任务：只有 id/name
        var archive = Assert.IsType<ManualTask>(process.GetFlowElement("archive"));

        Assert.Equal("归档", archive.Name);

        // 排他网关的默认流
        var gateway = Assert.IsType<ExclusiveGateway>(process.GetFlowElement("gw"));

        Assert.Equal("flowRejected", gateway.DefaultFlow);

        // 顺序流：条件表达式（含实体转义）
        var approved = Assert.IsType<SequenceFlow>(process.GetFlowElement("flowApproved"));

        Assert.Equal("${approved && amount > 1000}", approved.ConditionExpression);
        Assert.Equal("同意", approved.Name);

        var rejected = Assert.IsType<SequenceFlow>(process.GetFlowElement("flowRejected"));

        Assert.Null(rejected.ConditionExpression);
    }

    private static void AssertGraphicalInformation(BpmnModel model)
    {
        Assert.True(model.LocationMap.ContainsKey("approve"));
        Assert.Equal(300d, model.LocationMap["approve"].X);
        Assert.Equal(80d, model.LocationMap["approve"].Y);
        Assert.Equal(100d, model.LocationMap["approve"].Width);
        Assert.Equal(80d, model.LocationMap["approve"].Height);

        var edge = Assert.IsType<BpmnEdge>(model.Edges["flowApproved_di"]);

        Assert.Equal([245d, 345d], edge.Waypoints.Select(waypoint => waypoint.X));
        Assert.Equal([120d, 120d], edge.Waypoints.Select(waypoint => waypoint.Y));
    }

    private static BpmnModel Parse(string xml) =>
        new BpmnXmlConverter().ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static string Write(BpmnModel model)
    {
        using var buffer = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(model, buffer);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>一段"设计器产物"：bpmn-js 的前缀 + activiti 扩展属性 + DI。</summary>
    private static string DesignerOutput() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                          xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                          xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                          xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                          xmlns:activiti="http://activiti.org/bpmn"
                          id="Definitions_1"
                          targetNamespace="http://netbpm/bpmn"
                          exporter="bpmn-js"
                          exporterVersion="18.31.0">
          <bpmn:process id="leave-approval" name="请假申请" isExecutable="true">
            <bpmn:startEvent id="start" name="开始" />
            <bpmn:userTask id="approve" name="审批" activiti:assignee="kermit" activiti:owner="gonzo"
                           activiti:candidateUsers="alice,bob" activiti:candidateGroups="managers,hr"
                           activiti:formKey="approveForm" activiti:dueDate="2030-01-02T03:04:05"
                           activiti:priority="80" activiti:category="leave" activiti:skipExpression="${skipped}" />
            <bpmn:exclusiveGateway id="gw" name="是否同意" default="flowRejected" />
            <bpmn:serviceTask id="notify" name="通知" activiti:class="NetBpm.Demo.NotifyDelegate"
                              activiti:resultVariableName="notifyResult" default="flowNotifyFailed" />
            <bpmn:scriptTask id="tally" name="统计" scriptFormat="javascript"
                             activiti:resultVariable="tallyResult" activiti:autoStoreVariables="true">
              <bpmn:script>if (amount &gt; 100) { tallyResult = amount * 2; }</bpmn:script>
            </bpmn:scriptTask>
            <bpmn:manualTask id="archive" name="归档" />
            <bpmn:endEvent id="end" />
            <bpmn:sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <bpmn:sequenceFlow id="f2" sourceRef="approve" targetRef="gw" />
            <bpmn:sequenceFlow id="flowApproved" name="同意" sourceRef="gw" targetRef="notify">
              <bpmn:conditionExpression xsi:type="bpmn:tFormalExpression">${approved &amp;&amp; amount &gt; 1000}</bpmn:conditionExpression>
            </bpmn:sequenceFlow>
            <bpmn:sequenceFlow id="flowRejected" name="驳回" sourceRef="gw" targetRef="archive" />
            <bpmn:sequenceFlow id="flowNotifyFailed" name="通知失败" sourceRef="notify" targetRef="archive" />
            <bpmn:sequenceFlow id="f3" sourceRef="notify" targetRef="tally" />
            <bpmn:sequenceFlow id="f4" sourceRef="tally" targetRef="archive" />
            <bpmn:sequenceFlow id="f5" sourceRef="archive" targetRef="end" />
          </bpmn:process>
          <bpmndi:BPMNDiagram id="BPMNDiagram_1">
            <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="leave-approval">
              <bpmndi:BPMNShape id="approve_di" bpmnElement="approve">
                <dc:Bounds x="300" y="80" width="100" height="80" />
              </bpmndi:BPMNShape>
              <bpmndi:BPMNEdge id="flowApproved_di" bpmnElement="flowApproved">
                <di:waypoint x="245" y="120" />
                <di:waypoint x="345" y="120" />
              </bpmndi:BPMNEdge>
            </bpmndi:BPMNPlane>
          </bpmndi:BPMNDiagram>
        </bpmn:definitions>
        """;
}

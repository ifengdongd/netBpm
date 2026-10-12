using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;

namespace NetBpm.UnitTests;

/// <summary>
/// P2 的往返保真骨架测试：XML → Model → XML 的语义一致性。
/// <para>完整验收要求 554 个现成语料 100% 解析 + DI 坐标与扩展属性零丢失（见 §8 P2）。</para>
/// </summary>
public sealed class BpmnXmlConverterTests
{
    [Fact]
    public void ConvertToBpmnModel_ShouldParseAllSubsetElements()
    {
        var model = Parse(TestEngine.ParallelGatewayProcess());
        var process = Assert.IsType<Process>(model.GetMainProcess());

        Assert.Equal("parallel", process.Id);
        Assert.Single(process.FindFlowElementsOfType<StartEvent>());
        Assert.Single(process.FindFlowElementsOfType<EndEvent>());
        Assert.Equal(2, process.FindFlowElementsOfType<ServiceTask>().Count());
        Assert.Equal(2, process.FindFlowElementsOfType<ParallelGateway>().Count());
        Assert.Equal(6, process.FindFlowElementsOfType<SequenceFlow>().Count());
    }

    [Fact]
    public void ConvertToBpmnModel_ShouldWireIncomingAndOutgoingFlows()
    {
        var process = Parse(TestEngine.ParallelGatewayProcess()).GetMainProcess()!;

        var fork = Assert.IsType<ParallelGateway>(process.GetFlowElement("fork"));
        Assert.Single(fork.IncomingFlows);
        Assert.Equal(2, fork.OutgoingFlows.Count);

        var join = Assert.IsType<ParallelGateway>(process.GetFlowElement("join"));
        Assert.Equal(2, join.IncomingFlows.Count);
        Assert.Single(join.OutgoingFlows);
    }

    [Fact]
    public void ConvertToBpmnModel_ShouldReadExclusiveGatewayDefaultFlowAndCondition()
    {
        var process = Parse(TestEngine.ExclusiveGatewayProcess()).GetMainProcess()!;

        var gateway = Assert.IsType<ExclusiveGateway>(process.GetFlowElement("gw"));
        Assert.Equal("flowDefault", gateway.DefaultFlow);

        var conditional = Assert.IsType<SequenceFlow>(process.GetFlowElement("flowApproved"));
        Assert.Equal("${approved}", conditional.ConditionExpression);

        var defaultFlow = Assert.IsType<SequenceFlow>(process.GetFlowElement("flowDefault"));
        Assert.Null(defaultFlow.ConditionExpression);
    }

    [Fact]
    public void ConvertToBpmnModel_ShouldReadGraphicalInformation()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                         xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                         xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                         targetNamespace="http://netbpm/test">
              <process id="di" isExecutable="true">
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
              <bpmndi:BPMNDiagram id="diagram">
                <bpmndi:BPMNPlane id="plane" bpmnElement="di">
                  <bpmndi:BPMNShape id="start_di" bpmnElement="start">
                    <dc:Bounds x="100" y="80" width="30" height="30" />
                  </bpmndi:BPMNShape>
                  <bpmndi:BPMNEdge id="f1_di" bpmnElement="f1">
                    <di:waypoint x="130" y="95" />
                    <di:waypoint x="200" y="95" />
                  </bpmndi:BPMNEdge>
                </bpmndi:BPMNPlane>
              </bpmndi:BPMNDiagram>
            </definitions>
            """;

        var model = Parse(xml);

        // DI 坐标必须落到 LocationMap（设计器往返保真的依据，见 §6.2 / 风险 R5）
        Assert.True(model.LocationMap.ContainsKey("start"));
        Assert.Equal(100d, model.LocationMap["start"].X);
        Assert.Equal(30d, model.LocationMap["start"].Height);

        var edge = Assert.IsType<BpmnEdge>(model.Edges["f1_di"]);
        Assert.Equal(2, edge.Waypoints.Count);
    }

    [Fact]
    public void ConvertToXml_ShouldProduceRoundTrippableDocument()
    {
        var original = Parse(TestEngine.ExclusiveGatewayProcess());

        using var buffer = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(original, buffer);

        var reparsed = Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        var process = reparsed.GetMainProcess()!;

        Assert.Equal("exclusive", process.Id);
        Assert.Equal("${approved}", Assert.IsType<SequenceFlow>(process.GetFlowElement("flowApproved")).ConditionExpression);
        Assert.Equal("flowDefault", Assert.IsType<ExclusiveGateway>(process.GetFlowElement("gw")).DefaultFlow);
        Assert.Equal(2, process.FindFlowElementsOfType<ServiceTask>().Count());
    }

    [Fact]
    public void ConvertToBpmnModel_ShouldRejectOutOfSubsetElement()
    {
        // §1.3 的排除项必须显式失败，不能静默忽略（否则"上线才发现问题"）
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="bad" isExecutable="true">
                <startEvent id="start" />
                <subProcess id="sub" />
                <endEvent id="end" />
              </process>
            </definitions>
            """;

        var exception = Assert.Throws<BpmnXmlParseException>(() => Parse(xml));
        Assert.Contains("subProcess", exception.Message, StringComparison.Ordinal);
    }

    private static BpmnModel Parse(string xml) =>
        new BpmnXmlConverter().ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
}

using NetBpm.Workflow;

namespace NetBpm.UnitTests;

/// <summary>
/// 流程图 SVG 渲染：没有 bpmndi 时给说明而不是白图；有坐标时按流转状态着色。
/// </summary>
/// <remarks>
/// 这里的断言落在「能不能读懂」而不是「有没有产出字符串」：
/// 一张 20×20 的空白 SVG 在调用方眼里就是坏图，不会有人想到去怪 BPMN 定义。
/// </remarks>
public sealed class BpmnDiagramSvgRendererTests
{
    /// <summary>带图形信息的最小流程：开始 → 申请 → 结束。</summary>
    private const string WithDiagram = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                     xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                     xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                     targetNamespace="http://netbpm.dev/test">
          <process id="leave" isExecutable="true">
            <startEvent id="start" name="开始"/>
            <userTask id="apply" name="申请"/>
            <endEvent id="end" name="结束"/>
            <sequenceFlow id="flow1" sourceRef="start" targetRef="apply"/>
            <sequenceFlow id="flow2" sourceRef="apply" targetRef="end"/>
          </process>
          <bpmndi:BPMNDiagram id="diagram">
            <bpmndi:BPMNPlane id="plane" bpmnElement="leave">
              <bpmndi:BPMNShape id="shape_start" bpmnElement="start">
                <dc:Bounds x="100" y="100" width="36" height="36"/>
              </bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="shape_apply" bpmnElement="apply">
                <dc:Bounds x="200" y="80" width="100" height="80"/>
              </bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="shape_end" bpmnElement="end">
                <dc:Bounds x="360" y="100" width="36" height="36"/>
              </bpmndi:BPMNShape>
              <bpmndi:BPMNEdge id="edge_flow1" bpmnElement="flow1">
                <di:waypoint x="136" y="118"/>
                <di:waypoint x="200" y="120"/>
              </bpmndi:BPMNEdge>
              <bpmndi:BPMNEdge id="edge_flow2" bpmnElement="flow2">
                <di:waypoint x="300" y="120"/>
                <di:waypoint x="360" y="118"/>
              </bpmndi:BPMNEdge>
            </bpmndi:BPMNPlane>
          </bpmndi:BPMNDiagram>
        </definitions>
        """;

    /// <summary>与 <see cref="WithDiagram"/> 同结构，但没有任何 bpmndi 坐标。</summary>
    private const string WithoutDiagram = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     targetNamespace="http://netbpm.dev/test">
          <process id="leave" isExecutable="true">
            <startEvent id="start" name="开始"/>
            <userTask id="apply" name="申请"/>
            <endEvent id="end" name="结束"/>
            <sequenceFlow id="flow1" sourceRef="start" targetRef="apply"/>
          </process>
        </definitions>
        """;

    [Fact]
    public void RenderSvg_ShouldExplainInsteadOfDrawBlank_WhenDefinitionHasNoDiagram()
    {
        var svg = BpmnDiagramSvgRenderer.RenderSvg(WithoutDiagram);

        Assert.Contains("缺少图形信息", svg, StringComparison.Ordinal);

        // 说明同时放进 title / desc：页面里也许只是一块灰，日志与调用方要能识别出来
        Assert.Contains("<title>", svg, StringComparison.Ordinal);
        Assert.Contains("<desc>", svg, StringComparison.Ordinal);

        // 占位图不该带任何流转着色，否则看起来像「这单已经走过了」
        Assert.DoesNotContain("stroke=\"#dc2626\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("fill=\"#fee2e2\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSvg_ShouldHighlightVisitedAndActiveNodes()
    {
        var svg = BpmnDiagramSvgRenderer.RenderSvg(
            WithDiagram,
            new DiagramHighlight(["start", "apply"], ["apply"]));

        Assert.Contains("申请", svg, StringComparison.Ordinal);

        Assert.Contains("fill=\"#fef2f2\"", svg, StringComparison.Ordinal); // 走过的节点
        Assert.Contains("fill=\"#fee2e2\"", svg, StringComparison.Ordinal); // 当前停留的节点
        Assert.Contains("stroke=\"#dc2626\"", svg, StringComparison.Ordinal); // 走过节点 / 连线的描边

        Assert.DoesNotContain("缺少图形信息", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSvg_ShouldStayPlain_WhenHighlightIsNone()
    {
        var svg = BpmnDiagramSvgRenderer.RenderSvg(WithDiagram, DiagramHighlight.None);

        Assert.Contains("申请", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke=\"#dc2626\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("fill=\"#fee2e2\"", svg, StringComparison.Ordinal);
    }
}

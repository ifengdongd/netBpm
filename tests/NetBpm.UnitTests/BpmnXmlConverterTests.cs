using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;

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
    public void ConvertToBpmnModel_ShouldParseInclusiveGateway()
    {
        var process = Parse(TestEngine.InclusiveGatewayProcess()).GetMainProcess()!;

        var fork = Assert.IsType<InclusiveGateway>(process.GetFlowElement("inclFork"));
        Assert.Single(fork.IncomingFlows);
        Assert.Equal(3, fork.OutgoingFlows.Count);
        Assert.Equal("flowDefault", fork.DefaultFlow);

        var join = Assert.IsType<InclusiveGateway>(process.GetFlowElement("inclJoin"));
        Assert.Equal(3, join.IncomingFlows.Count);
        Assert.Single(join.OutgoingFlows);

        // 条件表达式照常读入
        var conditional = Assert.IsType<SequenceFlow>(process.GetFlowElement("flowA"));
        Assert.Equal("${a}", conditional.ConditionExpression);
    }

    [Fact]
    public void ConvertToBpmnModel_ShouldParseEventBasedGateway()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         targetNamespace="http://netbpm/test">
              <process id="evt" isExecutable="true">
                <startEvent id="start" />
                <eventBasedGateway id="evtGw" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="evtGw" />
                <sequenceFlow id="f2" sourceRef="evtGw" targetRef="end" />
              </process>
            </definitions>
            """;

        var process = Parse(xml).GetMainProcess()!;

        var gateway = Assert.IsType<EventGateway>(process.GetFlowElement("evtGw"));
        Assert.Single(gateway.OutgoingFlows);
    }

    [Fact]
    public void ConvertToXml_ShouldRoundTripInclusiveGatewayDefaultFlow()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         targetNamespace="http://netbpm/test">
              <process id="incl" isExecutable="true">
                <startEvent id="start" />
                <inclusiveGateway id="inclGw" default="flowDefault" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="inclGw" />
                <sequenceFlow id="flowConditional" sourceRef="inclGw" targetRef="end">
                  <conditionExpression xsi:type="tFormalExpression">${a}</conditionExpression>
                </sequenceFlow>
                <sequenceFlow id="flowDefault" sourceRef="inclGw" targetRef="end" />
              </process>
            </definitions>
            """;

        var model = Parse(xml);

        using var buffer = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(model, buffer);

        var converted = Encoding.UTF8.GetString(buffer.ToArray());

        Assert.Contains("inclusiveGateway", converted, StringComparison.Ordinal);
        Assert.Contains("default=\"flowDefault\"", converted, StringComparison.Ordinal);

        // 再解析一次：默认流必须还在（往返零丢失）
        var roundTripped = Parse(converted).GetMainProcess()!;
        var gateway = Assert.IsType<InclusiveGateway>(roundTripped.GetFlowElement("inclGw"));

        Assert.Equal("flowDefault", gateway.DefaultFlow);
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
    public void Documentation_ShouldBeParsedAndRoundTripped()
    {
        // documentation 挂在**任意** BPMN 元素上，落库只用两处
        // （ACT_RE_PROCDEF.DESCRIPTION_ = 流程级、ACT_HI_TASKINST.DESCRIPTION_ = userTask 级）。
        // 解析必须全覆盖（否则往返丢字段），Trim 首尾但保留内部换行与缩进。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         targetNamespace="http://netbpm/test">
              <process id="doc" isExecutable="true">
                <documentation>
                  流程说明
                    第二行缩进
                </documentation>
                <startEvent id="start" />
                <userTask id="approve">
                  <documentation>审批环节说明</documentation>
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve">
                  <documentation>连线上的文档（不落任何表，但往返不能丢）</documentation>
                </sequenceFlow>
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var model = Parse(xml);
        var process = model.GetMainProcess()!;

        // Trim 首尾、内部换行与缩进原样保留（对拍真值里的文档就是这样带缩进的原文）
        Assert.StartsWith("流程说明", process.Documentation, StringComparison.Ordinal);
        Assert.EndsWith("第二行缩进", process.Documentation, StringComparison.Ordinal);
        Assert.Contains('\n', process.Documentation!);
        Assert.Equal(
            "审批环节说明",
            Assert.IsType<UserTask>(process.GetFlowElement("approve")).Documentation);
        Assert.Equal(
            "连线上的文档（不落任何表，但往返不能丢）",
            Assert.IsType<SequenceFlow>(process.GetFlowElement("f1")).Documentation);

        // 往返：写回后重新解析，三处文档都还在
        using var buffer = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(model, buffer);

        var reparsed = Parse(Encoding.UTF8.GetString(buffer.ToArray())).GetMainProcess()!;

        Assert.Equal(process.Documentation, reparsed.Documentation);
        Assert.Equal(
            "审批环节说明",
            Assert.IsType<UserTask>(reparsed.GetFlowElement("approve")).Documentation);
        Assert.Equal(
            "连线上的文档（不落任何表，但往返不能丢）",
            Assert.IsType<SequenceFlow>(reparsed.GetFlowElement("f1")).Documentation);
    }

    [Fact]
    public void Gateway_ShouldParseAsyncJobAttributes()
    {
        // 异步断点是令牌推进层面的语义，**网关上也可以配**（Activiti 的 activiti:async 写在
        // exclusiveGateway 上会真的停在网关前）。只让活动读它就变成「配置了但没做」。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="asyncGw" isExecutable="true">
                <startEvent id="start" />
                <exclusiveGateway id="gw" default="flow2" activiti:async="true" />
                <userTask id="a" />
                <userTask id="b" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="gw" />
                <sequenceFlow id="flow2" sourceRef="gw" targetRef="b" />
                <sequenceFlow id="f2" sourceRef="gw" targetRef="a" />
                <sequenceFlow id="f3" sourceRef="a" targetRef="end" />
                <sequenceFlow id="f4" sourceRef="b" targetRef="end" />
              </process>
            </definitions>
            """;

        var model = Parse(xml);
        var gateway = Assert.IsType<ExclusiveGateway>(model.GetMainProcess()!.GetFlowElement("gw"));

        Assert.True(gateway.AsyncBefore);      // activiti:async 等价于 asyncBefore
        Assert.False(gateway.AsyncAfter);
        Assert.True(gateway.Exclusive);        // 缺省独占

        // 往返：旧写法 async 转成 asyncBefore 写回，语义不变
        using var buffer = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(model, buffer);

        var reparsed = Assert.IsType<ExclusiveGateway>(
            Parse(Encoding.UTF8.GetString(buffer.ToArray())).GetMainProcess()!.GetFlowElement("gw"));

        Assert.True(reparsed.AsyncBefore);
        Assert.Equal("flow2", reparsed.DefaultFlow);
    }

    [Fact]
    public void Documentation_ShouldNotSwallowFollowingSiblingElements()
    {
        // 回归：读文本用 ReadElementContentAsString 会把 reader 推进到结束标签之后，
        // 外层扫描循环再 Read 一次就跳过了下一个兄弟元素 ——
        // 表现为「紧随 <documentation> 之后的元素被当成子集外元素而拒绝」。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         targetNamespace="http://netbpm/test">
              <process id="docorder" isExecutable="true">
                <documentation>流程说明</documentation>
                <startEvent id="start" />
                <userTask id="approve">
                  <documentation>审批</documentation>
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var process = Parse(xml).GetMainProcess()!;

        // start / approve / end + 两条连线
        Assert.Equal(5, process.FlowElements.Count);
        Assert.NotNull(process.GetFlowElement("start"));
        Assert.NotNull(process.GetFlowElement("f1"));
        Assert.NotNull(process.GetFlowElement("f2"));
    }

    [Fact]
    public void FormProperty_ShouldRoundTripWithoutLoss()
    {
        // 回归：表单属性（activiti:formProperty）保存后重新加载为空 ——
        // 模型此前不承载 formProperty，解析时被静默丢弃，落库重序列化后永久丢失。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="form" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" name="审批" activiti:formKey="approvalForm">
                  <extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" readable="true" writable="true">
                      <activiti:value id="approved" name="同意" />
                      <activiti:value id="rejected" name="驳回" />
                    </activiti:formProperty>
                    <activiti:formProperty id="remark" name="备注" type="string" expression="${remark}" variable="remark" />
                  </extensionElements>
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var process = Parse(xml).GetMainProcess()!;
        var userTask = Assert.IsType<UserTask>(process.GetFlowElement("approve"));

        Assert.Equal(2, userTask.FormProperties.Count);

        var enumProperty = userTask.FormProperties[0];
        Assert.Equal("approvalResult", enumProperty.Id);
        Assert.Equal("审批结果", enumProperty.Name);
        Assert.Equal("enum", enumProperty.Type);
        Assert.True(enumProperty.Required);
        Assert.True(enumProperty.Readable);
        Assert.True(enumProperty.Writable);
        Assert.Equal(2, enumProperty.Values.Count);
        Assert.Equal(("approved", "同意"), (enumProperty.Values[0].Id, enumProperty.Values[0].Name));
        Assert.Equal(("rejected", "驳回"), (enumProperty.Values[1].Id, enumProperty.Values[1].Name));

        var stringProperty = userTask.FormProperties[1];
        Assert.Equal("remark", stringProperty.Id);
        Assert.Equal("${remark}", stringProperty.Expression);
        Assert.Equal("remark", stringProperty.Variable);
        Assert.Null(stringProperty.Required);

        // 落库走「模型 → 重新序列化」，所以序列化后的 XML 必须仍带全部表单属性
        using var buffer = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(Parse(xml), buffer);
        var converted = Encoding.UTF8.GetString(buffer.ToArray());

        Assert.Contains("activiti:formProperty", converted, StringComparison.Ordinal);
        Assert.Contains("activiti:value", converted, StringComparison.Ordinal);

        var roundTripped = Assert.IsType<UserTask>(Parse(converted).GetMainProcess()!.GetFlowElement("approve"));
        Assert.Equal(2, roundTripped.FormProperties.Count);
        Assert.Equal("approvalResult", roundTripped.FormProperties[0].Id);
        Assert.Equal(2, roundTripped.FormProperties[0].Values.Count);
        Assert.Equal("remark", roundTripped.FormProperties[1].Id);
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

    [Fact]
    public void ProcessLevelExtensionElements_ShouldBeRecordedAndRejectedByValidationRule()
    {
        // 回归：流程级 extensionElements（设计器「执行监听器」组写在 Process 上）曾把
        // <activiti:executionListener> 撞上解析器的「元素 <executionListener> 不在本子集
        // 支持范围内」误导性报错。现在应记到 Process 上，由校验规则给出准确拒绝原因。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="withListener" isExecutable="true">
                <extensionElements>
                  <activiti:executionListener event="start" class="A" />
                </extensionElements>
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        var process = Parse(xml).GetMainProcess()!;

        var listener = Assert.Single(process.ExecutionListeners);
        Assert.Equal("start", listener.Event);
        Assert.Equal("A", listener.ClassName);

        // 监听器已由引擎执行：不再被校验拒绝（形状由 listener-configuration 规则校验）
        Assert.DoesNotContain(new ProcessValidator().Validate(process), error => error.Rule == "unsupported-capability");
    }

    [Fact]
    public void ToleratedElementsWithChildren_ShouldBeSkippedWholeWithoutFalseRejection()
    {
        // 回归：容忍元素（laneSet / textAnnotation 等）若只跳过开始标签、不消费子树，
        // 其子元素会被流程级循环当成子集外元素拒绝 —— 必须整棵跳过。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                         targetNamespace="http://netbpm/test">
              <process id="withArtifacts" isExecutable="true">
                <laneSet id="ls">
                  <lane id="lane1" name="审批组">
                    <flowNodeRef>start</flowNodeRef>
                  </lane>
                </laneSet>
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
                <textAnnotation id="note">
                  <text>流程说明</text>
                </textAnnotation>
                <association id="assoc1" sourceRef="note" targetRef="start" />
              </process>
            </definitions>
            """;

        var process = Parse(xml).GetMainProcess()!;

        Assert.NotNull(process.GetFlowElement("start"));
        Assert.NotNull(process.GetFlowElement("end"));
    }

    [Fact]
    public void ProcessLevelListeners_ShouldRoundTripThroughSerialization()
    {
        // 回归：流程级 extensionElements 里的执行监听器曾被 WriteProcess 丢弃
        //（写出条件只看 EventListeners），部署 → 落库 → 回显后执行监听器全部消失。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="mixed" isExecutable="true">
                <extensionElements>
                  <activiti:executionListener event="start" delegateExpression="${P}" />
                  <activiti:executionListener event="end" delegateExpression="${P}" />
                  <activiti:eventListener events="TASK_COMPLETED" delegateExpression="${C}" />
                </extensionElements>
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        var model = Parse(xml);
        var process = model.GetMainProcess()!;
        Assert.Equal(2, process.ExecutionListeners.Count);
        Assert.Single(process.EventListeners);

        var output = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(model, output);
        output.Position = 0;
        var roundTripped = Parse(Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(2, roundTripped.GetMainProcess()!.ExecutionListeners.Count);
        Assert.Single(roundTripped.GetMainProcess()!.EventListeners);
    }

    /// <summary>
    /// 未知扩展元素必须原样往返：解析捕获 → 序列化写回 → 再解析结构一致。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 回归：解析侧早就把「认不出来的扩展」整体收下了（<c>CaptureExtensionElement</c>），
    /// 但写侧 <c>WriteExtensionElements</c> 只写已知的四类（执行/任务监听器、字段注入、表单属性），
    /// 从不遍历 <c>ExtensionElements</c> —— 于是设计器每保存一次，平台自定义扩展
    /// （<c>netbpm:parameters</c> 这类）就静默丢一次，而且还会吐出一个空的 <c>&lt;extensionElements/&gt;</c>。
    /// </para>
    /// <para>
    /// 断言打在「三层结构」上（元素名 / 命名空间 URI / 属性 / 文本 / 子元素），
    /// 只断言 <c>Contains("netbpm:parameters")</c> 的话，丢了属性或文本一样能过。
    /// </para>
    /// </remarks>
    [Fact]
    public void UnknownExtensionElements_ShouldRoundTripWithoutLoss()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:netbpm="http://netbpm.io/schema"
                         targetNamespace="http://netbpm/test">
              <process id="ext" isExecutable="true">
                <extensionElements>
                  <netbpm:catalog category="leave" />
                </extensionElements>
                <startEvent id="start" />
                <userTask id="approve" name="审批">
                  <extensionElements>
                    <netbpm:parameters>
                      <netbpm:parameter key="amount" required="true">金额</netbpm:parameter>
                    </netbpm:parameters>
                  </extensionElements>
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var model = Parse(xml);
        var process = model.GetMainProcess()!;
        var task = Assert.IsType<UserTask>(process.GetFlowElement("approve"));

        // ① 解析侧：流程级与元素级的未知扩展都被捕获（不能静默丢）
        var catalog = Assert.Single(process.ExtensionElements);
        Assert.Equal("catalog", catalog.ElementName);
        Assert.Equal("http://netbpm.io/schema", catalog.NamespaceUri);

        var parameters = Assert.Single(task.ExtensionElements);
        Assert.Equal("parameters", parameters.ElementName);
        Assert.Equal("http://netbpm.io/schema", parameters.NamespaceUri);

        var parameter = Assert.Single(parameters.ExtensionElements);
        Assert.Equal("parameter", parameter.ElementName);
        Assert.Equal("金额", parameter.ElementText);
        Assert.Equal("amount", Attribute(parameter, "key"));
        Assert.Equal("true", Attribute(parameter, "required"));

        // ② 写侧：原样写回
        using var buffer = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(model, buffer);
        var converted = Encoding.UTF8.GetString(buffer.ToArray());

        Assert.Contains("netbpm:parameters", converted, StringComparison.Ordinal);
        Assert.Contains("netbpm:parameter", converted, StringComparison.Ordinal);
        Assert.Contains("netbpm:catalog", converted, StringComparison.Ordinal);
        Assert.Contains("金额", converted, StringComparison.Ordinal);

        // 命名空间声明不能混进 Attributes：否则写回时同一个元素上会再声明一次 xmlns，
        // XmlWriter 会直接抛「同名属性已存在」（这也是解析侧过滤 xmlns 的原因）。
        Assert.DoesNotContain(
            parameters.Attributes.Values.SelectMany(attributes => attributes),
            attribute => attribute.Name is "xmlns" or "netbpm");

        // ③ 再解析：三层结构仍然一致
        var roundTripped = Parse(converted).GetMainProcess()!;
        var roundTask = Assert.IsType<UserTask>(roundTripped.GetFlowElement("approve"));

        Assert.Single(roundTripped.ExtensionElements);
        var roundParameters = Assert.Single(roundTask.ExtensionElements);
        Assert.Equal("http://netbpm.io/schema", roundParameters.NamespaceUri);

        var roundParameter = Assert.Single(roundParameters.ExtensionElements);
        Assert.Equal("金额", roundParameter.ElementText);
        Assert.Equal("amount", Attribute(roundParameter, "key"));
        Assert.Equal("true", Attribute(roundParameter, "required"));
    }

    /// <summary>取扩展元素上「无命名空间前缀」属性的值。</summary>
    private static string? Attribute(ExtensionElement element, string name) =>
        element.GetAttribute(string.Empty, name).FirstOrDefault()?.Value;

    private static BpmnModel Parse(string xml) =>
        new BpmnXmlConverter().ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
}

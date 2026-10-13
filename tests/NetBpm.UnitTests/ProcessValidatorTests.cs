using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;

namespace NetBpm.UnitTests;

/// <summary>P4 校验层：每条规则必须能定位到元素 id。</summary>
public sealed class ProcessValidatorTests
{
    private static readonly ProcessValidator Validator = new();

    [Fact]
    public void ValidProcess_ShouldPass()
    {
        var process = Parse(TestEngine.ParallelGatewayProcess());

        Assert.Empty(Validator.Validate(process));
    }

    [Fact]
    public void NodeWithoutOutgoingFlow_ShouldBeReported()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="p1" isExecutable="true">
                <startEvent id="start" />
                <manualTask id="dangling" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="dangling" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "dangling" && error.Rule == "node-has-outgoing-flow");
        Assert.Contains(errors, error => error.ElementId == "end" && error.Rule == "no-orphan-node");
    }

    [Fact]
    public void ExclusiveGatewayWithoutDefaultFlowAndUnconditionalFlow_ShouldBeReported()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         targetNamespace="http://netbpm/test">
              <process id="p2" isExecutable="true">
                <startEvent id="start" />
                <exclusiveGateway id="gw" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="gw" />
                <sequenceFlow id="f2" sourceRef="gw" targetRef="end">
                  <conditionExpression xsi:type="tFormalExpression">${a}</conditionExpression>
                </sequenceFlow>
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "gw" && error.Rule == "exclusive-gateway-has-selectable-flow");
    }

    [Fact]
    public void InclusiveGatewayProcess_ShouldPass()
    {
        var process = Parse(TestEngine.InclusiveGatewayProcess());

        Assert.Empty(Validator.Validate(process));
    }

    [Fact]
    public void InclusiveGatewayWithoutDefaultFlowAndUnconditionalFlow_ShouldBeReported()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         targetNamespace="http://netbpm/test">
              <process id="p3" isExecutable="true">
                <startEvent id="start" />
                <inclusiveGateway id="inclGw" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="inclGw" />
                <sequenceFlow id="f2" sourceRef="inclGw" targetRef="end">
                  <conditionExpression xsi:type="tFormalExpression">${a}</conditionExpression>
                </sequenceFlow>
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "inclGw" && error.Rule == "inclusive-gateway");
    }

    [Fact]
    public void EventGateway_ShouldBeRejectedInsteadOfSilentlyIgnored()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         targetNamespace="http://netbpm/test">
              <process id="p4" isExecutable="true">
                <startEvent id="start" />
                <eventBasedGateway id="evtGw" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="evtGw" />
                <sequenceFlow id="f2" sourceRef="evtGw" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "evtGw" && error.Rule == "event-gateway-unsupported");
    }

    [Fact]
    public void ServiceTaskWithoutImplementation_ShouldBeReported()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="p3" isExecutable="true">
                <startEvent id="start" />
                <serviceTask id="svc" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="svc" />
                <sequenceFlow id="f2" sourceRef="svc" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "svc" && error.Rule == "required-attributes");
    }

    [Fact]
    public void ExecutionListener_ShouldBeRejectedInsteadOfSilentlyIgnored()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p5" isExecutable="true">
                <startEvent id="start" />
                <manualTask id="work">
                  <extensionElements>
                    <activiti:executionListener event="start" class="SomeListener" />
                  </extensionElements>
                </manualTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="work" />
                <sequenceFlow id="f2" sourceRef="work" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "work" && error.Rule == "unsupported-capability");
    }

    [Fact]
    public void TaskListener_ShouldBeRejectedInsteadOfSilentlyIgnored()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p6" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" activiti:assignee="kermit">
                  <extensionElements>
                    <activiti:taskListener event="complete" class="SomeListener" />
                  </extensionElements>
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "approve" && error.Rule == "unsupported-capability");
    }

    [Fact]
    public void ServiceTaskFieldInjection_ShouldBeRejectedInsteadOfSilentlyIgnored()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p7" isExecutable="true">
                <startEvent id="start" />
                <serviceTask id="svc" activiti:delegateExpression="doWork">
                  <extensionElements>
                    <activiti:field name="url" stringValue="http://example" />
                  </extensionElements>
                </serviceTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="svc" />
                <sequenceFlow id="f2" sourceRef="svc" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "svc" && error.Rule == "unsupported-capability");
    }

    [Fact]
    public void ValidateAndThrow_ShouldThrowWithAllErrors()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="p4" isExecutable="true">
                <manualTask id="only" />
              </process>
            </definitions>
            """;

        var exception = Assert.Throws<ProcessValidationException>(() => Validator.ValidateAndThrow(Parse(xml)));

        Assert.True(exception.Errors.Count >= 3);
    }

    private static Bpmn.Model.Process Parse(string xml) =>
        new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)))
            .GetMainProcess()!;
}

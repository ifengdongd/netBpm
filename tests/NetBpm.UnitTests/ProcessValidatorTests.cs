using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
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
    public void ExecutionListener_ShouldBeAllowed()
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

        Assert.DoesNotContain(errors, error => error.Rule == "unsupported-capability");
    }

    [Fact]
    public void TaskListener_ShouldBeAllowed()
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

        Assert.DoesNotContain(errors, error => error.Rule == "unsupported-capability");
    }

    [Fact]
    public void ServiceTaskFieldInjection_ShouldBeAcceptedNowSupported()
    {
        // B2.1：服务任务字段注入已实现（ServiceTaskFieldInjector），部署期放开；
        // 监听器的字段注入仍被 unsupported-capability 拒绝（见 ServiceTaskFieldInjectionTests）
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

        Assert.DoesNotContain(errors, error => error.ElementId == "svc");
    }

    [Fact]
    public void UserTaskWithoutAssignment_ShouldBeWarningNotBlocking()
    {
        // 「无人办理的用户任务」在 Activiti 里是合法模型 —— 这里只能是提示，不能阻断部署
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="p8" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var process = Parse(xml);
        var warning = Assert.Single(Validator.Validate(process));

        Assert.Equal("user-task-assignment", warning.Rule);
        Assert.Equal("approve", warning.ElementId);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.False(warning.IsError);

        // 只有提示级问题时 ValidateAndThrow 不抛异常，并把这些提示交回调用方
        var warnings = Validator.ValidateAndThrow(process);
        Assert.Single(warnings);
    }

    [Fact]
    public void OrphanNode_ShouldBeWarningNotError()
    {
        // 孤立节点永远不会被执行，但不影响其余路径 —— Activiti 照常部署
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="p9" isExecutable="true">
                <startEvent id="start" />
                <endEvent id="end" />
                <userTask id="lost" activiti:assignee="kermit"
                          xmlns:activiti="http://activiti.org/bpmn" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
                <sequenceFlow id="f2" sourceRef="lost" targetRef="end" />
              </process>
            </definitions>
            """;

        var results = Validator.Validate(Parse(xml));

        Assert.Contains(results, result => result.ElementId == "lost"
            && result.Rule == "no-orphan-node"
            && result.Severity == ValidationSeverity.Warning);
        Assert.DoesNotContain(results, result => result.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void ValidateAndThrow_ShouldThrowOnlyErrorSeverity()
    {
        // 服务任务缺实现方式 = 运行期必然失败 ⇒ 阻断级
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="p10" isExecutable="true">
                <startEvent id="start" />
                <serviceTask id="svc" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="svc" />
                <sequenceFlow id="f2" sourceRef="svc" targetRef="end" />
              </process>
            </definitions>
            """;

        var exception = Assert.Throws<ProcessValidationException>(() => Validator.ValidateAndThrow(Parse(xml)));

        Assert.All(exception.Errors, error => Assert.Equal(ValidationSeverity.Error, error.Severity));
        Assert.Contains(exception.Errors, error => error.Rule == "required-attributes");
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

        // 没有开始事件 + 节点没有出流 = 2 处阻断级问题；
        // 「没有结束事件」「孤立节点」已降为提示级，不再出现在 Errors 里
        Assert.True(exception.Errors.Count >= 2);
        Assert.Contains(exception.Errors, error => error.Rule == "start-and-end-event");
        Assert.Contains(exception.Errors, error => error.Rule == "node-has-outgoing-flow");
    }

    [Fact]
    public void InvalidConditionExpression_ShouldBeRejectedAtDeployTime()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         targetNamespace="http://netbpm/test">
              <process id="p11" isExecutable="true">
                <startEvent id="start" />
                <manualTask id="work" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="work">
                  <conditionExpression xsi:type="tFormalExpression">${a ==}</conditionExpression>
                </sequenceFlow>
                <sequenceFlow id="f2" sourceRef="work" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "f1"
            && error.Rule == "expression-parseable"
            && error.IsError);
    }

    [Fact]
    public void DefaultFlowPointingToForeignFlow_ShouldBeRejected()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         targetNamespace="http://netbpm/test">
              <process id="p12" isExecutable="true">
                <startEvent id="start" />
                <exclusiveGateway id="gw" default="typo" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="gw" />
                <sequenceFlow id="f2" sourceRef="gw" targetRef="end">
                  <conditionExpression xsi:type="tFormalExpression">${a}</conditionExpression>
                </sequenceFlow>
                <sequenceFlow id="f3" sourceRef="gw" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "gw"
            && error.Rule == "default-flow"
            && error.IsError);
    }

    [Fact]
    public void ConditionOnDefaultFlow_ShouldBeWarning()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         targetNamespace="http://netbpm/test">
              <process id="p13" isExecutable="true">
                <startEvent id="start" />
                <exclusiveGateway id="gw" default="f2" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="gw" />
                <sequenceFlow id="f2" sourceRef="gw" targetRef="end">
                  <conditionExpression xsi:type="tFormalExpression">${a}</conditionExpression>
                </sequenceFlow>
              </process>
            </definitions>
            """;

        var results = Validator.Validate(Parse(xml));

        Assert.Contains(results, result => result.ElementId == "gw"
            && result.Rule == "default-flow"
            && result.Severity == ValidationSeverity.Warning);
        Assert.DoesNotContain(results, result => result.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void ProcessIdWithColon_ShouldBeWarning()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="bad:key" isExecutable="true">
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        var results = Validator.Validate(Parse(xml));

        // Activiti 允许任意 id（语料里就有 myProcess{0}），因此只提示、不阻断
        Assert.Contains(results, result => result.Rule == "process-id"
            && result.Severity == ValidationSeverity.Warning);
        Assert.DoesNotContain(results, result => result.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void TimerStartEvent_ShouldBeRejectedInsteadOfSilentlyIgnored()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="p14" isExecutable="true">
                <startEvent id="start">
                  <timerEventDefinition>
                    <timeDuration>PT1H</timeDuration>
                  </timerEventDefinition>
                </startEvent>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "start"
            && error.Rule == "unsupported-event-definition"
            && error.IsError);
    }

    [Fact]
    public void UnknownActivitiAttribute_ShouldBeWarning()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p15" isExecutable="true">
                <startEvent id="start" />
                <serviceTask id="svc" activiti:delegateExpression="doWork" activiti:mysteryFlag="true" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="svc" />
                <sequenceFlow id="f2" sourceRef="svc" targetRef="end" />
              </process>
            </definitions>
            """;

        var results = Validator.Validate(Parse(xml));

        Assert.Contains(results, result => result.ElementId == "svc"
            && result.Rule == "unknown-activiti-attribute"
            && result.Severity == ValidationSeverity.Warning);
        Assert.DoesNotContain(results, result => result.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void AsyncJobAttributes_ShouldBeParsedAndAccepted()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p-async" isExecutable="true">
                <startEvent id="start" />
                <serviceTask id="svc" activiti:delegateExpression="doWork"
                             activiti:asyncBefore="true" activiti:asyncAfter="true" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="svc" />
                <sequenceFlow id="f2" sourceRef="svc" targetRef="end" />
              </process>
            </definitions>
            """;

        var process = Parse(xml);
        var task = Assert.Single(process.FindFlowElementsOfType<ServiceTask>());

        Assert.True(task.AsyncBefore);
        Assert.True(task.AsyncAfter);
        Assert.True(task.Exclusive);

        var results = Validator.Validate(process);

        Assert.DoesNotContain(results, result => result.Rule == "unknown-activiti-attribute");
        Assert.DoesNotContain(results, result => result.Rule == "async-job");
    }

    [Fact]
    public void AsyncJobOnMultiInstance_ShouldBeRejected()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p-async-mi" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" activiti:asyncBefore="true">
                  <multiInstanceLoopCharacteristics isSequential="false" activiti:collection="assignees" />
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var results = Validator.Validate(Parse(xml));

        Assert.Contains(results, result => result.ElementId == "approve"
            && result.Rule == "async-job"
            && result.IsError);
    }

    [Fact]
    public void TaskListenerDeleteEvent_ShouldBeRejectedBecauseNotImplemented()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p16" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" activiti:assignee="kermit">
                  <extensionElements>
                    <activiti:taskListener event="delete" class="SomeListener" />
                  </extensionElements>
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validator.Validate(Parse(xml));

        Assert.Contains(errors, error => error.ElementId == "approve"
            && error.Rule == "listener-configuration"
            && error.IsError);
    }

    private static Bpmn.Model.Process Parse(string xml) =>
        new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)))
            .GetMainProcess()!;
}

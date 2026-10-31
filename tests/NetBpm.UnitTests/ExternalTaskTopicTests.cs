using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;

namespace NetBpm.UnitTests;

/// <summary>
/// <c>activiti:topic</c>（M12 外部任务）的解析与部署期校验。
/// <para>
/// 这里守的是<b>独立部署的前提</b>：BPMN 里声明了 topic，引擎才知道这一步要交给外部 worker。
/// 解析丢字段、校验放错行，都会让「声明了 topic 的节点」在运行时变成另一回事 ——
/// 要么被当成没实现而拒绝部署，要么被当成进程内委托去解析（然后报找不到委托）。
/// </para>
/// </summary>
public sealed class ExternalTaskTopicTests
{
    private static readonly ProcessValidator Validator = new();

    [Fact]
    public void ServiceTask_ShouldReadTopic_AndMarkExternalImplementation()
    {
        var process = Parse(WithServiceTask("""<serviceTask id="svc" activiti:topic="invoice" />"""));

        var task = Assert.Single(process.FindFlowElementsOfType<ServiceTask>());

        Assert.Equal("invoice", task.Topic);
        Assert.Equal(ServiceTaskImplementationType.ExternalTopic, task.ImplementationType);
    }

    [Fact]
    public void Topic_ShouldRoundTrip()
    {
        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(
                WithServiceTask("""<serviceTask id="svc" name="开票" activiti:topic="invoice" />"""))));

        using var buffer = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(model, buffer);

        var reparsed = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(buffer.ToArray()))
            .GetMainProcess()!;

        var task = Assert.Single(reparsed.FindFlowElementsOfType<ServiceTask>());

        // 往返丢字段是最隐蔽的一类缺陷：设计器保存一次就把 topic 抹了，
        // 线上表现为「节点突然开始报找不到委托」而不是「配置丢了」
        Assert.Equal("invoice", task.Topic);
        Assert.Equal("开票", task.Name);
        Assert.Equal(ServiceTaskImplementationType.ExternalTopic, task.ImplementationType);
    }

    [Fact]
    public void Topic_ShouldNotBeReportedAsUnknownAttribute()
    {
        var errors = Validator.Validate(Parse(
            WithServiceTask("""<serviceTask id="svc" activiti:topic="invoice" />""")));

        Assert.DoesNotContain(errors, error => error.Rule == "unknown-activiti-attribute");
    }

    [Fact]
    public void ServiceTaskWithoutImplementation_ShouldBeReported()
    {
        var errors = Validator.Validate(Parse(WithServiceTask("""<serviceTask id="svc" />""")));

        Assert.Contains(errors, error => error.ElementId == "svc" && error.Rule == "required-attributes");
    }

    [Fact]
    public void ServiceTaskWithBothClassAndTopic_ShouldBeReported()
    {
        // 同时写两种：执行时会按固定优先级取一种，另一种被静默忽略 ——
        // 部署期就该拦下，否则「我明明配了 topic」会成为最难排查的一类问题
        var errors = Validator.Validate(Parse(WithServiceTask(
            """<serviceTask id="svc" activiti:class="Demo.Handler" activiti:topic="invoice" />""")));

        Assert.Contains(errors, error => error is { ElementId: "svc", Rule: "required-attributes" });
    }

    [Fact]
    public void ServiceTaskWithOnlyTopic_ShouldPass()
    {
        var errors = Validator.Validate(Parse(
            WithServiceTask("""<serviceTask id="svc" activiti:topic="invoice" />""")));

        Assert.Empty(errors);
    }

    /// <summary>把 serviceTask 塞进一条最小可部署流程。</summary>
    private static string WithServiceTask(string serviceTaskXml) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="p_ext" isExecutable="true">
            <startEvent id="start" />
            {{serviceTaskXml}}
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="svc" />
            <sequenceFlow id="f2" sourceRef="svc" targetRef="end" />
          </process>
        </definitions>
        """;

    private static Process Parse(string xml) =>
        new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)))
            .GetMainProcess()!;
}

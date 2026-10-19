using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Persistence.Deployment;
using NetBpm.Runtime;

namespace NetBpm.UnitTests;

/// <summary>
/// 服务任务字段注入（activiti:field，任务清单 B2.1）与部署原始字节（B2.5）。
/// </summary>
public sealed class ServiceTaskFieldInjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FieldInjection_ShouldInjectStringLiteralAndExpressionValue()
    {
        var engine = TestEngine.Create();
        var injectable = new InjectableDelegate();

        engine.DelegateResolver.Register("injectable", injectable);

        await engine.StartProcessAsync(
            FieldInjectionProcess(),
            new Dictionary<string, object?> { ["days"] = 5 },
            Ct);

        // stringValue 注入字面量，expression 求值后注入（int 属性）
        Assert.Equal(["你好:5"], injectable.Log);
    }

    [Fact]
    public async Task FieldInjection_UnknownMember_ShouldFailLoudly()
    {
        var engine = TestEngine.Create();
        engine.DelegateResolver.Register("injectable", new InjectableDelegate());

        var error = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine
                .StartProcessAsync(FieldInjectionProcess(unknownFieldName: true), ct: Ct)
                .AsTask());

        Assert.Contains("NoSuchProperty", error.Message);
    }

    [Fact]
    public async Task FieldInjection_MissingValue_ShouldFailLoudly()
    {
        var engine = TestEngine.Create();
        engine.DelegateResolver.Register("injectable", new InjectableDelegate());

        var error = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine
                .StartProcessAsync(FieldInjectionProcess(missingValue: true), ct: Ct)
                .AsTask());

        Assert.Contains("缺少取值", error.Message);
    }

    [Fact]
    public void Validator_ServiceTaskFieldInjection_ShouldBeAccepted()
    {
        var errors = Validate(FieldInjectionProcess());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validator_ListenerFieldInjection_ShouldStillBeRejected()
    {
        // 监听器的字段注入仍属未实现：解析进模型但不执行，部署期显式拒绝（防静默忽略）
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="listener-field" isExecutable="true">
                <startEvent id="start">
                  <extensionElements>
                    <activiti:executionListener event="start" delegateExpression="logger">
                      <activiti:field name="x" stringValue="y" />
                    </activiti:executionListener>
                  </extensionElements>
                </startEvent>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validate(xml);

        var error = Assert.Single(errors);
        Assert.Equal("unsupported-capability", error.Rule);
        Assert.Contains("字段注入", error.Message);
    }

    // ─────────────────────────── B2.5 部署原始字节 ───────────────────────────

    [Fact]
    public async Task DeployRawBytes_ShouldPreserveOriginalXml()
    {
        // 重新序列化会丢掉 XML 注释 —— 原始字节透传后必须原样保存
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- 部署者备注：注释只在原始字节里能存活 -->
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="raw-bytes" isExecutable="true">
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        var converter = new BpmnXmlConverter();
        var model = converter.ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var store = new InMemoryProcessDefinitionStore(new NetBpm.Engine.InMemoryIdGenerator(), converter);
        var definition = await store.DeployAsync(
            model,
            "test.bpmn20.xml",
            rawResourceBytes: Encoding.UTF8.GetBytes(xml),
            ct: Ct);

        var stored = await store.GetResourceXmlAsync(definition.Id, Ct);

        Assert.NotNull(stored);
        Assert.Contains("部署者备注", stored);
        Assert.Equal(xml, stored);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static IReadOnlyList<ValidationError> Validate(string bpmnXml)
    {
        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        var process = model.GetMainProcess()!;

        return new ProcessValidator().Validate(process);
    }

    private static string FieldInjectionProcess(
        bool unknownFieldName = false,
        bool missingValue = false) =>
        $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="field-injection" isExecutable="true">
            <startEvent id="start" />
            <serviceTask id="work" activiti:delegateExpression="injectable">
              <extensionElements>
                <activiti:field name="{{(unknownFieldName ? "NoSuchProperty" : "Greeting")}}" stringValue="你好" />
                {{(missingValue
                    ? @"<activiti:field name=""Threshold"" />"
                    : @"<activiti:field name=""Threshold"" expression=""${days}"" />")}}
              </extensionElements>
            </serviceTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="work" />
            <sequenceFlow id="f2" sourceRef="work" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>带可注入属性的测试委托：把收到的字段值记录下来。</summary>
    internal sealed class InjectableDelegate : IServiceTaskDelegate
    {
        public string? Greeting { get; set; }

        public int Threshold { get; set; }

        public List<string> Log { get; } = [];

        public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
        {
            Log.Add($"{Greeting}:{Threshold}");
            return ValueTask.CompletedTask;
        }
    }
}

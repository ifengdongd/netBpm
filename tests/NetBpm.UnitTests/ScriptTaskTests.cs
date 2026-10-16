using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;

namespace NetBpm.UnitTests;

/// <summary>
/// 脚本任务（scriptTask，scriptFormat="csharp"）的运行期与部署期语义。
/// <para>
/// 运行期用「脚本任务 → 捕获委托 → 结束」的链路，通过捕获委托观察脚本写回的流程变量；
/// 部署期直接对 <see cref="ProcessValidator"/> 跑 <c>script-task</c> 规则。
/// </para>
/// </summary>
public sealed class ScriptTaskTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResultVariable_ShouldStoreLastExpressionValue()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            ScriptProcess("var answer = 6 * 7;\nanswer", resultVariable: "answer"),
            ct: Ct);

        var captured = engine.DelegateResolver.CapturedVariables.Single();
        Assert.Equal(42, captured["answer"]);
    }

    [Fact]
    public async Task VarsSnapshot_ShouldExposeProcessVariables()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            ScriptProcess("vars.amount * 2", resultVariable: "doubled"),
            new Dictionary<string, object?> { ["amount"] = 10 },
            Ct);

        var captured = engine.DelegateResolver.CapturedVariables.Single();
        Assert.Equal(20, captured["doubled"]);
    }

    [Fact]
    public async Task Script_ShouldUseFrameworkTypesViaImports()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            ScriptProcess("new[] { 1, 2, 3 }.Sum(x => x) + Math.Max(4, 2)", resultVariable: "total"),
            ct: Ct);

        var captured = engine.DelegateResolver.CapturedVariables.Single();
        Assert.Equal(10, captured["total"]);
    }

    [Fact]
    public async Task ExecutionGlobal_ShouldExposeEngineApi()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            ScriptProcess(
                "(await execution.GetVariableAsync(\"ticket\")).ToString() + \"-processed\"",
                resultVariable: "trace"),
            new Dictionary<string, object?> { ["ticket"] = "T-1" },
            Ct);

        var captured = engine.DelegateResolver.CapturedVariables.Single();
        Assert.Equal("T-1-processed", captured["trace"]);
    }

    [Fact]
    public async Task AutoStoreVariables_ShouldStoreScriptVariables()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            ScriptProcess("int computed = 21 + 21;\ncomputed", autoStore: true),
            ct: Ct);

        var captured = engine.DelegateResolver.CapturedVariables.Single();
        Assert.Equal(42, captured["computed"]);
    }

    [Fact]
    public async Task WithoutAutoStoreVariables_ShouldNotStoreScriptVariables()
    {
        // 与 Activiti 一致：autoStoreVariables 默认为 false，脚本里的变量只是脚本局部状态
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            ScriptProcess("int computed = 21 + 21;\ncomputed"),
            ct: Ct);

        var captured = engine.DelegateResolver.CapturedVariables.Single();
        Assert.False(captured.ContainsKey("computed"));
        Assert.Empty(captured);
    }

    [Fact]
    public async Task UnsupportedScriptFormat_ShouldFailLoudly()
    {
        // 本子集不内置 JavaScript 引擎 —— 未知 / 缺省格式必须响亮失败，不能静默跳过
        var engine = TestEngine.Create();

        var error = await Assert.ThrowsAsync<EngineException>(
            () => engine.StartProcessAsync(ScriptProcess("1 + 1", scriptFormat: "javascript"), ct: Ct).AsTask());

        Assert.Contains("scriptFormat", error.Message);
    }

    [Fact]
    public async Task MissingScript_ShouldFailLoudly()
    {
        var engine = TestEngine.Create();

        var error = await Assert.ThrowsAsync<EngineException>(
            () => engine.StartProcessAsync(ScriptProcess(string.Empty), ct: Ct).AsTask());

        Assert.Contains("没有脚本内容", error.Message);
    }

    [Fact]
    public async Task ScriptCompileError_ShouldBeWrappedAsEngineException()
    {
        var engine = TestEngine.Create();

        var error = await Assert.ThrowsAsync<EngineException>(
            () => engine.StartProcessAsync(ScriptProcess("int x = ;"), ct: Ct).AsTask());

        Assert.Contains("编译失败", error.Message);
    }

    [Fact]
    public async Task ScriptRuntimeError_ShouldBeWrappedAsEngineException()
    {
        var engine = TestEngine.Create();

        var error = await Assert.ThrowsAsync<EngineException>(
            () => engine.StartProcessAsync(ScriptProcess("""throw new InvalidOperationException("boom");"""), ct: Ct).AsTask());

        Assert.Contains("执行失败", error.Message);
        Assert.Contains("boom", error.InnerException?.Message);
    }

    // ─────────────────────────── 部署期校验 ───────────────────────────

    [Fact]
    public void Validator_ValidCsharpScriptTask_ShouldPass()
    {
        var errors = ValidateScriptTask(ScriptProcess("1 + 1"));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validator_MissingScript_ShouldBeRejected()
    {
        var errors = ValidateScriptTask(ScriptProcess(string.Empty));

        var error = Assert.Single(errors);
        Assert.Equal("script-task", error.Rule);
        Assert.Equal("calc", error.ElementId);
        Assert.Contains("<script>", error.Message);
    }

    [Fact]
    public void Validator_JavascriptFormat_ShouldBeRejected()
    {
        var errors = ValidateScriptTask(ScriptProcess("1 + 1", scriptFormat: "javascript"));

        var error = Assert.Single(errors);
        Assert.Equal("script-task", error.Rule);
        Assert.Contains("csharp", error.Message);
    }

    [Fact]
    public void Validator_MissingFormat_ShouldBeRejected()
    {
        // Activiti 对缺省 scriptFormat 回退 JavaScript —— 本子集部署期即拒绝
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="script" isExecutable="true">
                <startEvent id="start" />
                <scriptTask id="calc">
                  <script>1 + 1</script>
                </scriptTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="calc" />
                <sequenceFlow id="f2" sourceRef="calc" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = ValidateScriptTask(xml);

        Assert.All(errors, error => Assert.Equal("script-task", error.Rule));
        Assert.Contains(errors, error => error.Message.Contains("(未指定)"));
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static IReadOnlyList<ValidationError> ValidateScriptTask(string bpmnXml)
    {
        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        var process = model.GetMainProcess()!;

        return new ProcessValidator().Validate(process);
    }

    /// <summary>start → scriptTask → 捕获委托 → end。</summary>
    private static string ScriptProcess(
        string script,
        bool autoStore = false,
        string? resultVariable = null,
        string scriptFormat = "csharp")
    {
        var autoStoreAttr = autoStore ? " activiti:autoStoreVariables=\"true\"" : string.Empty;
        var resultAttr = resultVariable is null ? string.Empty : $" activiti:resultVariable=\"{resultVariable}\"";

        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="script" name="脚本任务" isExecutable="true">
                <startEvent id="start" />
                <scriptTask id="calc" scriptFormat="{{scriptFormat}}"{{autoStoreAttr}}{{resultAttr}}>
                  <script><![CDATA[{{script}}]]></script>
                </scriptTask>
                <serviceTask id="capture" activiti:delegateExpression="capture" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="calc" />
                <sequenceFlow id="f2" sourceRef="calc" targetRef="capture" />
                <sequenceFlow id="f3" sourceRef="capture" targetRef="end" />
              </process>
            </definitions>
            """;
    }
}

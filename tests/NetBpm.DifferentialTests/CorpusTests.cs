using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.DifferentialTests.Differential;

namespace NetBpm.DifferentialTests;

/// <summary>
/// 语料自身的体检（不依赖数据库）。
/// <para>
/// 语料是资产：一份语法坏掉、或压根过不了校验的语料，会让对拍结果毫无意义 ——
/// 所以它必须像代码一样被测试。同时它也兜住"生成器改了但语料没重新生成"这类漂移。
/// </para>
/// </summary>
public sealed class CorpusTests
{
    private static readonly IReadOnlyList<CorpusEntry> Corpus = CorpusRepository.Load(TestDatabase.CorpusRoot);

    /// <summary>操作脚本支持的动作（与 <c>DifferentialRunner.ApplyAsync</c> 保持一致）。</summary>
    private static readonly string[] AllowedActionKinds = ["completeTask", "completeAllTasks", "completeAnyTask"];

    [Fact]
    public void Corpus_ShouldContainAuthoredCases()
    {
        Assert.NotEmpty(Corpus);

        // 自建语料必须由脚本生成（§9.3），因此至少要覆盖三类形态
        var groups = Corpus.Select(entry => entry.Group).Distinct(StringComparer.Ordinal).ToList();

        Assert.Contains("authored/gateway", groups);
        Assert.Contains("authored/condition", groups);
        Assert.Contains("authored/impedance", groups);
    }

    [Fact]
    public void EveryCase_ShouldParseAndPassValidation()
    {
        var converter = new BpmnXmlConverter();
        var validator = new ProcessValidator();

        foreach (var entry in Corpus)
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(entry.ReadBpmn()));

            var model = converter.ConvertToBpmnModel(stream);
            var process = model.GetMainProcess()
                ?? throw new InvalidOperationException($"语料 '{entry.FullName}' 没有主流程。");

            // 只有阻断级问题才算「未通过校验」：提示级（孤立节点 / 无人办理等）Activiti 照常部署
            var errors = validator.Validate(process).Where(result => result.IsError).ToList();

            Assert.True(
                errors.Count == 0,
                $"语料 '{entry.FullName}' 未通过校验：{string.Join("；", errors.Select(error => error.Message))}");
        }
    }

    [Fact]
    public void EveryCase_ShouldHaveExecutableActionScript()
    {
        foreach (var entry in Corpus)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Case.Description), $"语料 '{entry.FullName}' 缺少 description（差异清单要靠它给结论）");

            foreach (var action in entry.Case.Actions)
            {
                Assert.True(
                    AllowedActionKinds.Contains(action.Kind),
                    $"语料 '{entry.FullName}' 使用了未知动作 '{action.Kind}'（支持 {string.Join(" / ", AllowedActionKinds)}）");

                // completeAnyTask 按流程实例自行定位待办，不需要 assignee
                if (action.Kind == "completeAnyTask")
                {
                    continue;
                }

                Assert.False(
                    string.IsNullOrWhiteSpace(action.Assignee),
                    $"语料 '{entry.FullName}' 的 {action.Kind} 必须声明 assignee（TaskQuery 要求至少一个条件）");
            }
        }
    }

    [Fact]
    public void EveryCase_ShouldOnlyUsePortableElementTypes()
    {
        // 对拍语料不能出现服务任务/脚本任务：它们依赖两端各自的委托与脚本实现，
        // 属"实现差异"而非"引擎语义差异"（见 DifferentialEngine 的语料约束）。
        // ⚠️ 用带尖括号的标记判断：文档/名称里出现这些词不算（例如 <documentation>serviceTask</documentation>）。
        foreach (var entry in Corpus)
        {
            var bpmn = entry.ReadBpmn();

            Assert.DoesNotContain("<serviceTask", bpmn, StringComparison.Ordinal);
            Assert.DoesNotContain("<scriptTask", bpmn, StringComparison.Ordinal);
            Assert.DoesNotContain("<subProcess", bpmn, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryCase_ShouldDeclareDistinctBusinessKey()
    {
        var names = Corpus.Select(entry => entry.FullName).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }
}

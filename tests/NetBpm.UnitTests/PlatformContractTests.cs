using System.Text.Json;
using NetBpm.Platform.Contracts;

namespace NetBpm.UnitTests;

/// <summary>
/// 对外契约的<b>序列化形状</b>守门：枚举一律 <c>SCREAMING_SNAKE_CASE</c>、属性一律 camelCase、
/// 时间一律 RFC 3339 UTC。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要专门守这个：枚举一旦被序列化成数字（<c>"status": 1</c>），新增一个成员就会让
/// 旧调用方静默读到 0；而 <c>"ACTIVE"</c> 读不懂会直接报错，问题在接入当天就暴露。
/// 数字枚举是「等到某天才发现语义漂移」的典型来源。
/// </para>
/// <para>
/// 序列化选项与宿主对齐：<see cref="JsonSerializerDefaults.Web"/>（camelCase + 大小写不敏感读）。
/// </para>
/// </remarks>
public sealed class PlatformContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ProcessDescriptor_ShouldSerialize_WithCamelCaseAndScreamingSnakeEnums()
    {
        var descriptor = new ProcessDescriptor(
            ProcessKey: "leave_process",
            Name: "请假申请",
            Category: "HR",
            Description: null,
            Icon: null,
            PageUrls: new Dictionary<string, string>(StringComparer.Ordinal) { ["start"] = "/hr/leave/new" },
            Status: ProcessStatus.Active,
            DeployedVersion: 3,
            DefinitionChecksum: "sha256:abc",
            ParameterSchemaVersion: 0,
            EntryPoints: [new ProcessEntryPoint("start", "发起请假", "start", null)],
            Labels: null,
            Capabilities: new ProcessCapabilities(
                CanStart: true,
                CanReadDefinition: true,
                CanReadDiagram: true,
                HasParameterSchema: false,
                HasFormSchema: true,
                SupportedSemantics: [ApprovalSemantic.Approve, ApprovalSemantic.Return]));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(descriptor, Options));
        var root = json.RootElement;

        // camelCase
        Assert.True(root.TryGetProperty("processKey", out _));
        Assert.True(root.TryGetProperty("deployedVersion", out _));
        Assert.True(root.TryGetProperty("definitionChecksum", out _));

        // 枚举是词汇不是序号
        Assert.Equal("ACTIVE", root.GetProperty("status").GetString());

        var capabilities = root.GetProperty("capabilities");

        Assert.True(capabilities.GetProperty("canStart").GetBoolean());
        Assert.False(capabilities.GetProperty("hasParameterSchema").GetBoolean());

        var semantics = capabilities.GetProperty("supportedSemantics")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Assert.Equal(["APPROVE", "RETURN"], semantics);

        // 便捷属性与能力声明必须恒等（避免两处真源）
        Assert.True(root.GetProperty("canStart").GetBoolean());
    }

    [Theory]
    [InlineData("1", ApprovalSemantic.Approve)]
    [InlineData("2", ApprovalSemantic.Reject)]
    [InlineData("3", ApprovalSemantic.Return)]
    [InlineData("approve", ApprovalSemantic.Approve)]
    [InlineData("REJECTED", ApprovalSemantic.Reject)]
    [InlineData("rework", ApprovalSemantic.Return)]
    [InlineData("transfer", ApprovalSemantic.Delegate)]
    [InlineData("refund_to_finance", ApprovalSemantic.Custom)]
    public void ApprovalSemantics_ShouldResolveKnownOptionIds_AndDefaultToCustom(
        string optionId,
        ApprovalSemantic expected)
    {
        Assert.Equal(expected, ApprovalSemantics.Resolve(optionId));
    }

    [Theory]
    [InlineData(ApprovalSemantic.Approve, ApprovalOutcomeClass.Positive)]
    [InlineData(ApprovalSemantic.Reject, ApprovalOutcomeClass.Negative)]
    // 退回计入负向：这一单没能在本环节通过，算中性会人为压低驳回率
    [InlineData(ApprovalSemantic.Return, ApprovalOutcomeClass.Negative)]
    [InlineData(ApprovalSemantic.Delegate, ApprovalOutcomeClass.Neutral)]
    [InlineData(ApprovalSemantic.Custom, ApprovalOutcomeClass.Neutral)]
    public void OutcomeClassOf_ShouldFollowSemantic(ApprovalSemantic semantic, ApprovalOutcomeClass expected)
    {
        Assert.Equal(expected, ApprovalSemantics.OutcomeClassOf(semantic));
    }

    [Fact]
    public void CursorPage_Empty_ShouldDescribeItselfConsistently()
    {
        var page = CursorPage<ProcessDescriptor>.Empty;

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void PlatformProblem_ShouldCarryStableTypeUri()
    {
        var problem = PlatformProblem.Create(ErrorCodes.ProcessNotFound, "流程不存在", 404);

        Assert.Equal("https://netbpm.dev/problems/process.not_found", problem.Type);
        Assert.Equal(ErrorCodes.ProcessNotFound, problem.Code);
        Assert.Equal(404, problem.Status);
    }
}

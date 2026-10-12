using NetBpm.DifferentialTests.Differential;

namespace NetBpm.DifferentialTests;

/// <summary>
/// 维度①（API 视图）与维度③（事件序列）比对器的自测。
/// <para>
/// 与维度②的 <see cref="SnapshotComparerTests"/> 同理：<b>比对器写错会让所有对拍结论失效</b>，
/// 所以三个维度都必须先被自己的用例钉住。
/// </para>
/// </summary>
public sealed class DimensionComparerTests
{
    // ─────────────────────────── 维度①：API 视图 ───────────────────────────

    [Fact]
    public void ApiView_WhenIdentical_ShouldReportFullMatch()
    {
        var view = SampleView();

        var result = ApiViewComparer.Compare(view, view);

        Assert.Empty(result.Differences);
        Assert.Equal(1d, result.MatchRate);
        Assert.True(result.IsWithinTarget());
    }

    [Fact]
    public void ApiView_WhenActiveActivityDiffers_ShouldReportTheCell()
    {
        // 这正是"流程停错节点"要捕获的差异
        var expected = SampleView() with
        {
            ProcessInstances = [new ApiProcessInstance("B1", IsEnded: false, UnfinishedActivityIds: ["approve"])],
        };

        var actual = SampleView() with
        {
            ProcessInstances = [new ApiProcessInstance("B1", IsEnded: false, UnfinishedActivityIds: ["review"])],
        };

        var result = ApiViewComparer.Compare(expected, actual);

        var difference = Assert.Single(result.Differences);
        Assert.Equal("api.processInstances", difference.Table);
        Assert.Equal("B1.unfinishedActivityIds", difference.Column);
        Assert.Equal("approve", difference.Expected);
        Assert.Equal("review", difference.Actual);
    }

    [Fact]
    public void ApiView_WhenTaskMissing_ShouldReportMissingRow()
    {
        var expected = SampleView() with { Tasks = [new ApiTask("approve", "kermit")] };
        var actual = SampleView() with { Tasks = [] };

        var result = ApiViewComparer.Compare(expected, actual);

        var difference = Assert.Single(result.Differences);
        Assert.Equal("api.tasks", difference.Table);
        Assert.Equal("approve|kermit", difference.Expected);
        Assert.Equal("缺失", difference.Actual);
    }

    [Fact]
    public void ApiView_WhenVariableValueDiffers_ShouldReportTheValueColumn()
    {
        var expected = SampleView() with { Variables = [new ApiVariable("amount", "integer", "1200")] };
        var actual = SampleView() with { Variables = [new ApiVariable("amount", "integer", "1200.0")] };

        var result = ApiViewComparer.Compare(expected, actual);

        // 列名带行键前缀，便于在差异台账里精确登记
        Assert.Equal("amount.value", Assert.Single(result.Differences).Column);
    }

    // ─────────────────────────── 维度③：事件序列 ───────────────────────────

    [Fact]
    public void Events_WhenIdentical_ShouldReportFullMatch()
    {
        var events = new List<TraceEvent>
        {
            new("PROCESS_STARTED", ActivityId: "start"),
            new("ACTIVITY_COMPLETED", ActivityId: "start"),
            new("SEQUENCEFLOW_TAKEN", FlowId: "f1"),
            new("ACTIVITY_STARTED", ActivityId: "approve"),
        };

        var result = EventSequenceComparer.Compare(events, events);

        Assert.Empty(result.Differences);
        Assert.Equal(4, result.ComparedCells);
    }

    [Fact]
    public void Events_WhenOrderSwapped_ShouldReportPositionalMismatch()
    {
        // §9.4：「事件顺序比事件集合更重要」——顺序错必须被抓到，不能按集合比对糊过去
        var expected = new List<TraceEvent>
        {
            new("ACTIVITY_COMPLETED", ActivityId: "start"),
            new("SEQUENCEFLOW_TAKEN", FlowId: "f1"),
        };

        var actual = new List<TraceEvent>
        {
            new("SEQUENCEFLOW_TAKEN", FlowId: "f1"),
            new("ACTIVITY_COMPLETED", ActivityId: "start"),
        };

        var result = EventSequenceComparer.Compare(expected, actual);

        Assert.Equal(2, result.Differences.Count);
        Assert.All(result.Differences, difference => Assert.Equal("type", difference.Column));
    }

    [Fact]
    public void Events_WhenExtraEventMissing_ShouldReportLengthDifference()
    {
        var expected = new List<TraceEvent> { new("PROCESS_STARTED") };
        var actual = new List<TraceEvent> { new("PROCESS_STARTED"), new("ACTIVITY_STARTED", ActivityId: "start") };

        var result = EventSequenceComparer.Compare(expected, actual);

        var difference = Assert.Single(result.Differences);
        Assert.Equal("<length>", difference.Column);
        Assert.Equal("1", difference.Expected);
        Assert.Equal("2", difference.Actual);
    }

    [Fact]
    public void Events_WhenOneSideCannotReportAField_ShouldNotTreatItAsDifference()
    {
        // 端口粒度不同（例如 TASK_COMPLETED 拿不到 taskDefinitionKey）不算语义差异
        var expected = new List<TraceEvent> { new("TASK_COMPLETED") };
        var actual = new List<TraceEvent> { new("TASK_COMPLETED", TaskDefinitionKey: "approve") };

        Assert.Empty(EventSequenceComparer.Compare(expected, actual).Differences);

        // 但两侧都填了就必须一致
        var filled = new List<TraceEvent> { new("TASK_COMPLETED", TaskDefinitionKey: "approve") };
        var conflicting = new List<TraceEvent> { new("TASK_COMPLETED", TaskDefinitionKey: "review") };

        var difference = Assert.Single(EventSequenceComparer.Compare(filled, conflicting).Differences);

        Assert.Equal("taskDefinitionKey", difference.Column);
    }

    // ─────────────────────────── 行序规范化 ───────────────────────────

    [Fact]
    public void RowOrdering_ShouldMakeComparisonPhysicalOrderIndependent()
    {
        // 行的物理顺序没有语义：同一个行集合、插入顺序不同 ⇒ 规范排序后必须逐行一致，
        // 否则对拍会被"谁先插入"这种无关因素污染。
        var ignored = DifferentialEngine.IgnoredColumns;
        var left = new[] { Row("2", "approve"), Row("1", "start") };
        var right = new[] { Row("1", "start"), Row("2", "approve") };

        var result = new SnapshotComparer(ignored).Compare(
            [new RowSnapshot("ACT_RU_EXECUTION", RowOrdering.Canonical(left, ignored))],
            [new RowSnapshot("ACT_RU_EXECUTION", RowOrdering.Canonical(right, ignored))]);

        Assert.Empty(result.Differences);
    }

    [Fact]
    public void IgnoredColumns_ShouldCoverIdValuesButNotSemanticColumns()
    {
        // 🔑 2026-10-12 首次真值比对后的口径修正（§15.10）：
        // ID 具体值**已进入忽略清单**（§9.4 原文允许"ID 具体值（若区块分配时序不同）"）——
        // Activiti 用 UUID、NetBpm 用计数器，两边必然不同，且 ID 一进排序键就会打乱行序、
        // 制造大面积假差异。本用例把这条口径钉死：ID 不产生差异，但**语义列**必须产生差异。
        var ignored = DifferentialEngine.IgnoredColumns;

        Assert.Contains("ID_", ignored);
        Assert.Contains("PROC_INST_ID_", ignored);
        Assert.Contains("DURATION_", ignored);

        var left = new[] { Row("1", "start") };
        var right = new[] { Row("9", "start") };

        Assert.Empty(new SnapshotComparer(ignored).Compare(
            [new RowSnapshot("ACT_RU_EXECUTION", RowOrdering.Canonical(left, ignored))],
            [new RowSnapshot("ACT_RU_EXECUTION", RowOrdering.Canonical(right, ignored))]).Differences);

        // 语义列不同必须照实报出来（不能被"忽略 ID"顺手吞掉）
        var difference = Assert.Single(new SnapshotComparer(ignored).Compare(
            [new RowSnapshot("ACT_RU_EXECUTION", RowOrdering.Canonical(left, ignored))],
            [new RowSnapshot("ACT_RU_EXECUTION", RowOrdering.Canonical([Row("1", "approve")], ignored))]).Differences);

        Assert.Equal("ACT_ID_", difference.Column);
        Assert.Equal("start", difference.Expected);
        Assert.Equal("approve", difference.Actual);
    }

    private static Dictionary<string, string?> Row(string id, string activity) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ID_"] = id,
            ["ACT_ID_"] = activity,
        };

    private static ApiView SampleView() => new()
    {
        ProcessInstances = [new ApiProcessInstance("B1", IsEnded: false, UnfinishedActivityIds: ["approve"])],
        Tasks = [new ApiTask("approve", "kermit")],
        Variables = [new ApiVariable("amount", "integer", "1200")],
    };
}

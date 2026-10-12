namespace NetBpm.DifferentialTests;

/// <summary>
/// 对拍器自身的测试（开发计划 §9.2 的第 ② 维 / §9.4 的门禁口径）。
/// <para>对拍器写错会让所有对拍结论失效，所以它必须先被测。</para>
/// </summary>
public sealed class SnapshotComparerTests
{
    private static readonly SnapshotComparer Comparer = new();

    [Fact]
    public void Compare_WhenSnapshotsAreIdentical_ShouldReportFullMatch()
    {
        var snapshot = new[]
        {
            Row("ACT_RU_EXECUTION", Row1(isActive: "1", isScope: "1", isConcurrent: "0")),
        };

        var result = Comparer.Compare(snapshot, snapshot);

        Assert.Empty(result.Differences);
        Assert.Equal(1d, result.MatchRate);
        Assert.True(result.IsWithinTarget());
    }

    [Fact]
    public void Compare_WhenFlagBitsDiffer_ShouldReportTheExactCell()
    {
        // 这是 R3「执行树标志位组合错误」要捕获的差异类型
        var expected = new[] { Row("ACT_RU_EXECUTION", Row1(isActive: "1", isScope: "1", isConcurrent: "0")) };
        var actual = new[] { Row("ACT_RU_EXECUTION", Row1(isActive: "1", isScope: "1", isConcurrent: "1")) };

        var result = Comparer.Compare(expected, actual);

        var difference = Assert.Single(result.Differences);
        Assert.Equal("IS_CONCURRENT_", difference.Column);
        Assert.Equal("0", difference.Expected);
        Assert.Equal("1", difference.Actual);
    }

    [Fact]
    public void Compare_WhenTimestampsDiffer_ShouldIgnoreTheColumn()
    {
        // §9.4：允许的差异仅限时间戳与 ID 具体值
        var expectedRow = Row1(isActive: "1", isScope: "1", isConcurrent: "0");
        var actualRow = Row1(isActive: "1", isScope: "1", isConcurrent: "0");
        actualRow["START_TIME_"] = "2026-10-12T10:00:00";

        var result = Comparer.Compare(
            [Row("ACT_RU_EXECUTION", expectedRow)],
            [Row("ACT_RU_EXECUTION", actualRow)]);

        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Compare_WhenExecutionIsMissing_ShouldReportRowCountDifference()
    {
        // Join 时「该删的执行没删」会表现为行数差异
        var expected = new[] { Row("ACT_RU_EXECUTION", Row1(isActive: "1", isScope: "1", isConcurrent: "0")) };
        var actual = new[]
        {
            Row(
                "ACT_RU_EXECUTION",
                Row1(isActive: "1", isScope: "1", isConcurrent: "0"),
                Row1(isActive: "1", isScope: "1", isConcurrent: "0")),
        };

        var result = Comparer.Compare(expected, actual);

        Assert.Contains(result.Differences, difference => difference.Column == "<row-count>");
    }

    [Fact]
    public void MatchRate_ShouldReflectTheNinetyNinePointFivePercentGate()
    {
        // 每行 6 个参与比对的列（START_TIME_ 被忽略）
        var expectedRows = Enumerable.Range(0, 200).Select(_ => Row1("1", "1", "0")).ToArray();
        var actualRows = expectedRows.Select(row => new Dictionary<string, string?>(row, StringComparer.Ordinal)).ToArray();
        actualRows[0]["IS_CONCURRENT_"] = "1";

        var result = Comparer.Compare([Row("ACT_RU_EXECUTION", expectedRows)], [Row("ACT_RU_EXECUTION", actualRows)]);

        Assert.Equal(1200, result.ComparedCells);
        Assert.Single(result.Differences);
        Assert.Equal(1d - (1d / 1200), result.MatchRate, precision: 6);
        Assert.True(result.IsWithinTarget());
    }

    [Fact]
    public void IsWithinTarget_ShouldApplyThe99Point5PercentGate()
    {
        // 199/200 = 99.5% ⇒ 刚好达线；198/199 ≈ 99.497% ⇒ 未达线
        Assert.True(new ComparisonResult([new Difference("T", 0, "C", "a", "b")], 200).IsWithinTarget());
        Assert.False(new ComparisonResult([new Difference("T", 0, "C", "a", "b")], 199).IsWithinTarget());
        Assert.True(new ComparisonResult([], 0).IsWithinTarget());
    }

    private static RowSnapshot Row(string table, params IReadOnlyDictionary<string, string?>[] rows) => new(table, rows);

    private static Dictionary<string, string?> Row1(string isActive, string isScope, string isConcurrent) => new(StringComparer.Ordinal)
    {
        ["ID_"] = "2501",
        ["PROC_INST_ID_"] = "2501",
        ["ACT_ID_"] = "start",
        ["IS_ACTIVE_"] = isActive,
        ["IS_SCOPE_"] = isScope,
        ["IS_CONCURRENT_"] = isConcurrent,
        ["START_TIME_"] = "2026-10-12T09:00:00",
    };
}

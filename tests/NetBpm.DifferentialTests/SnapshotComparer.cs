namespace NetBpm.DifferentialTests;

/// <summary>某张表的一次行快照（列名 → 值，值统一转成字符串便于跨语言比对）。</summary>
/// <param name="Table">表名（如 <c>ACT_RU_EXECUTION</c>）。</param>
/// <param name="Rows">行集合，按稳定排序键排列。</param>
public sealed record RowSnapshot(string Table, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows);

/// <summary>单处差异。</summary>
/// <param name="Table">表名。</param>
/// <param name="RowIndex">行序号。</param>
/// <param name="Column">列名。</param>
/// <param name="Expected">Activiti（预言机）的值。</param>
/// <param name="Actual">NetBpm（被测）的值。</param>
public sealed record Difference(string Table, int RowIndex, string Column, string? Expected, string? Actual)
{
    public override string ToString() =>
        $"{Table}[{RowIndex}].{Column}: 期望 '{Expected}' / 实际 '{Actual}'";
}

/// <summary>比对结果。</summary>
/// <param name="Differences">差异清单。</param>
/// <param name="ComparedCells">参与比对的单元格总数。</param>
public sealed record ComparisonResult(IReadOnlyList<Difference> Differences, int ComparedCells)
{
    /// <summary>一致率。</summary>
    public double MatchRate =>
        ComparedCells == 0 ? 1d : 1d - ((double)Differences.Count / ComparedCells);

    /// <summary>
    /// 是否达到开发计划 §9.4 的门禁：<b>一致率 ≥ 99.5%，且差异清单 100% 有结论</b>。
    /// 「有结论」需由人评审差异清单后显式确认，这里只负责一致率这一半。
    /// </summary>
    public bool IsWithinTarget(double target = 0.995) => MatchRate >= target;
}

/// <summary>
/// <c>ACT_*</c> 行快照比对器 —— 开发计划 §9.2 的「②是杀手锏」维度。
/// <para>
/// 之所以该维度最强：两端读写的是同一套表结构（§7.1），因此能捕获 API 层看不出的
/// 执行树内部差异，例如 <c>IS_SCOPE_</c> 标志位错、子执行多建/少建、Join 时该删的执行没删。
/// </para>
/// </summary>
public sealed class SnapshotComparer
{
    private readonly HashSet<string> _ignoredColumns;

    /// <param name="ignoredColumns">
    /// 允许差异的列。§9.4 明确只允许「时间戳」与「ID 具体值（若区块分配时序不同）」两类，
    /// 这里再加一个已登记的例外：<c>ENGINE_VERSION_</c> 必然与 Activiti 的版本串不同。
    /// </param>
    public SnapshotComparer(IEnumerable<string>? ignoredColumns = null)
    {
        _ignoredColumns = new HashSet<string>(
            ignoredColumns
            ??
            [
                // 时间戳：执行时刻必然不同
                "START_TIME_", "END_TIME_", "DUE_DATE_", "CREATE_TIME_", "CLAIM_TIME_",
                "LAST_UPDATED_TIME_", "LOCK_TIME_", "DEPLOY_TIME_",
                // 已登记的例外：引擎版本串
                "ENGINE_VERSION_",
            ],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>比较两个快照集合（只比较两边都存在的表）。</summary>
    public ComparisonResult Compare(
        IReadOnlyList<RowSnapshot> expected,
        IReadOnlyList<RowSnapshot> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = new List<Difference>();
        var comparedCells = 0;
        var actualByTable = actual.ToDictionary(snapshot => snapshot.Table, StringComparer.OrdinalIgnoreCase);

        foreach (var expectedTable in expected)
        {
            if (!actualByTable.TryGetValue(expectedTable.Table, out var actualTable))
            {
                differences.Add(new Difference(expectedTable.Table, -1, "<table>", "存在", "缺失"));
                continue;
            }

            if (expectedTable.Rows.Count != actualTable.Rows.Count)
            {
                differences.Add(new Difference(
                    expectedTable.Table,
                    -1,
                    "<row-count>",
                    expectedTable.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    actualTable.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            var rowCount = Math.Min(expectedTable.Rows.Count, actualTable.Rows.Count);

            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var expectedRow = expectedTable.Rows[rowIndex];
                var actualRow = actualTable.Rows[rowIndex];

                // 🔑 必须**并集**遍历：Java 侧的 JSON 会省略 null 字段，若只遍历预言机行的列，
                // 被测侧独有的列（如 START_USER_ID_）会被静默跳过 —— 那是单向偷懒，不是一致。
                // 两边缺席/null 视为相等（absent ≡ null），不会引入噪音。
                var columns = expectedRow.Keys
                    .Union(actualRow.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(column => !_ignoredColumns.Contains(column))
                    .OrderBy(column => column, StringComparer.Ordinal);

                foreach (var column in columns)
                {
                    comparedCells++;

                    var expectedValue = expectedRow.GetValueOrDefault(column);
                    var actualValue = actualRow.GetValueOrDefault(column);

                    if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
                    {
                        differences.Add(new Difference(expectedTable.Table, rowIndex, column, expectedValue, actualValue));
                    }
                }
            }
        }

        return new ComparisonResult(differences, comparedCells);
    }
}

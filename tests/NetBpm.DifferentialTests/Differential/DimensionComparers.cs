namespace NetBpm.DifferentialTests.Differential;

/// <summary>
/// 维度①：API 返回值视图比对（开发计划 §9.2）。
/// <para>
/// 两侧先把各自的 API/查询结果规范化成 <see cref="ApiView"/>，再比语义：
/// 这样"Dart/C# 的 DTO 形状不同"这类结构性差异不会污染结论（§9.4 要求结构性差异 100% 消除）。
/// </para>
/// </summary>
public static class ApiViewComparer
{
    public static ComparisonResult Compare(ApiView expected, ApiView actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = new List<Difference>();
        var cells = 0;

        CompareCollection(
            "api.processInstances",
            expected.ProcessInstances,
            actual.ProcessInstances,
            instance => instance.BusinessKey ?? string.Empty,
            (instance, column) => column switch
            {
                "businessKey" => instance.BusinessKey,
                "isEnded" => instance.IsEnded ? "1" : "0",
                "unfinishedActivityIds" => string.Join(",", instance.UnfinishedActivityIds),
                _ => null,
            },
            ["businessKey", "isEnded", "unfinishedActivityIds"],
            differences,
            ref cells);

        CompareCollection(
            "api.tasks",
            expected.Tasks,
            actual.Tasks,
            task => $"{task.TaskDefinitionKey}|{task.Assignee}",
            (task, column) => column switch
            {
                "taskDefinitionKey" => task.TaskDefinitionKey,
                "assignee" => task.Assignee,
                _ => null,
            },
            ["taskDefinitionKey", "assignee"],
            differences,
            ref cells);

        CompareCollection(
            "api.variables",
            expected.Variables,
            actual.Variables,
            variable => variable.Name,
            (variable, column) => column switch
            {
                "name" => variable.Name,
                "type" => variable.Type,
                "value" => variable.Value,
                _ => null,
            },
            ["name", "type", "value"],
            differences,
            ref cells);

        return new ComparisonResult(differences, cells);
    }

    /// <summary>按稳定键对齐两个集合，逐列比对（键集合不同即记为行级差异）。</summary>
    private static void CompareCollection<T>(
        string table,
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        Func<T, string> keySelector,
        Func<T, string, string?> valueSelector,
        IReadOnlyList<string> columns,
        List<Difference> differences,
        ref int cells)
    {
        var expectedByKey = expected.ToDictionary(keySelector, StringComparer.Ordinal);
        var actualByKey = actual.ToDictionary(keySelector, StringComparer.Ordinal);

        foreach (var key in expectedByKey.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!actualByKey.TryGetValue(key, out var actualItem))
            {
                differences.Add(new Difference(table, -1, "<present>", key, "缺失"));
                continue;
            }

            var expectedItem = expectedByKey[key];

            foreach (var column in columns)
            {
                cells++;
                var expectedValue = valueSelector(expectedItem, column);
                var actualValue = valueSelector(actualItem, column);

                if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
                {
                    differences.Add(new Difference(table, -1, $"{key}.{column}", expectedValue, actualValue));
                }
            }
        }

        foreach (var key in actualByKey.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!expectedByKey.ContainsKey(key))
            {
                differences.Add(new Difference(table, -1, "<present>", "缺失", key));
            }
        }
    }
}

/// <summary>
/// 维度③：事件序列比对（开发计划 §9.2 / §9.4「事件顺序比事件集合更重要」）。
/// <para>
/// 因此这里是<b>按位置逐项</b>比对：错位、多一个、少一个都会被记成差异；
/// 不做集合比对 —— 那会把顺序缺陷（最容易出错的地方）掩盖掉。
/// </para>
/// <para>
/// 字段比对规则：<c>type</c> 必须相等；其余字段<b>只比双方都填了的</b>
/// （某侧端口拿不到的字段留空，不算差异）。这样允许两侧采集粒度不同，
/// 又能保证共同能观测到的信息必须一致。
/// </para>
/// </summary>
public static class EventSequenceComparer
{
    public static ComparisonResult Compare(
        IReadOnlyList<TraceEvent> expected,
        IReadOnlyList<TraceEvent> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = new List<Difference>();
        var cells = Math.Max(expected.Count, actual.Count);

        for (var index = 0; index < Math.Min(expected.Count, actual.Count); index++)
        {
            var expectedEvent = expected[index];
            var actualEvent = actual[index];

            if (!string.Equals(expectedEvent.Type, actualEvent.Type, StringComparison.Ordinal))
            {
                differences.Add(new Difference(
                    "events",
                    index,
                    "type",
                    Describe(expectedEvent),
                    Describe(actualEvent)));

                continue;
            }

            CompareField("activityId", expectedEvent.ActivityId, actualEvent.ActivityId, index, differences);
            CompareField("taskDefinitionKey", expectedEvent.TaskDefinitionKey, actualEvent.TaskDefinitionKey, index, differences);
            CompareField("flowId", expectedEvent.FlowId, actualEvent.FlowId, index, differences);
            CompareField("variableName", expectedEvent.VariableName, actualEvent.VariableName, index, differences);
        }

        if (expected.Count != actual.Count)
        {
            differences.Add(new Difference(
                "events",
                -1,
                "<length>",
                expected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                actual.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new ComparisonResult(differences, cells);
    }

    private static void CompareField(
        string column,
        string? expected,
        string? actual,
        int index,
        List<Difference> differences)
    {
        // 一侧留空 = 该侧端口拿不到 ⇒ 不比（口径差异，不是语义差异）
        if (expected is null || actual is null)
        {
            return;
        }

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            differences.Add(new Difference("events", index, column, expected, actual));
        }
    }

    private static string Describe(TraceEvent traceEvent)
    {
        var parts = new List<string> { traceEvent.Type };

        if (traceEvent.ActivityId is not null)
        {
            parts.Add($"activity={traceEvent.ActivityId}");
        }

        if (traceEvent.FlowId is not null)
        {
            parts.Add($"flow={traceEvent.FlowId}");
        }

        if (traceEvent.VariableName is not null)
        {
            parts.Add($"variable={traceEvent.VariableName}");
        }

        return string.Join(' ', parts);
    }
}

/// <summary>
/// 行快照的<b>规范化排序</b>（维度②的前置契约）。
///
/// <para>
/// 为什么必须排序：<c>ACT_*</c> 行的物理顺序没有语义，而 <c>ID_</c> 具体值被 §9.4 明确列为
/// "允许差异"。若按插入顺序逐行比对，一旦两端 id 分配时序不同就会产生整表假差异。
/// 规范化排序把"行集合"变成"行集合的规范序"，于是逐行比对既保留"多行/少行"的检出能力，
/// 又对 id 具体值免疫。
/// </para>
/// <para>
/// 🔒 <b>两端必须实现同一规则</b>（Java 侧见 <c>tools/Oracle</c> 的 <c>TableDumper</c>）：
/// 对每一行，按<b>忽略列之外</b>的列名升序拼接 <c>列名=值</c>（<c>NULL</c> 写作空串），
/// 用 <c>String.CompareOrdinal</c> 比较；仅当规范串相等时（内容完全相同的重复行）再按出现顺序保持稳定。
/// </para>
/// </summary>
public static class RowOrdering
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> Canonical(
        IEnumerable<IReadOnlyDictionary<string, string?>> rows,
        IReadOnlySet<string> ignoredColumns)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(ignoredColumns);

        return
        [
            .. rows
                .Select((row, index) => (row, index))
                .OrderBy(item => CanonicalKey(item.row, ignoredColumns), StringComparer.Ordinal)
                .ThenBy(item => item.index)
                .Select(item => item.row),
        ];
    }

    /// <summary>规范串：按列名 ordinal 升序拼 <c>列名=值</c>。</summary>
    public static string CanonicalKey(
        IReadOnlyDictionary<string, string?> row,
        IReadOnlySet<string> ignoredColumns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(ignoredColumns);

        var builder = new System.Text.StringBuilder();

        foreach (var column in row.Keys
            .Where(column => !ignoredColumns.Contains(column))
            .OrderBy(column => column, StringComparer.Ordinal))
        {
            builder.Append(column).Append('=').Append(row[column] ?? string.Empty).Append('\u001f');
        }

        return builder.ToString();
    }
}

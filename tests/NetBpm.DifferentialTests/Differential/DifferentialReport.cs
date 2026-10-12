using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBpm.DifferentialTests.Differential;

/// <summary>§9.4 的一致率门禁。</summary>
/// <param name="Api">维度①目标（≥ 99.5%）。</param>
/// <param name="Tables">维度②目标（≥ 99.5%）。</param>
/// <param name="Events">维度③目标（≥ 99%，事件顺序比集合更重要）。</param>
/// <param name="Overall">整体门禁（≥ 99.5%）。</param>
public sealed record DifferentialThresholds(
    double Api = 0.995,
    double Tables = 0.995,
    double Events = 0.99,
    double Overall = 0.995);

/// <summary>没有参与比对的结果（语料缺预言机快照时用）。</summary>
internal static class EmptyComparison
{
    public static readonly ComparisonResult Instance = new([], 0);
}

/// <summary>单个语料的结论（三个维度各一个 <see cref="ComparisonResult"/>）。</summary>
/// <param name="CaseName">语料标识。</param>
/// <param name="OraclePresent">是否找到了预言机快照（没有则无法对拍）。</param>
/// <param name="Note">备注（跳过原因 / 语料本身的错误）。</param>
/// <param name="Api">维度①。</param>
/// <param name="Tables">维度②。</param>
/// <param name="Events">维度③。</param>
public sealed record CaseResult(
    string CaseName,
    bool OraclePresent,
    string? Note,
    ComparisonResult Api,
    ComparisonResult Tables,
    ComparisonResult Events);

/// <summary>
/// 对拍报告（§9.4）：分维度汇总一致率，并按"差异清单 100% 有结论"的要求
/// 标出**尚未有结论**的差异 —— 门禁要求两者同时满足。
/// </summary>
public sealed record DifferentialReport
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required IReadOnlyList<CaseResult> Cases { get; init; }

    /// <summary>已有结论的差异条数（在台账里登记过）。</summary>
    public int ConcludedDifferences { get; init; }

    /// <summary>尚未有结论的差异（门禁不允许非空）。</summary>
    public IReadOnlyList<string> PendingConclusions { get; init; } = [];

    /// <summary>
    /// 跑不动的语料（用例名 → 原因）。
    /// <para>
    /// ⚠️ 它<b>不参与一致率</b>，但会让门禁失败：跑不动 ≠ 一致。原因文本通常直接指出引擎缺哪块能力。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ExecutionFailures { get; init; } = [];

    /// <summary>
    /// 不适用于本脚本口径的语料（用例名 → 原因），例如同一节点上同时挂着多个同 key 的待办。
    /// <para>
    /// 与 <see cref="ExecutionFailures"/> 的区别：这些是<b>脚本口径</b>限制，不是引擎缺陷，
    /// 因此不进一致率、也不让门禁失败；但必须列出来（不许静默丢弃语料）。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> InapplicableCases { get; init; } = [];

    public ComparisonResult Api => Aggregate(result => result.Api);

    public ComparisonResult Tables => Aggregate(result => result.Tables);

    public ComparisonResult Events => Aggregate(result => result.Events);

    public ComparisonResult Overall => Aggregate(
        result => new ComparisonResult(
            [.. result.Api.Differences, .. result.Tables.Differences, .. result.Events.Differences],
            result.Api.ComparedCells + result.Tables.ComparedCells + result.Events.ComparedCells));

    /// <summary>被对拍过的语料数（有预言机快照的）。</summary>
    public int ComparedCaseCount => Cases.Count(result => result.OraclePresent);

    public bool WithinGate(DifferentialThresholds? thresholds = null)
    {
        var limits = thresholds ?? new DifferentialThresholds();

        return ComparedCaseCount > 0
            && Api.IsWithinTarget(limits.Api)
            && Tables.IsWithinTarget(limits.Tables)
            && Events.IsWithinTarget(limits.Events)
            && Overall.IsWithinTarget(limits.Overall)
            && PendingConclusions.Count == 0
            // 跑不动 ≠ 一致：执行失败必须让门禁失败，否则"跑不动"会被一致率悄悄吸收
            && ExecutionFailures.Count == 0;
    }

    public string ToMarkdown(DifferentialThresholds? thresholds = null)
    {
        var limits = thresholds ?? new DifferentialThresholds();
        var builder = new StringBuilder();

        builder.AppendLine("# 对拍报告（开发计划 §9.4）").AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"参与对拍的语料：**{ComparedCaseCount}**（无预言机快照的语料只产出被测侧快照，不参与一致率）").AppendLine();
        builder.AppendLine("| 维度 | 参与比对单元格 | 差异 | 一致率 | 目标 | 达线 |");
        builder.AppendLine("|---|---:|---:|---:|---:|:--:|");

        AppendDimension(builder, "① API 返回值", Api, limits.Api);
        AppendDimension(builder, "② ACT_* 行快照", Tables, limits.Tables);
        AppendDimension(builder, "③ 事件序列", Events, limits.Events);
        AppendDimension(builder, "**整体**", Overall, limits.Overall);

        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"差异台账：已登记 {ConcludedDifferences} 条，**待结论 {PendingConclusions.Count} 条**").AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"跑不动的语料：**{ExecutionFailures.Count} 个**（不参与一致率，但会让门禁失败）").AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"不适用的语料：{InapplicableCases.Count} 个（脚本口径限制，非引擎缺陷；不参与一致率）").AppendLine();

        if (InapplicableCases.Count > 0)
        {
            builder.AppendLine("## 不适用的语料").AppendLine();

            foreach (var inapplicable in InapplicableCases)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {inapplicable}");
            }

            builder.AppendLine();
        }

        if (ExecutionFailures.Count > 0)
        {
            builder.AppendLine("## 跑不动的语料").AppendLine();
            builder.AppendLine("> 跑不动 ≠ 一致。原因往往直接指出引擎缺哪块能力，修完后应重新跑批。").AppendLine();

            foreach (var failure in ExecutionFailures)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {failure}");
            }

            builder.AppendLine();
        }

        if (PendingConclusions.Count > 0)
        {
            builder.AppendLine("## 待结论的差异").AppendLine();
            builder.AppendLine("> §9.4 要求差异清单 100% 有结论：要么修，要么在台账里登记为「有意偏离」。").AppendLine();

            foreach (var pending in PendingConclusions)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {pending}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## 语料明细").AppendLine();
        builder.AppendLine("| 语料 | 预言机 | ① | ② | ③ | 备注 |");
        builder.AppendLine("|---|:--:|---:|---:|---:|---|");

        foreach (var result in Cases)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {result.CaseName} | {(result.OraclePresent ? "有" : "无")} "
                + $"| {Format(result.Api)} | {Format(result.Tables)} | {Format(result.Events)} "
                + $"| {result.Note ?? string.Empty} |");
        }

        return builder.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(this, ReportJsonOptions);

    private static string Format(ComparisonResult result) =>
        result.ComparedCells == 0
            ? "-"
            : $"{result.MatchRate * 100:F2}%（{result.Differences.Count}/{result.ComparedCells}）";

    private static void AppendDimension(StringBuilder builder, string label, ComparisonResult result, double target) =>
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"| {label} | {result.ComparedCells} | {result.Differences.Count} "
            + $"| {result.MatchRate * 100:F2}% | {target * 100:F1}% | {(result.IsWithinTarget(target) ? "✅" : "❌")} |");

    private ComparisonResult Aggregate(Func<CaseResult, ComparisonResult> selector)
    {
        var compared = 0;
        var differences = new List<Difference>();

        foreach (var result in Cases.Where(result => result.OraclePresent))
        {
            var dimension = selector(result);
            compared += dimension.ComparedCells;
            differences.AddRange(dimension.Differences);
        }

        return new ComparisonResult(differences, compared);
    }
}

/// <summary>
/// 差异台账（<c>tests/corpus/known-issues.json</c>）：每条差异要么修掉，要么在这里登记为
/// 「有意偏离」并写明理由 —— 这样 §9.4 的"差异清单 100% 有结论"就从一句口号变成可机检的条件。
/// </summary>
/// <param name="CaseName">语料标识，<c>*</c> 表示任意。</param>
/// <param name="Dimension">维度：<c>api</c> / <c>tables</c> / <c>events</c> / <c>*</c>。</param>
/// <param name="Table">差异所在"表"（维度①里是 <c>api.tasks</c> 这类名），<c>*</c> 表示任意。</param>
/// <param name="Column">差异列，支持前缀匹配（如 <c>ID_</c>），<c>*</c> 表示任意。</param>
/// <param name="Reason">为什么可以接受（必须写清，否则等于没结论）。</param>
public sealed record KnownIssue(
    string CaseName,
    string Dimension,
    string Table,
    string Column,
    string Reason);

/// <summary>差异台账的装载与匹配。</summary>
public sealed class DifferenceLedger
{
    private static readonly JsonSerializerOptions LedgerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly IReadOnlyList<KnownIssue> _issues;

    public DifferenceLedger(IReadOnlyList<KnownIssue>? issues = null) => _issues = issues ?? [];

    public int Count => _issues.Count;

    public static DifferenceLedger Load(string path)
    {
        if (!File.Exists(path))
        {
            return new DifferenceLedger();
        }

        var issues = JsonSerializer.Deserialize<IReadOnlyList<KnownIssue>>(
            File.ReadAllText(path),
            LedgerJsonOptions) ?? [];

        return new DifferenceLedger(issues);
    }

    /// <summary>该差异是否已在台账里登记（= 有结论）。</summary>
    public bool Covers(string caseName, string dimension, Difference difference)
    {
        ArgumentNullException.ThrowIfNull(difference);

        return _issues.Any(issue =>
            Matches(issue.CaseName, caseName, exact: true)
            && Matches(issue.Dimension, dimension, exact: true)
            && Matches(issue.Table, difference.Table, exact: true)
            && Matches(issue.Column, difference.Column, exact: false));
    }

    private static bool Matches(string pattern, string value, bool exact) =>
        pattern == "*"
        || string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase)
        || (!exact && value.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));
}

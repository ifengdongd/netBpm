using System.Text;
using NetBpm.Engine;

namespace NetBpm.DifferentialTests.Differential;

/// <summary>
/// 对拍执行器（开发计划 P10 / §9.2）。
/// <para>
/// 流程：清表 → 部署语料 → 启动实例 → 执行操作脚本 → 采三维度快照 →
/// 与同目录的 <c>&lt;stem&gt;.oracle.json</c>（Java 预言机产物）比对 → 汇总报告。
/// </para>
/// <para>
/// 没有预言机快照时仍然跑完并写出**被测侧快照**：这样可以先看引擎是否跑得动、
/// 也可以把它当基线做"两次跑结果一致"的自洽性检查；但它<b>不参与一致率</b>
/// （拿自己跟自己比得到 100% 是没有意义的）。
/// </para>
/// </summary>
public sealed class DifferentialRunner
{
    /// <summary>执行失败备注的前缀（报告据此把它们从普通备注里区分出来）。</summary>
    public const string ExecutionFailurePrefix = "执行失败：";

    /// <summary>
    /// 语料不适用备注的前缀：与"跑不动"区分开 —— 这类是<b>脚本口径</b>限制
    /// （例如同一个节点上同时挂了多个同 key 的待办，无法确定性挑选），不是引擎缺陷。
    /// </summary>
    public const string InapplicablePrefix = "语料不适用：";

    private const string AmbiguousTaskMessage = "无法确定性地挑选";

    /// <summary>
    /// "语料不适用"（而非引擎缺陷）的判定 —— 执行器与测试共用，避免两处口径漂移：
    /// <list type="bullet">
    ///   <item><description>同一节点上多个同 key 待办 ⇒ 脚本无法确定性挑选；</description></item>
    ///   <item><description>值超出 <c>ACT_*</c> 列宽（如名称 &gt; 255）⇒ MySQL 严格模式下两端都跑不了
    ///   （我们的 DDL 列宽与 Activiti 一致，见 <c>db/mysql/0001_create_schema.sql</c>）。</description></item>
    /// </list>
    /// </summary>
    public static bool IsInapplicable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return (exception is InvalidOperationException
                && exception.Message.Contains(AmbiguousTaskMessage, StringComparison.Ordinal))
            || exception.Message.Contains("Data too long", StringComparison.Ordinal);
    }

    private readonly DifferentialEngine _engine;
    private readonly string _artifactRoot;
    private readonly DifferenceLedger _ledger;
    private readonly DifferentialThresholds _thresholds;

    public DifferentialRunner(
        DifferentialEngine engine,
        string artifactRoot,
        DifferenceLedger? ledger = null,
        DifferentialThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);

        _engine = engine;
        _artifactRoot = artifactRoot;
        _ledger = ledger ?? new DifferenceLedger();
        _thresholds = thresholds ?? new DifferentialThresholds();
    }

    public DifferentialThresholds Thresholds => _thresholds;

    /// <summary>跑一批语料并产出报告（同时把报告中写到 <paramref name="artifactRoot"/>）。</summary>
    public async Task<DifferentialReport> RunAllAsync(
        IReadOnlyList<CorpusEntry> cases,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cases);

        var results = new List<CaseResult>();
        var pending = new List<string>();
        var concluded = 0;
        var executionFailures = new List<string>();
        var inapplicable = new List<string>();

        foreach (var entry in cases)
        {
            ct.ThrowIfCancellationRequested();

            var result = await RunCaseAsync(entry, ct).ConfigureAwait(false);

            results.Add(result);

            if (result.Note is { } note)
            {
                if (note.StartsWith(ExecutionFailurePrefix, StringComparison.Ordinal))
                {
                    executionFailures.Add($"{entry.FullName} ← {note[ExecutionFailurePrefix.Length..].Trim()}");
                }
                else if (note.StartsWith(InapplicablePrefix, StringComparison.Ordinal))
                {
                    inapplicable.Add($"{entry.FullName} ← {note[InapplicablePrefix.Length..].Trim()}");
                }
            }

            foreach (var (dimension, differences) in DimensionsOf(result))
            {
                foreach (var difference in differences)
                {
                    if (_ledger.Covers(entry.FullName, dimension, difference))
                    {
                        concluded++;
                        continue;
                    }

                    pending.Add($"{entry.FullName} [{dimension}] {difference}");
                }
            }
        }

        var report = new DifferentialReport
        {
            Cases = results,
            ConcludedDifferences = concluded,
            PendingConclusions = pending,
            ExecutionFailures = executionFailures,
            InapplicableCases = inapplicable,
        };

        WriteArtifacts(report);

        return report;
    }

    /// <summary>跑单个语料：既产出快照，也给出比对结论。</summary>
    public async Task<CaseResult> RunCaseAsync(CorpusEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        OracleSnapshot actual;

        try
        {
            actual = await ExecuteCaseAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 🔑 语料是"资产"，一个语料跑不动不能拖垮整批：如实记下来继续。
            // 失败原因进报告 —— 它往往直接指出引擎缺哪块能力（例如 EL 语法未实现）。
            var prefix = IsInapplicable(exception) ? InapplicablePrefix : ExecutionFailurePrefix;

            return new CaseResult(
                entry.FullName,
                OraclePresent: false,
                Note: prefix + Describe(exception),
                EmptyComparison.Instance,
                EmptyComparison.Instance,
                EmptyComparison.Instance);
        }

        OracleSnapshotJson.WriteFile(ActualSnapshotPath(entry), actual);

        if (!File.Exists(entry.OracleFile))
        {
            return new CaseResult(
                entry.FullName,
                OraclePresent: false,
                Note: $"尚无预言机快照（{Path.GetFileName(entry.OracleFile)}），已写出被测侧快照供比对前检查",
                EmptyComparison.Instance,
                EmptyComparison.Instance,
                EmptyComparison.Instance);
        }

        var expected = OracleSnapshotJson.Read(File.ReadAllText(entry.OracleFile));

        return new CaseResult(
            entry.FullName,
            OraclePresent: true,
            Note: null,
            ApiViewComparer.Compare(expected.Api, actual.Api),
            CompareTables(expected, actual),
            EventSequenceComparer.Compare(expected.Events, actual.Events));
    }

    /// <summary>异常摘要：类型 + 首行消息（堆栈不塞进报告，需要时按用例名复现）。</summary>
    private static string Describe(Exception exception)
    {
        var message = exception.Message.Split('\n', '\r')[0].Trim();

        return message.Length == 0 ? exception.GetType().Name : $"{exception.GetType().Name}: {message}";
    }

    /// <summary>只执行语料并采快照（自洽性检查 / 生成被测侧基线用）。</summary>
    public async Task<OracleSnapshot> ExecuteCaseAsync(CorpusEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _engine.Reset();

        var definition = await _engine.Engine
            .DeployAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(entry.ReadBpmn())),
                entry.Case.ResourceName,
                deploymentName: entry.FullName,
                ct: ct)
            .ConfigureAwait(false);

        var processInstanceId = await _engine.Engine
            .StartProcessInstanceAsync(
                definition.Id,
                // businessKey 两端必须一致：用语料标识（Java 侧同规则）
                BusinessKeyOf(entry),
                // ⚠️ startUserId 必须为 null（匿名启动）：Java 预言机没有认证用户
                // ⇒ ACT_HI_PROCINST.START_USER_ID_ 为 null。两侧都必须是"匿名"，否则该列全线差异。
                startUserId: null,
                CorpusVariables.ToClr(entry.Case.Variables),
                ct)
            .ConfigureAwait(false);

        foreach (var action in entry.Case.Actions)
        {
            ct.ThrowIfCancellationRequested();
            await ApplyAsync(action, processInstanceId, ct).ConfigureAwait(false);
        }

        return await _engine
            .CaptureAsync(entry.FullName, processInstanceId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>语料标识即业务主键（Java 预言机必须用同一规则）。</summary>
    public static string BusinessKeyOf(CorpusEntry entry) => entry.FullName;

    private async Task ApplyAsync(CaseAction action, string processInstanceId, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case "completeAnyTask":
                {
                    // 「完成任一待办」：两种引擎按同一条规则挑（key 最小的那个），
                    // 因此脚本只表达"推进到下一步"，不依赖谁写的语料。
                    var open = await _engine.Engine
                        .FindTasksAsync(new NetBpm.Runtime.TaskQuery(ProcessInstanceId: processInstanceId), ct)
                        .ConfigureAwait(false);

                    if (open.Count == 0)
                    {
                        // 没有待办 ⇒ 要么实例已结束、要么卡在无任务的节点上：都属于"脚本到此为止"，
                        // 卡住与否由三维度快照去回答。
                        return;
                    }

                    var ordered = open
                        .OrderBy(task => task.TaskDefinitionKey, StringComparer.Ordinal)
                        .ThenBy(task => task.Assignee, StringComparer.Ordinal)
                        .ToList();

                    if (ordered.Count > 1
                        && string.Equals(ordered[0].TaskDefinitionKey, ordered[1].TaskDefinitionKey, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"流程实例上有 {ordered.Count} 个同 key（'{ordered[0].TaskDefinitionKey}'）的待办，"
                            + "无法确定性地挑选；请改用 completeTask + taskDefinitionKey 明确指定。");
                    }

                    await _engine.Engine
                        .CompleteTaskAsync(ordered[0].Id, CorpusVariables.ToClr(action.Variables), ct)
                        .ConfigureAwait(false);

                    break;
                }

            case "completeTask":
                {
                    var task = await ResolveSingleTaskAsync(action, ct).ConfigureAwait(false);

                    await _engine.Engine
                        .CompleteTaskAsync(task.Id, CorpusVariables.ToClr(action.Variables), ct)
                        .ConfigureAwait(false);

                    break;
                }

            case "completeAllTasks":
                {
                    var tasks = await FindTasksAsync(action, ct).ConfigureAwait(false);

                    // 排序保证确定性：同一办理人手上多个任务时，完成顺序不能依赖 id（两端 id 不同）
                    IEnumerable<Engine.Tasks.TaskEntity> ordered = tasks
                        .OrderBy(task => task.TaskDefinitionKey, StringComparer.Ordinal)
                        .ThenBy(task => task.Id, StringComparer.Ordinal);

                    if (action.Limit is { } limit)
                    {
                        ordered = ordered.Take(limit);
                    }

                    foreach (var task in ordered)
                    {
                        await _engine.Engine
                            .CompleteTaskAsync(task.Id, CorpusVariables.ToClr(action.Variables), ct)
                            .ConfigureAwait(false);
                    }

                    break;
                }

            default:
                throw new InvalidOperationException(
                    $"未知的操作脚本动作 '{action.Kind}'（支持 completeTask / completeAllTasks）。");
        }
    }

    private async Task<Engine.Tasks.TaskEntity> ResolveSingleTaskAsync(CaseAction action, CancellationToken ct)
    {
        var tasks = await FindTasksAsync(action, ct).ConfigureAwait(false);

        if (tasks.Count == 0)
        {
            throw new InvalidOperationException(
                $"按 assignee='{action.Assignee}' / taskDefinitionKey='{action.TaskDefinitionKey}' 找不到待办。");
        }

        if (tasks.Count > 1)
        {
            throw new InvalidOperationException(
                $"按 assignee='{action.Assignee}' 查到 {tasks.Count} 个待办，语料有歧义：" +
                "请用 actions[].taskDefinitionKey 指定节点（否则完成顺序不确定，两端可能挑到不同任务）。");
        }

        return tasks[0];
    }

    private async Task<IReadOnlyList<Engine.Tasks.TaskEntity>> FindTasksAsync(
        CaseAction action,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.Assignee))
        {
            throw new InvalidOperationException("操作脚本必须先声明 assignee（TaskQuery 要求至少一个条件）。");
        }

        var tasks = await _engine.Engine
            .FindTasksAsync(new NetBpm.Runtime.TaskQuery(action.Assignee), ct)
            .ConfigureAwait(false);

        return action.TaskDefinitionKey is { Length: > 0 } key
            ? [.. tasks.Where(task => string.Equals(task.TaskDefinitionKey, key, StringComparison.Ordinal))]
            : [.. tasks];
    }

    /// <summary>
    /// 维度②比对。两侧的行再<b>按规范序重排一次</b>：契约要求预言机也按同一规则排序，
    /// 这里重排是防御性的 —— 让"排序契约"由被测方强制执行，避免因对方排序未实现而产生整表假差异。
    /// </summary>
    private static ComparisonResult CompareTables(OracleSnapshot expected, OracleSnapshot actual) =>
        new SnapshotComparer(DifferentialEngine.IgnoredColumns).Compare(
            ToRowSnapshots(expected.Tables),
            ToRowSnapshots(actual.Tables));

    private static IReadOnlyList<RowSnapshot> ToRowSnapshots(
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> tables) =>
    [
        .. tables
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new RowSnapshot(
                pair.Key,
                RowOrdering.Canonical(pair.Value, DifferentialEngine.IgnoredColumns))),
    ];

    private static IReadOnlyList<(string Dimension, IReadOnlyList<Difference> Differences)> DimensionsOf(CaseResult result) =>
    [
        ("api", result.Api.Differences),
        ("tables", result.Tables.Differences),
        ("events", result.Events.Differences),
    ];

    private string ActualSnapshotPath(CorpusEntry entry) =>
        Path.Combine(_artifactRoot, "differential", entry.FullName.Replace('/', '_') + ".actual.json");

    private void WriteArtifacts(DifferentialReport report)
    {
        var directory = Path.Combine(_artifactRoot, "differential");
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "report.md"), report.ToMarkdown(_thresholds));
        File.WriteAllText(Path.Combine(directory, "report.json"), report.ToJson());
    }
}

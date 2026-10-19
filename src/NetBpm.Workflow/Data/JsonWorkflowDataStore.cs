using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NetBpm.Workflow;

/// <summary>
/// 「业务数据落在哪儿、现在长什么样」—— <c>GET /business</c> 之类的核对页面只认这个接口。
/// </summary>
/// <remarks>
/// ⚠️ 它<b>不属于</b>应用层的 <see cref="IWorkflowDataStore"/>：那份端口是「怎么存业务数据」，
/// 这份是「怎么把存的东西给人看」——只有开发/演示环境的核对页面需要。
/// </remarks>
public interface IWorkflowStoreInspector
{
    /// <summary>存储类型标识（如 <c>Json</c> / <c>MySql</c>，取值与宿主的存储切换配置一致）。</summary>
    string Store { get; }

    /// <summary>落点自述（一句话说清数据落在哪儿，会显示在页面上）。</summary>
    string Location { get; }

    /// <summary>
    /// 把业务数据 dump 成可序列化的快照；<b>键名由实现方决定</b>
    /// （JSON 实现给区块名，MySQL 实现给表名 —— 「表名」对看库的人才有意义）。
    /// </summary>
    /// <param name="ct">取消标记。</param>
    ValueTask<IReadOnlyDictionary<string, object?>> SnapshotAsync(CancellationToken ct = default);
}

/// <summary>
/// JSON 文档的参数表行：「实例 × 参数名」一行，更新即覆盖。
/// </summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="Name">参数名。</param>
/// <param name="Value">参数值（字符串形式）。</param>
/// <param name="UpdatedAt">最后更新时间。</param>
public sealed record JsonParameterRow(
    string ProcessInstanceId,
    string Name,
    string? Value,
    DateTime UpdatedAt);

/// <summary>
/// JSON 存储的文档形状：<b>五个区块就是应用层的五类业务数据</b>，用公共记录类型直接落盘 ——
/// 换成真库时数据形状不变，变的只是落点。
/// </summary>
public sealed record JsonWorkflowDocument
{
    /// <summary>已发起的流程实例登记。</summary>
    public IReadOnlyList<WorkflowInstanceRecord> Instances { get; init; } = [];

    /// <summary>审批结论（流程走完后产生）。</summary>
    public IReadOnlyList<WorkflowOutcomeRecord> Results { get; init; } = [];

    /// <summary>流程参数表（实例 × 参数名，每组合一行）。</summary>
    public IReadOnlyList<JsonParameterRow> Parameters { get; init; } = [];

    /// <summary>审批记录（每次提交一条，最旧的在前）—— 审批历史的数据源。</summary>
    public IReadOnlyList<WorkflowApprovalRecord> Approvals { get; init; } = [];

    /// <summary>当前待办（实例 × 环节），流程每前进一步整体替换一次，实例结束时清空。</summary>
    public IReadOnlyList<WorkflowCurrentTaskRecord> CurrentTasks { get; init; } = [];
}

/// <summary>
/// <b>JSON 文件版</b>的 <see cref="IWorkflowDataStore"/>：所有业务数据落一个 JSON 文件，
/// 记录类型与端口完全一致 —— 开发、演示、单机部署零落库即可跑通完整审批链路。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 它是「业务数据端口」的<b>参考实现</b>：五个区块与真库表一一对应
/// （实例登记 / 结论 / 参数表 / 审批记录 / 当前待办），宿主换真库时数据形状不变，变的只是落点。
/// </para>
/// <para>
/// 并发策略：读-改-写整体在 <see cref="SemaphoreSlim"/> 内串行，落盘用「临时文件 + 原子替换」，
/// 避免并发写把 JSON 写坏。文件内容刻意写成宽松友好的格式（中文不转义、无 BOM），方便随手打开核对。
/// </para>
/// <para>
/// 文件路径取配置 <c>Workflow:JsonDataFile</c>（默认 <c>data/workflow-business.json</c>，
/// 相对路径基于 <see cref="IHostEnvironment.ContentRootPath"/>）。
/// </para>
/// </remarks>
public sealed class JsonWorkflowDataStore : IWorkflowDataStore, IWorkflowStoreInspector, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,

        // 中文不转义成 \uXXXX —— 业务数据是要给人看的
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 写文件用的 UTF-8：<b>不带 BOM</b>。
    /// <para>
    /// 不能用 <see cref="Encoding.UTF8"/> —— 那个实例会在文件头写 BOM，
    /// 带 BOM 的 JSON 用 <c>jq</c> / <c>JSON.parse</c> 都会直接报错，
    /// 而这个文件存在的意义就是「能随手打开核对」。
    /// </para>
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>构造存储：路径取配置 <c>Workflow:JsonDataFile</c>（默认 <c>data/workflow-business.json</c>）。</summary>
    public JsonWorkflowDataStore(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = configuration["Workflow:JsonDataFile"];
        var relative = string.IsNullOrWhiteSpace(configured) ? "data/workflow-business.json" : configured;

        FilePath = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(environment.ContentRootPath, relative);
    }

    /// <summary>业务数据文件绝对路径（核对页面会显示它，方便直接打开）。</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public string Store => "Json";

    /// <inheritdoc />
    public string Location => $"JSON 文件 · {FilePath}";

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, object?>> SnapshotAsync(CancellationToken ct = default)
    {
        var document = await LoadAsync(ct).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["instances"] = document.Instances,
            ["results"] = document.Results,
            ["parameters"] = document.Parameters,
            ["approvals"] = document.Approvals,
            ["currentTasks"] = document.CurrentTasks,
        };
    }

    /// <summary>释放内部互斥信号量（容器在应用关闭时调用）。</summary>
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>读整个业务数据文件；文件不存在时返回空文档。</summary>
    public async ValueTask<JsonWorkflowDocument> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            return new JsonWorkflowDocument();
        }

        // 读用 Encoding.UTF8 没问题：内部的 StreamReader 会识别并跳过 BOM，
        // 手改过的文件（有些编辑器会加 BOM）照样读得进来。
        var json = await File.ReadAllTextAsync(FilePath, Encoding.UTF8, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonWorkflowDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<JsonWorkflowDocument>(json, SerializerOptions)
                ?? new JsonWorkflowDocument();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"业务数据文件 '{FilePath}' 不是合法 JSON：{exception.Message}", exception);
        }
    }

    // ───────────────────────── 端口：业务登记 ─────────────────────────

    /// <inheritdoc />
    public async ValueTask<WorkflowInstanceRecord?> FindInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var document = await LoadAsync(ct).ConfigureAwait(false);

        return document.Instances.FirstOrDefault(item =>
            string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowInstanceRecord>> FindInstancesAsync(CancellationToken ct = default)
    {
        var document = await LoadAsync(ct).ConfigureAwait(false);

        return
        [
            .. document.Instances
                .OrderByDescending(item => item.Base.ApplyTime)
                .Select(item => item),
        ];
    }

    /// <inheritdoc />
    public ValueTask SaveInstanceAsync(WorkflowInstanceRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return MutateAsync(
            document => document with
            {
                Instances =
                [
                    .. Without(document.Instances, record.ProcessInstanceId),
                    record,
                ],
            },
            ct);
    }

    // ───────────────────────── 端口：审批结论 ─────────────────────────

    /// <inheritdoc />
    public async ValueTask<WorkflowOutcomeRecord?> FindResultAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var document = await LoadAsync(ct).ConfigureAwait(false);

        return document.Results.FirstOrDefault(item =>
            string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public ValueTask SaveResultAsync(WorkflowOutcomeRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return MutateAsync(
            document => document with
            {
                Results = [.. Without(document.Results, record.ProcessInstanceId), record],
            },
            ct);
    }

    // ───────────────────────── 端口：流程参数表 ─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, string?>> FindParametersAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var document = await LoadAsync(ct).ConfigureAwait(false);

        return ParametersOf(document, processInstanceId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只动 <paramref name="values"/> 里提到的参数名，其余行保持原样 —— 这正是
    /// 「有值就按照 key 更新 value」的落点，也意味着调用方可以只传变化的那几个键。
    /// 值为 <see langword="null"/> 表示显式置空（会把该行写成 null，而不是删掉）。
    /// </remarks>
    public ValueTask SaveParametersAsync(
        string processInstanceId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var updatedAt = DateTime.UtcNow;

        return MutateAsync(
            document => document with
            {
                Parameters =
                [
                    // 保留其他实例的行，以及本实例里这次没有提到的参数
                    .. document.Parameters.Where(item =>
                        !string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal)
                        || !values.ContainsKey(item.Name)),

                    // 本次提到的参数，一律按新值覆盖
                    .. values.Select(item => new JsonParameterRow(
                        processInstanceId,
                        item.Key,
                        item.Value,
                        updatedAt)),
                ],
            },
            ct);
    }

    // ───────────────────────── 端口：审批记录 ─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowApprovalRecord>> FindApprovalsAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var document = await LoadAsync(ct).ConfigureAwait(false);

        return
        [
            .. document.Approvals.Where(item =>
                string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal)),
        ];
    }

    /// <inheritdoc />
    /// <remarks>追加而不是覆盖：一个实例可以提交很多次（驳回后重提），历史要留住全部。</remarks>
    public ValueTask SaveApprovalAsync(WorkflowApprovalRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return MutateAsync(
            document => document with { Approvals = [.. document.Approvals, record] },
            ct);
    }

    // ───────────────────────── 端口：当前待办 ─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowCurrentTaskRecord>> FindCurrentTasksAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var document = await LoadAsync(ct).ConfigureAwait(false);

        return
        [
            .. document.CurrentTasks.Where(item =>
                string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal)),
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    /// 整体替换：先去掉该实例的全部旧待办，再放新的 <paramref name="tasks"/>。
    /// 传空集就是「实例已结束，没有待办」—— 同样是一次合法的替换。
    /// </remarks>
    public ValueTask ReplaceCurrentTasksAsync(
        string processInstanceId,
        IReadOnlyList<WorkflowCurrentTaskRecord> tasks,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentNullException.ThrowIfNull(tasks);

        return MutateAsync(
            document => document with
            {
                CurrentTasks =
                [
                    .. document.CurrentTasks.Where(item =>
                        !string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal)),
                    .. tasks,
                ],
            },
            ct);
    }

    // ───────────────────────── 内部工具 ─────────────────────────

    /// <summary>读某实例的参数表，摊成「参数名 → 值」。</summary>
    private static Dictionary<string, string?> ParametersOf(JsonWorkflowDocument document, string processInstanceId)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var row in document.Parameters)
        {
            if (string.Equals(row.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
            {
                parameters[row.Name] = row.Value;
            }
        }

        return parameters;
    }

    private static IEnumerable<T> Without<T>(
        IReadOnlyList<T> items,
        string processInstanceId)
        where T : notnull =>
        items
            .Where(item =>
            {
                var id = item switch
                {
                    WorkflowInstanceRecord record => record.ProcessInstanceId,
                    WorkflowOutcomeRecord record => record.ProcessInstanceId,
                    WorkflowApprovalRecord record => record.ProcessInstanceId,
                    WorkflowCurrentTaskRecord record => record.ProcessInstanceId,
                    JsonParameterRow row => row.ProcessInstanceId,
                    _ => null,
                };

                return !string.Equals(id, processInstanceId, StringComparison.Ordinal);
            });

    private async ValueTask MutateAsync(
        Func<JsonWorkflowDocument, JsonWorkflowDocument> mutate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);

            await WriteAsync(mutate(document), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask WriteAsync(JsonWorkflowDocument document, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 临时文件 + 原子替换：并发写不会把目标文件写坏
        var tempPath = FilePath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, ct).ConfigureAwait(false);
        }

        File.Move(tempPath, FilePath, overwrite: true);
    }
}

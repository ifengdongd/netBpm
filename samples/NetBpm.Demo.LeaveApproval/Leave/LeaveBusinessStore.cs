using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetBpm.Workflow;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 请假流程的业务数据存储：<b>Demo 直接落一个 JSON 文件</b>，并实现应用层的
/// <see cref="IWorkflowDataStore"/> 端口。
/// </summary>
/// <remarks>
/// <para>
/// 真实项目这里应换成业务库（请假单表 + 审批结论表 + 流程参数表 + 审批记录表）。
/// Demo 选 JSON 文件是为了「零依赖 + 看得见」：文件内容就是业务数据本身，用记事本打开即可核对。
/// </para>
/// <para>
/// 🔑 <b>端口是通用的，落表方式是宿主自己的事</b>：应用层只说「给我一份参数快照」，
/// 这里把它摊成请假单表的 <c>days</c> / <c>reason</c> 两列 —— 请假单本来就有这两列，
/// 存成 JSON 字典反而不像业务表。
/// </para>
/// <para>
/// 并发策略：读-改-写整体在 <see cref="SemaphoreSlim"/> 内串行，落盘用「临时文件 + 原子替换」，
/// 避免并发写把 JSON 写坏。
/// </para>
/// </remarks>
public sealed class LeaveBusinessStore : IWorkflowDataStore, ILeaveBusinessInspector, IDisposable
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
    /// 不能用 <see cref="Encoding.UTF8"/> —— 那个实例的 <c>encoderShouldEmitUTF8Identifier</c> 是 true，
    /// 会在文件头写 BOM。带 BOM 的 JSON 用 <c>jq</c> / <c>JSON.parse</c> / 大多数解析器都会直接报错，
    /// 而这个文件存在的意义就是「能随手打开核对」。
    /// </para>
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>构造存储：路径取配置 <c>Leave:DataFile</c>（默认 <c>data/leave-business.json</c>）。</summary>
    public LeaveBusinessStore(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = configuration["Leave:DataFile"];
        var relative = string.IsNullOrWhiteSpace(configured) ? "data/leave-business.json" : configured;

        FilePath = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(environment.ContentRootPath, relative);
    }

    /// <summary>业务数据文件绝对路径（演示页会显示它，方便直接打开核对）。</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public string Store => "Json";

    /// <inheritdoc />
    public string Location => $"JSON 文件 · {FilePath}";

    /// <inheritdoc />
    /// <remarks>
    /// 直接把五个区块摊出来 —— 键名与 <see cref="LeaveBusinessDocument"/> 的属性一一对应，
    /// 所以「页面看到的」就是「文件里存的」，中间没有加工。
    /// </remarks>
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
    public async ValueTask<LeaveBusinessDocument> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            return new LeaveBusinessDocument();
        }

        // 读用 Encoding.UTF8 没问题：File.ReadAllTextAsync 内部的 StreamReader 会识别并跳过 BOM，
        // 所以手改过的文件（有些编辑器会加 BOM）照样读得进来。
        var json = await File.ReadAllTextAsync(FilePath, Encoding.UTF8, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new LeaveBusinessDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<LeaveBusinessDocument>(json, SerializerOptions)
                ?? new LeaveBusinessDocument();
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

        var record = document.Instances
            .FirstOrDefault(item => string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));

        return record is null ? null : new WorkflowInstanceRecord(record.ProcessInstanceId, record.Base);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowInstanceRecord>> FindInstancesAsync(CancellationToken ct = default)
    {
        var document = await LoadAsync(ct).ConfigureAwait(false);

        return
        [
            .. document.Instances
                .OrderByDescending(item => item.Base.ApplyTime)
                .Select(item => new WorkflowInstanceRecord(item.ProcessInstanceId, item.Base)),
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    /// 请假单的 <c>days</c> / <c>reason</c> 两列是<b>流程参数表的投影</b>：
    /// 应用层调本方法前会先写参数表（见 <c>WorkflowService.SubmitAsync</c> 的注释），
    /// 所以这里从参数表里取即可，不另存一份、也就不会两处不一致。
    /// </remarks>
    public ValueTask SaveInstanceAsync(WorkflowInstanceRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return MutateAsync(
            document =>
            {
                var parameters = ParametersOf(document, record.ProcessInstanceId);

                return document with
                {
                    Instances =
                    [
                        .. Without(document.Instances, record.ProcessInstanceId),
                        new LeaveInstanceRecord(
                            record.ProcessInstanceId,
                            record.Base,
                            LeaveParameterNames.DaysOf(parameters),
                            LeaveParameterNames.ReasonOf(parameters)),
                    ],
                };
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

        var record = document.Results
            .FirstOrDefault(item => string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));

        return record is null
            ? null
            : new WorkflowOutcomeRecord(
                record.ProcessInstanceId,
                record.Base,
                record.Outcome,
                record.ActivityId,
                record.ActivityName,
                record.CompletedAt,
                ToParameters(record.Days, record.Reason));
    }

    /// <inheritdoc />
    public ValueTask SaveResultAsync(WorkflowOutcomeRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var stored = new LeaveResultRecord(
            record.ProcessInstanceId,
            record.Base,
            Days: LeaveParameterNames.DaysOf(record.Parameters),
            Reason: LeaveParameterNames.ReasonOf(record.Parameters),
            Outcome: record.Outcome,
            ActivityId: record.ActivityId,
            ActivityName: record.ActivityName,
            CompletedAt: record.CompletedAt);

        return MutateAsync(
            document => document with
            {
                Results = [.. Without(document.Results, record.ProcessInstanceId), stored],
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
                    .. values.Select(item => new ProcessParameterRecord(
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
    private static Dictionary<string, string?> ParametersOf(LeaveBusinessDocument document, string processInstanceId)
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

    /// <summary>请假单的两列 → 通用参数快照（与应用层的 <see cref="WorkflowOutcomeRecord.Parameters"/> 对接）。</summary>
    private static Dictionary<string, string?> ToParameters(int days, string? reason)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [LeaveParameterNames.Days] = days.ToString(CultureInfo.InvariantCulture),
        };

        if (reason is not null)
        {
            parameters[LeaveParameterNames.Reason] = reason;
        }

        return parameters;
    }

    private static IEnumerable<LeaveInstanceRecord> Without(
        IReadOnlyList<LeaveInstanceRecord> items,
        string processInstanceId) =>
        items.Where(item => !string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));

    private static IEnumerable<LeaveResultRecord> Without(
        IReadOnlyList<LeaveResultRecord> items,
        string processInstanceId) =>
        items.Where(item => !string.Equals(item.ProcessInstanceId, processInstanceId, StringComparison.Ordinal));

    private async ValueTask MutateAsync(
        Func<LeaveBusinessDocument, LeaveBusinessDocument> mutate,
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

    private async ValueTask WriteAsync(LeaveBusinessDocument document, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(document, SerializerOptions);
        var temporary = FilePath + ".tmp";

        await File.WriteAllTextAsync(temporary, json, Utf8NoBom, ct).ConfigureAwait(false);

        // 原子替换：避免读到写了一半的文件
        File.Move(temporary, FilePath, overwrite: true);
    }
}

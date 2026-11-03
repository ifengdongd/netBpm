using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBpm.ServerTaskWorker;

/// <summary>
/// 用 JSON 文件模拟业务库 —— 只为让样例开箱能跑。
/// </summary>
/// <remarks>
/// <para>
/// 接真实系统时把 <see cref="ILeaveBillStore"/> 换成你们的仓储（MySQL / Oracle / 内部服务调用）。
/// 对应的 SQL 写法见 <c>docs/leave-worker-3007-integration.md</c> §3.4，核心只有一句
/// （<c>bill_no</c> 就是流程的 <c>businessKey</c>）：
/// </para>
/// <code>
/// update leave_bill set status = 'APPROVED', handled_at = now(), process_instance_id = ?
///  where bill_no = ? and status = 'PENDING'
/// </code>
/// <para>
/// 影响行数为 0 就说明别人已经处理过（或状态不可变），再查一次现有状态决定怎么回执。
/// 这里用文件 + 进程内串行锁实现同一套语义。
/// </para>
/// </remarks>
public sealed class JsonLeaveBillStore : ILeaveBillStore, IDisposable
{
    private static readonly JsonSerializerOptions FileJson = new(WorkerJson.Options)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;
    private readonly ILogger<JsonLeaveBillStore> _logger;

    /// <summary>构造。</summary>
    /// <param name="options">运行参数（取落盘路径）。</param>
    /// <param name="logger">日志。</param>
    public JsonLeaveBillStore(WorkerOptions options, ILogger<JsonLeaveBillStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _file = Path.GetFullPath(options.DataFile);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BillUpsert> MarkProcessedAsync(
        string businessId,
        string processInstanceId,
        string status,
        LeaveBillPatch patch,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(businessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(patch);

        // 串行化读改写：并发任务（maxTasks>1）同时落盘会互相覆盖。
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);

            // 🔑 按业务单号找单据（不是实例 id）：同一张单重新发起时实例 id 变了，
            // 业务上仍然是同一行记录 —— 用实例 id 当主键会凭空多出一张单。
            var bill = all.TryGetValue(businessId, out var existing)
                ? existing
                : new LeaveBill
                {
                    Id = businessId,
                    ProcessInstanceId = processInstanceId,
                    Status = LeaveBillStatus.Pending,
                    CreatedAt = Now(),
                };

            bill.ProcessInstanceId = processInstanceId;

            // 幂等口径有两条，缺一条就会出事故：
            //   1. 状态已经是目标值 → 直接返回，不改 handledAt、不改引用号（重复投递不重复落账）；
            //   2. 已经是另一个终态 → 也直接返回并标记 conflict，**不覆盖**。
            //      「已拒绝」的单据被后来的通过请求改成「已通过」，是最难查的一类数据错误。
            if (string.Equals(bill.Status, status, StringComparison.Ordinal))
            {
                return new BillUpsert(bill, Duplicated: true, Conflict: false);
            }

            if (!string.Equals(bill.Status, LeaveBillStatus.Pending, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "单据 {BillId} 已是终态 {Current}，忽略 {Target}（实例 {InstanceId}）",
                    bill.Id,
                    bill.Status,
                    status,
                    processInstanceId);

                return new BillUpsert(bill, Duplicated: true, Conflict: true);
            }

            bill.Applicant = patch.Applicant ?? bill.Applicant;
            bill.Days = patch.Days ?? bill.Days;
            bill.Reason = patch.Reason ?? bill.Reason;
            bill.Comment = patch.Comment ?? bill.Comment;
            bill.Status = status;
            bill.HandledAt = Now();

            if (string.Equals(status, LeaveBillStatus.Approved, StringComparison.Ordinal))
            {
                bill.ApprovedVia = patch.Via;
            }
            else if (string.Equals(status, LeaveBillStatus.Rejected, StringComparison.Ordinal))
            {
                bill.RejectedVia = patch.Via;
            }

            all[businessId] = bill;

            await WriteAsync(all, ct).ConfigureAwait(false);

            return new BillUpsert(bill, Duplicated: false, Conflict: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<Dictionary<string, LeaveBill>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_file))
        {
            return new Dictionary<string, LeaveBill>(StringComparer.Ordinal);
        }

        var raw = await File.ReadAllTextAsync(_file, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, LeaveBill>(StringComparer.Ordinal);
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, LeaveBill>>(raw, FileJson);

        return parsed is null
            ? new Dictionary<string, LeaveBill>(StringComparer.Ordinal)
            : new Dictionary<string, LeaveBill>(parsed, StringComparer.Ordinal);
    }

    private async Task WriteAsync(Dictionary<string, LeaveBill> all, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_file);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写临时文件再改名：进程被杀时不会留下半个 JSON（下次读直接炸）。
        var temp = _file + ".tmp";
        var content = JsonSerializer.Serialize(all, FileJson);

        await File.WriteAllTextAsync(temp, content, ct).ConfigureAwait(false);
        File.Move(temp, _file, overwrite: true);
    }

    private static string Now() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

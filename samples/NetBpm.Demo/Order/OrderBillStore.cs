using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using NetBpm.Demo.Common;
using NetBpm.Workflow;

namespace NetBpm.Demo.Order;

/// <summary>
/// 订单业务数据的端口：提交申请写入（status=1）、审批结束回写（100 / -1）、列表与详情读取。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 它是<b>业务自己的表</b>（<c>order_bill</c>）的落点，与流程引擎的业务数据端口
/// <see cref="IWorkflowDataStore"/>（instances / 审批结论 / 参数表…）是两回事：
/// 引擎那套五块数据由公共的 <see cref="JsonWorkflowDataStore"/> 落
/// <c>data/workflow.json</c>，订单行落本实现的 <c>data/order-bill.json</c>。
/// </para>
/// <para>
/// JSON 文件版参考 <see cref="JsonWorkflowDataStore"/> 的写法：
/// 读-改-写在信号量内串行，落盘「临时文件 + 原子替换」；换 MySQL 真库时照
/// <c>db/mysql/0006_create_order_bill.sql</c> 落 <c>order_bill</c> 表即可。
/// </para>
/// </remarks>
public interface IOrderBillStore
{
    /// <summary>落点自述（会打在启动日志里，方便打开核对）。</summary>
    string Location { get; }

    /// <summary>新增一行订单（提交申请时，status 固定 1=审批中）。</summary>
    ValueTask<OrderBillRow> AddAsync(OrderBillRow bill, CancellationToken ct = default);

    /// <summary>按订单号查一行；查不到返回 <see langword="null"/>。</summary>
    ValueTask<OrderBillRow?> FindAsync(long businessId, CancellationToken ct = default);

    /// <summary>全部订单（新的在前）。</summary>
    ValueTask<IReadOnlyList<OrderBillRow>> ListAsync(CancellationToken ct = default);

    /// <summary>回填流程实例 id（发起流程成功后调用，用于按订单反查审批进度）。</summary>
    ValueTask<OrderBillRow?> SetProcessInstanceAsync(long businessId, string processInstanceId, CancellationToken ct = default);

    /// <summary>
    /// 回写订单状态：-1=审批拒绝 / 100=审批通过（审批结束的服务任务委托调用），
    /// 同时记下最终审批意见。订单不存在返回 <see langword="null"/>。
    /// </summary>
    ValueTask<OrderBillRow?> UpdateStatusAsync(long businessId, short status, string? approveComment, CancellationToken ct = default);
}

/// <summary>JSON 文件版订单存储：<see cref="IOrderBillStore"/> 的演示实现。</summary>
public sealed class OrderBillStore : IOrderBillStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,

        // 中文不转义成 \uXXXX —— 业务数据是要给人看的
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>构造存储：路径取配置 <c>Order:BillDataFile</c>（默认 <c>data/order-bill.json</c>）。</summary>
    public OrderBillStore(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = configuration[ConstField.OrderBillDataFileConfigKey];
        var relative = string.IsNullOrWhiteSpace(configured) ? "data/order-bill.json" : configured;

        FilePath = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(environment.ContentRootPath, relative);
    }

    /// <summary>订单数据文件绝对路径。</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public string Location => $"JSON 文件 · {FilePath}";

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    // ───────────────────────── 端口实现 ─────────────────────────

    /// <inheritdoc />
    public async ValueTask<OrderBillRow> AddAsync(OrderBillRow bill, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bill);

        return await MutateAsync(
            document =>
            {
                document.Bills.Add(bill);
                return bill;
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OrderBillRow?> FindAsync(long businessId, CancellationToken ct = default)
    {
        var document = await LoadAsync(ct).ConfigureAwait(false);

        return document.Bills.FirstOrDefault(bill => bill.BusinessId == businessId);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OrderBillRow>> ListAsync(CancellationToken ct = default)
    {
        var document = await LoadAsync(ct).ConfigureAwait(false);

        return [.. document.Bills.OrderByDescending(bill => bill.CreateTime)];
    }

    /// <inheritdoc />
    public async ValueTask<OrderBillRow?> SetProcessInstanceAsync(
        long businessId,
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return await MutateAsync(
            document => ReplaceOrNull(document, businessId, bill => bill with { ProcessInstanceId = processInstanceId }),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OrderBillRow?> UpdateStatusAsync(
        long businessId,
        short status,
        string? approveComment,
        CancellationToken ct = default)
    {
        return await MutateAsync(
            document => ReplaceOrNull(document, businessId, bill => bill with
            {
                Status = status,
                ApproveComment = approveComment,
                ModifyTime = DateTime.UtcNow,
            }),
            ct).ConfigureAwait(false);
    }

    // ───────────────────────── 内部工具 ─────────────────────────

    private static OrderBillRow? ReplaceOrNull(OrderBillDocument document, long businessId, Func<OrderBillRow, OrderBillRow> update)
    {
        var index = document.Bills.FindIndex(bill => bill.BusinessId == businessId);

        if (index < 0)
        {
            return null;
        }

        var updated = update(document.Bills[index]);
        document.Bills[index] = updated;

        return updated;
    }

    private async ValueTask<T> MutateAsync<T>(Func<OrderBillDocument, T> mutate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            var result = mutate(document);
            await WriteAsync(document, ct).ConfigureAwait(false);

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<OrderBillDocument> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
        {
            return new OrderBillDocument();
        }

        var json = await File.ReadAllTextAsync(FilePath, Encoding.UTF8, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new OrderBillDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<OrderBillDocument>(json, SerializerOptions)
                ?? new OrderBillDocument();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"订单数据文件 '{FilePath}' 不是合法 JSON：{exception.Message}", exception);
        }
    }

    private async ValueTask WriteAsync(OrderBillDocument document, CancellationToken ct)
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

/// <summary>JSON 存储的文档形状：订单行列表。</summary>
public sealed record OrderBillDocument
{
    /// <summary>订单行（与 <c>order_bill</c> 表的行一一对应）。</summary>
    public List<OrderBillRow> Bills { get; init; } = [];
}

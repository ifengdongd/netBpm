using System.Collections.Concurrent;
using NetBpm.Demo.Order;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 订单业务存储的<b>内存实现</b>：进程内一张字典，重启即清空。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不再落库：平台化之后 <c>order_bill</c> 这类<b>业务表归接入方自己的库</b>，
/// 平台脚本（<c>db/*/netbpm_init.sql</c>）只建引擎表与四张流程视图表，不再建业务单表。
/// 示例工程没必要为演示单表自建一套 DDL，所以订单行放在进程内存里 ——
/// 审批状态回写、伪删除、按单号反查这些<b>端口语义一个不少</b>，
/// 演示的是「业务侧如何与流程引擎协作」，而不是「业务表怎么建」。
/// </para>
/// <para>
/// 真实项目把本类换成自己的库里那张表即可（端口 <see cref="IOrderBillStore"/> 不变）：
/// 提交申请置 status=1、审批结束回写 100 / -1，与这里逐条对应。
/// </para>
/// <para>
/// 线程安全：端点与流程监听器（服务任务委托）会并发读写，故用
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>；读-改-写用 <c>AddOrUpdate</c> 收口，
/// 不会出现「先 Find 再 Update」之间的丢更新。
/// </para>
/// </remarks>
public sealed class InMemoryOrderBillStore : IOrderBillStore
{
    private readonly ConcurrentDictionary<long, OrderBillRow> _rows = new();
    private readonly ConcurrentDictionary<long, byte> _deleted = new();
    private readonly ILogger<InMemoryOrderBillStore> _logger;

    /// <summary>构造存储。</summary>
    public InMemoryOrderBillStore(ILogger<InMemoryOrderBillStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    public string Location => "内存（示例自带，平台不再建业务单表）";

    /// <inheritdoc />
    public ValueTask<OrderBillRow> AddAsync(OrderBillRow bill, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bill);

        if (!_rows.TryAdd(bill.BusinessId, bill))
        {
            // 与真库主键冲突同一表现：发号器出了重复单号
            throw new InvalidOperationException($"订单 {bill.BusinessId} 已存在，无法重复写入。");
        }

        _deleted.TryRemove(bill.BusinessId, out _);

        return ValueTask.FromResult(bill);
    }

    /// <inheritdoc />
    public ValueTask<OrderBillRow?> FindAsync(long businessId, CancellationToken ct = default) =>
        ValueTask.FromResult(Visible(businessId));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OrderBillRow>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<OrderBillRow> rows = _rows.Values
            .Where(row => !_deleted.ContainsKey(row.BusinessId))
            .OrderByDescending(row => row.CreateTime)
            .ThenByDescending(row => row.BusinessId)
            .ToList();

        return ValueTask.FromResult(rows);
    }

    /// <inheritdoc />
    public ValueTask<OrderBillRow?> SetProcessInstanceAsync(
        long businessId,
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return ValueTask.FromResult(Mutate(businessId, row => row with
        {
            ProcessInstanceId = processInstanceId,
            ModifyTime = DateTime.UtcNow,
        }));
    }

    /// <inheritdoc />
    public ValueTask<OrderBillRow?> UpdateStatusAsync(
        long businessId,
        short status,
        string? approveComment,
        CancellationToken ct = default)
    {
        var updated = Mutate(businessId, row => row with
        {
            Status = status,
            ApproveComment = approveComment,
            ModifyTime = DateTime.UtcNow,
        });

        if (updated is null)
        {
            _logger.LogWarning("订单 {BusinessId} 不存在（或已删除），状态 {Status} 未回写。", businessId, status);
        }

        return ValueTask.FromResult(updated);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(long businessId, CancellationToken ct = default)
    {
        // 伪删除：行留着、打标记；之后的列表与明细都看不见它
        if (!_rows.ContainsKey(businessId) || !_deleted.TryAdd(businessId, 0))
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(true);
    }

    /// <summary>看得见的那一行（不存在或已伪删除给 <see langword="null"/>）。</summary>
    private OrderBillRow? Visible(long businessId) =>
        _rows.TryGetValue(businessId, out var row) && !_deleted.ContainsKey(businessId) ? row : null;

    /// <summary>读-改-写：命中就换掉，返回改后的行；命不中返回 <see langword="null"/>。</summary>
    private OrderBillRow? Mutate(long businessId, Func<OrderBillRow, OrderBillRow> change)
    {
        if (Visible(businessId) is null)
        {
            return null;
        }

        var updated = _rows.AddOrUpdate(
            businessId,
            _ => throw new InvalidOperationException($"订单 {businessId} 在改写期间消失。"),
            (_, current) => change(current));

        return updated;
    }
}

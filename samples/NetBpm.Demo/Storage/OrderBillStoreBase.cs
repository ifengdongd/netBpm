using NetBpm.Demo.Common;
using NetBpm.Demo.Order;
using NetBpm.Demo.Storage.DataAccess;
using NetBpm.Persistence;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 订单业务存储的<b>逻辑基类</b>：全部 CRUD 委托给公共数据操作接口
/// <see cref="IDataAccessor{TEntity}"/>（本类不再自己拼 SQL）。
/// </summary>
/// <remarks>
/// 端口语义：读-改-写、审批结束回写状态（100 / -1）；落点是 db/*/0006 建的
/// <c>order_bill</c> 表（MySQL 无前缀 / SQL Server 在 <c>wf</c> schema —— 表名前缀由方言的
/// 标识符引用处理，见 <see cref="MySqlDataAccessor{TEntity}"/> / <see cref="SqlServerDataAccessor{TEntity}"/>）。
/// 伪删除（<see cref="DeleteAsync"/>）由访问器把 <c>deleted_flag</c> 置 1；列表与明细自动过滤已删除行。
/// </remarks>
public abstract class OrderBillStoreBase : IOrderBillStore
{
    private readonly IDataAccessor<OrderBillRow> _accessor;
    private readonly ILogger _logger;
    private readonly string _connectionString;

    /// <summary>构造存储（方言访问器由 DI 按 <c>NetBpm:DatabaseType</c> 注入）。</summary>
    protected OrderBillStoreBase(
        IDataAccessor<OrderBillRow> accessor,
        IConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _logger = logger;
        _connectionString = configuration["NetBpm:ConnectionString"] ?? "(未配置)";
    }

    /// <summary>落点展示名（Location 前缀）：MySQL / SQL Server。</summary>
    protected abstract string DisplayName { get; }

    /// <inheritdoc />
    public string Location => $"{DisplayName} · {DatabaseOf(_connectionString)}";

    /// <inheritdoc />
    public async ValueTask<OrderBillRow> AddAsync(OrderBillRow bill, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bill);

        // 主键（订单单号）由业务侧发好、随实体写入；插入后原样返回
        await _accessor.InsertAsync(bill, ct).ConfigureAwait(false);

        return bill;
    }

    /// <inheritdoc />
    public ValueTask<OrderBillRow?> FindAsync(long businessId, CancellationToken ct = default) =>
        _accessor.FindByIdAsync(businessId, ct);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OrderBillRow>> ListAsync(CancellationToken ct = default) =>
        await _accessor.FindListAsync(orderBy: "create_time desc", ct: ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<OrderBillRow?> SetProcessInstanceAsync(
        long businessId,
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var bill = await _accessor.FindByIdAsync(businessId, ct).ConfigureAwait(false);
        if (bill is null)
        {
            return null;
        }

        var updated = bill with { ProcessInstanceId = processInstanceId, ModifyTime = DateTime.UtcNow };

        return await _accessor.UpdateAsync(updated, ct).ConfigureAwait(false) == 0
            ? null
            : await FindAsync(businessId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OrderBillRow?> UpdateStatusAsync(
        long businessId,
        short status,
        string? approveComment,
        CancellationToken ct = default)
    {
        var bill = await _accessor.FindByIdAsync(businessId, ct).ConfigureAwait(false);

        if (bill is null)
        {
            _logger.LogWarning("订单 {BusinessId} 不存在（或已删除），状态 {Status} 未回写。", businessId, status);
            return null;
        }

        var updated = bill with { Status = status, ApproveComment = approveComment, ModifyTime = DateTime.UtcNow };

        if (await _accessor.UpdateAsync(updated, ct).ConfigureAwait(false) == 0)
        {
            _logger.LogWarning("订单 {BusinessId} 状态 {Status} 回写未命中任何行。", businessId, status);
            return null;
        }

        return await FindAsync(businessId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(long businessId, CancellationToken ct = default) =>
        await _accessor.DeleteAsync(businessId, ct).ConfigureAwait(false) > 0;

    /// <summary>连接串 → 「主机[:端口]/库名」（不外泄密码）。</summary>
    private static string DatabaseOf(string connectionString)
    {
        var server = Field(connectionString, "Server")
            ?? Field(connectionString, "Host")
            ?? Field(connectionString, "Data Source")
            ?? "?";
        var port = Field(connectionString, "Port");
        var database = Field(connectionString, "Database")
            ?? Field(connectionString, "Initial Catalog")
            ?? "?";

        return port is null ? $"{server}/{database}" : $"{server}:{port}/{database}";
    }

    private static string? Field(string connectionString, string name) =>
        connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && string.Equals(pair[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair[1].Trim())
            .FirstOrDefault(value => value.Length > 0);
}

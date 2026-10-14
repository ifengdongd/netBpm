using System.Globalization;
using Dapper;
using NetBpm.Demo.Order;
using NetBpm.Persistence;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 订单业务数据落 <b>MySQL <c>order_bill</c> 表</b>的 <see cref="IOrderBillStore"/> 实现。
/// </summary>
/// <remarks>
/// 与 JSON 版 <see cref="OrderBillStore"/> 同一套端口语义（读-改-写、状态回写），
/// 落点换成 0006 建的 <c>order_bill</c> 表；状态回写在事务里完成。
/// </remarks>
public sealed class MySqlOrderBillStore : IOrderBillStore
{
    private readonly IDbConnectionFactory _connections;
    private readonly ILogger<MySqlOrderBillStore> _logger;
    private readonly string _connectionString;
    private readonly long _tenantId;

    /// <summary>构造存储。</summary>
    public MySqlOrderBillStore(
        IDbConnectionFactory connections,
        IConfiguration configuration,
        ILogger<MySqlOrderBillStore> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _logger = logger;
        _connectionString = configuration["NetBpm:ConnectionString"] ?? "(未配置)";
        _tenantId = configuration.GetValue("Order:TenantId", 1L);
    }

    /// <inheritdoc />
    public string Location => $"MySQL · {DatabaseOf(_connectionString)}";

    /// <inheritdoc />
    public async ValueTask<OrderBillRow> AddAsync(OrderBillRow bill, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bill);

        await using var connection = _connections.CreateConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into order_bill
                (tenant_id, business_id, product_name, amount, remark, status, approve_comment,
                 apply_user, apply_dept_id, business_dept_id, process_instance_id, create_time)
            values
                (@TenantId, @BusinessId, @ProductName, @Amount, @Remark, @Status, @ApproveComment,
                 @ApplyUser, @ApplyDeptId, @BusinessDeptId, @ProcessInstanceId, @CreateTime)
            """,
            ToParams(bill),
            cancellationToken: ct)).ConfigureAwait(false);

        return bill;
    }

    /// <inheritdoc />
    public async ValueTask<OrderBillRow?> FindAsync(long businessId, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var row = await connection.QueryFirstOrDefaultAsync<OrderBillRowDto>(new CommandDefinition(
            SelectSql + " where business_id = @businessId limit 1",
            new { businessId },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : ToRow(row);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OrderBillRow>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<OrderBillRowDto>(new CommandDefinition(
            SelectSql + " order by create_time desc",
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(ToRow)];
    }

    /// <inheritdoc />
    public async ValueTask<OrderBillRow?> SetProcessInstanceAsync(
        long businessId,
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "update order_bill set process_instance_id = @processInstanceId where business_id = @businessId",
            new { businessId, processInstanceId },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected == 0 ? null : await FindAsync(businessId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OrderBillRow?> UpdateStatusAsync(
        long businessId,
        short status,
        string? approveComment,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            update order_bill
               set status          = @status,
                   approve_comment = @approveComment,
                   modify_time     = @now
             where business_id = @businessId
            """,
            new { businessId, status, approveComment, now = DateTime.UtcNow },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (affected == 0)
        {
            _logger.LogWarning("订单 {BusinessId} 不存在，状态 {Status} 未回写。", businessId, status);
            return null;
        }

        return await FindAsync(businessId, ct).ConfigureAwait(false);
    }

    // ───────────────────────── 内部工具 ─────────────────────────

    private const string SelectSql = """
        select tenant_id           as TenantId,
               business_id         as BusinessId,
               product_name        as ProductName,
               amount              as Amount,
               remark              as Remark,
               status              as Status,
               approve_comment     as ApproveComment,
               apply_user          as ApplyUser,
               apply_dept_id       as ApplyDeptId,
               business_dept_id    as BusinessDeptId,
               process_instance_id as ProcessInstanceId,
               create_time         as CreateTime,
               modify_time         as ModifyTime
          from order_bill
        """;

    private static object ToParams(OrderBillRow bill) => new
    {
        bill.TenantId,
        bill.BusinessId,
        bill.ProductName,
        bill.Amount,
        bill.Remark,
        bill.Status,
        bill.ApproveComment,
        bill.ApplyUser,
        bill.ApplyDeptId,
        bill.BusinessDeptId,
        bill.ProcessInstanceId,
        bill.CreateTime,
    };

    private static OrderBillRow ToRow(OrderBillRowDto row) => new(
        TenantId: row.TenantId,
        BusinessId: row.BusinessId,
        ProductName: row.ProductName,
        Amount: row.Amount,
        Remark: row.Remark,
        Status: row.Status,
        ApproveComment: row.ApproveComment,
        ApplyUser: row.ApplyUser,
        ApplyDeptId: row.ApplyDeptId,
        BusinessDeptId: row.BusinessDeptId,
        ProcessInstanceId: row.ProcessInstanceId,
        CreateTime: DateTime.SpecifyKind(row.CreateTime, DateTimeKind.Utc),
        ModifyTime: row.ModifyTime is { } modifyTime
            ? DateTime.SpecifyKind(modifyTime, DateTimeKind.Utc)
            : null);

    private sealed class OrderBillRowDto
    {
        public long TenantId { get; set; }

        public long BusinessId { get; set; }

        public string? ProductName { get; set; }

        public decimal? Amount { get; set; }

        public string? Remark { get; set; }

        public short Status { get; set; }

        public string? ApproveComment { get; set; }

        public string ApplyUser { get; set; } = string.Empty;

        public string ApplyDeptId { get; set; } = string.Empty;

        public string? BusinessDeptId { get; set; }

        public string? ProcessInstanceId { get; set; }

        public DateTime CreateTime { get; set; }

        public DateTime? ModifyTime { get; set; }
    }

    /// <summary>连接串 → 「主机[:端口]/库名」（不外泄密码）。</summary>
    private static string DatabaseOf(string connectionString)
    {
        var server = Field(connectionString, "Server") ?? Field(connectionString, "Host") ?? "?";
        var port = Field(connectionString, "Port");
        var database = Field(connectionString, "Database") ?? "?";

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

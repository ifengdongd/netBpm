using System.Data;
using NetBpm.Demo.Order;

namespace NetBpm.Demo.Storage.DataAccess;

/// <summary>
/// <see cref="OrderBillRow"/> ↔ <c>order_bill</c> 表列的双向映射（方言无关，
/// 标识符引用与行数上限由 <see cref="MySqlDataAccessor{TEntity}"/> / <see cref="SqlServerDataAccessor{TEntity}"/> 处理）。
/// </summary>
public sealed class OrderBillMapper : IEntityMapper<OrderBillRow>
{
    /// <summary>表规格：主键 business_id（业务侧发号）；伪删除列 deleted_flag（1 = 已删除）。</summary>
    public EntityTableSpec Table { get; } = new(
        TableName: "order_bill",
        KeyColumn: "business_id",
        Columns:
        [
            "tenant_id", "business_id", "product_name", "amount", "remark", "status",
            "approve_comment", "apply_user", "apply_dept_id", "business_dept_id",
            "process_instance_id", "create_time", "modify_time", "deleted_flag",
        ],
        UpdateColumns:
        [
            "product_name", "amount", "remark", "status", "approve_comment",
            "apply_user", "apply_dept_id", "business_dept_id", "process_instance_id", "modify_time",
        ],
        SoftDeleteColumn: "deleted_flag",
        SoftDeleteValue: (byte)1);

    /// <inheritdoc />
    public long GetKey(OrderBillRow entity) => entity.BusinessId;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToColumns(OrderBillRow entity) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tenant_id"] = entity.TenantId,
            ["business_id"] = entity.BusinessId,
            ["product_name"] = entity.ProductName,
            ["amount"] = entity.Amount,
            ["remark"] = entity.Remark,
            ["status"] = entity.Status,
            ["approve_comment"] = entity.ApproveComment,
            ["apply_user"] = entity.ApplyUser,
            ["apply_dept_id"] = entity.ApplyDeptId,
            ["business_dept_id"] = entity.BusinessDeptId,
            ["process_instance_id"] = entity.ProcessInstanceId,
            ["create_time"] = entity.CreateTime,
            ["modify_time"] = entity.ModifyTime,
            ["deleted_flag"] = (byte)0,
        };

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> ToUpdateColumns(OrderBillRow entity) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["product_name"] = entity.ProductName,
            ["amount"] = entity.Amount,
            ["remark"] = entity.Remark,
            ["status"] = entity.Status,
            ["approve_comment"] = entity.ApproveComment,
            ["apply_user"] = entity.ApplyUser,
            ["apply_dept_id"] = entity.ApplyDeptId,
            ["business_dept_id"] = entity.BusinessDeptId,
            ["process_instance_id"] = entity.ProcessInstanceId,
            ["modify_time"] = entity.ModifyTime ?? DateTime.UtcNow,
        };

    /// <inheritdoc />
    public OrderBillRow? FromRow(IReadOnlyDictionary<string, object?> row)
    {
        if (row.Count == 0)
        {
            return null;
        }

        return new OrderBillRow(
            TenantId: AsLong(row["tenant_id"]),
            BusinessId: AsLong(row["business_id"]),
            ProductName: AsString(row["product_name"]),
            Amount: AsDecimal(row["amount"]),
            Remark: AsString(row["remark"]),
            Status: AsShort(row["status"]),
            ApproveComment: AsString(row["approve_comment"]),
            ApplyUser: AsString(row["apply_user"]) ?? string.Empty,
            ApplyDeptId: AsString(row["apply_dept_id"]) ?? string.Empty,
            BusinessDeptId: AsString(row["business_dept_id"]),
            ProcessInstanceId: AsString(row["process_instance_id"]),
            CreateTime: AsDateTime(row["create_time"]) ?? DateTime.UtcNow,
            ModifyTime: AsDateTime(row["modify_time"]));
    }

    // ───────────────────────── 列值转换（DBNull → null）─────────────────────────

    private static long AsLong(object? value) => value switch
    {
        null or DBNull => 0L,
        long number => number,
        int number => number,
        _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static short AsShort(object? value) => value switch
    {
        null or DBNull => (short)0,
        short number => number,
        _ => Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static decimal? AsDecimal(object? value) => value switch
    {
        null or DBNull => null,
        decimal number => number,
        _ => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string? AsString(object? value) => value switch
    {
        null or DBNull => null,
        string text => text,
        _ => value.ToString(),
    };

    private static DateTime? AsDateTime(object? value) => value switch
    {
        null or DBNull => null,
        DateTime moment => DateTime.SpecifyKind(moment, DateTimeKind.Utc),
        _ => null,
    };
}

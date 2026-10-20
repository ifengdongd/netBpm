namespace NetBpm.Persistence;

/// <summary>
/// 数据库层约束冲突的识别（与方言绑定，与 <see cref="TransientDatabaseErrors"/> 同层）。
/// <para>
/// 🔑 供「读-算-写」路径做冲突重试：例如 revision 由 <c>max+1</c> 计算得出时，
/// 并发写入会撞唯一索引 —— 数据库保证正确性（拒绝第二笔），调用方捕获后重读重算即可。
/// </para>
/// </summary>
public static class DatabaseErrors
{
    /// <summary>判定异常是否为唯一索引 / 唯一约束冲突。</summary>
    public static bool IsUniqueViolation(Exception exception) =>
        IsSqlServerUniqueViolation(exception) || IsMySqlDuplicateEntry(exception);

    /// <summary>SQL Server：2601 = 唯一索引重复键，2627 = 唯一约束冲突。</summary>
    private static bool IsSqlServerUniqueViolation(Exception exception) =>
        exception is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    /// <summary>MySQL：1062 = 重复键（Duplicate entry）。</summary>
    private static bool IsMySqlDuplicateEntry(Exception exception) =>
        exception is MySqlConnector.MySqlException { Number: 1062 };
}

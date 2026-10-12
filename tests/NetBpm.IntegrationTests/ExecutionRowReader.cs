using System.Globalization;
using Dapper;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary><c>ACT_RU_EXECUTION</c> 的一行（测试视角的原始快照）。</summary>
/// <param name="Id">执行 id。</param>
/// <param name="ProcessInstanceId"><c>PROC_INST_ID_</c>。</param>
/// <param name="RootProcessInstanceId"><c>ROOT_PROC_INST_ID_</c>。</param>
/// <param name="ParentId">父执行 id（根执行为 null）。</param>
/// <param name="ActivityId">当前活动 id。</param>
/// <param name="IsActive"><c>IS_ACTIVE_</c>。</param>
/// <param name="IsScope"><c>IS_SCOPE_</c>。</param>
/// <param name="IsConcurrent"><c>IS_CONCURRENT_</c>。</param>
/// <param name="Revision"><c>REV_</c>。</param>
internal sealed record ExecutionRow(
    string Id,
    string? ProcessInstanceId,
    string? RootProcessInstanceId,
    string? ParentId,
    string? ActivityId,
    bool IsActive,
    bool IsScope,
    bool IsConcurrent,
    int Revision);

/// <summary>
/// 直接读库取执行树快照 —— 这正是开发计划 §9.2 的「维度② ACT_* 行快照」。
/// <para>
/// 刻意用 Dapper 的<b>非泛型</b>查询 + 手工取值，不走属性映射：
/// 各驱动对 <c>TINYINT</c> / <c>bit</c> 的返回类型不一致，手工转数值最稳，
/// 也保证断言的是「库里真实的值」而不是映射层的猜测。
/// </para>
/// </summary>
internal static class ExecutionRowReader
{
    private const string SelectSql = """
        select ID_, PROC_INST_ID_, ROOT_PROC_INST_ID_, PARENT_ID_, ACT_ID_,
               IS_ACTIVE_, IS_SCOPE_, IS_CONCURRENT_, REV_
        from ACT_RU_EXECUTION
        order by ID_
        """;

    public static List<ExecutionRow> ReadAll(IDbSession session)
    {
        // ⚠️ 必须带上 session.Transaction：MySqlConnector 在连接已有活动事务时，
        // 要求命令显式指定该事务，否则抛
        // "The transaction associated with this command is not the connection's active transaction"。
        var rows = session.Connection.Query(SelectSql, transaction: session.Transaction);
        var result = new List<ExecutionRow>();

        foreach (IDictionary<string, object> row in rows)
        {
            result.Add(new ExecutionRow(
                Text(row, "ID_")!,
                Text(row, "PROC_INST_ID_"),
                Text(row, "ROOT_PROC_INST_ID_"),
                Text(row, "PARENT_ID_"),
                Text(row, "ACT_ID_"),
                Flag(row, "IS_ACTIVE_"),
                Flag(row, "IS_SCOPE_"),
                Flag(row, "IS_CONCURRENT_"),
                Number(row, "REV_")));
        }

        return result;
    }

    private static object? Raw(IDictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value : null;

    private static string? Text(IDictionary<string, object> row, string column) => Raw(row, column) as string;

    private static int Number(IDictionary<string, object> row, string column) =>
        Raw(row, column) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    private static bool Flag(IDictionary<string, object> row, string column) => Number(row, column) != 0;
}

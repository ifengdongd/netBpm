using Dapper;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence;

/// <summary>
/// 变量行与其 <c>longString</c> 字节行的删除序列，一处实现。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 顺序不能颠倒：<c>ACT_FK_VAR_BYTEARRAY</c> 是 RESTRICT 外键，必须先摘掉变量行的引用
/// （或先删变量行），再删 <c>ACT_GE_BYTEARRAY</c>。过去这段「捕获 id → 删变量 → 删字节」
/// 在任务、执行、变量三个仓储里各抄了一遍，其中两处的捕获 SQL 还各不相同 ——
/// 改一处漏两处，最后表现成「删实例后留下孤儿 bytearray」。
/// </para>
/// <para>
/// 只删 <c>DEPLOYMENT_ID_ is null</c> 的字节行：部署资源（BPMN XML / 流程图）也在这张表里，
/// 但它们归部署所有，绝不能因为删变量被顺带清掉。
/// </para>
/// </remarks>
internal static class VariableCleanup
{
    /// <summary>SQL Server 单语句参数上限 2100，留出余量。</summary>
    private const int MaxParametersPerStatement = 1000;

    private const string CaptureByTaskIdSql =
        "select BYTEARRAY_ID_ from ACT_RU_VARIABLE where TASK_ID_ = @taskId and BYTEARRAY_ID_ is not null";

    private const string DeleteByTaskIdSql =
        "delete from ACT_RU_VARIABLE where TASK_ID_ = @taskId";

    private const string CaptureTaskVariablesByProcessInstanceSql =
        "select V.BYTEARRAY_ID_ from ACT_RU_VARIABLE V "
        + "inner join ACT_RU_TASK T on T.ID_ = V.TASK_ID_ "
        + "where T.PROC_INST_ID_ = @processInstanceId and V.BYTEARRAY_ID_ is not null";

    private const string DeleteTaskVariablesByProcessInstanceSql =
        "delete from ACT_RU_VARIABLE where TASK_ID_ in (select ID_ from ACT_RU_TASK where PROC_INST_ID_ = @processInstanceId)";

    private const string DeleteByteArraysByIdsSql =
        "delete from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is null and ID_ in @ids";

    private const string DeleteByteArrayByIdSql =
        "delete from ACT_GE_BYTEARRAY where ID_ = @id";

    /// <summary>删除单条变量行及其字节行（历史行保留）。</summary>
    public static async Task DeleteOneAsync(
        IDbSession session,
        string variableId,
        string? byteArrayId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(variableId);

        await session.Connection.ExecuteAsync(new CommandDefinition(
            "delete from ACT_RU_VARIABLE where ID_ = @id",
            new { id = variableId },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(byteArrayId))
        {
            await session.Connection.ExecuteAsync(new CommandDefinition(
                DeleteByteArrayByIdSql,
                new { id = byteArrayId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    /// <summary>删除某任务的局部变量及其字节行。</summary>
    public static Task DeleteByTaskIdAsync(IDbSession session, string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        return DeleteAsync(session, CaptureByTaskIdSql, DeleteByTaskIdSql, new { taskId }, ct);
    }

    /// <summary>删除某流程实例下所有任务的局部变量及其字节行（实例级联删除用）。</summary>
    public static Task DeleteTaskVariablesByProcessInstanceAsync(
        IDbSession session,
        string processInstanceId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return DeleteAsync(
            session,
            CaptureTaskVariablesByProcessInstanceSql,
            DeleteTaskVariablesByProcessInstanceSql,
            new { processInstanceId },
            ct);
    }

    /// <summary>
    /// 「先捕获字节行 id → 删变量行 → 按 id 删字节行」的通用序列。
    /// </summary>
    /// <param name="session">会话（事务可为 null，命令外自动提交）。</param>
    /// <param name="captureByteArrayIdsSql">返回 <c>BYTEARRAY_ID_</c> 单列的查询。</param>
    /// <param name="deleteVariablesSql">删除变量行的语句。</param>
    /// <param name="args">两条语句共用的参数对象。</param>
    /// <param name="ct">取消标记。</param>
    public static async Task DeleteAsync(
        IDbSession session,
        string captureByteArrayIdsSql,
        string deleteVariablesSql,
        object args,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureByteArrayIdsSql);
        ArgumentException.ThrowIfNullOrWhiteSpace(deleteVariablesSql);

        var byteArrayIds = (await session.Connection.QueryAsync<string>(new CommandDefinition(
                captureByteArrayIdsSql,
                args,
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false)).ToList();

        await session.Connection.ExecuteAsync(new CommandDefinition(
            deleteVariablesSql,
            args,
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await DeleteByteArraysAsync(session, byteArrayIds, ct).ConfigureAwait(false);
    }

    /// <summary>按 id 删除字节行（只删非部署资源），必要时分片。</summary>
    public static async Task DeleteByteArraysAsync(
        IDbSession session,
        IReadOnlyList<string> byteArrayIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (byteArrayIds.Count == 0)
        {
            return;
        }

        foreach (var chunk in byteArrayIds.Chunk(MaxParametersPerStatement))
        {
            await session.Connection.ExecuteAsync(new CommandDefinition(
                DeleteByteArraysByIdsSql,
                new { ids = chunk },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }
    }
}

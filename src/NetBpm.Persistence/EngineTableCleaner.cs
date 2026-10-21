using Dapper;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence;

/// <summary>
/// 清空引擎表的数据（<b>保留表结构</b>）。供开发、测试、CI 使用。
/// <para>
/// ⚠️ <b>会删除全部流程数据与部署</b>，不要在生产库上执行。
/// 删除顺序按外键依赖排列（变量的外键没有级联删除，必须先于执行删除）。
/// </para>
/// </summary>
public static class EngineTableCleaner
{
    /// <summary>
    /// 按外键依赖排好的删除顺序。
    /// <para>
    /// ⚠️ 顺序不能随意：<c>ACT_RU_IDENTITYLINK.TASK_ID_</c> 指向 <c>ACT_RU_TASK.ID_</c>
    /// （<c>ACT_FK_TSKASS_TASK</c>），因此身份关系必须<b>先于</b>任务删除。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> OrderedTables { get; } =
    [
        // 历史表（DDL 里没有外键，顺序不受约束；放前面以先清理日志性数据）
        "ACT_HI_IDENTITYLINK",
        "ACT_HI_VARINST",
        "ACT_HI_ACTINST",
        "ACT_HI_TASKINST",
        "ACT_HI_PROCINST",

        "ACT_EVT_OUTBOX",        // 0012 新增；无外键
        "ACT_HI_MIGRATION",
        "ACT_RU_TIMER_JOB",      // 0006 新增；无外键，先于执行删除
        "ACT_RU_JOB",            // 0012 新增；无外键，先于执行删除
        "ACT_RU_VARIABLE",       // → ACT_RU_EXECUTION
        "ACT_RU_IDENTITYLINK",   // → ACT_RU_TASK / ACT_RU_EXECUTION / ACT_RE_PROCDEF
        "ACT_RU_TASK",           // → ACT_RU_EXECUTION / ACT_RE_PROCDEF
        "ACT_RU_EXECUTION",      // → 自身（PARENT_ID_）/ ACT_RE_PROCDEF
        "ACT_RE_PROCDEF",
        "ACT_GE_BYTEARRAY",      // → ACT_RE_DEPLOYMENT
        "ACT_RE_DEPLOYMENT",
    ];

    /// <summary>清空全部引擎表（不动 <c>ACT_GE_PROPERTY</c>，否则会丢掉 <c>next.dbid</c> 与结构版本）。</summary>
    /// <remarks>
    /// 逐表探测是否存在：<c>0006</c>（定时作业）是可选脚本，老库没执行过时清库不应直接报错。
    /// 探测失败（缺表 / 无权限）一律跳过 —— 本方法只用于开发与 CI，宁可少删也不要中断。
    /// </remarks>
    public static void Purge(IDbSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        foreach (var table in OrderedTables)
        {
            if (!TableExists(session, table))
            {
                continue;
            }

            session.Connection.Execute($"delete from {table}", transaction: session.Transaction);
        }
    }

    private static bool TableExists(IDbSession session, string table)
    {
        try
        {
            session.Connection.Execute(
                $"select 1 from {table} where 1 = 0",
                transaction: session.Transaction);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

using NetBpm.Engine;
using NetBpm.Engine.Command;

namespace NetBpm.Persistence;

/// <summary>
/// 数据库层可重试错误的识别（与方言绑定，故放在 Persistence，而不是引擎内核）。
/// <para>
/// 🔑 <b>为什么必须重试死锁</b>：并行分支 / 会签同时提交时，两个事务会在
/// <c>ACT_RU_EXECUTION</c>（网关 Join 的父作用域行）或 <c>ACT_RU_VARIABLE</c>
/// （<c>nrOfCompletedInstances</c>）上形成循环等待。数据库会主动挑一个牺牲品回滚，
/// 抛出的不是引擎自己的 <see cref="OptimisticLockingException"/> ——
/// 若只重试乐观锁，用户就会看到一次莫名其妙的失败（SQL Server 1205 已实测复现）。
/// </para>
/// <para>
/// 复用的是同一套重试机制：牺牲品事务已被数据库回滚，<c>ResetForRetry</c> 再换一个
/// 全新会话重跑整条命令，语义与乐观锁冲突完全一致（副作用仍是至少一次）。
/// </para>
/// </summary>
public static class TransientDatabaseErrors
{
    /// <summary>判定异常是否属于「换一次事务重跑就能成功」的瞬态错误。</summary>
    public static bool IsRetryable(Exception exception) =>
        exception is OptimisticLockingException
        || IsSqlServerTransient(exception)
        || IsMySqlTransient(exception);

    /// <summary>生成带数据库瞬态判定的重试策略（宿主 / 测试 harness 的统一入口）。</summary>
    public static CommandRetryPolicy CreateRetryPolicy(
        int maxAttempts = 3,
        TimeSpan? baseDelay = null) =>
        new()
        {
            MaxAttempts = maxAttempts,
            BaseDelay = baseDelay ?? TimeSpan.FromMilliseconds(10),
            ShouldRetry = IsRetryable,
        };

    /// <summary>SQL Server：1205 = 死锁牺牲品，1222 = 锁请求超时。</summary>
    private static bool IsSqlServerTransient(Exception exception) =>
        exception is Microsoft.Data.SqlClient.SqlException { Number: 1205 or 1222 };

    /// <summary>MySQL：1213 = 死锁，1205 = 锁等待超时。</summary>
    private static bool IsMySqlTransient(Exception exception) =>
        exception is MySqlConnector.MySqlException { Number: 1213 or 1205 };
}

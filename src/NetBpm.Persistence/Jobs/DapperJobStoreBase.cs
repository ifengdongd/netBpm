using Dapper;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence.Jobs;

/// <summary>
/// 作业存储（定时作业 / 异步作业）的公共 SQL 与租约操作。
/// </summary>
/// <remarks>
/// <para>
/// 两张表的<b>结构差异只在少数几列</b>（定时作业多 <c>TYPE_</c> / <c>BOUNDARY_EVENT_ID_</c>，
/// 异步作业多 <c>PHASE_</c>），而租约、失败退避、死信、级联删除的语句<b>逐字相同</b>。
/// 过去两个仓储各抄一份 300 多行的实现，改一处漏一处；这里把「与实体无关」的部分收上来，
/// 子类只保留 <c>Insert</c> / <c>FindById</c> / <c>FindDue</c> 这类涉及列与映射的方法。
/// </para>
/// <para>
/// 🔑 租约语义（<c>LOCK_OWNER_</c> / <c>LOCK_EXP_TIME_</c>）两处必须完全一致：
/// 「无人持有 / 租约过期 / 本来就是自己」才抢得到，最后一项保证嵌套抢占（外层轮询已抢、
/// 内层命令再抢一次）不会自我否决。
/// </para>
/// </remarks>
public abstract class DapperJobStoreBase
{
    protected DapperJobStoreBase(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        Sessions = sessions;
    }

    /// <summary>会话提供者（命令内复用命令事务）。</summary>
    protected IDbSessionProvider Sessions { get; }

    /// <summary>作业表名。</summary>
    protected abstract string Table { get; }

    /// <summary>死信重试时恢复的重试次数（两种作业的退避策略不同）。</summary>
    protected abstract int DefaultMaxRetries { get; }

    private string AcquireLeaseSql => $"""
        update {Table}
        set LOCK_OWNER_ = @owner, LOCK_EXP_TIME_ = @leaseUntil
        where ID_ = @id and DEAD_LETTER_ = 0
          and (LOCK_EXP_TIME_ is null or LOCK_EXP_TIME_ <= @now or LOCK_OWNER_ = @owner)
        """;

    private string RenewLeaseSql => $"""
        update {Table}
        set LOCK_EXP_TIME_ = @leaseUntil
        where ID_ = @id and LOCK_OWNER_ = @owner
        """;

    private string ReleaseLeaseSql => $"""
        update {Table}
        set LOCK_OWNER_ = null, LOCK_EXP_TIME_ = null
        where ID_ = @id and LOCK_OWNER_ = @owner
        """;

    private string UpdateDueDateSql => $"update {Table} set DUEDATE_ = @dueDate where ID_ = @id";

    private string UpdateFailureSql => $"""
        update {Table}
        set RETRIES_ = case when coalesce(RETRIES_, 3) > 0 then coalesce(RETRIES_, 3) - 1 else 0 end,
            DEAD_LETTER_ = case when coalesce(RETRIES_, 3) > 1 then 0 else 1 end,
            EXCEPTION_MSG_ = @error,
            LAST_FAILURE_ = @now,
            DUEDATE_ = @nextDue
        where ID_ = @id
        """;

    private string RetryDeadLetterSql => $"""
        update {Table}
        set RETRIES_ = @retries, DEAD_LETTER_ = 0, EXCEPTION_MSG_ = null, DUEDATE_ = @now
        where ID_ = @id and DEAD_LETTER_ = 1
        """;

    private string DeleteDeadLetterSql => $"delete from {Table} where ID_ = @id and DEAD_LETTER_ = 1";

    private string DeleteByIdSql => $"delete from {Table} where ID_ = @id";

    private string DeleteByExecutionSql => $"delete from {Table} where EXECUTION_ID_ = @executionId";

    private string DeleteByProcessInstanceSql =>
        $"delete from {Table} where PROCESS_INSTANCE_ID_ = @processInstanceId";

    private string CountSql => $"select count(*) from {Table}";

    /// <summary>租约式抢占：只有「无人持有 / 已过期 / 自己持有」才成功。</summary>
    protected async ValueTask<bool> TryAcquireCoreAsync(
        string jobId,
        string owner,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约时长必须为正。");
        }

        using var lease = Sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            AcquireLeaseSql,
            new { id = jobId, owner, now = nowUtc, leaseUntil = nowUtc + leaseDuration },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <summary>续租：只有持有者本人能续。</summary>
    protected async ValueTask<bool> RenewCoreAsync(
        string jobId,
        string owner,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        using var lease = Sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            RenewLeaseSql,
            new { id = jobId, owner, leaseUntil = nowUtc + leaseDuration },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <summary>释放租约（执行失败但不进死信时用）。</summary>
    protected async ValueTask<bool> ReleaseCoreAsync(string jobId, string owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = Sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            ReleaseLeaseSql,
            new { id = jobId, owner },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <summary>按 id 删除（条件删除，返回是否真的删掉一行）。</summary>
    protected async ValueTask<bool> TryDeleteCoreAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = Sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteByIdSql,
            new { id = jobId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <summary>删掉某执行下的全部作业。</summary>
    protected async ValueTask<int> DeleteByExecutionCoreAsync(string executionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        using var lease = Sessions.Acquire();

        return await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteByExecutionSql,
            new { executionId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>删掉某流程实例下的全部作业。</summary>
    protected async ValueTask<int> DeleteByProcessInstanceCoreAsync(string processInstanceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = Sessions.Acquire();

        return await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteByProcessInstanceSql,
            new { processInstanceId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>作业总数（诊断用）。</summary>
    protected async ValueTask<int> CountCoreAsync(CancellationToken ct)
    {
        using var lease = Sessions.Acquire();

        return await lease.Session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            CountSql,
            transaction: lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>改期（挂起顺延 / 重新排队）。</summary>
    protected async ValueTask RescheduleCoreAsync(string jobId, DateTime dueDate, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = Sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            UpdateDueDateSql,
            new { id = jobId, dueDate },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>记失败：递减重试、按退避改期，用尽即进死信（单语句原子完成）。</summary>
    protected async ValueTask MarkFailureCoreAsync(
        string jobId,
        string reason,
        DateTime nowUtc,
        DateTime nextDueUtc,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = Sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            UpdateFailureSql,
            new
            {
                id = jobId,
                // 数据库列长度有限：过长的异常消息截断，避免整条更新失败
                error = reason.Length > 1000 ? reason[..1000] : reason,
                now = nowUtc,
                nextDue = nextDueUtc,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>把死信作业恢复成待执行。</summary>
    protected async ValueTask<bool> RetryDeadLetterCoreAsync(string jobId, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = Sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            RetryDeadLetterSql,
            new { id = jobId, now = nowUtc, retries = DefaultMaxRetries },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <summary>删除死信作业（只有死信才能删）。</summary>
    protected async ValueTask<bool> DeleteDeadLetterCoreAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = Sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteDeadLetterSql,
            new { id = jobId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }
}

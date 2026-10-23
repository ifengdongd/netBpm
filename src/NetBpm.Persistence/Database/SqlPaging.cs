using Dapper;
using NetBpm.Engine;

namespace NetBpm.Persistence;

/// <summary>
/// 方言分页子句的唯一实现（执行树 / 历史 / 作业 / outbox / 管理端共用）。
/// </summary>
/// <remarks>
/// <para>
/// 之前每个仓储各写一份 <c>BuildPaging</c>，其中 <c>100000</c> 这个隐式上限被复制了 4 次 ——
/// 改一处就漏三处，是本项目最典型的「复制式方言分叉」。
/// </para>
/// <para>
/// ⚠️ <b>「不限条数」必须显式表达</b>：<see cref="Build"/> 的 <c>take</c> 为
/// <see langword="null"/> 直接抛错；确实只想跳过、不限制条数的调用方必须写明
/// <see cref="BuildUnlimited"/>。理由是一次静默的全表读取就能把进程打满，
/// 这种取舍不该藏在默认分支里。
/// </para>
/// </remarks>
internal static class SqlPaging
{
    /// <summary>
    /// <see cref="BuildUnlimited"/> 的隐式上限（防止退化成「全表进内存」）。
    /// </summary>
    /// <remarks>
    /// 名字里的 <c>Unlimited</c> 是相对「调用方没给 take」而言：SQL 里仍带一个足够大的
    /// <c>offset/fetch</c> 上限，避免一次误用把整表拉进内存。
    /// </remarks>
    public const int UnlimitedFetch = 100_000;

    /// <summary>
    /// 构造分页子句，并把 <c>skip</c> / <c>take</c> 参数写入 <paramref name="parameters"/>。
    /// </summary>
    /// <param name="databaseType">方言。</param>
    /// <param name="firstResult">跳过条数（负数按 0 处理）。</param>
    /// <param name="take">取多少条；<b>必须显式给出</b>（<see langword="null"/> 会抛错）。</param>
    /// <param name="parameters">Dapper 参数集合（会被就地修改）。</param>
    /// <exception cref="EngineException"><paramref name="take"/> 为 <see langword="null"/>。</exception>
    public static string Build(
        DatabaseType databaseType,
        int firstResult,
        int? take,
        DynamicParameters parameters)
    {
        if (take is null)
        {
            throw new EngineException(
                "分页必须给出 take（通常来自 MaxResults）。若确实只跳过、不限制条数，"
                + $"请显式调用 {nameof(SqlPaging)}.{nameof(BuildUnlimited)}（上限 {UnlimitedFetch} 行）。");
        }

        return BuildCore(databaseType, firstResult, take.Value, parameters);
    }

    /// <summary>
    /// 只跳过、不限制条数：SQL 里仍带 <see cref="UnlimitedFetch"/> 兜底上限。
    /// </summary>
    /// <remarks>
    /// 这是<b>显式的</b>取舍（不是默认分支）：调用方明知自己在读一个大集合，
    /// 且已接受「超过上限会被截断」。需要全量的场景请自己分页。
    /// </remarks>
    public static string BuildUnlimited(
        DatabaseType databaseType,
        int firstResult,
        DynamicParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var skip = Math.Max(0, firstResult);

        if (skip == 0)
        {
            return string.Empty;
        }

        parameters.Add("skip", skip);

        return databaseType == DatabaseType.MySql
            ? $" limit {UnlimitedFetch} offset @skip"
            : $" offset @skip rows fetch next {UnlimitedFetch} rows only";
    }

    private static string BuildCore(
        DatabaseType databaseType,
        int firstResult,
        int take,
        DynamicParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Add("skip", Math.Max(0, firstResult));
        parameters.Add("take", Math.Max(1, take));

        return databaseType == DatabaseType.MySql
            ? " limit @take offset @skip"
            : " offset @skip rows fetch next @take rows only";
    }
}

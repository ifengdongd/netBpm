using System.Globalization;

namespace NetBpm.Rest;

/// <summary>
/// 统一分页信封（<c>?envelope=true</c> 时返回）。
/// <para>
/// 默认仍返回裸数组 + <c>X-Total-Count</c>，避免破坏既有调用方；
/// 新接入方建议使用信封，一次拿到分页元数据，不必额外读响应头。
/// </para>
/// </summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">符合条件的总数（不受分页影响）。</param>
/// <param name="FirstResult">本页跳过的条数。</param>
/// <param name="MaxResults">本页最多返回的条数。</param>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int FirstResult,
    int MaxResults);

/// <summary>列表响应的统一出口：裸数组（默认）或分页信封（<c>envelope=true</c>）。</summary>
public static class PagedResults
{
    /// <summary>写入 <c>X-Total-Count</c> 并按开关返回数组或信封。</summary>
    public static IResult Create<T>(
        IReadOnlyList<T> items,
        int total,
        int firstResult,
        int maxResults,
        HttpResponse response,
        bool envelope)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(response);

        response.Headers["X-Total-Count"] = total.ToString(CultureInfo.InvariantCulture);

        return envelope
            ? Results.Ok(new PagedResponse<T>(items, total, firstResult, maxResults))
            : Results.Ok(items);
    }
}

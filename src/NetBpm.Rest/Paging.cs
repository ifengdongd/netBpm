namespace NetBpm.Rest;

/// <summary>
/// 列表端点统一的分页参数校验。
/// <para>
/// 此前各端点用 <c>Math.Clamp(maxResults, 1, 200)</c> 静默纠正非法值：
/// <c>maxResults=0</c> 会返回 1 条，负数 <c>firstResult</c> 被当成 0 —— 调用方拿到的是
/// 「看起来成功、其实参数错了」的结果。这里改为：
/// <list type="bullet">
///   <item><description><c>maxResults &lt;= 0</c>、<c>firstResult &lt; 0</c> → 400（语义错误）；</description></item>
///   <item><description><c>maxResults &gt; 200</c> → 钳到 200（契约上限，保留既有前端行为）。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class Paging
{
    /// <summary>默认页大小。</summary>
    internal const int DefaultPageSize = 20;

    /// <summary>单页上限。</summary>
    internal const int MaxPageSize = 200;

    /// <summary>校验分页参数；非法时给出可直接返回的 400 响应。</summary>
    internal static bool TryRead(
        int? firstResult,
        int? maxResults,
        out int first,
        out int size,
        out IResult? error)
    {
        first = firstResult ?? 0;
        size = maxResults ?? DefaultPageSize;
        error = null;

        if (first < 0)
        {
            error = Results.BadRequest(new ErrorResponse(
                "firstResult 不能为负数。",
                Code: EngineErrorCode.ValidationError));
            return false;
        }

        if (size <= 0)
        {
            error = Results.BadRequest(new ErrorResponse(
                "maxResults 必须大于 0（不传则默认 20）。",
                Code: EngineErrorCode.ValidationError));
            return false;
        }

        if (size > MaxPageSize)
        {
            size = MaxPageSize;
        }

        return true;
    }
}

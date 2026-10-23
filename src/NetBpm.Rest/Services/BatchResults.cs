namespace NetBpm.Rest.Services;

/// <summary>批量结果的统一拼装（逐项成功 / 失败，不因单项失败回滚已成功项）。</summary>
internal static class BatchResults
{
    /// <summary>按逐项结果汇总为响应。</summary>
    internal static BatchResultResponse From(List<BatchItemResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var succeeded = items.Count(item => item.Success);

        return new BatchResultResponse(items.Count, succeeded, items.Count - succeeded, items);
    }
}

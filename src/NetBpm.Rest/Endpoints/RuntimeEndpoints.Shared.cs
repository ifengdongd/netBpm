namespace NetBpm.Rest.Endpoints;

/// <summary>运行态端点的共享校验（批量条数 / 请求体形状）。</summary>
public static partial class RuntimeEndpoints
{
    /// <summary>单次批量操作的条数上限。</summary>
    private const int MaxBatchSize = 200;

    /// <summary>400（请求形状不合法）—— 错误码定义收敛在 <see cref="EndpointResults"/>，这里只是本组端点的简写。</summary>
    private static IResult InvalidRequest(string message) => EndpointResults.InvalidRequest(message);

    /// <summary>
    /// 批量 id 列表的统一校验：空 → 400，超过 <see cref="MaxBatchSize"/> → 400。
    /// <para>此前 7 个批量端点各自复制同一段判断，文案与上限容易分叉。</para>
    /// </summary>
    private static bool TryValidateBatch<T>(
        IReadOnlyList<T>? values,
        string field,
        out IReadOnlyList<T> items,
        out IResult? error)
    {
        error = null;

        if (values is not { Count: > 0 })
        {
            items = [];

            error = InvalidRequest($"{field} 不能为空。");
            return false;
        }

        if (values.Count > MaxBatchSize)
        {
            items = values;

            error = InvalidRequest($"单次批量操作最多 {MaxBatchSize} 条。");
            return false;
        }

        items = values;
        return true;
    }
}

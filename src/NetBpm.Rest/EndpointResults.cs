namespace NetBpm.Rest;

/// <summary>
/// 端点层的通用错误结果：把「状态码 + 错误码」的搭配固定在一处。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：同一语义在不同端点里曾经一会儿带 <c>code</c>、一会儿裸着返回
/// （<c>new ErrorResponse("...")</c> 的 <see cref="ErrorResponse.Code"/> 为 null）。
/// 调用方没法只按 <c>code</c> 分支，只能去匹配中文文案 —— 文案一改就断。
/// </para>
/// <para>
/// 这里只收「形状固定、状态码唯一」的两类：请求不合法（400 <c>validation_error</c>）与
/// 目标不存在（404 <c>not_found</c>，跨租户同样按不存在处理，不泄露存在性）。
/// 需要 <c>conflict</c> / <c>forbidden</c> / 字段明细的场合仍就地构造 —— 那些的条件与载荷各不相同，
/// 硬套一个助手只会让调用点更难读。
/// </para>
/// </remarks>
internal static class EndpointResults
{
    /// <summary>400：请求形状不合法（参数缺失 / 语义错误 / 请求体不符合格式）。</summary>
    internal static IResult InvalidRequest(string message) =>
        Results.BadRequest(new ErrorResponse(message, Code: EngineErrorCode.ValidationError));

    /// <summary>404：目标不存在或跨租户不可见。</summary>
    internal static IResult NotFound(string message) =>
        Results.NotFound(new ErrorResponse(message, Code: EngineErrorCode.NotFound));
}

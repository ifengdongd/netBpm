namespace NetBpm.ServerTaskWorker;

/// <summary>
/// 引擎返回的错误。
/// </summary>
/// <remarks>
/// <see cref="StatusCode"/> 是 HTTP 码，<see cref="Code"/> 是机器可读错误码（如 <c>conflict</c>）。
/// 两者都要留：只按 message 判断会在版本升级后失效。
/// </remarks>
public sealed class NetBpmApiException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="message">引擎给的可读消息。</param>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="code">机器可读错误码。</param>
    /// <param name="responseBody">原始响应体（排错用）。</param>
    public NetBpmApiException(string message, int statusCode, string? code, string? responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        ResponseBody = responseBody;
    }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>机器可读错误码。</summary>
    public string? Code { get; }

    /// <summary>原始响应体。</summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// 锁被别人拿走了 / 任务已完成。
    /// </summary>
    /// <remarks>
    /// 这属于「正常竞争」而不是故障：锁过期后被另一个 worker 领走，回执必然 409。
    /// 处理方式与网络错误不同 —— 对方的业务处理会覆盖同一个幂等入口，这里什么都不用补救。
    /// </remarks>
    public bool IsLockConflict => StatusCode == 409;
}

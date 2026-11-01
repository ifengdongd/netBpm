namespace NetBpm.Rest.Platform;

/// <summary>
/// 平台面<b>写动作</b>被服务端拒绝（终止实例 / 认领 / 转办等）。
/// </summary>
/// <remarks>
/// <para>
/// 端点不直接拼 <c>IResult</c>：写动作的拒绝原因有 403 / 404 / 409 / 422 四种，
/// 且每个原因都要带稳定错误码。让服务抛异常、端点统一转成
/// <see cref="PlatformProblem"/>，错误码与状态码的对应关系就只写一遍。
/// </para>
/// <para>
/// 与 <c>SubmitDecisionException</c> / <c>StartInstanceException</c> 形状一致是刻意的：
/// 前端按 <c>code</c> 分支，不按抛它的服务分支。
/// </para>
/// </remarks>
public sealed class PlatformActionRejectedException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="code">稳定错误码（见 <c>ErrorCodes</c>）。</param>
    /// <param name="title">短标题。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <param name="detail">具体说明。</param>
    public PlatformActionRejectedException(string code, string title, int status, string? detail = null)
        : base(title)
    {
        Code = code;
        Title = title;
        Status = status;
        Detail = detail;
    }

    /// <summary>稳定错误码。</summary>
    public string Code { get; }

    /// <summary>短标题。</summary>
    public string Title { get; }

    /// <summary>HTTP 状态码。</summary>
    public int Status { get; }

    /// <summary>具体说明。</summary>
    public string? Detail { get; }
}

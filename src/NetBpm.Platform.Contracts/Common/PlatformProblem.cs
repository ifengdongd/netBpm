using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 错误响应体，遵循 RFC 9457（Problem Details for HTTP APIs）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意<b>不用</b>国内常见的 <c>{ code, message, data }</c> 信封：
/// 一是与 HTTP 状态码语义重复，二是网关 / 反代 / 客户端库的通用错误处理全都要额外适配。
/// </para>
/// <para>
/// 扩展成员：<see cref="Code"/>（稳定错误码，见 <see cref="ErrorCodes"/>）、
/// <see cref="TraceId"/>（排查用）、<see cref="Errors"/>（字段级明细，仅 422 / 400 使用）。
/// </para>
/// </remarks>
/// <param name="Type">问题类型的稳定 URI，形如 <c>https://netbpm.dev/problems/decision.not_in_option_set</c>。</param>
/// <param name="Title">人类可读的短标题（面向开发者，不面向终端用户）。</param>
/// <param name="Status">与 HTTP 状态码一致。</param>
/// <param name="Detail">本次失败的具体说明，可以包含节点 id、选项 id 等定位信息。</param>
/// <param name="Instance">请求 URI。</param>
/// <param name="Code">稳定错误码。</param>
/// <param name="TraceId">服务端追踪 id，与响应头 <c>X-Request-Id</c> 相同。</param>
/// <param name="Errors">字段级错误明细。</param>
public sealed record PlatformProblem(
    string Type,
    string Title,
    int Status,
    string? Detail,
    string? Instance,
    string Code,
    string? TraceId,
    IReadOnlyList<PlatformFieldError>? Errors)
{
    /// <summary>构造一个不含字段明细的问题对象。</summary>
    /// <param name="code">稳定错误码。</param>
    /// <param name="title">短标题。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <param name="detail">具体说明。</param>
    public static PlatformProblem Create(string code, string title, int status, string? detail = null)
    {
        return new PlatformProblem(
            Type: "https://netbpm.dev/problems/" + code,
            Title: title,
            Status: status,
            Detail: detail,
            Instance: null,
            Code: code,
            TraceId: null,
            Errors: null);
    }
}

/// <summary>字段级错误明细（参数校验、选项校验用）。</summary>
/// <param name="Field">
/// 出错字段的对外路径，例如 <c>parameters.days</c> 或 <c>decision.optionId</c>；
/// 与请求体里的命名风格保持一致（camelCase）。
/// </param>
/// <param name="Code">该字段的错误码，取值来自 <see cref="ErrorCodes"/>。</param>
/// <param name="Message">给人看的原因。</param>
public sealed record PlatformFieldError(string Field, string Code, string Message);

/// <summary>
/// 游标分页结果。
/// </summary>
/// <remarks>
/// 用 cursor 而不是 offset / page：审批数据是持续写入的，offset 分页在并发写入下会漏行或重复行。
/// 游标是<b>不透明字符串</b>，调用方不得解析；服务端实现可以换编码而不破坏兼容。
/// </remarks>
/// <typeparam name="T">条目类型。</typeparam>
/// <param name="Items">本页条目。</param>
/// <param name="NextCursor">下一页游标；为 <see langword="null"/> 表示没有更多。</param>
/// <param name="HasMore">是否还有下一页（等价于 <c>NextCursor is not null</c>，为了让客户端少判空）。</param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore)
{
    /// <summary>构造一个空页。</summary>
    public static CursorPage<T> Empty { get; } = new([], null, false);
}

/// <summary>实例 / 待办列表的排序方向。</summary>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<SortDirection>))]
public enum SortDirection
{
    /// <summary>升序（按时间由早到晚）。</summary>
    Ascending = 0,

    /// <summary>降序（按时间由晚到早，列表默认）。</summary>
    Descending = 1,
}

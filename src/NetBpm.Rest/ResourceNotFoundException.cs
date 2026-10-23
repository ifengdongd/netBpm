using NetBpm.Engine;

namespace NetBpm.Rest;

/// <summary>
/// 带稳定错误码的「目标不存在」异常，由 <see cref="GlobalExceptionHandler"/> 原样映射成 404。
/// <para>
/// 存在的理由：端点此前用 <c>Results.NotFound(new ErrorResponse(..., Code: "..."))</c> 就地构造 404，
/// 业务代码（Service / 访问辅助）无法表达「这个 404 的 code 是什么」，只能返回 <see langword="null"/>
/// 让端点再翻译一次 —— 翻译点一多，code 就开始各写各的。
/// </para>
/// <para>
/// 继承 <see cref="EngineObjectNotFoundException"/>：批量端点靠 <c>catch (EngineException)</c> 做逐项隔离，
/// 404 必须在同一条 catch 里被收成「该项失败」而不是整体中断；同时全局异常处理器会先匹配本类型，
/// 从而保留自定义 <see cref="Code"/>。
/// </para>
/// </summary>
public sealed class ResourceNotFoundException : EngineObjectNotFoundException
{
    /// <summary>构造。</summary>
    /// <param name="message">对外消息。</param>
    /// <param name="code">稳定错误码（见 <see cref="EngineErrorCode"/>）。</param>
    public ResourceNotFoundException(string message, string code = EngineErrorCode.NotFound)
        : base(message) => Code = code;

    /// <summary>稳定错误码（见 <see cref="EngineErrorCode"/>）。</summary>
    public string Code { get; }
}

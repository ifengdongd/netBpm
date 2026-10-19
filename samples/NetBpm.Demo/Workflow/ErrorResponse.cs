namespace NetBpm.Demo.Workflow;

/// <summary>统一错误响应（两条流程与跨流程端点共用同一形状）。</summary>
/// <param name="Message">错误说明。</param>
/// <param name="Errors">明细（如表单字段校验错误）。</param>
public sealed record ErrorResponse(string Message, IReadOnlyList<string>? Errors = null);

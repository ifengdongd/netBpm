namespace NetBpm.Workflow;

/// <summary>
/// 调用方给的输入不合法（表单校验不通过、参数名冲突、实例未部署……）。
/// </summary>
/// <remarks>
/// <see cref="Errors"/> 是明细（如逐个表单字段的校验错误），<see cref="Exception.Message"/> 是概括。
/// HTTP 宿主通常把它映射成 <c>400</c> —— <c>Errors</c> 直接回给前端逐条显示。
/// </remarks>
public sealed class WorkflowValidationException : Exception
{
    /// <summary>构造异常。</summary>
    public WorkflowValidationException()
    {
    }

    /// <summary>构造异常。</summary>
    /// <param name="message">错误说明。</param>
    public WorkflowValidationException(string message)
        : base(message)
    {
    }

    /// <summary>构造异常。</summary>
    /// <param name="message">错误说明。</param>
    /// <param name="innerException">内部异常。</param>
    public WorkflowValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>构造异常（带字段级明细）。</summary>
    /// <param name="message">错误说明。</param>
    /// <param name="errors">明细，如 <c>表单字段 'days' 需要整数，实际收到 "两天"。</c></param>
    public WorkflowValidationException(string message, IReadOnlyList<string> errors)
        : base(message) => Errors = errors;

    /// <summary>字段级明细（可能为空）。</summary>
    public IReadOnlyList<string> Errors { get; } = [];
}

/// <summary>
/// 目标不存在（流程实例、任务）。
/// </summary>
/// <remarks>
/// 与 <see cref="WorkflowValidationException"/> 的区别是<b>语义</b>：这里是「这个东西没有」，
/// HTTP 宿主通常映射成 <c>404</c>；后者是「你给的不对」，映射成 <c>400</c>。
/// </remarks>
public sealed class WorkflowNotFoundException : Exception
{
    /// <summary>构造异常。</summary>
    public WorkflowNotFoundException()
    {
    }

    /// <summary>构造异常。</summary>
    /// <param name="message">错误说明。</param>
    public WorkflowNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>构造异常。</summary>
    /// <param name="message">错误说明。</param>
    /// <param name="innerException">内部异常。</param>
    public WorkflowNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

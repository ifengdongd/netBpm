namespace NetBpm.Bpmn.Validation;

/// <summary>一条校验错误。错误信息必须可定位到具体元素 id（P4 验收标准）。</summary>
/// <param name="Rule">规则名（对应 Activiti 的 <c>Validator</c> 名称）。</param>
/// <param name="ElementId">出错元素 id。</param>
/// <param name="ElementType">出错元素类型名。</param>
/// <param name="Message">错误说明。</param>
public sealed record ValidationError(string Rule, string? ElementId, string? ElementType, string Message)
{
    public override string ToString() => $"[{Rule}] {ElementType} '{ElementId}': {Message}";
}

/// <summary>校验失败异常。流程部署前抛出，对应 Activiti 的 <c>BpmnParseException</c>。</summary>
public sealed class ProcessValidationException : Exception
{
    public ProcessValidationException(IReadOnlyList<ValidationError> errors)
        : base(BuildMessage(errors))
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors;
    }

    /// <summary>全部校验错误。</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    private static string BuildMessage(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return errors.Count == 1
            ? errors[0].ToString()
            : $"流程校验未通过，共 {errors.Count} 处问题：{Environment.NewLine}"
              + string.Join(Environment.NewLine, errors.Select(error => "  " + error));
    }
}

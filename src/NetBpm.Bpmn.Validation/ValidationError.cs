namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 校验问题的严重级别。对应 Activiti <c>ValidationError.isWarning()</c> 的二分：
/// <para>
/// <b>Error</b> —— 部署期必须阻断：按当前引擎语义，这条流程<b>跑不起来</b>或<b>会静默偏离</b>
/// Activiti 行为（例如事件网关未实现、顺序流引用断裂）。<br/>
/// <b>Warning</b> —— 只提示、不阻断：Activiti 允许这样的模型，本引擎也能执行，
/// 但设计意图很可能写错了（例如孤立节点、缺失结束事件、无人办理的用户任务）。
/// </para>
/// <para>
/// 分级的判据是「<b>部署后是否还能正确执行</b>」，而不是「是否符合最佳实践」——
/// 后者一律落到 Warning，否则会把 Activiti 能跑的流程挡在门外（P10 对拍会直接暴露为部署行为不一致）。
/// </para>
/// </summary>
public enum ValidationSeverity
{
    /// <summary>提示级：照常部署，由调用方展示给设计者。</summary>
    Warning = 0,

    /// <summary>阻断级：部署失败，抛出 <see cref="ProcessValidationException"/>。</summary>
    Error = 1,
}

/// <summary>一条校验结果。错误信息必须可定位到具体元素 id（P4 验收标准）。</summary>
/// <param name="Rule">规则名（对应 Activiti 的 <c>Validator</c> 名称）。</param>
/// <param name="ElementId">出错元素 id。</param>
/// <param name="ElementType">出错元素类型名。</param>
/// <param name="Message">错误说明。</param>
/// <param name="Severity">
/// 严重级别，默认 <see cref="ValidationSeverity.Error"/> ——
/// 既有规则无需逐个改调用点，只有明确可放行的规则才显式标 Warning。
/// </param>
public sealed record ValidationError(
    string Rule,
    string? ElementId,
    string? ElementType,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error)
{
    /// <summary>是否为阻断级问题。</summary>
    public bool IsError => Severity == ValidationSeverity.Error;

    public override string ToString() => Severity == ValidationSeverity.Warning
        ? $"[{Rule}]（提示）{ElementType} '{ElementId}': {Message}"
        : $"[{Rule}] {ElementType} '{ElementId}': {Message}";
}

/// <summary>
/// 校验失败异常。流程部署前抛出，对应 Activiti 的 <c>BpmnParseException</c>。
/// <para>
/// ⚠️ 只承载<b>阻断级</b>问题（<see cref="ValidationSeverity.Error"/>）；
/// 提示级问题随 <see cref="Bpmn.Validation.IProcessValidator.ValidateAndThrow"/> 的返回值交回调用方，
/// 不抛异常（Activiti 也只在 <c>isWarning()</c> 为 false 时中断解析）。
/// </para>
/// </summary>
public sealed class ProcessValidationException : Exception
{
    public ProcessValidationException(IReadOnlyList<ValidationError> errors)
        : base(BuildMessage(errors))
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors;
    }

    /// <summary>全部阻断级校验错误。</summary>
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

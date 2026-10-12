using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 一条流程校验规则。对应 Activiti 的 <c>Validator</c>（27 个 → 本方案 8 个）。
/// </summary>
public interface IProcessValidationRule
{
    /// <summary>规则名（用于错误定位）。</summary>
    string Name { get; }

    IEnumerable<ValidationError> Validate(Process process);
}

/// <summary>流程校验器。对应 Activiti 的 <c>ProcessValidator</c>。</summary>
public interface IProcessValidator
{
    /// <summary>返回全部错误；为空表示校验通过。</summary>
    IReadOnlyList<ValidationError> Validate(Process process);

    /// <summary>校验失败时抛出 <see cref="ProcessValidationException"/>。</summary>
    void ValidateAndThrow(Process process);
}

using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 一条流程校验规则。对应 Activiti 的 <c>Validator</c>（27 个 → 本方案 8 个）。
/// <para>
/// 规则可以产出两种级别（<see cref="ValidationSeverity"/>）：阻断级会让部署失败，
/// 提示级只做展示 —— 分级由规则自己决定，校验器只负责分流。
/// </para>
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
    /// <summary>返回全部校验结果（含提示级）；为空表示完全通过。</summary>
    IReadOnlyList<ValidationError> Validate(Process process);

    /// <summary>
    /// 校验并在存在<b>阻断级</b>问题时抛出 <see cref="ProcessValidationException"/>。
    /// </summary>
    /// <returns>不阻断部署的提示级结果（可能为空），供部署接口回传给设计器。</returns>
    IReadOnlyList<ValidationError> ValidateAndThrow(Process process);
}

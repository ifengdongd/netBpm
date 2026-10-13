using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 默认流程校验器：按顺序跑完全部规则并汇总错误（不提前返回，便于一次性修完）。
/// <para>前端（§6.2 第 4 项）也做即时校验，但那只是体验 —— <b>后端这里的复校才是可信边界</b>。</para>
/// </summary>
public sealed class ProcessValidator : IProcessValidator
{
    private readonly IReadOnlyList<IProcessValidationRule> _rules;

    /// <summary>使用默认的 8 条规则构造。</summary>
    public ProcessValidator()
        : this(
        [
            new NodeHasOutgoingFlowRule(),
            new ExclusiveGatewayHasSelectableFlowRule(),
            new NoOrphanNodeRule(),
            new UniqueElementIdRule(),
            new ParallelGatewayFlowCountRule(),
            new InclusiveGatewayRule(),
            new EventGatewayUnsupportedRule(),
            new StartAndEndEventRule(),
            new SequenceFlowReferenceRule(),
            new RequiredAttributesRule(),
            new UnsupportedCapabilityRule(),
        ])
    {
    }

    /// <summary>使用自定义规则集构造（供测试与扩展）。</summary>
    public ProcessValidator(IReadOnlyList<IProcessValidationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>已注册的规则名。</summary>
    public IReadOnlyList<string> RuleNames => _rules.Select(rule => rule.Name).ToList();

    /// <inheritdoc />
    public IReadOnlyList<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var errors = new List<ValidationError>();

        foreach (var rule in _rules)
        {
            errors.AddRange(rule.Validate(process));
        }

        return errors;
    }

    /// <inheritdoc />
    public void ValidateAndThrow(Process process)
    {
        var errors = Validate(process);

        if (errors.Count > 0)
        {
            throw new ProcessValidationException(errors);
        }
    }
}

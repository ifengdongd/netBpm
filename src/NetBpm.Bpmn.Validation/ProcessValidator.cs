using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 默认流程校验器：按顺序跑完全部规则并汇总结果（不提前返回，便于一次性修完）。
/// <para>前端（§6.2 第 4 项）也做即时校验，但那只是体验 —— <b>后端这里的复校才是可信边界</b>。</para>
/// <para>
/// 结果分两级：<see cref="ValidationSeverity.Error"/> 阻断部署，
/// <see cref="ValidationSeverity.Warning"/> 只提示。分级的理由见 <see cref="ValidationSeverity"/>。
/// </para>
/// </summary>
public sealed class ProcessValidator : IProcessValidator
{
    private readonly IReadOnlyList<IProcessValidationRule> _rules;

    /// <summary>使用默认的规则集构造（含脚本任务与多实例规则，共 15 条）。</summary>
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
            new ListenerConfigurationRule(),
            new UnsupportedCapabilityRule(),
            new ScriptTaskRule(),
            new MultiInstanceRule(),
            new UserTaskAssignmentRule(),
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

        var results = new List<ValidationError>();

        foreach (var rule in _rules)
        {
            results.AddRange(rule.Validate(process));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<ValidationError> ValidateAndThrow(Process process)
    {
        var results = Validate(process);

        // 只有 Error 阻断：Warning 是「Activiti 能跑、我们也能跑」的模型问题，
        // 一律放行并回传，避免把子集差异变成额外的部署门槛
        var errors = results.Where(result => result.IsError).ToList();

        if (errors.Count > 0)
        {
            throw new ProcessValidationException(errors);
        }

        return results.Count == 0 ? [] : [.. results];
    }
}

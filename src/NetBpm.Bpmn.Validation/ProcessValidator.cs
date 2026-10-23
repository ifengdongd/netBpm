using NetBpm.Bpmn.Model;
using NetBpm.El;

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

    /// <summary>使用默认的规则集构造（含脚本任务、多实例、异步作业、边界定时器与表达式预解析规则，共 23 条）。</summary>
    /// <remarks>表达式规则使用自己的兜底解析器；宿主应优先用 <see cref="ProcessValidator(IExpressionManager)"/> 共享运行期实例。</remarks>
    public ProcessValidator()
        : this(new ExpressionManager())
    {
    }

    /// <summary>
    /// 使用默认规则集构造，并让表达式校验共用运行期的解析器实例。
    /// </summary>
    /// <param name="expressions">运行期的 <see cref="IExpressionManager"/>（DI 单例）。</param>
    public ProcessValidator(IExpressionManager expressions)
        : this(
        [
            new ProcessIdRule(),
            new NodeHasOutgoingFlowRule(),
            new ExclusiveGatewayHasSelectableFlowRule(),
            new NoOrphanNodeRule(),
            new UniqueElementIdRule(),
            new ParallelGatewayFlowCountRule(),
            new InclusiveGatewayRule(),
            new EventGatewayUnsupportedRule(),
            new StartAndEndEventRule(),
            new SequenceFlowReferenceRule(),
            new DefaultFlowRule(),
            new UnsupportedEventDefinitionRule(),
            new BoundaryEventConfigurationRule(),
            new TerminateEndEventRule(),
            new ExpressionParseRule(expressions),
            new RequiredAttributesRule(),
            new ListenerConfigurationRule(),
            new UnsupportedCapabilityRule(),
            new UnknownActivitiAttributeRule(),
            new ScriptTaskRule(),
            new MultiInstanceRule(),
            new AsyncJobRule(),
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

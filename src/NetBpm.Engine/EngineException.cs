namespace NetBpm.Engine;

/// <summary>引擎异常基类。对应 Activiti 的 <c>ActivitiException</c>。</summary>
public class EngineException : Exception
{
    public EngineException(string message)
        : base(message)
    {
    }

    public EngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 目标对象不存在。对应 Activiti 的 <c>ActivitiObjectNotFoundException</c>。
/// <para>
/// 单独一个类型是为了让 REST 层能稳定地把「找不到」映射成 404，
/// 而不是靠匹配异常消息里的中文（消息会改，类型不会）。
/// </para>
/// </summary>
public class EngineObjectNotFoundException : EngineException
{
    public EngineObjectNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 状态冲突：对象存在，但当前状态不允许该操作（已认领、仍有运行实例、任务挂起等）。
/// <para>
/// REST 层映射为 <b>409 Conflict</b>——调用方应刷新状态后重试或改走其它操作，
/// 而不是把它当成参数错误（400）。
/// </para>
/// </summary>
public class EngineConflictException : EngineException
{
    public EngineConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 乐观锁冲突。对应 Activiti 的 <c>ActivitiOptimisticLockingException</c>。
/// <para>
/// ⚠️ 不要吞掉它重试：Activiti 在 <c>REV_</c> 不匹配时直接抛出，
/// 「重试」是调用方（业务层）的决定。逐行照搬的判据见 §7.3 的 <c>REV_</c> 列。
/// </para>
/// </summary>
public sealed class OptimisticLockingException : EngineException
{
    public OptimisticLockingException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 明确「尚未实现」的异常。
/// <para>
/// 用于子集/阶段边界：宁可响亮失败，也不静默降级成"看着能跑、语义已错"（§5.7 / R2）。
/// </para>
/// </summary>
public sealed class EngineNotImplementedException : EngineException
{
    public EngineNotImplementedException(string capability, string planPhase, string reason)
        : base($"【{planPhase} 未实现】{capability}：{reason}")
    {
        Capability = capability;
        PlanPhase = planPhase;
        Reason = reason;
    }

    /// <summary>缺失的能力名称。</summary>
    public string Capability { get; }

    /// <summary>开发计划中对应的阶段代号。</summary>
    public string PlanPhase { get; }

    /// <summary>缺失原因 / 补充说明。</summary>
    public string Reason { get; }
}

namespace NetBpm.Engine.Execution;

/// <summary>执行对象的辅助方法。</summary>
public static class ExecutionExtensions
{
    /// <summary>
    /// 把面向行为类的只读视图还原为可写的执行实体。
    /// 行为类签名保持与开发计划 §5.4 一致的 <see cref="IDelegateExecution"/>，
    /// 需要修改标志位时通过本方法取回实体。
    /// </summary>
    public static IExecutionEntity AsEntity(this IDelegateExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return execution as IExecutionEntity
            ?? throw new EngineException(
                $"执行 '{execution.Id}' 不是可写的 IExecutionEntity，无法推进流程。");
    }
}

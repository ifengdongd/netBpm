using NetBpm.Bpmn.Model;
using NetBpm.Engine.Services;

namespace NetBpm.UnitTests;

/// <summary>
/// 测试引擎的流程元素解析器：把 <see cref="TestEngine.StartProcessAsync"/> 进来的流程对象
/// 按 definitionId 登记下来，供跳转等需要「目标节点 id → 元素对象」的命令在内存引擎下工作。
/// </summary>
public sealed class TestDefinitionResolver : IProcessDefinitionResolver
{
    private readonly Dictionary<string, Process> _processes = new(StringComparer.Ordinal);

    /// <summary>登记（或覆盖）一个流程模型。</summary>
    public void Register(string processDefinitionId, Process process) =>
        _processes[processDefinitionId] = process;

    /// <inheritdoc />
    public ValueTask<IFlowElement?> ResolveFlowElementAsync(
        string? processDefinitionId,
        string? activityId,
        CancellationToken ct = default)
    {
        if (processDefinitionId is null || activityId is null)
        {
            return ValueTask.FromResult<IFlowElement?>(null);
        }

        return ValueTask.FromResult<IFlowElement?>(_processes.TryGetValue(processDefinitionId, out var process)
            ? process.GetFlowElement(activityId)
            : null);
    }

    /// <inheritdoc />
    public ValueTask<Process?> ResolveProcessModelAsync(
        string? processDefinitionId,
        CancellationToken ct = default)
    {
        if (processDefinitionId is null)
        {
            return ValueTask.FromResult<Process?>(null);
        }

        return ValueTask.FromResult(_processes.TryGetValue(processDefinitionId, out var process) ? process : null);
    }
}

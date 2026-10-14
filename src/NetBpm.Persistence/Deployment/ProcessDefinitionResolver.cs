using System.Collections.Concurrent;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Services;

namespace NetBpm.Persistence.Deployment;

/// <summary>
/// <see cref="IProcessDefinitionResolver"/> 的持久化实现：按流程定义 id 加载 BPMN 模型并缓存。
/// <para>
/// 缓存是安全的：流程定义一旦部署就<b>不可变</b>（重新部署会生成新的
/// <c>{key}:{version}:{id}</c>），因此同一个 id 的模型不会变化。
/// </para>
/// <para>
/// ⚠️ 缓存上限：条目数等于部署过的流程定义数。若长期运行且频繁重部署，
/// 应在 P11 换成带容量的 LRU —— 这里刻意不提前引入复杂度。
/// </para>
/// </summary>
public sealed class ProcessDefinitionResolver : IProcessDefinitionResolver
{
    private readonly IProcessDefinitionStore _definitionStore;
    private readonly ConcurrentDictionary<string, Process?> _processCache = new(StringComparer.Ordinal);

    public ProcessDefinitionResolver(IProcessDefinitionStore definitionStore)
    {
        ArgumentNullException.ThrowIfNull(definitionStore);
        _definitionStore = definitionStore;
    }

    /// <inheritdoc />
    public async ValueTask<IFlowElement?> ResolveFlowElementAsync(
        string? processDefinitionId,
        string? activityId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(processDefinitionId) || string.IsNullOrEmpty(activityId))
        {
            return null;
        }

        var process = await GetProcessAsync(processDefinitionId, ct).ConfigureAwait(false);

        return process?.GetFlowElement(activityId);
    }

    /// <inheritdoc />
    public async ValueTask<Process?> ResolveProcessModelAsync(
        string? processDefinitionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(processDefinitionId))
        {
            return null;
        }

        return await GetProcessAsync(processDefinitionId, ct).ConfigureAwait(false);
    }

    private async ValueTask<Process?> GetProcessAsync(string processDefinitionId, CancellationToken ct)
    {
        if (_processCache.TryGetValue(processDefinitionId, out var cached))
        {
            return cached;
        }

        var model = await _definitionStore.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false);
        var process = model?.GetMainProcess();

        _processCache[processDefinitionId] = process;
        return process;
    }
}

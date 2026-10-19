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
/// 缓存按容量做 LRU 淘汰：长期运行 + 频繁重部署时，条目数不再随部署次数无界增长。
/// </para>
/// </summary>
public sealed class ProcessDefinitionResolver : IProcessDefinitionResolver
{
    private const int DefaultCacheCapacity = 256;

    private readonly IProcessDefinitionStore _definitionStore;
    private readonly BoundedCache<string, Process?> _processCache;

    public ProcessDefinitionResolver(
        IProcessDefinitionStore definitionStore,
        int cacheCapacity = DefaultCacheCapacity)
    {
        ArgumentNullException.ThrowIfNull(definitionStore);

        _definitionStore = definitionStore;
        _processCache = new BoundedCache<string, Process?>(cacheCapacity, StringComparer.Ordinal);
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
        if (_processCache.TryGet(processDefinitionId, out var cached))
        {
            return cached;
        }

        var model = await _definitionStore.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false);
        var process = model?.GetMainProcess();

        _processCache.Set(processDefinitionId, process);
        return process;
    }
}

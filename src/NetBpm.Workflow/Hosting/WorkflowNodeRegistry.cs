using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 「环节名 → 环节 id」对照表 —— 从已部署的流程定义里现取，不在代码里抄节点名单。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：业务侧的审批历史 / 当前待办（如 <c>deal_history</c> / <c>instance_current_task</c>）
/// 通常只记<b>环节名</b>，而跨系统对得上的是<b>环节 id</b>（引擎侧的
/// <c>ACT_HI_ACTINST.ACT_ID_</c>、<c>taskDefinitionKey</c>）。两者之间要有张对照表 ——
/// 真实系统里那是「流程模型 / 环节配置」表。
/// </para>
/// <para>
/// 本表直接从<b>已部署的流程定义</b>里构建：改了图重启就生效，不会与代码里的名单漂移。
/// 构建入口见 <see cref="WorkflowRuntimeContext"/>（部署完成后填充）。
/// </para>
/// </remarks>
public sealed class WorkflowNodeRegistry
{
    /// <summary>还没装载的注册表：查什么都是 <see langword="null"/>。</summary>
    public static WorkflowNodeRegistry Empty { get; } = new(null);

    private readonly Dictionary<string, string?> _idsByName;

    private WorkflowNodeRegistry(Dictionary<string, string?>? idsByName) => _idsByName = idsByName ?? [];

    /// <summary>是否已从流程定义装载。</summary>
    public bool IsLoaded => _idsByName.Count > 0;

    /// <summary>节点总数（诊断用）。</summary>
    public int Count => _idsByName.Count;

    /// <summary>从流程定义里读出所有节点（含网关 / 事件）的名字与 id。</summary>
    /// <param name="engine">引擎门面。</param>
    /// <param name="processDefinitionId">已部署的流程定义 id。</param>
    /// <param name="logger">日志。</param>
    /// <param name="ct">取消标记。</param>
    public static async ValueTask<WorkflowNodeRegistry> BuildAsync(
        IProcessEngine engine,
        string processDefinitionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        var model = await engine.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false);
        var nodes = model?.GetMainProcess()?.FindFlowElementsOfType<FlowNode>() ?? [];

        var idsByName = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var id = node.Id;

            // 没有 id 的节点既不会被引用、也没法回填，直接跳过
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            // 没名字的节点用 id 当键，这样按 id 也能查到自己
            var key = id;
            var name = node.Name;

            if (!string.IsNullOrWhiteSpace(name))
            {
                key = name;
            }

            // 同名环节：说不清是哪一步，留成 null 让调用方退化为只展示名字
            if (!idsByName.TryAdd(key, id))
            {
                idsByName[key] = null;
            }
        }

        logger.LogInformation(
            "环节名 → id 映射已装载（{Count} 条）：{Map}",
            idsByName.Count,
            string.Join(" / ", idsByName.Select(pair => $"{pair.Key}={pair.Value ?? "(重名)"}")));

        return new WorkflowNodeRegistry(idsByName);
    }

    /// <summary>
    /// 按环节名找 id；查不到（改名了 / 不是本流程的环节 / 同名）返回 <see langword="null"/>。
    /// </summary>
    public string? FindId(string? activityName) =>
        !string.IsNullOrWhiteSpace(activityName) && _idsByName.TryGetValue(activityName, out var id)
            ? id
            : null;
}

using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Runtime;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 「环节名 → 环节 id」小字典。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：业务表（<c>deal_history</c> / <c>instance_current_task</c>）只记<b>环节名</b>，
/// 而跨系统对得上的是<b>环节 id</b>（引擎侧的 <c>ACT_HI_ACTINST.ACT_ID_</c>、<c>taskDefinitionKey</c>）。
/// 两者之间要有个对照表 —— 真实系统里那是「流程模型 / 环节配置」表。
/// </para>
/// <para>
/// Demo 直接从<b>已部署的流程定义</b>里现取，而不是在代码里再抄一份 12 个节点的名单：
/// 改了图重启就生效，不用回来改代码，也就不会漂移。
/// </para>
/// </remarks>
public sealed class LeaveNodeRegistry
{
    /// <summary>还没装载的注册表：查什么都是 <see langword="null"/>。</summary>
    public static LeaveNodeRegistry Empty { get; } = new(null);

    private readonly Dictionary<string, string?> _idsByName;

    private LeaveNodeRegistry(Dictionary<string, string?>? idsByName) => _idsByName = idsByName ?? [];

    /// <summary>是否已从流程定义装载。</summary>
    public bool IsLoaded => _idsByName.Count > 0;

    /// <summary>节点总数（诊断用）。</summary>
    public int Count => _idsByName.Count;

    /// <summary>从流程定义里读出所有节点（含网关 / 事件）的名字与 id。</summary>
    /// <param name="engine">引擎门面。</param>
    /// <param name="processDefinitionId">已部署的流程定义 id。</param>
    /// <param name="logger">日志。</param>
    /// <param name="ct">取消标记。</param>
    public static async ValueTask<LeaveNodeRegistry> BuildAsync(
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

        return new LeaveNodeRegistry(idsByName);
    }

    /// <summary>
    /// 按环节名找 id；查不到（改名了 / 不是本流程的环节 / 同名）返回 <see langword="null"/>。
    /// </summary>
    public string? FindId(string? activityName) =>
        !string.IsNullOrWhiteSpace(activityName) && _idsByName.TryGetValue(activityName, out var id)
            ? id
            : null;
}

/// <summary>
/// 部署之后才有值的运行时上下文。
/// </summary>
/// <remarks>
/// 流程定义是在 <c>app.Build()</c> <b>之后</b>才部署的（见 <see cref="LeaveProcessBootstrapper"/>），
/// 所以「当前流程定义 id」「环节名 → id 映射」这类值拿不到构造注入 ——
/// 用一个单例装起来，部署完填进去，之后只读。
/// </remarks>
public sealed class LeaveRuntime
{
    private bool _initialized;

    /// <summary>最近一次部署出来的流程定义 id。</summary>
    public string ProcessDefinitionId { get; private set; } = string.Empty;

    /// <summary>环节名 → 环节 id 映射（未装载时为空表）。</summary>
    public LeaveNodeRegistry Nodes { get; private set; } = LeaveNodeRegistry.Empty;

    /// <summary>部署完调用一次，把上面两项填上。</summary>
    public async ValueTask InitializeAsync(
        IProcessEngine engine,
        string processDefinitionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        ProcessDefinitionId = processDefinitionId;
        Nodes = await LeaveNodeRegistry.BuildAsync(engine, processDefinitionId, logger, ct).ConfigureAwait(false);
        _initialized = true;
    }

    /// <summary>确认已装载；没装载说明启动流程被绕过了，早点报错比默默写错数据好。</summary>
    public void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "LeaveRuntime 尚未初始化：流程定义部署后必须调用 LeaveRuntime.InitializeAsync（见 Program.cs）。");
        }
    }
}

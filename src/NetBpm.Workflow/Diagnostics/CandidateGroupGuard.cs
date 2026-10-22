using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Identity;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 部署后自检（仅告警）：流程里声明了候选组 / 候选人，但容器里的
/// <see cref="IUserGroupManager"/> 仍是 <see cref="EmptyUserGroupManager"/> 时给出明确提示。
/// </summary>
/// <remarks>
/// <para>
/// 这是集成期最常见的「静默故障」：<c>AddNetBpm</c> 默认注册空适配器，忘了替换成业务实现时，
/// 候选组展开出 0 个人 —— 发起成功、流程停在第一个 <c>userTask</c>、任何人打开待办都看不到，
/// <b>日志里一个错都没有</b>。等发现时流程实例已经卡了一堆。
/// </para>
/// <para>
/// 为什么只告警不抛：确实存在「候选关系全部由外部系统在发起前算好、只写 assignee」的合法用法，
/// 那种流程即使声明了候选组也不会用到适配器，硬失败会误伤。
/// </para>
/// </remarks>
public static class CandidateGroupGuard
{
    /// <summary>检查刚部署的流程定义，必要时输出一条告警。</summary>
    /// <param name="services">服务提供者（取 <see cref="IUserGroupManager"/>）。</param>
    /// <param name="engine">引擎门面。</param>
    /// <param name="processDefinitionId">刚部署的流程定义 id。</param>
    /// <param name="logger">日志。</param>
    /// <param name="ct">取消标记。</param>
    public static async ValueTask WarnIfUnresolvableAsync(
        IServiceProvider services,
        IProcessEngine engine,
        string processDefinitionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        // 业务实现多半是有状态的单例，用 GetService 而不是 GetRequiredService：
        // 宿主手工拼服务图时可能压根没注册，这里不该替它抛异常（真正的缺失会在执行期报）。
        if (services.GetService<IUserGroupManager>() is not EmptyUserGroupManager)
        {
            return;
        }

        var model = await engine.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false);
        var process = model?.GetMainProcess();

        if (process is null)
        {
            return;
        }

        var declaring = process
            .FindFlowElementsOfType<UserTask>()
            .Where(task => task.CandidateGroups.Count > 0 || task.CandidateUsers.Count > 0)
            .ToArray();

        if (declaring.Length == 0)
        {
            return;
        }

        var groups = declaring
            .SelectMany(task => task.CandidateGroups)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();

        logger.LogWarning(
            "流程定义 {ProcessDefinitionId} 有 {TaskCount} 个人工任务声明了候选组/候选人（{Tasks}；{Groups}），" +
            "但 IUserGroupManager 仍是 EmptyUserGroupManager —— 候选组会展开成 0 个用户，待办将静默消失。" +
            "请用 AddNetBpmUserGroupManager<T>() 注册业务适配器；若候选关系确由外部系统预先算好，可忽略本条告警。",
            processDefinitionId,
            declaring.Length,
            string.Join("、", declaring.Select(task => task.Id ?? "?")),
            groups.Length == 0 ? "仅 candidateUsers" : "候选组：" + string.Join("/", groups));
    }
}

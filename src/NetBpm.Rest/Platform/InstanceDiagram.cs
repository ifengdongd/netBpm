using Microsoft.Extensions.Logging;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 实例的<b>流转状态图</b>（<c>GET /instances/{id}/diagram.svg</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>GET /processes/{key}/diagram.svg</c> 的区别只有一处：那张是<b>流程定义</b>的样子
/// （谁都能看，与单据无关），这张是<b>这一张单据</b>走到哪了——走过的节点与连线标红、
/// 当前停留的环节加粗。同一个流程的两张单据会给出两张不同的图，这正是它存在的理由。
/// </para>
/// <para>
/// 可见性走实例读模型，不另开一套判定：能看见实例才能看见它的图。
/// 图里带环节名（表单标题 / 部门名），越权拿到一张图等于拿到一张单据的骨架。
/// </para>
/// </remarks>
public interface IInstanceDiagram
{
    /// <summary>
    /// 渲染实例的流转状态图（SVG）。
    /// </summary>
    /// <remarks>
    /// 返回 <see langword="null"/> 表示「拿不到这张图」：实例对调用方不可见，或定义原文缺失。
    /// 两种情况在端点上都收敛为 404——与 <c>/processes/{key}/diagram.svg</c> 同口径
    /// （那里定义原文缺失也是 404，而不是 500）。
    /// </remarks>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<string?> RenderAsync(
        string instanceId,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class InstanceDiagram : IInstanceDiagram
{
    private readonly IInstanceCatalog _catalog;
    private readonly IProcessEngine _engine;
    private readonly ILogger<InstanceDiagram> _logger;

    /// <summary>构造。</summary>
    /// <param name="catalog">实例读模型（存在性、可见性、定义 id 的唯一来源）。</param>
    /// <param name="engine">引擎门面（历史活动 = 着色数据源；定义原文 = 图形数据源）。</param>
    /// <param name="logger">日志。</param>
    public InstanceDiagram(
        IInstanceCatalog catalog,
        IProcessEngine engine,
        ILogger<InstanceDiagram> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        _catalog = catalog;
        _engine = engine;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<string?> RenderAsync(
        string instanceId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(context);

        var view = await _catalog.FindAsync(instanceId, context, ct).ConfigureAwait(false);

        if (view is null)
        {
            return null;
        }

        var xml = await ReadXmlAsync(view.ProcessDefinitionId, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(xml))
        {
            _logger.LogWarning(
                "实例 {InstanceId} 的流程图渲染不出来：定义 {ProcessDefinitionId} 没有 BPMN 原文。",
                instanceId,
                view.ProcessDefinitionId);

            return null;
        }

        // 着色数据源取历史活动而不是待办：待办只有用户任务，网关 / 服务任务 / 正在流转的
        // 中间事件都不在里面，只按待办着色会让图在非人工节点处「断线」。
        var activities = await _engine.FindHistoricActivitiesAsync(instanceId, ct).ConfigureAwait(false);

        var visited = activities
            .Select(activity => activity.ActivityId)
            .Where(activityId => !string.IsNullOrEmpty(activityId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var active = activities
            .Where(activity => activity.IsActive)
            .Select(activity => activity.ActivityId)
            .Where(activityId => !string.IsNullOrEmpty(activityId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (activities.Count == 0)
        {
            // 没开历史（NetBpm:EnableHistory=false）时引擎不记 <c>ACT_HI_ACTINST</c>，
            // 但「现在停在哪」仍然回答得了：实例视图的当前环节来自运行态待办。
            // 走这条路降级成「只标当前」而不是返回 404——图还是这张单的图，
            // 只是少了走过的痕迹；报「不存在」会让人以为单据没了。
            active = [.. view.CurrentActivityIds];
        }

        return BpmnDiagramSvgRenderer.RenderSvg(xml, new DiagramHighlight(visited, active));
    }

    /// <summary>读定义原文；读不到只记日志（渲染是读路径，不该因为一次数据库抖动整页打不开）。</summary>
    private async ValueTask<string?> ReadXmlAsync(string processDefinitionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(processDefinitionId))
        {
            return null;
        }

        try
        {
            return await _engine.GetProcessDefinitionXmlAsync(processDefinitionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "读流程定义原文失败（{ProcessDefinitionId}）。", processDefinitionId);

            return null;
        }
    }
}

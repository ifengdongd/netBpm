using NetBpm.Engine.History;
using NetBpm.Runtime;

namespace NetBpm.Rest.Services;

/// <summary>分页查询结果（当前页 + 符合条件的总数）。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="Total">符合条件的总数（不受分页影响）。</param>
public sealed record PagedItems<T>(IReadOnlyList<T> Items, int Total);

/// <summary>
/// 历史 / 流程图查询服务：<b>一律走 <c>ACT_HI_*</c></b>。
/// <para>
/// 实例结束后运行时行已被删除，只有历史表还能回答「走过哪些节点、谁办过、变量最后是什么值」。
/// 查询形状（DTO 映射 + COUNT 配套查询）收在这里，端点只负责分页参数校验与响应信封。
/// </para>
/// <para>
/// 授权仍在过滤器（<see cref="Security.RuntimeAccess.RequireHistoryAccess"/>）：
/// 历史变量与运行态变量同口径，实例已结束时只有发起人 / 管理员可读。
/// </para>
/// </summary>
public interface IHistoryQueryService
{
    /// <summary>分页查历史流程实例（已办结检索 / 审计 / 报表）。</summary>
    Task<PagedItems<HistoricProcessInstanceResponse>> FindInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct);

    /// <summary>查单个历史实例；没有历史记录时返回 <see langword="null"/>。</summary>
    Task<HistoricProcessInstanceResponse?> FindInstanceAsync(string processInstanceId, CancellationToken ct);

    /// <summary>查历史活动（流程图着色数据源）。</summary>
    Task<IReadOnlyList<HistoricActivityResponse>> FindActivitiesAsync(
        string processInstanceId,
        CancellationToken ct);

    /// <summary>分页查历史任务（已办列表 / 跨实例审计）。</summary>
    Task<PagedItems<HistoricTaskResponse>> FindTasksAsync(HistoricTaskQuery query, CancellationToken ct);

    /// <summary>查历史变量（运行态删除后仍在）。</summary>
    Task<IReadOnlyList<HistoricVariableResponse>> FindVariablesAsync(
        string processInstanceId,
        CancellationToken ct);

    /// <summary>流程图数据：BPMN XML + 活动高亮序列；实例既无运行态也无历史时返回 <see langword="null"/>。</summary>
    Task<DiagramResponse?> GetDiagramAsync(string processInstanceId, CancellationToken ct);
}

/// <summary><see cref="IHistoryQueryService"/> 的默认实现。</summary>
internal sealed class HistoryQueryService(IProcessEngine engine) : IHistoryQueryService
{
    public async Task<PagedItems<HistoricProcessInstanceResponse>> FindInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var instances = await engine.FindHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false);

        return new PagedItems<HistoricProcessInstanceResponse>(
            [.. instances.Select(HistoricProcessInstanceResponse.From)],
            total);
    }

    public async Task<HistoricProcessInstanceResponse?> FindInstanceAsync(
        string processInstanceId,
        CancellationToken ct)
    {
        var instance = await engine.FindHistoricProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        return instance is null ? null : HistoricProcessInstanceResponse.From(instance);
    }

    public async Task<IReadOnlyList<HistoricActivityResponse>> FindActivitiesAsync(
        string processInstanceId,
        CancellationToken ct)
    {
        var activities = await engine.FindHistoricActivitiesAsync(processInstanceId, ct).ConfigureAwait(false);

        return [.. activities.Select(HistoricActivityResponse.From)];
    }

    public async Task<PagedItems<HistoricTaskResponse>> FindTasksAsync(
        HistoricTaskQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tasks = await engine.FindHistoricTasksAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountHistoricTasksAsync(query, ct).ConfigureAwait(false);

        return new PagedItems<HistoricTaskResponse>([.. tasks.Select(HistoricTaskResponse.From)], total);
    }

    public async Task<IReadOnlyList<HistoricVariableResponse>> FindVariablesAsync(
        string processInstanceId,
        CancellationToken ct)
    {
        var variables = await engine.FindHistoricVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

        return [.. variables.Select(HistoricVariableResponse.From)];
    }

    public async Task<DiagramResponse?> GetDiagramAsync(string processInstanceId, CancellationToken ct)
    {
        var diagram = await engine.GetDiagramAsync(processInstanceId, ct).ConfigureAwait(false);

        return diagram is null
            ? null
            : new DiagramResponse(
                diagram.ProcessInstanceId,
                diagram.ProcessDefinitionId,
                diagram.BpmnXml,
                diagram.IsRunning,
                [.. diagram.Activities.Select(HistoricActivityResponse.From)]);
    }
}

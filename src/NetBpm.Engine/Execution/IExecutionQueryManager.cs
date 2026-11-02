using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Execution;

/// <summary>
/// 运行中流程实例查询条件（数据库宿主下推到 SQL）。
/// </summary>
public sealed record ProcessInstanceFilter(
    string? ProcessDefinitionId = null,
    IReadOnlyCollection<string>? ProcessDefinitionIds = null,
    /// <summary>
    /// 实例 id 白名单（<c>in</c> 语义，落在 <c>ACT_RU_EXECUTION.ID_</c> 上）。
    /// </summary>
    /// <remarks>
    /// 给「我参与过的单据」这类<b>应用层已算出精确集合</b>的过滤用：
    /// 没有它就只能在应用侧把全部候选拉回来再筛，实例上万后翻页就是全表扫描。
    /// </remarks>
    IReadOnlyCollection<string>? ProcessInstanceIds = null,
    string? BusinessKey = null,
    string? StartUserId = null,
    string? TenantId = null,
    /// <summary>租户白名单（<c>in</c> 语义）；与 <paramref name="TenantId"/> 同时给出时两个条件都生效。</summary>
    IReadOnlyCollection<string>? TenantIds = null,
    bool? IsActive = null,
    DateTime? StartedAfter = null,
    DateTime? StartedBefore = null,
    int FirstResult = 0,
    int? MaxResults = 20,
    bool Descending = true);

/// <summary>
/// 运行中实例查询的服务端下推端口（<b>可选能力</b>）。
/// <para>未实现时门面回退到「取全部根执行 + 内存过滤」，语义相同。</para>
/// </summary>
public interface IExecutionQueryManager
{
    /// <summary>分页查运行中的流程实例（根执行），按开始时间排序。</summary>
    ValueTask<IReadOnlyList<IExecutionEntity>> QueryProcessInstancesAsync(
        ProcessInstanceFilter filter,
        CancellationToken ct = default);

    /// <summary>数运行中的流程实例（与 <see cref="QueryProcessInstancesAsync"/> 同口径）。</summary>
    ValueTask<int> CountProcessInstancesAsync(
        ProcessInstanceFilter filter,
        CancellationToken ct = default);
}

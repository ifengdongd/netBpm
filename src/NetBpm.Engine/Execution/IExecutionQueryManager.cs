using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Execution;

/// <summary>
/// 运行中流程实例查询条件（数据库宿主下推到 SQL）。
/// </summary>
public sealed record ProcessInstanceFilter(
    string? ProcessDefinitionId = null,
    IReadOnlyCollection<string>? ProcessDefinitionIds = null,
    string? BusinessKey = null,
    string? StartUserId = null,
    string? TenantId = null,
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

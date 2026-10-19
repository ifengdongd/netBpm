namespace NetBpm.Runtime;

/// <summary>
/// 任务可变字段的增量更新。
/// <para>
/// 每个字段独立可空：<see langword="null"/> 表示<b>不修改</b>。
/// 到期时间 / 描述本身可空，无法用 <see langword="null"/> 表达「清空」，
/// 因此提供 <see cref="ClearDueDate"/> / <see cref="ClearDescription"/> 显式清空标志
/// （对齐 Activiti <c>setDueDate(null)</c> / <c>setDescription(null)</c> 的清空语义）。
/// </para>
/// </summary>
/// <param name="Name">任务名。</param>
/// <param name="Description">任务描述。</param>
/// <param name="Priority">优先级（0 起，Activiti 默认 0）。</param>
/// <param name="DueDate">到期时间（UTC）。</param>
/// <param name="Category">任务分类。</param>
/// <param name="Owner">
/// 所有者；空串表示清空，<see langword="null"/> 表示不修改。
/// </param>
/// <param name="ClearDueDate">为 <see langword="true"/> 时清空到期时间（优先级高于 <see cref="DueDate"/>）。</param>
/// <param name="ClearDescription">为 <see langword="true"/> 时清空描述（优先级高于 <see cref="Description"/>）。</param>
public sealed record TaskUpdate(
    string? Name = null,
    string? Description = null,
    int? Priority = null,
    DateTime? DueDate = null,
    string? Category = null,
    string? Owner = null,
    bool ClearDueDate = false,
    bool ClearDescription = false);

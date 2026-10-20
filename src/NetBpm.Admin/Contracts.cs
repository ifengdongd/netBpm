namespace NetBpm.Admin;

/// <summary>流程模型列表摘要（不含 BPMN XML / SVG 正文）。</summary>
public sealed class BpmnModelSummary
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ProcessKey { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime ModifyTime { get; set; }
}

/// <summary>模型分页结果。</summary>
/// <param name="Items">当前页。</param>
/// <param name="Total">满足条件的总数。</param>
public sealed record BpmnModelPage(IReadOnlyList<BpmnModelSummary> Items, int Total);

/// <summary>流程模型详情（含 BPMN XML 与 SVG 预览）。</summary>
public sealed class BpmnModelDetail
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ProcessKey { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string BpmnXml { get; set; } = string.Empty;

    public string? Svg { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? ModifyTime { get; set; }
}

/// <summary>保存流程模型请求（<c>PUT /api/admin/models/{id}</c>，upsert 语义）。</summary>
public sealed class SaveBpmnModelRequest
{
    public string Name { get; set; } = string.Empty;

    public string ProcessKey { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string BpmnXml { get; set; } = string.Empty;

    public string? Svg { get; set; }
}

/// <summary>模型历史清理结果（<c>DELETE /api/admin/models/{id}/history</c>）。</summary>
/// <param name="Removed">删除的版本快照数。</param>
/// <param name="ModelId">模型 id。</param>
/// <param name="KeepLatest">保留的最近版本数。</param>
public sealed record ModelHistoryPruneResponse(int Removed, string ModelId, int KeepLatest);

/// <summary>
/// 保存模型的结果（含新版本号与校验明细）。
/// <para>
/// 保存是<b>草稿</b>语义：阻断级问题不会拒绝保存（否则用户没法保存半成品），
/// 但会随响应返回；真正阻断发生在发布/部署（<see cref="SaveBpmnModelResponse.Errors"/> 非空时发布会失败）。
/// </para>
/// </summary>
/// <param name="Id">模型 id。</param>
/// <param name="Revision">本次写入的版本号（从 1 递增）。</param>
/// <param name="Errors">阻断级校验问题（保存不拦截，发布前必须修掉）。</param>
/// <param name="Warnings">提示级校验问题。</param>
public sealed record SaveBpmnModelResponse(
    string Id,
    int Revision,
    IReadOnlyList<NetBpm.Bpmn.Validation.ValidationError> Errors,
    IReadOnlyList<NetBpm.Bpmn.Validation.ValidationError> Warnings);

/// <summary>模型版本摘要（不含 XML 正文）。</summary>
public sealed class BpmnModelRevisionSummary
{
    public string Id { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public int Revision { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ProcessKey { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>save / rollback。</summary>
    public string Action { get; set; } = string.Empty;

    public string? Operator { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>模型版本详情（含 BPMN XML / SVG 快照）。</summary>
public sealed class BpmnModelRevisionDetail
{
    public string Id { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public int Revision { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ProcessKey { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string BpmnXml { get; set; } = string.Empty;

    public string? Svg { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? Operator { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>流程类型（workflow_type 注册表行）。</summary>
public sealed class ProcessType
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Describe { get; set; } = string.Empty;

    public string ViewUrl { get; set; } = string.Empty;

    public string ApproveUrl { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool Enabled { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? ModifyTime { get; set; }
}

/// <summary>保存流程类型请求（创建 / 更新共用；id 由后端生成）。</summary>
public sealed class SaveProcessTypeRequest
{
    public string Name { get; set; } = string.Empty;

    public string Describe { get; set; } = string.Empty;

    public string ViewUrl { get; set; } = string.Empty;

    public string ApproveUrl { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool Enabled { get; set; }
}

/// <summary>
/// 历史归档请求（<c>POST /api/admin/history/archive</c>）。
/// <para>只归档<b>已结束</b>且结束时间早于 <see cref="Before"/> 的实例；原历史行在同一事务内删除。</para>
/// </summary>
public sealed class ArchiveHistoryRequest
{
    /// <summary>结束时间上界（必填；严格小于）。</summary>
    public DateTime? Before { get; set; }

    /// <summary>只统计不写库（预演）。</summary>
    public bool DryRun { get; set; }

    /// <summary>本次最多归档的实例数（1..1000，默认 200）。</summary>
    public int? BatchSize { get; set; }

    /// <summary>只归档该租户（受限身份由令牌决定，不能跨租户）。</summary>
    public string? TenantId { get; set; }
}

namespace NetBpm.Admin;

/// <summary>流程模型列表摘要（不含 BPMN XML / SVG 正文）。</summary>
public sealed class BpmnModelSummary
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ProcessKey { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime ModifyTime { get; set; }

    /// <summary>最新版本号（0 表示还没有任何快照）；保存时用作 <c>If-Match</c> 的期望值。</summary>
    public int Revision { get; set; }

    /// <summary>发布状态（线上定义 + 模型版本推导，只读）。</summary>
    public BpmnModelPublishState Publish { get; set; } = new();
}

/// <summary>
/// 模型的发布状态：由「同 key 的最新部署定义」与「版本历史里的 publish 快照」推导。
/// <para>
/// 不额外落表：历史表已经记录了每次发布（<c>action=publish</c>），
/// 是否未发布 = 最新版本号是否大于最后一次 publish 的版本号。
/// </para>
/// </summary>
public sealed class BpmnModelPublishState
{
    /// <summary>当前模型的最新版本号（模型从未保存过时为 <see langword="null"/>）。</summary>
    public int? CurrentRevision { get; set; }

    /// <summary>最后一次发布对应的模型版本号。</summary>
    public int? PublishedRevision { get; set; }

    /// <summary>线上最新定义版本（按 processKey 取最大版本）。</summary>
    public int? LatestPublishedVersion { get; set; }

    /// <summary>线上最新定义的部署时间。</summary>
    public DateTime? LatestPublishedAt { get; set; }

    /// <summary>线上最新定义 id（可直接用于查看 XML / 挂起 / 发起）。</summary>
    public string? LatestProcessDefinitionId { get; set; }

    /// <summary>最后一次发布之后是否又保存过（即存在未发布的修改）。</summary>
    public bool HasUnpublishedChanges { get; set; }
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

    /// <summary>最新版本号（0 表示还没有任何快照）；保存时用作 <c>If-Match</c> 的期望值。</summary>
    public int Revision { get; set; }

    /// <summary>发布状态（线上定义 + 模型版本推导，只读）。</summary>
    public BpmnModelPublishState Publish { get; set; } = new();
}

/// <summary>保存冲突（<c>If-Match</c> 与当前版本不一致）响应体，HTTP 409。</summary>
/// <param name="Message">可读说明。</param>
/// <param name="CurrentRevision">服务端当前版本号，客户端可提示用户刷新后重试。</param>
public sealed record RevisionConflictResponse(string Message, int CurrentRevision);

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
/// 发布模型请求体（<c>POST /api/admin/models/{id}/publish</c>，<b>可省略</b>）。
/// </summary>
/// <param name="Svg">
/// 部署时的流程图图片（SVG 文本，由管理端在部署前用 bpmn-js 渲染生成）。
/// 提供时随部署把当前 XML 连同图片写入一条 <c>publish</c> 版本快照，
/// 版本历史里即可直接查看「部署当时」的流程图。
/// </param>
/// <summary>发布模型请求（<c>POST /api/admin/models/{id}/publish</c>，body 可省略）。</summary>
/// <param name="Svg">部署前渲染的流程图（写入 publish 版本快照；可省略）。</param>
/// <param name="SkipIfUnchanged">
/// 内容与最后一次发布一致（没有未发布的修改）时跳过部署，直接回放线上最新定义。
/// 用于「重复点发布不应产生新版本」的场景；默认 false，保持旧行为。
/// </param>
public sealed record PublishModelRequest(string? Svg = null, bool SkipIfUnchanged = false);

/// <summary>
/// 局部更新流程模型（<c>PATCH /api/admin/models/{id}</c>）。
/// <para>
/// 只更新提供的字段：设计器保存不再发送 <c>description / svg</c>，
/// 用 PATCH 可以避免 PUT 全量语义把未提交字段清空。
/// </para>
/// </summary>
public sealed class PatchBpmnModelRequest
{
    public string? Name { get; set; }

    public string? ProcessKey { get; set; }

    public string? Description { get; set; }

    public string? BpmnXml { get; set; }

    public string? Svg { get; set; }
}

/// <summary>
/// 发布前预检结果（<c>POST /api/admin/models/{id}/validate</c>）。
/// <para>与发布同口径：<see cref="Errors"/> 非空时发布会失败，<see cref="Warnings"/> 只提示。</para>
/// </summary>
public sealed record ModelValidationResponse(
    IReadOnlyList<NetBpm.Bpmn.Validation.ValidationError> Errors,
    IReadOnlyList<NetBpm.Bpmn.Validation.ValidationError> Warnings);

/// <summary>
/// 一次「模型发布」记录（<c>GET /api/admin/models/{id}/publications</c>）。
/// </summary>
/// <param name="Revision">发布快照对应的模型版本号。</param>
/// <param name="CreatedAt">发布时间（UTC）。</param>
/// <param name="Operator">发布人。</param>
/// <param name="ProcessDefinitionId">匹配到的线上定义 id（无法唯一匹配时为 null）。</param>
/// <param name="Version">匹配到的线上定义版本。</param>
/// <param name="Linked">
/// 是否与部署定义唯一匹配。发布快照与部署记录没有外键，按同 key + 部署时间就近匹配；
/// 并发发布同一 key 时可能无法唯一匹配，此时返回 null 并置 false。
/// </param>
public sealed record ModelPublicationResponse(
    int Revision,
    DateTime CreatedAt,
    string? Operator,
    string? ProcessDefinitionId,
    int? Version,
    bool Linked);

/// <summary>复制模型请求（<c>POST /api/admin/models/{id}/duplicate</c>）。</summary>
/// <param name="Name">副本名称；留空用「原名（副本）」。</param>
public sealed record DuplicateBpmnModelRequest(string? Name);

/// <summary>定时作业改期请求（<c>POST /api/admin/jobs/{jobId}/due-date</c>）。</summary>
/// <param name="DueDate">新的到期时间（UTC 或带时区偏移的 ISO 8601）。</param>
public sealed record RescheduleTimerJobRequest(DateTime DueDate);

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

    /// <summary>save / rollback / publish。</summary>
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

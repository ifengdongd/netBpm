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

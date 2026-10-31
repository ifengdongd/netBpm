using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 流程在目录里的状态。
/// </summary>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<ProcessStatus>))]
public enum ProcessStatus
{
    /// <summary>草稿：有流程模型，但从未部署，不能发起。</summary>
    Draft = 0,

    /// <summary>生效：有最新部署版本，可以发起与推进。</summary>
    Active = 1,

    /// <summary>挂起：定义存在但不接受新实例，已有的实例也不能推进。</summary>
    Suspended = 2,

    /// <summary>已下线：历史实例可查，不可发起。</summary>
    Retired = 3,
}

/// <summary>
/// 流程的「入口」——调用方据此决定从哪个节点发起、该入口的默认参数是什么。
/// </summary>
/// <remarks>
/// 现状的 Demo 只有单一入口（startEvent → 首个 userTask），但平台化后一条流程可能有多个入口
/// （如「正常请假」与「补签请假」），故入口必须是列表而不是单值。
/// </remarks>
/// <param name="Key">入口标识（稳定字符串，随定义发布）。</param>
/// <param name="Name">入口显示名。</param>
/// <param name="StartActivityId">对应的开始事件 id。</param>
/// <param name="DefaultParameters">该入口的默认参数（键 = 参数名）。</param>
public sealed record ProcessEntryPoint(
    string Key,
    string? Name,
    string? StartActivityId,
    IReadOnlyDictionary<string, string?>? DefaultParameters);

/// <summary>
/// 流程名片（流程目录的一条）——外部系统接入时第一件要拿到的东西。
/// </summary>
/// <remarks>
/// <para>
/// 设计原则：<b>调用方不需要知道 BPMN，也不需要知道流程定义 id</b>。
/// 它拿到 <see cref="ProcessKey"/>、名称、图标、入口、参数规范版本，
/// 就能渲染「发起 xxx」的入口页；拿到 <see cref="DeployedVersion"/> 与
/// <see cref="DefinitionChecksum"/> 就能判断自己缓存的流程图是否过期。
/// </para>
/// <para>
/// <see cref="AllowedFromCaller"/> 是客户端级授权的结果（服务端已按客户端白名单过滤）：
/// 调用方不需要自己判断「我能不能用这条流程」，未授权的流程根本不会出现在列表里。
/// </para>
/// </remarks>
/// <param name="ProcessKey">流程 key（对外唯一标识，稳定不变）。</param>
/// <param name="Name">流程名称。</param>
/// <param name="Category">分类（用于分组展示，可为空）。</param>
/// <param name="Description">说明，允许 Markdown。</param>
/// <param name="Icon">图标标识或 URL，可为空。</param>
/// <param name="PageUrls">
/// 业务页面地址模板（键 = 用途 <c>start</c> / <c>view</c> / <c>approve</c>，值 = 带占位符的 URL）。
/// 平台<b>只做流程不做页面</b>：待办卡片与消息通知按这里跳回业务系统，占位符由平台填充。
/// </param>
/// <param name="Status">状态。</param>
/// <param name="DeployedVersion">当前生效版本号；未部署时为 0。</param>
/// <param name="DefinitionChecksum">当前定义的内容校验和（流程图 / 定义原文的缓存键）。</param>
/// <param name="ParameterSchemaVersion">参数规范版本；变化时调用方应重新拉取 schema。</param>
/// <param name="EntryPoints">发起入口列表。</param>
/// <param name="Labels">本地化标签（键 = 语言，值 = 展示名）；只有一种语言时可省略。</param>
/// <param name="Capabilities">
/// 能力声明：<b>本调用方</b>此刻能为这条流程做什么（已含白名单与 scope 判定）。
/// </param>
public sealed record ProcessDescriptor(
    string ProcessKey,
    string Name,
    string? Category,
    string? Description,
    string? Icon,
    IReadOnlyDictionary<string, string>? PageUrls,
    ProcessStatus Status,
    int DeployedVersion,
    string? DefinitionChecksum,
    int ParameterSchemaVersion,
    IReadOnlyList<ProcessEntryPoint> EntryPoints,
    IReadOnlyDictionary<string, string>? Labels,
    ProcessCapabilities Capabilities)
{
    /// <summary>当前是否允许发起新实例（等价于 <c>Capabilities.CanStart</c>）。</summary>
    /// <remarks>保留这个便捷属性是为了让调用方不必层层取 <c>capabilities.canStart</c>；两者恒等。</remarks>
    public bool CanStart => Capabilities.CanStart;
}

/// <summary>
/// 流程目录查询条件（对应 <c>GET /workflow/v1/processes</c> 的查询参数）。
/// </summary>
/// <param name="Keyword">按名称 / key 模糊匹配。</param>
/// <param name="Category">按分类过滤。</param>
/// <param name="Status">按状态过滤。</param>
/// <param name="Cursor">分页游标。</param>
/// <param name="Limit">本页条数，服务端会夹到 1..100。</param>
public sealed record ProcessQuery(
    string? Keyword = null,
    string? Category = null,
    ProcessStatus? Status = null,
    string? Cursor = null,
    int? Limit = null);

/// <summary>
/// 流程定义原文（<b>只在显式索取时返回</b>）。
/// </summary>
/// <remarks>
/// BPMN XML 对外只作为「定义原文」提供，用于审计留档与设计器展示，不参与业务交互。
/// 调用方不需要解析它——要通过 <see cref="ProcessDescriptor"/> 与各个视图拿结构化数据。
/// </remarks>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="ProcessDefinitionId">定义 id（引擎内部标识，形如 <c>leave_process:3:1234</c>）。</param>
/// <param name="Version">版本号。</param>
/// <param name="Checksum">
/// 内容校验和，形如 <c>sha256:{hex}</c>，与 <see cref="ProcessDescriptor.DefinitionChecksum"/> 恒等
/// （两处用同一个函数生成，调用方可以直接比对）。
/// </param>
/// <param name="DeployedAt">部署时间。</param>
/// <param name="Suspended">是否已挂起。</param>
/// <param name="Xml">定义原文；未索取时为 <see langword="null"/>。</param>
public sealed record ProcessDefinitionInfo(
    string ProcessKey,
    string ProcessDefinitionId,
    int Version,
    string Checksum,
    DateTimeOffset? DeployedAt,
    bool Suspended,
    string? Xml);

/// <summary>
/// 流程版本摘要（版本列表用，不含原文）。
/// </summary>
/// <param name="Version">版本号。</param>
/// <param name="ProcessDefinitionId">定义 id。</param>
/// <param name="Checksum">内容校验和。</param>
/// <param name="DeployedAt">部署时间。</param>
/// <param name="Suspended">是否已挂起。</param>
/// <param name="IsCurrent">是否为当前生效版本。</param>
public sealed record ProcessVersionInfo(
    int Version,
    string ProcessDefinitionId,
    string Checksum,
    DateTimeOffset? DeployedAt,
    bool Suspended,
    bool IsCurrent);

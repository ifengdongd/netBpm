using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBpm.ServerTaskWorker;

/// <summary>
/// 领取到的外部任务（只取 worker 用得上的字段）。
/// </summary>
/// <remarks>
/// 字段名用 <see cref="JsonPropertyNameAttribute"/> 显式钉死，不吃全局命名策略 ——
/// 这是跨进程契约，不能因为本地序列化配置改动就变。
/// </remarks>
public sealed class ExternalTask
{
    /// <summary>任务 id。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>主题（与 BPMN 的 <c>activiti:topic</c> 一致）。</summary>
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    /// <summary>流程实例 id。</summary>
    /// <remarks>
    /// 每次发起都是唯一值，可以拿来兜底关联，但它**不是**业务单号：
    /// 同一个单据重新发起会得到新的实例 id，业务上仍然是同一张单。
    /// 关联业务请优先用 <see cref="BusinessKey"/>。
    /// </remarks>
    [JsonPropertyName("processInstanceId")]
    public string? ProcessInstanceId { get; set; }

    /// <summary>
    /// 业务主键（发起流程时传入的 <c>businessKey</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是「流程 ↔ 业务单据」的<b>唯一稳定关联键</b>：发起时传 <c>businessKey=LEAVE-2026-0001</c>，
    /// worker 领到任务就能直接定位到业务表那一行并改 <c>status</c>，不用拿实例 id 去反查。
    /// </para>
    /// <para>
    /// ⚠️ 发起时没传就是 <c>null</c>（此时才退回用实例 id 兜底）。所以「拿不到业务号」这件事
    /// 通常不是引擎的问题，而是发起方没传 —— 控制台发起时填「业务主键」即可。
    /// </para>
    /// </remarks>
    [JsonPropertyName("businessKey")]
    public string? BusinessKey { get; set; }

    /// <summary>流程定义 id。</summary>
    [JsonPropertyName("processDefinitionId")]
    public string? ProcessDefinitionId { get; set; }

    /// <summary>节点 id。</summary>
    [JsonPropertyName("activityId")]
    public string? ActivityId { get; set; }

    /// <summary>节点名。</summary>
    [JsonPropertyName("activityName")]
    public string? ActivityName { get; set; }

    /// <summary>租户。</summary>
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    /// <summary>剩余重试次数。</summary>
    [JsonPropertyName("retries")]
    public int? Retries { get; set; }

    /// <summary>
    /// 领取时的流程变量快照。
    /// </summary>
    /// <remarks>
    /// 键是流程变量名（<c>applyUser</c> / <c>days</c> …），必须原样保留大小写，
    /// 所以全局序列化配置里不能设 <c>DictionaryKeyPolicy</c>。
    /// </remarks>
    [JsonPropertyName("payload")]
    public Dictionary<string, JsonElement> Payload { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>领取请求体。</summary>
internal sealed class FetchRequest
{
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("topics")]
    public string[] Topics { get; set; } = [];

    [JsonPropertyName("maxTasks")]
    public int MaxTasks { get; set; }

    [JsonPropertyName("lockDurationMs")]
    public int LockDurationMs { get; set; }

    [JsonPropertyName("waitSeconds")]
    public int WaitSeconds { get; set; }
}

/// <summary>成功回执请求体。</summary>
internal sealed class CompleteRequest
{
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    /// <summary>要写回流程实例的变量 —— 不是覆盖 payload，是追加/覆盖到实例变量表。</summary>
    [JsonPropertyName("variables")]
    public Dictionary<string, object?> Variables { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>失败回执请求体。</summary>
internal sealed class FailureRequest
{
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("errorDetails")]
    public string? ErrorDetails { get; set; }

    /// <summary>剩余重试次数。<c>0</c> = 别再试了，直接进死信。</summary>
    [JsonPropertyName("retries")]
    public int? Retries { get; set; }
}

/// <summary>
/// 手工调用 <c>POST /api/serverTask/{name}</c> 的请求体。
/// </summary>
/// <remarks>
/// 只要求 <c>processInstanceId</c> + <c>payload</c>，其余可省 —— 目的是让人能用 curl
/// 单独验证业务逻辑，而不必先跑出一条真实的流程实例。
/// </remarks>
public sealed class ServerTaskRequest
{
    /// <summary>任务 id（手工调用时可省，默认 <c>manual</c>）。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>流程实例 id —— 业务关联键。</summary>
    [JsonPropertyName("processInstanceId")]
    public string? ProcessInstanceId { get; set; }

    /// <summary>业务主键（与领取到的外部任务同名字段）；手工调用时建议一起给，才走真实关联逻辑。</summary>
    [JsonPropertyName("businessKey")]
    public string? BusinessKey { get; set; }

    /// <summary>节点 id（仅用于排错）。</summary>
    [JsonPropertyName("activityId")]
    public string? ActivityId { get; set; }

    /// <summary>变量快照。</summary>
    [JsonPropertyName("payload")]
    public Dictionary<string, JsonElement>? Payload { get; set; }
}

/// <summary>
/// worker 与引擎之间的 JSON 口径。
/// </summary>
public static class WorkerJson
{
    /// <summary>请求/响应共用选项。</summary>
    /// <remarks>
    /// 只设 <see cref="JsonSerializerOptions.PropertyNamingPolicy"/>：它影响的是 <c>PropertyName</c>，
    /// 不影响字典键。<c>DictionaryKeyPolicy</c> 保持默认，否则 <c>applyUser</c> 会被改成 <c>applyUser</c>
    /// 之外的样子（首字母大写/驼峰转换），payload 直接读不到。
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

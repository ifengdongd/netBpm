using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBpm.ExternalWorker;

/// <summary>领取到的外部任务（只取 worker 用得上的字段）。</summary>
public sealed class ExternalTaskDto
{
    /// <summary>任务 id。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>主题。</summary>
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    /// <summary>流程实例 id。</summary>
    [JsonPropertyName("processInstanceId")]
    public string? ProcessInstanceId { get; set; }

    /// <summary>业务键。</summary>
    [JsonPropertyName("businessKey")]
    public string? BusinessKey { get; set; }

    /// <summary>节点名。</summary>
    [JsonPropertyName("activityName")]
    public string? ActivityName { get; set; }

    /// <summary>租户。</summary>
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    /// <summary>剩余重试次数。</summary>
    [JsonPropertyName("retries")]
    public int? Retries { get; set; }

    /// <summary>领取时的变量快照。</summary>
    [JsonPropertyName("payload")]
    public Dictionary<string, object?> Payload { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>领取请求体。</summary>
internal sealed class FetchRequest
{
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("topics")]
    public string[]? Topics { get; set; }

    [JsonPropertyName("maxTasks")]
    public int? MaxTasks { get; set; }

    [JsonPropertyName("lockDurationMs")]
    public int? LockDurationMs { get; set; }

    [JsonPropertyName("waitSeconds")]
    public int? WaitSeconds { get; set; }
}

/// <summary>成功回执请求体。</summary>
internal sealed class CompleteRequest
{
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public Dictionary<string, JsonElement>? Variables { get; set; }
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

    [JsonPropertyName("retries")]
    public int? Retries { get; set; }

    [JsonPropertyName("retryAfter")]
    public DateTimeOffset? RetryAfter { get; set; }
}

using System.Text.Json;

namespace NetBpm.ExternalWorker;

/// <summary>
/// JSON 约定：与 ASP.NET 默认的 Web 风格对齐（camelCase、大小写不敏感）。
/// </summary>
internal static class JsonDefaults
{
    /// <summary>序列化 / 反序列化选项。</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

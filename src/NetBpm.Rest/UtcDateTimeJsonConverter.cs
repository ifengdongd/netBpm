using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBpm.Rest;

/// <summary>
/// 统一把 <see cref="DateTime"/> 按 UTC 序列化。
/// <para>
/// 引擎写入的时间都是 UTC，但 MySQL <c>DATETIME</c> / SQL Server <c>datetime2</c> 读回时
/// <c>Kind=Unspecified</c>，默认序列化会得到 <c>"2026-10-19T06:20:32"</c>（没有 <c>Z</c>），
/// 浏览器 <c>new Date(...)</c> 会按本地时区解释，导致非 UTC 客户端整体偏移。
/// </para>
/// <para>反序列化同样把缺省时区按 UTC 处理，保证请求 / 响应口径一致。</para>
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        Normalize(reader.GetDateTime());

    public override void Write(
        Utf8JsonWriter writer,
        DateTime value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(Normalize(value));

    private static DateTime Normalize(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // 数据库读回的时间视为 UTC（引擎唯一的时间口径）
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

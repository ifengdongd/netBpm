using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 枚举序列化为 <c>SCREAMING_SNAKE_CASE</c> 的字符串转换器。
/// </summary>
/// <remarks>
/// <para>
/// 对外契约规定枚举必须是稳定字符串（`"ALL_APPROVED"`），不能是数字——
/// 数字会让「新增一个枚举值」变成静默的破坏性变更（客户端把未知值当 0 处理）。
/// </para>
/// <para>
/// 做成转换器而不是让每个宿主自己配 <c>JsonSerializerOptions</c>：序列化策略必须<b>跟着类型走</b>，
/// 否则独立服务的配置一改，契约就悄悄变了。
/// </para>
/// </remarks>
/// <typeparam name="TEnum">枚举类型。</typeparam>
public sealed class ScreamingSnakeCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>构造转换器（固定使用 <see cref="JsonNamingPolicy.SnakeCaseUpper"/>）。</summary>
    public ScreamingSnakeCaseEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseUpper)
    {
    }
}

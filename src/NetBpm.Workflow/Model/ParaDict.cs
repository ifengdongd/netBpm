using System.Text.Json;

namespace NetBpm.Workflow;

/// <summary>
/// 流程参数集合（ParaDict）—— 除 <see cref="BaseModel"/> 那 7 个审批元数据之外，
/// 流程还需要携带的业务参数。
/// </summary>
/// <remarks>
/// <para>
/// 存在的意义是<b>可扩展</b>：一条流程今天要 <c>days</c> / <c>reason</c>，
/// 明天可能要 <c>leaveType</c> / <c>city</c> / <c>attachmentCount</c> ——
/// 有了它，加参数不必改模型、不必加字段、不必改接口签名。
/// </para>
/// <para>三条约定：</para>
/// <list type="number">
///   <item><description>
///   <b>值一律按字符串收</b>。来源可能是 JSON 数字、布尔或文本，统一按文本入库；
///   需要数值比较时不用操心类型 —— 流程表达式里的 <c>&gt;</c> / <c>==</c> 会自己做隐式转换
///   （<c>"2"</c> 与 <c>3</c> 比较时按长整数解析，见 <c>BooleanOperations</c>）。
///   </description></item>
///   <item><description>
///   <b>同时展开为扁平流程变量</b>。键名原样成为变量名，所以网关条件仍写 <c>${days &gt; 3}</c>，
///   而不是 <c>${paraDict['days'] &gt; 3}</c> —— 与 Activiti 既有流程、设计器画出的图保持一致。
///   </description></item>
///   <item><description>
///   <b>更新语义是「按 key 覆盖」</b>。提交审批时传进来的参数只动它提到的那些键，
///   其余键保持原样（未传 = 不改，传 <c>null</c> = 置空）。
///   </description></item>
/// </list>
/// </remarks>
public sealed record ParaDict
{
    /// <summary>空参数集。</summary>
    public static ParaDict Empty { get; } = new();

    /// <summary>参数值（键 = 参数名，值 = 字符串形式的参数值）。</summary>
    public IReadOnlyDictionary<string, string?> Values { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>是否没有任何参数。</summary>
    public bool IsEmpty => Values.Count == 0;

    /// <summary>构造参数集（<paramref name="values"/> 为 <see langword="null"/> 时返回 <see cref="Empty"/>）。</summary>
    public static ParaDict From(IEnumerable<KeyValuePair<string, string?>>? values)
    {
        if (values is null)
        {
            return Empty;
        }

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            map[name] = value;
        }

        return map.Count == 0 ? Empty : new ParaDict { Values = map };
    }

    /// <summary>
    /// 从 HTTP 请求体里的 JSON 对象构造参数集。
    /// </summary>
    /// <remarks>
    /// 键去空白后为空的一律丢弃；值按<b>原文</b>收（数字 / 布尔取原始文本，字符串取内容，
    /// <c>null</c> 保持 <c>null</c>）—— 也就是「值一律按字符串收」那条约定的落点。
    /// </remarks>
    /// <param name="values">请求里的参数字典（ASP.NET 绑定出来的 <see cref="JsonElement"/>）。</param>
    public static ParaDict FromJson(IDictionary<string, JsonElement>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Empty;
        }

        var map = new Dictionary<string, string?>(values.Count, StringComparer.Ordinal);

        foreach (var (name, element) in values)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            map[name.Trim()] = ElementText(element);
        }

        return map.Count == 0 ? Empty : new ParaDict { Values = map };
    }

    /// <summary>
    /// 按 key 覆盖合并。
    /// <para><paramref name="updates"/> 里提到的键更新，没提到的键保持原样。</para>
    /// </summary>
    public ParaDict Merge(ParaDict? updates)
    {
        if (updates is null || updates.IsEmpty)
        {
            return this;
        }

        var merged = new Dictionary<string, string?>(Values, StringComparer.Ordinal);

        foreach (var (name, value) in updates.Values)
        {
            merged[name] = value;
        }

        return new ParaDict { Values = merged };
    }

    /// <summary>
    /// 展开成扁平流程变量（键名即变量名，值为字符串）。
    /// <para>值为 <see langword="null"/> 的键不写入 —— 与 <see cref="BaseModel.ToVariables"/> 保持一致。</para>
    /// </summary>
    public Dictionary<string, object?> ToVariables()
    {
        var variables = new Dictionary<string, object?>(Values.Count, StringComparer.Ordinal);

        foreach (var (name, value) in Values)
        {
            if (value is not null)
            {
                variables[name] = value;
            }
        }

        return variables;
    }

    /// <summary>返回与 <see cref="BaseModelVariables.All"/> 重名的键（调用方据此拒绝请求）。</summary>
    /// <remarks>
    /// 参数名撞上审批元数据字段会污染 <see cref="BaseModel"/>，属于用错而非「覆盖」，因此不静默放行。
    /// </remarks>
    public IReadOnlyList<string> ConflictingKeys() =>
        [.. Values.Keys.Where(name => BaseModelVariables.All.Contains(name, StringComparer.Ordinal))];

    /// <summary>按文本读取 JSON 值：数字与布尔取原文，字符串取内容，<c>null</c> 保持 <see langword="null"/>。</summary>
    private static string? ElementText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };
}

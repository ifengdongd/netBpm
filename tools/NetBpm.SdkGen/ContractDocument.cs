using System.Text.Json.Nodes;

namespace NetBpm.SdkGen;

/// <summary>一个路径参数或查询参数。</summary>
public sealed record OperationParameter(string Name, string Location, JsonNode? Schema, bool Required)
{
    /// <summary>路径参数（<c>in: path</c>），必须由调用方按位置传入。</summary>
    public bool IsPath => string.Equals(Location, "path", StringComparison.Ordinal);
}

/// <summary>一个被治理的操作：<see cref="Method"/> + <see cref="Path"/> 唯一定位。</summary>
public sealed record ContractOperation(
    string Method,
    string Path,
    IReadOnlyList<OperationParameter> Parameters,
    JsonNode? RequestSchema,
    bool RequestRequired,
    JsonNode? SuccessSchema,
    IReadOnlyList<JsonNode?> ErrorSchemas,
    bool ReturnsText)
{
    public IReadOnlyList<OperationParameter> PathParameters =>
        [.. Parameters.Where(parameter => parameter.IsPath)];

    public IReadOnlyList<OperationParameter> QueryParameters =>
        [.. Parameters.Where(parameter => !parameter.IsPath)];

    /// <summary>成功响应是不是游标分页（<c>CursorPageOf*</c>）：是的话值得生成 <c>iterate()</c>。</summary>
    public bool IsCursorPage =>
        Schemas.RefName(SuccessSchema) is { } name
        && name.StartsWith("CursorPageOf", StringComparison.Ordinal);

    /// <summary>分页包装指向的元素类型名（<c>CursorPageOfInstanceSummary</c> → <c>InstanceSummary</c>）。</summary>
    public string? PageItemTypeName =>
        Schemas.RefName(SuccessSchema) is { } name && name.StartsWith("CursorPageOf", StringComparison.Ordinal)
            ? name["CursorPageOf".Length..]
            : null;
}

/// <summary>
/// 已提交的 OpenAPI 快照 + 「从被治理操作可达的 schema 闭包」。
/// </summary>
/// <remarks>
/// <para>
/// 快照里躺着 118 个 schema，其中大半属于 <c>/api/**</c>（引擎原语直通面）或 <c>/admin/v1</c>。
/// 把它们全塞进 SDK 会得到一份「一半类型用不上」的模型层，还会把引擎内部演进变成 SDK 的破坏性变更。
/// 因此这里从 23 个业务面操作出发做一次可达性分析，只保留真正会出现在报文里的类型——
/// 这也顺带回答了「SDK 该有多大」这个评审必问的问题：<b>由快照决定，不由人手决定</b>。
/// </para>
/// </remarks>
public sealed class ContractDocument
{
    private readonly Dictionary<string, JsonNode?> _schemas;

    private ContractDocument(
        string openApiVersion,
        IReadOnlyDictionary<string, JsonNode?> schemas,
        IReadOnlyList<ContractOperation> operations)
    {
        OpenApiVersion = openApiVersion;
        _schemas = new Dictionary<string, JsonNode?>(schemas, StringComparer.Ordinal);
        Operations = operations;
        ReachableSchemaNames = ComputeClosure(schemas, operations);
    }

    /// <summary>快照声明的 OpenAPI 版本（只用于生成文件头，不参与逻辑）。</summary>
    public string OpenApiVersion { get; }

    /// <summary>被治理的操作，按「路径 → 方法」稳定排序。</summary>
    public IReadOnlyList<ContractOperation> Operations { get; }

    /// <summary>可达 schema 名，按名称序（生成产物因此稳定）。</summary>
    public IReadOnlyList<string> ReachableSchemaNames { get; }

    public JsonNode? Schema(string name) => _schemas[name];

    public static ContractDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到 OpenAPI 快照：{path}。首次生成请先跑 ContractSnapshotTests（NETBPM_UPDATE_CONTRACTS=1）。",
                path);
        }

        var document = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"无法解析 OpenAPI 快照：{path}");

        var schemas = document["components"]?["schemas"] as JsonObject
            ?? throw new InvalidOperationException($"快照缺少 components.schemas：{path}");

        var operations = ReadOperations(document);

        return new ContractDocument(
            document["openapi"]?.GetValue<string>() ?? "unknown",
            schemas.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            operations);
    }

    /// <summary>读操作清单：只收 <c>/workflow/v1</c>，与 <c>ContractSnapshotTests</c> 的治理范围保持一致。</summary>
    private static List<ContractOperation> ReadOperations(JsonNode document)
    {
        var paths = document["paths"] as JsonObject
            ?? throw new InvalidOperationException("快照缺少 paths。");

        var operations = new List<ContractOperation>();

        foreach (var (path, pathItem) in paths)
        {
            if (!path.StartsWith("/workflow/v1", StringComparison.Ordinal) || pathItem is not JsonObject item)
            {
                continue;
            }

            foreach (var (method, operation) in item)
            {
                if (operation is not JsonObject definition)
                {
                    continue;
                }

                operations.Add(new ContractOperation(
                    method.ToUpperInvariant(),
                    path,
                    ReadParameters(definition),
                    ReadRequestSchema(definition, out var requestRequired),
                    requestRequired,
                    ReadSuccessSchema(definition, out var returnsText),
                    ReadErrorSchemas(definition),
                    returnsText));
            }
        }

        return [.. operations.OrderBy(operation => operation.Path, StringComparer.Ordinal)
            .ThenBy(operation => operation.Method, StringComparer.Ordinal)];
    }

    private static List<OperationParameter> ReadParameters(JsonObject operation) =>
        [.. (operation["parameters"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(parameter => new OperationParameter(
                parameter["name"]?.GetValue<string>() ?? throw new InvalidOperationException("参数缺少 name。"),
                parameter["in"]?.GetValue<string>() ?? "query",
                parameter["schema"],
                parameter["required"]?.GetValue<bool>() ?? false))];

    private static JsonNode? ReadRequestSchema(JsonObject operation, out bool required)
    {
        required = false;

        if (operation["requestBody"] is not JsonObject body)
        {
            return null;
        }

        required = body["required"]?.GetValue<bool>() ?? false;

        return JsonContent(body);
    }

    private static JsonNode? ReadSuccessSchema(JsonObject operation, out bool returnsText)
    {
        returnsText = false;

        var responses = operation["responses"] as JsonObject;

        var success = responses?
            .Where(pair => pair.Key.Length == 3 && pair.Key[0] == '2')
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value as JsonObject)
            .FirstOrDefault(response => response is not null);

        if (success?["content"] is not JsonObject content)
        {
            return null;
        }

        if (content.ContainsKey("application/json"))
        {
            return JsonContent(success);
        }

        // 流程图端点回 image/svg+xml：SDK 把它当文本处理
        returnsText = content.Count > 0;

        return null;
    }

    /// <summary>
    /// 非 2xx 的 JSON 响应体：<c>PlatformProblem</c> 就是从这里进来的。
    /// </summary>
    /// <remarks>
    /// 它们也是 SDK 的一部分——<c>NetBpmApiError.problem</c> 需要类型，凭印象手写一个
    /// 「差不多的问题体」正是要避免的事。
    /// </remarks>
    private static List<JsonNode?> ReadErrorSchemas(JsonObject operation) =>
        [.. (operation["responses"] as JsonObject ?? [])
            .Where(pair => !(pair.Key.Length == 3 && pair.Key[0] == '2'))
            .Select(pair => JsonContent(pair.Value as JsonObject ?? []))
            .Where(schema => schema is not null)];

    private static JsonNode? JsonContent(JsonObject carrier) =>
        (carrier["content"] as JsonObject)?["application/json"]?["schema"];

    private static readonly string[] CompositionKeywords = ["oneOf", "anyOf", "allOf"];

    /// <summary>
    /// 可达性分析：从每个操作的入参与成功响应出发，顺着 <c>$ref</c> / <c>properties</c> /
    /// <c>items</c> / <c>additionalProperties</c> / <c>oneOf</c> 走一遍，收集被引用的 schema 名。
    /// </summary>
    private static string[] ComputeClosure(
        IReadOnlyDictionary<string, JsonNode?> schemas,
        IReadOnlyList<ContractOperation> operations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();

        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject map when Schemas.RefName(map) is { } reference:
                    if (seen.Add(reference))
                    {
                        pending.Push(reference);
                    }

                    return;

                case JsonObject map:
                    if (map["properties"] is JsonObject properties)
                    {
                        foreach (var (_, child) in properties)
                        {
                            Visit(child);
                        }
                    }

                    Visit(map["items"]);

                    if (map["additionalProperties"] is { } additional)
                    {
                        Visit(additional);
                    }

                    foreach (var keyword in CompositionKeywords)
                    {
                        if (map[keyword] is JsonArray branches)
                        {
                            foreach (var branch in branches)
                            {
                                Visit(branch);
                            }
                        }
                    }

                    return;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        Visit(item);
                    }

                    return;

                default:
                    return;
            }
        }

        foreach (var operation in operations)
        {
            foreach (var parameter in operation.Parameters)
            {
                Visit(parameter.Schema);
            }

            Visit(operation.RequestSchema);
            Visit(operation.SuccessSchema);

            foreach (var error in operation.ErrorSchemas)
            {
                Visit(error);
            }

            // $ref 命中后要展开它自己的引用（广度上等价于递归，用显式栈防止深层嵌套爆栈）
            while (pending.Count > 0)
            {
                Visit(schemas.GetValueOrDefault(pending.Pop()));
            }
        }

        return [.. seen.OrderBy(name => name, StringComparer.Ordinal)];
    }
}

/// <summary>JSON 结构上的小工具。</summary>
internal static class Schemas
{
    /// <summary>取 <c>$ref</c> 指向的 schema 名（<c>#/components/schemas/X</c> → <c>X</c>）。</summary>
    public static string? RefName(JsonNode? node)
    {
        if (node is not JsonObject map || map["$ref"] is not JsonValue reference)
        {
            return null;
        }

        var text = reference.GetValue<string>();

        return text[(text.LastIndexOf('/') + 1)..];
    }

    /// <summary>把 <c>type</c> 读成集合（OpenAPI 3.1 允许 <c>["null","string"]</c>）。</summary>
    public static IReadOnlyList<string> Types(JsonNode? node)
    {
        if (node is not JsonObject map)
        {
            return [];
        }

        return map["type"] switch
        {
            JsonValue value => [value.GetValue<string>()],
            JsonArray array => [.. array.OfType<JsonValue>().Select(item => item.GetValue<string>())],
            _ => [],
        };
    }

    /// <summary>取 <c>format</c>（<c>date-time</c> / <c>int64</c> / <c>double</c> …）。</summary>
    public static string? Format(JsonNode? node) =>
        node is JsonObject map && map["format"] is JsonValue format ? format.GetValue<string>() : null;

    /// <summary>枚举取值。</summary>
    public static IReadOnlyList<string> EnumValues(JsonNode? node) =>
        node is JsonObject map && map["enum"] is JsonArray values
            ? [.. values.OfType<JsonValue>().Select(item => item.GetValue<string>())]
            : [];
}

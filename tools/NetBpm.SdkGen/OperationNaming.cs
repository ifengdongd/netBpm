using System.Text;
using System.Text.Json.Nodes;

namespace NetBpm.SdkGen;

/// <summary>操作 → 「资源分组 + 方法名」。</summary>
public sealed record OperationName(string Resource, string ResourceType, string Method);

/// <summary>
/// 把 <c>方法 + 路径</c> 翻译成 SDK 里的方法名。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不从路径「聪明地」推导：SDK 的方法名是对外 API 的一部分，改一次就是所有调用方编译失败。
/// 推导规则（单数化、动词选择、扩展名剥离）总会对某几个端点给出别扭的名字，改规则又会顺手改掉
/// 已有的名字——于是「重构生成器」变成「破坏 SDK」。所以已知的 22 条路径配一张显式命名表，
/// 只有表里没出现过的新端点才走推导，并在生成日志里<b>告警</b>出来让人补表。
/// </para>
/// </remarks>
public static class OperationNaming
{
    private static readonly Dictionary<(string Method, string Path), OperationName> Table = new()
    {
        // 实例
        [("GET", "/workflow/v1/instances")] = new("instances", "Instance", "list"),
        [("POST", "/workflow/v1/instances")] = new("instances", "Instance", "start"),
        [("GET", "/workflow/v1/instances/{instanceId}")] = new("instances", "Instance", "get"),
        [("GET", "/workflow/v1/instances/{instanceId}/diagram.svg")] = new("instances", "Instance", "getDiagram"),
        [("GET", "/workflow/v1/instances/{instanceId}/history")] = new("instances", "Instance", "getHistory"),
        [("GET", "/workflow/v1/instances/{instanceId}/parameters")] = new("instances", "Instance", "getParameters"),
        [("GET", "/workflow/v1/instances/{instanceId}/parameters/history")] = new("instances", "Instance", "getParameterHistory"),
        [("POST", "/workflow/v1/instances/{instanceId}/terminate")] = new("instances", "Instance", "terminate"),

        // 流程目录
        [("GET", "/workflow/v1/processes")] = new("processes", "Process", "list"),
        [("GET", "/workflow/v1/processes/{processKey}")] = new("processes", "Process", "get"),
        [("GET", "/workflow/v1/processes/{processKey}/definition")] = new("processes", "Process", "getDefinition"),
        [("GET", "/workflow/v1/processes/{processKey}/diagram.svg")] = new("processes", "Process", "getDiagram"),
        [("GET", "/workflow/v1/processes/{processKey}/form/schema")] = new("processes", "Process", "getFormSchema"),
        [("GET", "/workflow/v1/processes/{processKey}/parameters/schema")] = new("processes", "Process", "getParameterSchema"),
        [("GET", "/workflow/v1/processes/{processKey}/versions")] = new("processes", "Process", "listVersions"),

        // 待办
        [("GET", "/workflow/v1/tasks")] = new("tasks", "Task", "list"),
        [("GET", "/workflow/v1/tasks/{taskId}")] = new("tasks", "Task", "get"),
        [("GET", "/workflow/v1/tasks/{taskId}/attachments")] = new("tasks", "Task", "getAttachments"),
        [("GET", "/workflow/v1/tasks/{taskId}/form")] = new("tasks", "Task", "getForm"),
        [("POST", "/workflow/v1/tasks/{taskId}/claim")] = new("tasks", "Task", "claim"),
        [("POST", "/workflow/v1/tasks/{taskId}/submit")] = new("tasks", "Task", "submit"),
        [("POST", "/workflow/v1/tasks/{taskId}/transfer")] = new("tasks", "Task", "transfer"),
        [("POST", "/workflow/v1/tasks/{taskId}/unclaim")] = new("tasks", "Task", "unclaim"),
    };

    /// <summary>命名表里显式登记过的操作个数（生成器用它自检「表没跟上快照」）。</summary>
    public static int KnownCount => Table.Count;

    public static bool IsExplicitlyNamed(ContractOperation operation) =>
        Table.ContainsKey((operation.Method, operation.Path));

    public static OperationName Resolve(ContractOperation operation)
    {
        if (Table.TryGetValue((operation.Method, operation.Path), out var known))
        {
            return known;
        }

        return Derive(operation);
    }

    /// <summary>
    /// 兜底推导：<c>资源 + 剩余段</c>。只服务「表还没跟上」的新端点——名字丑但可读，
    /// 而且生成器会告警，评审时能被发现并补表。
    /// </summary>
    private static OperationName Derive(ContractOperation operation)
    {
        // /workflow/v1/instances/{instanceId}/terminate → ["instances", "{instanceId}", "terminate"]
        var segments = operation.Path.Trim('/').Split('/').Skip(2).ToArray();

        var resource = segments.Length > 0 ? segments[0] : "root";

        var rest = segments
            .Skip(1)
            .Select(segment => segment.Trim('{', '}').Split('.')[0])
            .Where(segment => segment.Length > 0 && !segment.EndsWith("Id", StringComparison.Ordinal))
            .ToArray();

        var verb = operation.Method switch
        {
            "GET" => rest.Length == 0 ? "list" : "get",
            "POST" => rest.Length == 0 ? "create" : "post",
            "PUT" => "replace",
            "DELETE" => "delete",
            _ => operation.Method.ToLowerInvariant(),
        };

        var method = rest.Length == 0
            ? verb
            : string.Concat(
                verb,
                string.Concat(rest.Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..])));

        return new OperationName(resource, Pascal(resource), method);
    }

    /// <summary><c>instances</c> → <c>Instances</c>。</summary>
    public static string Pascal(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary><c>InstanceStatus</c> → <c>instanceStatus</c>。</summary>
    public static string Camel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}

/// <summary>schema → TypeScript / Java 类型的映射（三处复用：模型、客户端参数、返回值）。</summary>
public static class TypeMapper
{
    /// <summary>TS 类型表达式。</summary>
    /// <remarks>
    /// <b>数值的三元组</b>：快照里 <c>long</c>/<c>int</c> 会写成 <c>["integer","string"]</c>——
    /// 那是服务端 <c>JsonNumberHandling.AllowReadingFromString</c> 的产物：<b>线上恒为数字</b>，
    /// 字符串只是入参被额外放宽的形态。所以 SDK 统一映射成 <c>number</c>，不生成联合类型，
    /// 否则每个调用点都要写 <c>typeof x === "number" ? …</c>。
    /// </remarks>
    public static string TypeScript(JsonNode? schema)
    {
        if (schema is not JsonObject map)
        {
            return "unknown";
        }

        if (Schemas.RefName(map) is { } reference)
        {
            return reference;
        }

        if (map["oneOf"] is JsonArray oneOf)
        {
            return Union(oneOf);
        }

        if (map["anyOf"] is JsonArray anyOf)
        {
            return Union(anyOf);
        }

        var types = Schemas.Types(map);

        if (types.Count == 0)
        {
            return Schemas.EnumValues(map).Count > 0 ? Union(Schemas.EnumValues(map)) : "unknown";
        }

        var parts = new List<string>();
        var nullable = false;

        foreach (var type in types)
        {
            switch (type)
            {
                case "null":
                    nullable = true;

                    break;

                case "string":
                    // 与 integer/number 同时出现是 long 的入参放宽（见上面的 remarks）：按数字走
                    if (!types.Contains("integer") && !types.Contains("number"))
                    {
                        Add(parts, "string");
                    }

                    break;

                case "boolean":
                    Add(parts, "boolean");

                    break;

                case "integer" or "number":
                    Add(parts, "number");

                    break;

                case "array":
                    Add(parts, ArrayTypeScript(map["items"]));

                    break;

                case "object":
                    Add(parts, ObjectTypeScript(map));

                    break;

                default:
                    Add(parts, "unknown");

                    break;
            }
        }

        if (parts.Count == 0)
        {
            return nullable ? "null" : "unknown";
        }

        // null 放最后：读起来是「主类型，可能为空」，与 C#/Java 习惯一致
        return nullable ? string.Join(" | ", parts) + " | null" : string.Join(" | ", parts);
    }

    /// <summary>Java 类型表达式（对象用 record、枚举用 enum，可空用包装类型，因此不需要额外标注）。</summary>
    public static string Java(JsonNode? schema)
    {
        if (schema is not JsonObject map)
        {
            return "Object";
        }

        if (Schemas.RefName(map) is { } reference)
        {
            return reference;
        }

        if (map["oneOf"] is JsonArray oneOf)
        {
            return JavaUnion(oneOf);
        }

        if (map["anyOf"] is JsonArray anyOf)
        {
            return JavaUnion(anyOf);
        }

        var types = Schemas.Types(map);

        if (types.Count == 0)
        {
            return "Object";
        }

        // 可空由包装类型表达（Integer 而不是 int），这里只需要挑出「非 null 的那个」
        var nonNull = types.Where(type => type != "null").ToArray();

        // 只有 null（oneOf 的 `{"type":"null"}` 分支）：Java 里没有「只有 null 的类型」
        if (nonNull.Length == 0)
        {
            return "Object";
        }

        var primary = nonNull[0];

        return primary switch
        {
            "string" => "String",
            "boolean" => "Boolean",
            "integer" => Schemas.Format(map) == "int64" ? "Long" : "Integer",
            "number" => "Double",
            "array" => $"List<{Java(map["items"])}>",
            "object" => JavaObject(map),
            _ => "Object",
        };
    }

    /// <summary>数组元素是联合类型时必须加括号：<c>(number | null)[]</c>。</summary>
    private static string ArrayTypeScript(JsonNode? items)
    {
        var element = TypeScript(items);

        return element.Contains(" | ", StringComparison.Ordinal) ? $"({element})[]" : $"{element}[]";
    }

    /// <summary>字典（<c>additionalProperties</c>）与裸对象的表达。</summary>
    private static string ObjectTypeScript(JsonObject map)
    {
        if (map["additionalProperties"] is { } additional)
        {
            return $"{{ [key: string]: {TypeScript(additional)} }}";
        }

        if (map["properties"] is JsonObject properties && properties.Count > 0)
        {
            var required = RequiredNames(map);

            var members = properties.Select(pair =>
                $"{PropertyName(pair.Key)}{(required.Contains(pair.Key) ? string.Empty : "?")}: {TypeScript(pair.Value)};");

            return $"{{ {string.Join(' ', members)} }}";
        }

        return "Record<string, unknown>";
    }

    private static string JavaObject(JsonObject map)
    {
        if (map["additionalProperties"] is { } additional)
        {
            return $"Map<String, {Java(additional)}>";
        }

        return "Map<String, Object>";
    }

    private static string JavaUnion(JsonArray branches)
    {
        var types = branches
            .Select(Java)
            .Where(type => !string.Equals(type, "Object", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return types.Length switch
        {
            0 => "Object",
            1 => types[0],

            // 真正的联合类型在 Java 里没有对应物：退到 Object，由调用方按需转型
            _ => "Object",
        };
    }

    private static string Union(JsonArray branches) =>
        string.Join(
            " | ",
            branches
                .Select(TypeScript)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(type => string.Equals(type, "null", StringComparison.Ordinal) ? 1 : 0));

    /// <summary>枚举取值的联合类型：<c>"A" | "B"</c>。</summary>
    public static string Union(IReadOnlyList<string> values) =>
        string.Join(" | ", values.Select(value => $"\"{value}\""));

    private static void Add(List<string> parts, string type)
    {
        if (!parts.Contains(type, StringComparer.Ordinal))
        {
            parts.Add(type);
        }
    }

    /// <summary>TS 属性名：非法标识符要加引号（<c>"a-b"?: string</c>）。</summary>
    public static string PropertyName(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_' || name[0] == '$')
        && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '$')
            ? name
            : $"\"{name}\"";

    /// <summary>schema 的 <c>required</c> 集合。</summary>
    public static HashSet<string> RequiredNames(JsonObject map) =>
        map["required"] is JsonArray required
            ? [.. required.OfType<JsonValue>().Select(item => item.GetValue<string>())]
            : [];

    /// <summary>把一个字符串写成 Java 字符串字面量（转义引号与反斜杠）。</summary>
    public static string JavaStringLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>生成出来的一个文件（相对仓库根 + 内容）。</summary>
public sealed record GeneratedFile(string RelativePath, string Content)
{
    /// <summary>绝对路径。</summary>
    public string Resolve(string repositoryRoot) => Path.Combine(repositoryRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>文件落盘与文本规整（行尾、缩进常量都收在这里）。</summary>
public static class GeneratedText
{
    /// <summary>生成文件统一用 CRLF（.editorconfig 的 <c>end_of_line = crlf</c>），且不带 BOM。</summary>
    public static string Normalize(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>写文件（自动建目录）；内容与磁盘完全一致时不动文件，避免无意义的时间戳变化。</summary>
    public static bool WriteIfChanged(string path, string content)
    {
        var normalized = Normalize(content);

        if (File.Exists(path))
        {
            var existing = Normalize(File.ReadAllText(path));

            if (string.Equals(existing, normalized, StringComparison.Ordinal))
            {
                return false;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return true;
    }
}

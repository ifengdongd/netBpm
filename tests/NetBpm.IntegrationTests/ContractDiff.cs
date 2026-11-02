using System.Text.Json.Nodes;

namespace NetBpm.IntegrationTests;

/// <summary>一处契约变更的严重程度。</summary>
internal enum ContractChangeSeverity
{
    /// <summary>破坏性：已接入的调用方会因此失败（字段消失、由可选取必填、类型改变）。</summary>
    Breaking = 0,

    /// <summary>兼容：老调用方不受影响（新增端点、新增可选字段、新增响应码）。</summary>
    Compatible = 1,
}

/// <summary>一处契约变更。</summary>
/// <param name="Severity">严重程度。</param>
/// <param name="Location">位置（形如 <c>GET /workflow/v1/instances/{id}</c> 或 <c>components.schemas.InstanceView.properties.title</c>）。</param>
/// <param name="Detail">说明（给人看的那句话）。</param>
internal sealed record ContractChange(
    ContractChangeSeverity Severity,
    string Location,
    string Detail)
{
    /// <summary>报告中的一行。</summary>
    public override string ToString() =>
        $"[{(Severity == ContractChangeSeverity.Breaking ? "破坏性" : "兼容")}] {Location}：{Detail}";
}

/// <summary>
/// OpenAPI 文档的结构化比对：把两份文档的差异分成「破坏性」与「兼容」两类。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能直接 diff 文本</b>：生成器会调整键序、加可选字段、改描述；
/// 文本 diff 每次都要人工判断「这一行算不算破坏兼容」，判断几次之后就不再有人看了。
/// 契约治理要的是那<b>一句话</b>：这次改动会不会让已接入的系统挂掉。
/// </para>
/// <para>
/// 判定口径（与 <c>docs/platform-api-refactoring-plan.md</c> §7.2 的兼容策略一致）：
/// <list type="bullet">
///   <item><description>新增路径 / 方法 / 响应码 / 可选字段 → 兼容；</description></item>
///   <item><description>删除路径 / 方法 / 响应码 / 响应字段 → 破坏；</description></item>
///   <item><description>请求体字段由可选变必填、或直接删掉请求字段 → 破坏；</description></item>
///   <item><description>类型改变、枚举取值减少 → 破坏。</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class OpenApiContractDiff
{
    /// <summary>比对两份文档，返回差异清单（空表示契约未变）。</summary>
    /// <param name="committed">快照（已提交的契约）。</param>
    /// <param name="current">当前实现生成的文档。</param>
    public static IReadOnlyList<ContractChange> Compare(JsonNode? committed, JsonNode? current)
    {
        var changes = new List<ContractChange>();

        CompareObject(
            AsObject(committed)?["paths"],
            AsObject(current)?["paths"],
            "paths",
            new ComparisonContext(changes, SchemaDirection.Response, IsPathLevel: true));

        // 共享组件：只比两边都有的（新增组件在没被引用前不影响任何调用方）
        CompareComponents(AsObject(committed)?["components"], AsObject(current)?["components"], changes);

        return changes;
    }

    private static void CompareComponents(JsonNode? committed, JsonNode? current, List<ContractChange> changes)
    {
        var oldSchemas = AsObject(committed)?["schemas"];
        var newSchemas = AsObject(current)?["schemas"];

        if (oldSchemas is not JsonObject oldMap || newSchemas is not JsonObject newMap)
        {
            return;
        }

        foreach (var (name, oldSchema) in oldMap)
        {
            if (newMap[name] is not { } newSchema)
            {
                // 组件消失：只有当它还被 $ref 引用时才是破坏（引用点会各自报一次）
                continue;
            }

            CompareSchema(
                oldSchema,
                newSchema,
                $"components.schemas.{name}",
                SchemaDirection.Response,
                changes);
        }
    }

    /// <summary>路径级比对：<c>paths</c> 的一层是路径，下一层是 HTTP 方法。</summary>
    private static void CompareObject(
        JsonNode? committed,
        JsonNode? current,
        string location,
        ComparisonContext context)
    {
        var oldMap = AsObject(committed);
        var newMap = AsObject(current);

        if (oldMap is null && newMap is null)
        {
            return;
        }

        oldMap ??= [];
        newMap ??= [];

        foreach (var (key, oldValue) in oldMap)
        {
            if (!newMap.TryGetPropertyValue(key, out var newValue))
            {
                context.Changes.Add(new ContractChange(
                    ContractChangeSeverity.Breaking,
                    location == "paths" ? key : $"{location}/{key}",
                    "已移除（调用方会拿到 404）"));

                continue;
            }

            if (context.IsPathLevel && IsHttpMethod(key))
            {
                CompareOperation(oldValue, newValue, $"{key} {LocationOf(location, key)}", context);
            }
            else if (context.IsPathLevel)
            {
                // 非方法键（如 path 级的 parameters / servers）：按键继续往下走
                CompareObject(oldValue, newValue, $"{location}/{key}", context with { IsPathLevel = false });
            }
        }

        foreach (var (key, _) in newMap)
        {
            if (!oldMap.ContainsKey(key))
            {
                context.Changes.Add(new ContractChange(
                    ContractChangeSeverity.Compatible,
                    location == "paths" ? key : $"{location}/{key}",
                    "新增"));
            }
        }
    }

    private static void CompareOperation(
        JsonNode? committed,
        JsonNode? current,
        string location,
        ComparisonContext context)
    {
        var oldOperation = AsObject(committed);
        var newOperation = AsObject(current);

        if (oldOperation is null || newOperation is null)
        {
            return;
        }

        CompareParameters(oldOperation["parameters"], newOperation["parameters"], location, context.Changes);
        CompareRequestBody(oldOperation["requestBody"], newOperation["requestBody"], location, context.Changes);
        CompareResponses(oldOperation["responses"], newOperation["responses"], location, context.Changes);
    }

    private static void CompareParameters(
        JsonNode? committed,
        JsonNode? current,
        string location,
        List<ContractChange> changes)
    {
        if (committed is not JsonArray oldArray || current is not JsonArray newArray)
        {
            return;
        }

        var newByName = IndexParameters(newArray);

        foreach (var oldParameter in oldArray)
        {
            var key = ParameterKey(oldParameter);

            if (!newByName.TryGetValue(key, out var newParameter))
            {
                changes.Add(new ContractChange(
                    ContractChangeSeverity.Breaking,
                    location,
                    $"参数 '{key}' 已移除（还在传它的调用方会被拒绝）"));

                continue;
            }

            var wasRequired = ReadBool(oldParameter, "required");
            var isRequired = ReadBool(newParameter, "required");

            if (!wasRequired && isRequired)
            {
                changes.Add(new ContractChange(
                    ContractChangeSeverity.Breaking,
                    location,
                    $"参数 '{key}' 由可选变为必填"));
            }

            CompareSchema(
                AsObject(oldParameter)?["schema"],
                AsObject(newParameter)?["schema"],
                $"{location} 参数 {key}",
                SchemaDirection.Request,
                changes);
        }

        var oldKeys = oldArray.Select(ParameterKey).ToHashSet(StringComparer.Ordinal);

        foreach (var newParameter in newArray)
        {
            var key = ParameterKey(newParameter);

            if (oldKeys.Contains(key))
            {
                continue;
            }

            var required = ReadBool(newParameter, "required");

            changes.Add(new ContractChange(
                required ? ContractChangeSeverity.Breaking : ContractChangeSeverity.Compatible,
                location,
                required ? $"新增必填参数 '{key}'" : $"新增可选参数 '{key}'"));
        }
    }

    private static void CompareRequestBody(
        JsonNode? committed,
        JsonNode? current,
        string location,
        List<ContractChange> changes)
    {
        var oldBody = AsObject(committed);
        var newBody = AsObject(current);

        if (oldBody is null && newBody is null)
        {
            return;
        }

        if (oldBody is not null && newBody is null)
        {
            changes.Add(new ContractChange(
                ContractChangeSeverity.Compatible,
                location,
                "请求体已移除（以前要求的字段现在不再需要）"));

            return;
        }

        if (oldBody is null || newBody is null)
        {
            return;
        }

        if (!ReadBool(oldBody, "required") && ReadBool(newBody, "required"))
        {
            changes.Add(new ContractChange(
                ContractChangeSeverity.Breaking,
                location,
                "请求体由可选变为必填"));
        }

        CompareSchema(
            JsonSchemaOf(oldBody),
            JsonSchemaOf(newBody),
            $"{location} 请求体",
            SchemaDirection.Request,
            changes);
    }

    private static void CompareResponses(
        JsonNode? committed,
        JsonNode? current,
        string location,
        List<ContractChange> changes)
    {
        var oldResponses = AsObject(committed);
        var newResponses = AsObject(current);

        if (oldResponses is null || newResponses is null)
        {
            return;
        }

        foreach (var (status, oldResponse) in oldResponses)
        {
            if (!newResponses.TryGetPropertyValue(status, out var newResponse))
            {
                changes.Add(new ContractChange(
                    ContractChangeSeverity.Breaking,
                    location,
                    $"响应码 {status} 不再声明（调用方按它写分支会失配）"));

                continue;
            }

            CompareSchema(
                JsonSchemaOf(AsObject(oldResponse)),
                JsonSchemaOf(AsObject(newResponse)),
                $"{location} {status}",
                SchemaDirection.Response,
                changes);
        }

        foreach (var (status, _) in newResponses)
        {
            if (!oldResponses.ContainsKey(status))
            {
                changes.Add(new ContractChange(
                    ContractChangeSeverity.Compatible,
                    location,
                    $"新增响应码 {status}"));
            }
        }
    }

    /// <summary>
    /// 比对一个 schema。方向决定「新增 / 删除字段」算哪一类：
    /// 响应少一个字段是破坏（读者会拿到 null），请求少一个字段同样是破坏（调用方还在传）。
    /// </summary>
    private static void CompareSchema(
        JsonNode? committed,
        JsonNode? current,
        string location,
        SchemaDirection direction,
        List<ContractChange> changes)
    {
        if (committed is null && current is null)
        {
            return;
        }

        if (committed is null || current is null)
        {
            // 一侧没有 schema（如 oneOf 对齐不上）：只能报「变了」，无法细说
            changes.Add(new ContractChange(
                ContractChangeSeverity.Breaking,
                location,
                "schema 从「有」变成「无」或反之"));

            return;
        }

        var oldSchema = AsObject(committed);
        var newSchema = AsObject(current);

        if (oldSchema is null || newSchema is null)
        {
            return;
        }

        CompareType(oldSchema, newSchema, location, changes);
        CompareRef(oldSchema, newSchema, location, changes);
        CompareEnum(oldSchema, newSchema, location, changes);
        CompareProperties(oldSchema, newSchema, location, direction, changes);
        CompareRequired(oldSchema, newSchema, location, changes);

        // 数组项：元素 schema 变了同样可能破坏调用方解析
        if (oldSchema["items"] is not null && newSchema["items"] is not null)
        {
            CompareSchema(oldSchema["items"], newSchema["items"], location + "[]", direction, changes);
        }
    }

    private static void CompareType(
        JsonObject committed,
        JsonObject current,
        string location,
        List<ContractChange> changes)
    {
        var oldType = ReadType(committed);
        var newType = ReadType(current);

        if (oldType is null || newType is null)
        {
            // 没写 type（$ref / anyOf）：交给 $ref 与 components 比对去处理
            return;
        }

        if (!string.Equals(oldType, newType, StringComparison.Ordinal))
        {
            changes.Add(new ContractChange(
                ContractChangeSeverity.Breaking,
                location,
                $"类型由 {oldType} 变为 {newType}"));
        }
    }

    private static void CompareRef(
        JsonObject committed,
        JsonObject current,
        string location,
        List<ContractChange> changes)
    {
        var oldRef = ReadString(committed, "$ref");
        var newRef = ReadString(current, "$ref");

        if (oldRef is null || newRef is null)
        {
            return;
        }

        if (!string.Equals(oldRef, newRef, StringComparison.Ordinal))
        {
            changes.Add(new ContractChange(
                ContractChangeSeverity.Breaking,
                location,
                $"引用由 {oldRef} 变为 {newRef}（结构可能整体不同）"));
        }
    }

    private static void CompareEnum(
        JsonObject committed,
        JsonObject current,
        string location,
        List<ContractChange> changes)
    {
        if (committed["enum"] is not JsonArray oldEnum || current["enum"] is not JsonArray newEnum)
        {
            return;
        }

        var newValues = newEnum.Select(ValueText).ToHashSet(StringComparer.Ordinal);

        foreach (var oldValue in oldEnum)
        {
            var text = ValueText(oldValue);

            if (!newValues.Contains(text))
            {
                changes.Add(new ContractChange(
                    ContractChangeSeverity.Breaking,
                    location,
                    $"枚举取值 '{text}' 已移除（调用方按它写的分支会失配）"));
            }
        }

        var oldValues = oldEnum.Select(ValueText).ToHashSet(StringComparer.Ordinal);

        foreach (var newValue in newEnum)
        {
            var text = ValueText(newValue);

            if (!oldValues.Contains(text))
            {
                changes.Add(new ContractChange(
                    ContractChangeSeverity.Compatible,
                    location,
                    $"新增枚举取值 '{text}'（客户端须容忍未知值）"));
            }
        }
    }

    private static void CompareProperties(
        JsonObject committed,
        JsonObject current,
        string location,
        SchemaDirection direction,
        List<ContractChange> changes)
    {
        var oldProperties = AsObject(committed["properties"]);
        var newProperties = AsObject(current["properties"]);

        if (oldProperties is null && newProperties is null)
        {
            return;
        }

        oldProperties ??= [];
        newProperties ??= [];

        var newRequired = ReadStringSet(current["required"]);

        foreach (var (name, oldProperty) in oldProperties)
        {
            if (!newProperties.TryGetPropertyValue(name, out var newProperty))
            {
                // 响应少字段 = 读者拿到 null；请求少字段 = 调用方还在传，两者都算破坏
                changes.Add(new ContractChange(
                    ContractChangeSeverity.Breaking,
                    $"{location}.{name}",
                    direction == SchemaDirection.Response
                        ? "响应字段已移除（调用方会拿到 null）"
                        : "请求字段已移除（还在传它的调用方会被拒绝）"));

                continue;
            }

            CompareSchema(oldProperty, newProperty, $"{location}.{name}", direction, changes);
        }

        foreach (var (name, _) in newProperties)
        {
            if (oldProperties.ContainsKey(name))
            {
                continue;
            }

            var breaking = direction == SchemaDirection.Request && newRequired.Contains(name);

            changes.Add(new ContractChange(
                breaking ? ContractChangeSeverity.Breaking : ContractChangeSeverity.Compatible,
                $"{location}.{name}",
                breaking ? "新增必填请求字段" : "新增字段（可选）"));
        }
    }

    private static void CompareRequired(
        JsonObject committed,
        JsonObject current,
        string location,
        List<ContractChange> changes)
    {
        var oldRequired = ReadStringSet(committed["required"]);
        var newRequired = ReadStringSet(current["required"]);

        foreach (var name in newRequired)
        {
            if (!oldRequired.Contains(name))
            {
                changes.Add(new ContractChange(
                    ContractChangeSeverity.Breaking,
                    $"{location}.{name}",
                    "由可选变为必填"));
            }
        }
    }

    // ─────────────────────────── 小工具 ───────────────────────────

    /// <summary>比对方向：请求体里「新增字段」可能是破坏（若同时是必填），响应体里「删除字段」一定是破坏。</summary>
    private enum SchemaDirection
    {
        Request,
        Response,
    }

    private sealed record ComparisonContext(
        List<ContractChange> Changes,
        SchemaDirection Direction,
        bool IsPathLevel);

    private static bool IsHttpMethod(string name) =>
        name is "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "trace";

    private static string LocationOf(string location, string key) =>
        location == "paths" ? key : $"{location}/{key}";

    private static JsonObject? AsObject(JsonNode? node) => node as JsonObject;

    private static JsonNode? JsonSchemaOf(JsonObject? container) =>
        AsObject(container?["content"])?["application/json"] is JsonObject media
            ? media["schema"]
            : null;

    private static Dictionary<string, JsonNode?> IndexParameters(JsonArray parameters)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        foreach (var parameter in parameters)
        {
            result[ParameterKey(parameter)] = parameter;
        }

        return result;
    }

    private static string ParameterKey(JsonNode? parameter) =>
        $"{ReadString(AsObject(parameter), "in") ?? "?"}:{ReadString(AsObject(parameter), "name") ?? "?"}";

    private static bool ReadBool(JsonNode? node, string property) =>
        AsObject(node)?[property] is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static string? ReadString(JsonObject? node, string property) =>
        node?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? ReadType(JsonObject schema) =>
        ReadString(schema, "type") ?? (schema["type"] is JsonArray array && array.Count > 0
            ? ValueText(array[0])
            : null);

    private static HashSet<string> ReadStringSet(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return [.. array.Select(ValueText)];
    }

    private static string ValueText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node?.ToJsonString() ?? "null";
}

using System.Text.Json.Nodes;

namespace NetBpm.IntegrationTests;

/// <summary>
/// OpenAPI 3.1 子集校验器：判断一个响应体是否仍然符合<b>已发布契约</b>声明的形状。
/// </summary>
/// <remarks>
/// <para>
/// 为什么自己写而不引 <c>JsonSchema.Net</c>：本仓的测试不引新依赖，而这个子集只服务一件事——
/// 校验 <c>contracts/openapi/workflow-v1.json</c> 里出现过的那些结构。
/// 快照由 <c>System.Text.Json</c> 的 schema 导出器生成，用到的关键词是有限且稳定的：
/// <c>$ref</c> / <c>type</c> / <c>properties</c> / <c>required</c> / <c>items</c> /
/// <c>enum</c> / <c>oneOf</c> / <c>anyOf</c> / <c>allOf</c>。
/// </para>
/// <para>
/// 不追求「规范完备」：遇到不认识的键名<b>直接放过</b>。这条策略是刻意的——
/// 校验器的价值在于「发现契约被破坏」，而不是「证明自己实现了完整规范」；
/// 一个因为漏实现某个关键词而误报的校验器，会很快被人关掉。
/// </para>
/// </remarks>
internal static class JsonSchemaValidator
{
    /// <summary>校验一个实例是否符合 schema，返回违规说明（空表示通过）。</summary>
    /// <param name="root">整个 OpenAPI 文档（用于解 <c>$ref</c>）。</param>
    /// <param name="schema">要校验的 schema。</param>
    /// <param name="instance">实际值。</param>
    /// <param name="path">当前路径（拼进违规说明，便于定位）。</param>
    public static IReadOnlyList<string> Validate(
        JsonObject root,
        JsonNode? schema,
        JsonNode? instance,
        string path)
    {
        ArgumentNullException.ThrowIfNull(root);

        var violations = new List<string>();

        Walk(root, schema, instance, path, violations);

        return violations;
    }

    private static void Walk(
        JsonObject root,
        JsonNode? schema,
        JsonNode? instance,
        string path,
        List<string> violations)
    {
        if (schema is not JsonObject shape)
        {
            // true / 空 schema：不约束
            return;
        }

        // $ref 优先：它指向的才是真约束
        if (shape["$ref"] is JsonValue reference
            && reference.TryGetValue<string>(out var pointer)
            && Resolve(root, pointer) is { } target)
        {
            Walk(root, target, instance, path, violations);

            return;
        }

        if (shape["allOf"] is JsonArray allOf)
        {
            foreach (var part in allOf)
            {
                Walk(root, part, instance, path, violations);
            }
        }

        // oneOf / anyOf：只要有一支通过就算通过；全不通过才报（把每支的说明拼起来）
        foreach (var keyword in (string[])["oneOf", "anyOf"])
        {
            if (shape[keyword] is not JsonArray branches)
            {
                continue;
            }

            var attempts = new List<IReadOnlyList<string>>(branches.Count);

            foreach (var branch in branches)
            {
                var branchViolations = new List<string>();

                Walk(root, branch, instance, path, branchViolations);
                attempts.Add(branchViolations);
            }

            if (attempts.Exists(item => item.Count == 0))
            {
                continue;
            }

            violations.Add(
                $"{path}: 不匹配 {keyword} 的任何一支（{string.Join(" | ", attempts.Select(item => item[0]))}）");
        }

        if (shape["enum"] is JsonArray enumValues
            && !enumValues.Any(value => JsonNode.DeepEquals(Normalize(value), Normalize(instance))))
        {
            violations.Add($"{path}: 取值 '{instance?.ToJsonString()}' 不在枚举内");
        }

        CheckType(shape["type"], instance, path, violations);
        CheckObject(root, shape, instance, path, violations);
        CheckArray(root, shape, instance, path, violations);
    }

    private static void CheckType(JsonNode? typeNode, JsonNode? instance, string path, List<string> violations)
    {
        if (typeNode is null)
        {
            return;
        }

        // 3.1 允许 "type": ["integer", "string"]（本仓 long 就是这么导出的）
        var allowed = typeNode switch
        {
            JsonValue single when single.TryGetValue<string>(out var text) => [text],
            JsonArray array => array.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray(),
            _ => [],
        };

        if (allowed.Length == 0 || allowed.Any(expected => Matches(expected, instance)))
        {
            return;
        }

        violations.Add(
            $"{path}: 类型应为 [{string.Join(", ", allowed)}]，实际为 {Describe(instance)}");
    }

    private static bool Matches(string expected, JsonNode? instance)
    {
        if (expected == "null")
        {
            return instance is null;
        }

        if (instance is null)
        {
            return false;
        }

        return expected switch
        {
            "object" => instance is JsonObject,
            "array" => instance is JsonArray,
            "string" => instance is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String,
            "boolean" => instance is JsonValue flag && flag.GetValueKind() is System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False,
            "integer" => instance is JsonValue number && IsInteger(number),
            "number" => instance is JsonValue numeric && numeric.GetValueKind() == System.Text.Json.JsonValueKind.Number,
            _ => true,
        };
    }

    private static bool IsInteger(JsonValue value)
    {
        if (value.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            // long 允许从字符串读（JsonNumberHandling.AllowReadingFromString）
            return long.TryParse(value.GetValue<string>(), out _);
        }

        if (value.GetValueKind() != System.Text.Json.JsonValueKind.Number)
        {
            return false;
        }

        return long.TryParse(value.ToJsonString(), out _);
    }

    private static void CheckObject(
        JsonObject root,
        JsonObject shape,
        JsonNode? instance,
        string path,
        List<string> violations)
    {
        if (instance is not JsonObject actual)
        {
            return;
        }

        if (shape["required"] is JsonArray required)
        {
            foreach (var name in required.Select(item => item?.GetValue<string>()).OfType<string>())
            {
                if (!actual.ContainsKey(name))
                {
                    violations.Add($"{path}: 缺少必填字段 '{name}'");
                }
            }
        }

        if (shape["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var (name, propertySchema) in properties)
        {
            if (actual.TryGetPropertyValue(name, out var value))
            {
                Walk(root, propertySchema, value, path + "." + name, violations);
            }
        }
    }

    private static void CheckArray(
        JsonObject root,
        JsonObject shape,
        JsonNode? instance,
        string path,
        List<string> violations)
    {
        if (instance is not JsonArray actual || shape["items"] is not { } items)
        {
            return;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            Walk(root, items, actual[index], $"{path}[{index}]", violations);
        }
    }

    /// <summary>解 <c>#/components/schemas/Name</c> 这类本地引用。</summary>
    private static JsonNode? Resolve(JsonObject root, string pointer)
    {
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonNode? current = root;

        foreach (var segment in pointer[2..].Split('/'))
        {
            // JSON Pointer 转义：~1 → '/'，~0 → '~'
            var name = segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            current = current is JsonObject map ? map[name] : null;

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static JsonNode? Normalize(JsonNode? value) => value?.DeepClone();

    private static string Describe(JsonNode? instance) => instance switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue value => value.GetValueKind().ToString().ToLowerInvariant(),
        _ => instance.GetType().Name,
    };
}

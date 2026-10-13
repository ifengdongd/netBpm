using System.Globalization;
using System.Text.Json;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Tasks;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 表单映射结果。<see cref="Errors"/> 非空表示提交不合法，调用方应返回 400 且<b>不要</b>推进流程。
/// </summary>
/// <param name="Variables">映射后的流程变量（键 = 目标变量名）。</param>
/// <param name="Errors">校验错误。</param>
public sealed record WorkflowFormMapping(
    Dictionary<string, object?> Variables,
    IReadOnlyList<string> Errors)
{
    /// <summary>是否通过校验。</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// 表单映射：把「流程定义里的 <c>activiti:formProperty</c>」变成可用的表单契约与流程变量。
/// </summary>
/// <remarks>
/// <para>
/// 这是「审批选项不写在前端、也不写在 C# 里」的关键一环：<b>选项的真身是 BPMN 的
/// <c>activiti:formProperty</c></b>，改选项、加字段只改流程定义、不动代码。
/// </para>
/// <para>
/// ⚠️ 引擎本身对 formProperty 是「只透传不执行」（见 <see cref="FormProperty"/> 的类注释），
/// 所以「表单值 → 流程变量」这一步必须由宿主补上，这里就是那份实现：
/// 目标变量名取 <c>variable</c>（未写则用 <c>id</c>），类型按 <c>type</c> 转换。
/// 对 <c>approvalResult</c> 而言，<c>variable="result"</c> 正是「result 来自 approvalResult」的落点。
/// </para>
/// </remarks>
public static class WorkflowFormMapper
{
    /// <summary>按任务找到流程定义里对应的 <c>userTask</c>（取表单属性 / 候选关系用）。</summary>
    public static async ValueTask<UserTask?> FindUserTaskAsync(
        IProcessEngine engine,
        TaskEntity task,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(task);

        if (string.IsNullOrWhiteSpace(task.ProcessDefinitionId)
            || string.IsNullOrWhiteSpace(task.TaskDefinitionKey))
        {
            return null;
        }

        var model = await engine.GetBpmnModelAsync(task.ProcessDefinitionId, ct).ConfigureAwait(false);
        var process = model?.GetMainProcess();

        return process?
            .FindFlowElementsOfType<UserTask>()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, task.TaskDefinitionKey, StringComparison.Ordinal));
    }

    /// <summary>构造任务视图（表单属性连同 enum 选项一起下发）。</summary>
    public static WorkflowTaskView ToView(TaskEntity task, UserTask? definition)
    {
        ArgumentNullException.ThrowIfNull(task);

        var form = (definition?.FormProperties ?? [])
            .Select(property => new WorkflowFormProperty(
                Id: property.Id ?? string.Empty,
                Name: property.Name,
                Type: property.Type,
                Required: property.Required ?? false,
                Variable: property.Variable,
                Default: property.Default,
                Options: [.. property.Values.Select(value => new WorkflowFormOption(value.Id ?? string.Empty, value.Name))]))
            .ToArray();

        return new WorkflowTaskView(
            TaskId: task.Id,
            Name: task.Name,
            DefinitionKey: task.TaskDefinitionKey,
            ProcessInstanceId: task.ProcessInstanceId,
            FormKey: task.FormKey,
            Assignee: task.Assignee,
            CandidateUsers: [.. definition?.CandidateUsers ?? []],
            CandidateGroups: [.. definition?.CandidateGroups ?? []],
            CreateTime: task.CreateTime,
            Form: form);
    }

    /// <summary>
    /// 把提交的表单值按 <c>formProperty</c> 的 <c>variable</c> / <c>type</c> 映射成流程变量。
    /// </summary>
    /// <remarks>
    /// 刻意<b>严格</b>：未声明的字段、非法枚举值、缺必填项都直接报错。
    /// 宽松处理只会让「提交错了但流程照样走」这种事故更难查。
    /// </remarks>
    /// <param name="definition">目标用户任务（为空时任何提交都会被判为非法）。</param>
    /// <param name="values">提交的表单值（键 = <c>formProperty/@id</c>）。</param>
    public static WorkflowFormMapping MapToVariables(
        UserTask? definition,
        IDictionary<string, JsonElement>? values)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new List<string>();

        var properties = definition?.FormProperties ?? [];
        var submitted = values ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var name in submitted.Keys)
        {
            if (!properties.Any(property => string.Equals(property.Id, name, StringComparison.Ordinal)))
            {
                errors.Add(
                    $"表单字段 '{name}' 不在流程定义的表单属性里（可用：{string.Join(", ", properties.Select(p => p.Id))}）。");
            }
        }

        foreach (var property in properties)
        {
            var id = property.Id ?? string.Empty;
            var target = TargetVariableName(property);

            var hasValue = submitted.TryGetValue(id, out var element)
                && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

            if (!hasValue)
            {
                if (property.Required ?? false)
                {
                    errors.Add($"表单字段 '{id}'（{property.Name ?? id}）为必填。");
                }
                else if (!string.IsNullOrEmpty(property.Default))
                {
                    // 默认值统一当字符串处理，复用同一套类型转换 / 选项校验
                    var (ok, value) = Coerce(
                        property, JsonSerializer.SerializeToElement(property.Default), errors, useDefault: true);

                    if (ok)
                    {
                        variables[target] = value;
                    }
                }

                continue;
            }

            var (converted, convertedValue) = Coerce(property, element, errors, useDefault: false);
            if (converted)
            {
                variables[target] = convertedValue;
            }
        }

        return new WorkflowFormMapping(variables, errors);
    }

    /// <summary>变量名：<c>variable</c> 优先，未写则与 <c>id</c> 同名。</summary>
    public static string TargetVariableName(FormProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return string.IsNullOrWhiteSpace(property.Variable) ? property.Id ?? string.Empty : property.Variable;
    }

    /// <summary>
    /// 取某个 enum 表单属性里某个选项的显示名（审批记录里的「通过 / 拒绝」就来自这里）。
    /// </summary>
    /// <param name="definition">目标用户任务。</param>
    /// <param name="formPropertyId">表单属性 id（审批结论用 <see cref="ApprovalResults.FormPropertyId"/>）。</param>
    /// <param name="optionId">选项 id。</param>
    /// <returns>显示名；属性或选项不存在时返回 <see langword="null"/>。</returns>
    public static string? FindOptionName(UserTask? definition, string formPropertyId, string? optionId)
    {
        if (definition is null || string.IsNullOrWhiteSpace(optionId))
        {
            return null;
        }

        return definition.FormProperties
            .FirstOrDefault(property => string.Equals(property.Id, formPropertyId, StringComparison.Ordinal))?
            .Values
            .FirstOrDefault(value => string.Equals(value.Id, optionId, StringComparison.Ordinal))?
            .Name;
    }

    private static (bool Ok, object? Value) Coerce(
        FormProperty property,
        JsonElement value,
        List<string> errors,
        bool useDefault)
    {
        var id = property.Id ?? string.Empty;
        var type = (property.Type ?? "string").ToUpperInvariant();
        var actual = useDefault ? "默认值" : Describe(value);

        switch (type)
        {
            case "LONG" or "INTEGER" or "INT" or "SHORT":
                if (TryInt64(value, out var integer))
                {
                    return (true, integer);
                }

                errors.Add($"表单字段 '{id}' 需要整数，实际收到 {actual}。");
                return (false, null);

            case "DOUBLE" or "FLOAT" or "NUMBER":
                if (TryDouble(value, out var number))
                {
                    return (true, number);
                }

                errors.Add($"表单字段 '{id}' 需要数字，实际收到 {actual}。");
                return (false, null);

            case "BOOLEAN":
                if (TryBoolean(value, out var flag))
                {
                    return (true, flag);
                }

                errors.Add($"表单字段 '{id}' 需要布尔值，实际收到 {actual}。");
                return (false, null);

            case "DATE":
                if (TryDate(property, value, out var moment))
                {
                    return (true, moment);
                }

                errors.Add($"表单字段 '{id}' 需要日期（格式 {property.DatePattern ?? "ISO-8601"}），实际收到 {actual}。");
                return (false, null);

            case "ENUM":
                var option = AsText(value);
                if (option is not null
                    && property.Values.Any(candidate => string.Equals(candidate.Id, option, StringComparison.Ordinal)))
                {
                    // 与 Activiti 一致：enum 落成「选项 id」字符串
                    return (true, option);
                }

                errors.Add(
                    $"表单字段 '{id}' 的取值 '{option}' 不是合法选项（可用：{string.Join(", ", property.Values.Select(v => v.Id))}）。");
                return (false, null);

            default:
                return (true, AsText(value));
        }
    }

    private static string? AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static bool TryInt64(JsonElement value, out long result)
    {
        result = 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out result),
            JsonValueKind.String => long.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result),
            _ => false,
        };
    }

    private static bool TryDouble(JsonElement value, out double result)
    {
        result = 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out result),
            JsonValueKind.String => double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result),
            _ => false,
        };
    }

    private static bool TryBoolean(JsonElement value, out bool result)
    {
        result = false;

        return value.ValueKind switch
        {
            JsonValueKind.True => result = true,
            JsonValueKind.False => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out result),
            _ => false,
        };
    }

    private static bool TryDate(FormProperty property, JsonElement value, out DateTime result)
    {
        result = default;

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(property.DatePattern)
            ? DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result)
            : DateTime.TryParseExact(text, property.DatePattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"\"{value.GetString()}\"",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        _ => value.ValueKind.ToString(),
    };
}

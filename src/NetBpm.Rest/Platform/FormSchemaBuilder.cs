using NetBpm.Bpmn.Model;
using NetBpm.Platform.Contracts;
using NetBpm.Workflow;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 把 BPMN 的 <c>activiti:formProperty</c> 派生为平台契约里的 <see cref="FormField"/>。
/// </summary>
/// <remarks>
/// <para>
/// 单独抽出来是因为它有<b>两个</b>使用方：待办详情（<c>GET /tasks/{id}</c> 的 <c>form</c>）
/// 与流程级表单规范（<c>GET /processes/{key}/form/schema</c>）。
/// 两处各写一遍的结局是可预见的——某天一边认 <c>datetime</c> 另一边只认 <c>date</c>，
/// 于是同一个字段在发起页和审批页渲染成两种控件。
/// </para>
/// <para>
/// 审批结论字段（<c>approvalResult</c>）<b>不是表单字段</b>：它是审批按钮的数据源，
/// 走 <see cref="TaskView.Options"/>（由审批语义字典解析）。把它混进表单会让
/// 「下拉框里选一个结论」这种错误引导直接出现在调用方的渲染里。
/// </para>
/// </remarks>
internal static class FormSchemaBuilder
{
    /// <summary>从环节派生表单字段；无字段返回 <see langword="null"/>。</summary>
    /// <param name="userTask">BPMN 用户任务（为空表示取不到定义）。</param>
    public static List<FormField>? BuildFields(UserTask? userTask)
    {
        var properties = userTask?.FormProperties
            .Where(property => !IsApprovalProperty(property))
            .ToList();

        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        var fields = new List<FormField>(properties.Count);

        for (var index = 0; index < properties.Count; index++)
        {
            fields.Add(BuildField(properties[index], index));
        }

        return fields;
    }

    /// <summary>单个表单字段：<c>formProperty</c> → <see cref="FormField"/>。</summary>
    /// <param name="property">BPMN 表单属性。</param>
    /// <param name="sortOrder">渲染顺序（BPMN 里的书写顺序）。</param>
    public static FormField BuildField(FormProperty property, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(property);

        var type = MapFieldType(property.Type);

        IReadOnlyList<FormOption>? options = type == FormFieldType.Enum
            ? property.Values.Select(value => new FormOption(value.Id ?? string.Empty, value.Name)).ToList()
            : null;

        return new FormField(
            FieldId: property.Id ?? string.Empty,
            Label: property.Name,
            Type: type,
            Required: property.Required ?? false,
            ReadOnly: property.Writable == false,
            DefaultValue: property.Default,
            Options: options,
            Placeholder: null,
            SortOrder: sortOrder);
    }

    /// <summary>类型映射：BPMN 的 <c>type</c> 字符串 → <see cref="FormFieldType"/>。</summary>
    /// <param name="type">BPMN 声明的类型名（缺省按字符串）。</param>
    public static FormFieldType MapFieldType(string? type) => (type ?? "string").ToUpperInvariant() switch
    {
        "LONG" or "INTEGER" or "INT" or "SHORT" => FormFieldType.Long,
        "DOUBLE" or "FLOAT" or "NUMBER" => FormFieldType.Decimal,
        "BOOLEAN" => FormFieldType.Boolean,
        "DATE" => FormFieldType.Date,
        "DATETIME" or "DATE_TIME" => FormFieldType.DateTime,
        "ENUM" => FormFieldType.Enum,
        "FILE" => FormFieldType.File,
        "TEXT" or "TEXTAREA" => FormFieldType.Text,
        _ => FormFieldType.String,
    };

    /// <summary>是否是审批结论字段（<c>approvalResult</c>，走 options 不走字段）。</summary>
    /// <param name="property">BPMN 表单属性。</param>
    public static bool IsApprovalProperty(FormProperty property) =>
        property is not null
        && string.Equals(property.Id, ApprovalResults.FormPropertyId, StringComparison.Ordinal);
}

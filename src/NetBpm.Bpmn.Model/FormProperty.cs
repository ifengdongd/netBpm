namespace NetBpm.Bpmn.Model;

/// <summary>
/// 用户任务表单属性（<c>activiti:formProperty</c>，挂在 <c>extensionElements</c> 下）。
/// <para>
/// 与 <see cref="UserTask.FormKey"/> 一样<b>只透传不执行</b>：本类存在的意义是设计器
/// 「保存 → 落库 → 回显」的往返保真 —— 若模型不承载它，解析时会被静默丢弃，
/// 重新序列化落库后数据永久丢失（前端属性面板回显为空）。
/// </para>
/// </summary>
public class FormProperty
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    /// <summary>类型（string / long / date / boolean / enum，见 Activiti 表单属性）。</summary>
    public string? Type { get; set; }

    /// <summary>值表达式（<c>expression</c>）。</summary>
    public string? Expression { get; set; }

    /// <summary>绑定流程变量名（<c>variable</c>）。</summary>
    public string? Variable { get; set; }

    /// <summary>默认值（<c>default</c>，enum 类型用）。</summary>
    public string? Default { get; set; }

    /// <summary>日期格式（<c>datePattern</c>，date 类型用）。</summary>
    public string? DatePattern { get; set; }

    /// <summary>是否必填（<c>required</c>）；未写出的属性为 null。</summary>
    public bool? Required { get; set; }

    /// <summary>是否可读（<c>readable</c>）；未写出的属性为 null。</summary>
    public bool? Readable { get; set; }

    /// <summary>是否可写（<c>writable</c>）；未写出的属性为 null。</summary>
    public bool? Writable { get; set; }

    /// <summary>枚举选项（<c>activiti:value</c> 子元素，仅 type=enum）。</summary>
    public IList<FormPropertyValue> Values { get; } = [];
}

/// <summary>表单属性的枚举选项（<c>activiti:value</c>）。</summary>
public class FormPropertyValue
{
    public string? Id { get; set; }

    public string? Name { get; set; }
}

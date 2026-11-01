using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>表单字段类型。</summary>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<FormFieldType>))]
public enum FormFieldType
{
    /// <summary>单行文本。</summary>
    String = 0,

    /// <summary>多行文本。</summary>
    Text = 1,

    /// <summary>整数。</summary>
    Long = 2,

    /// <summary>小数。</summary>
    Decimal = 3,

    /// <summary>勾选。</summary>
    Boolean = 4,

    /// <summary>日期。</summary>
    Date = 5,

    /// <summary>日期时间。</summary>
    DateTime = 6,

    /// <summary>单选 / 多选（取值受 <see cref="FormField.Options"/> 约束）。</summary>
    Enum = 7,

    /// <summary>附件引用（<b>只存引用，不存文件</b>）。</summary>
    File = 8,
}

/// <summary>表单选项。</summary>
/// <param name="Value">取值（提交时回传）。</param>
/// <param name="Label">显示名。</param>
public sealed record FormOption(string Value, string? Label);

/// <summary>
/// 表单字段定义。
/// </summary>
/// <param name="FieldId">字段 id（提交时作为 <c>formValues</c> 的键）。</param>
/// <param name="Label">显示名。</param>
/// <param name="Type">类型。</param>
/// <param name="Required">是否必填。</param>
/// <param name="ReadOnly">是否只读（历史页回显）。</param>
/// <param name="DefaultValue">默认值。</param>
/// <param name="Options">枚举候选值。</param>
/// <param name="Placeholder">输入提示。</param>
/// <param name="SortOrder">渲染顺序。</param>
public sealed record FormField(
    string FieldId,
    string? Label,
    FormFieldType Type,
    bool Required,
    bool ReadOnly,
    string? DefaultValue,
    IReadOnlyList<FormOption>? Options,
    string? Placeholder,
    int SortOrder);

/// <summary>
/// 表单定义（一个流程 / 一个环节一份）。
/// </summary>
/// <remarks>
/// 平台<b>不做表单引擎</b>：这份 schema 只服务于「提交时校验 + 历史回显」，
/// 不负责布局与样式。真正的表单渲染仍在调用方（或独立的前端表单系统）。
/// </remarks>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="ActivityId">环节 id；为 <see langword="null"/> 表示流程级表单。</param>
/// <param name="Fields">字段列表。</param>
public sealed record FormSchema(
    string ProcessKey,
    string? ActivityId,
    IReadOnlyList<FormField> Fields);

/// <summary>
/// 审批时的表单快照——<b>历史详情页按当时的定义渲染</b>。
/// </summary>
/// <remarks>
/// 现状只透传 <c>activiti:formProperty</c>，不落库：流程定义改版后，旧单据的历史详情
/// 会用新字段去渲染旧值，表现为「字段没了 / 值错位」。
/// 快照在提交时一次性冻住，之后与定义解耦。
/// </remarks>
/// <param name="TaskId">任务 id（也是拉这份快照的钥匙：<c>GET /tasks/{id}/form</c>）。</param>
/// <param name="DefinitionVersion">提交时的定义版本。</param>
/// <param name="Schema">当时的表单定义。</param>
/// <param name="Values">当时的表单值（键 = 字段 id）。</param>
/// <param name="CapturedAt">快照时间。</param>
/// <param name="InstanceId">所属实例 id（时间线里按它串起「哪一屏」）。</param>
/// <param name="ActivityName">环节名（写入时定稿：定义改名不影响历史页）。</param>
/// <param name="ActorId">提交人（真人主体）。</param>
/// <param name="OptionId">本次审批选项 id。</param>
/// <param name="Attachments">当时的附件引用（只存引用，不存文件本体）。</param>
public sealed record FormSnapshot(
    string TaskId,
    int DefinitionVersion,
    FormSchema Schema,
    IReadOnlyDictionary<string, string?> Values,
    DateTimeOffset CapturedAt,
    string? InstanceId = null,
    string? ActivityName = null,
    string? ActorId = null,
    string? OptionId = null,
    IReadOnlyList<AttachmentRef>? Attachments = null);

/// <summary>
/// 流程级表单规范——一个流程定义下<b>所有环节</b>的表单字段（<c>GET /processes/{key}/form/schema</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="FormSnapshot"/> 的分工：快照回答「这一次提交当时填了什么」（历史页），
/// 本契约回答「这个流程会让人填什么」（接入方开发期、发起页预渲染、表单迁移对拍）。
/// </para>
/// <para>
/// 只含<b>审批环节</b>的字段：发起时填的东西走<b>参数</b>（<c>param.*</c>，见
/// <see cref="ParameterSchema"/>），不是 <c>activiti:formProperty</c>。两者的生命周期也不同——
/// 参数随实例走（每次提交产生新 revision），表单随定义走（一次审批一屏）。
/// </para>
/// <para>
/// 也不含审批按钮：审批可选项由审批语义字典按租户 + 环节解析（<see cref="TaskView.Options"/>），
/// 属于语义层不属于表单声明。<c>approvalResult</c> 因此不在这里出现——
/// 把它当字段渲染出来，会诱导调用方做成「下拉框里选结论」。
/// </para>
/// <para>
/// 返回的是<b>定义版本</b>维度的一份：同一个 key 不同版本可能字段不同，
/// 所以带 <see cref="DefinitionVersion"/>，调用方缓存时不能只按 key 缓存。
/// </para>
/// </remarks>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="ProcessDefinitionId">定义 id（含版本指纹）。</param>
/// <param name="DefinitionVersion">定义版本号。</param>
/// <param name="Activities">
/// 各环节的表单（已按 BPMN 书写顺序）。<b>没有字段的环节不出现</b>——
/// 空对象集合会让调用方以为「这些环节有表单但是空的」。
/// </param>
public sealed record ProcessFormSchema(
    string ProcessKey,
    string ProcessDefinitionId,
    int DefinitionVersion,
    IReadOnlyList<FormSchema> Activities);

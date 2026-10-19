using System.Globalization;

namespace NetBpm.Workflow;

/// <summary>
/// 审批结果。取值与流程定义里 <c>activiti:formProperty id="approvalResult"</c> 的枚举选项一一对应。
/// </summary>
/// <remarks>
/// <para>
/// 数值刻意与 BPMN 选项 id 对齐（<c>1</c> / <c>2</c> / <c>3</c>），因此：
/// </para>
/// <list type="number">
///   <item><description>流程变量 <c>result</c> 落成的字符串 <c>"2"</c> 能直接解析回 <see cref="Refused"/>；</description></item>
///   <item><description>网关条件 <c>${result == 2}</c> 照样成立 —— EL 的 <c>==</c> 遇到整型字面量会先把字符串转成长整数。</description></item>
/// </list>
/// <para>
/// ⚠️ <b>改这里的数值等于改流程语义</b>：BPMN 里的选项 id 与判定条件必须同步改，
/// 否则流程会静默走到别的分支（选中即停，不会报错）。漂移由
/// <see cref="ApprovalResultFormGuard"/> 在部署期拦下。
/// </para>
/// </remarks>
public enum ApprovalResult
{
    /// <summary>通过（选项 id <c>1</c>）。</summary>
    Approved = 1,

    /// <summary>拒绝（选项 id <c>2</c>）。</summary>
    Refused = 2,

    /// <summary>驳回修改（选项 id <c>3</c>）。</summary>
    ReturnedForModification = 3,
}

/// <summary>
/// <see cref="ApprovalResult"/> 与「BPMN 选项 id / 中文显示名」之间的转换。
/// </summary>
/// <remarks>
/// 流程定义是审批选项的<b>唯一来源</b>（前端照 <c>activiti:value</c> 渲染，见 <see cref="WorkflowFormMapper"/>），
/// 这里提供的是宿主侧的强类型视图：<c>result</c> 这个流程变量按枚举读，而不是散落的魔术字符串。
/// </remarks>
public static class ApprovalResults
{
    /// <summary>
    /// 流程定义里承载该枚举的表单属性 id（<c>activiti:formProperty/@id</c>）。
    /// <para>
    /// 它是本套件的<b>约定</b>：审批环节的「结论」都放在这个名字的表单字段里，
    /// <see cref="WorkflowService"/> 据此在审批记录里还原「批准 / 拒绝 / 驳回」。
    /// </para>
    /// </summary>
    public const string FormPropertyId = "approvalResult";

    /// <summary>全部取值，顺序与表单里的展示顺序一致。</summary>
    public static IReadOnlyList<ApprovalResult> All { get; } =
        [ApprovalResult.Approved, ApprovalResult.Refused, ApprovalResult.ReturnedForModification];

    /// <summary>
    /// 转 BPMN 选项 id（<c>activiti:value/@id</c>），同时也是流程变量 <c>result</c> 的取值形态。
    /// </summary>
    public static string ToOptionId(this ApprovalResult result) =>
        ((int)result).ToString(CultureInfo.InvariantCulture);

    /// <summary>转中文显示名（<c>activiti:value/@name</c>）。</summary>
    public static string ToDisplayName(this ApprovalResult result) => result switch
    {
        ApprovalResult.Approved => "通过",
        ApprovalResult.Refused => "拒绝",
        ApprovalResult.ReturnedForModification => "驳回修改",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "未知的审批结果。"),
    };

    /// <summary>按 BPMN 选项 id 解析；不是合法选项时返回 <see langword="null"/>。</summary>
    public static ApprovalResult? TryParseOptionId(string? optionId)
    {
        if (string.IsNullOrWhiteSpace(optionId))
        {
            return null;
        }

        return int.TryParse(optionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && Enum.IsDefined((ApprovalResult)number)
                ? (ApprovalResult)number
                : null;
    }

    /// <summary>
    /// 按流程变量值解析（宽容）。
    /// <para>
    /// 变量在不同宿主下可能是字符串 <c>"2"</c>、<c>int 2</c> 或 <c>long 2</c>
    /// （见 <see cref="VariableValues"/> 的说明），这里统一收口。
    /// </para>
    /// </summary>
    public static ApprovalResult? TryParse(object? value) => value switch
    {
        null => null,
        ApprovalResult result => result,
        int number => Enum.IsDefined((ApprovalResult)number) ? (ApprovalResult)number : null,
        long number => Enum.IsDefined((ApprovalResult)(int)number) ? (ApprovalResult)(int)number : null,
        _ => TryParseOptionId(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>严格解析：不是合法审批结果时抛 <see cref="InvalidOperationException"/>。</summary>
    /// <exception cref="InvalidOperationException">取值不是合法的审批结果。</exception>
    public static ApprovalResult Parse(object? value) =>
        TryParse(value) ?? throw new InvalidOperationException(
            $"'{value}' 不是合法的审批结果（可用：{string.Join(" / ", All.Select(item => $"{item.ToOptionId()} {item.ToDisplayName()}"))}）。");
}

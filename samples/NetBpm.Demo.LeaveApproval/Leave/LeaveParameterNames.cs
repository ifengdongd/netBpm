using NetBpm.Workflow;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 请假流程的业务参数名 —— 也就是 <see cref="ParaDict"/> 里那两个键。
/// </summary>
/// <remarks>
/// 与 <see cref="BaseModelVariables"/> 一个道理：让「BPMN 里的表单字段」「网关条件
/// <c>${days &gt; 3}</c>」「业务代码」三处引用同一个常量。要加参数（<c>leaveType</c>、
/// <c>backupUser</c> …）在这里加一行，其余地方不用动。
/// </remarks>
public static class LeaveParameterNames
{
    /// <summary>请假天数。网关条件 <c>${days &gt; 3}</c> 直接引用这个变量。</summary>
    public const string Days = "days";

    /// <summary>请假事由。</summary>
    public const string Reason = "reason";

    /// <summary>从参数表里读天数（读不到按 0）。</summary>
    /// <param name="parameters">流程参数表内容。</param>
    public static int DaysOf(IReadOnlyDictionary<string, string?>? parameters) =>
        VariableValues.AsInt(parameters is not null && parameters.TryGetValue(Days, out var value) ? value : null);

    /// <summary>从参数表里读事由（读不到按 <see langword="null"/>）。</summary>
    /// <param name="parameters">流程参数表内容。</param>
    public static string? ReasonOf(IReadOnlyDictionary<string, string?>? parameters) =>
        parameters is not null && parameters.TryGetValue(Reason, out var value) ? value : null;
}

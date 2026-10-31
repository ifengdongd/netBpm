using NetBpm.Platform.Contracts;

namespace NetBpm.Rest.Platform;

/// <summary>参数写入发生在哪个环节。</summary>
/// <remarks>
/// 只区分两个环节，是因为 <c>writableBy</c> 声明的就是这两类角色：
/// 发起（<c>APPLICANT</c>）与审批（<c>APPROVER</c>）。第三个角色 <c>ADMIN</c> 与环节无关，
/// 它表达的是「管理视角可以越权改」，因此两个环节都认。
/// </remarks>
internal enum ParameterWriteStage
{
    /// <summary>发起实例（<c>POST /instances</c>）。</summary>
    Start = 0,

    /// <summary>提交审批（<c>POST /tasks/{id}/submit</c>）。</summary>
    Approve = 1,
}

/// <summary>
/// 参数写侧授权：按 <see cref="ParameterDefinition.WritableBy"/> 判定「这个环节能不能写这个参数」。
/// </summary>
/// <remarks>
/// <para>
/// 补的是 §4.3 里最后一个缺口：schema 早就能<b>表达</b> <c>writableBy</c>（v1.13），
/// 但写校验一直没按它拒绝——「声明出来了」和「执行了」是两件事，后者才叫契约。
/// </para>
/// <para>
/// 判定规则（与 <see cref="ParameterDefinition.WritableBy"/> 的文档逐字对应）：
/// </para>
/// <list type="bullet">
///   <item><description><b>未声明</b>（<see langword="null"/> 或空列表）：发起时可写、之后只读。
///     这是兼容基线——老 BPMN 不写这个属性，仍然能像以前那样在发起时传参数。</description></item>
///   <item><description><b>声明了</b>：只认列表里出现的角色。发起环节看 <c>APPLICANT</c>，
///     审批环节看 <c>APPROVER</c>；两个环节都额外接受 <c>ADMIN</c>，但仅当调用方确实持管理范围。</description></item>
///   <item><description><c>NONE</c> 或只写了另一个环节的角色：拒绝（<see cref="ErrorCodes.ParameterReadOnly"/>）。</description></item>
///   <item><description><b>认不出的角色名一律忽略</b>：写错一个角色不该让参数对所有人可写，
///     也不该在升级时把老声明整体判死。</description></item>
/// </list>
/// <para>
/// 刻意<b>不</b>按请求体里自述的角色判定（那等于让调用方自己发授权）。
/// </para>
/// </remarks>
internal static class ParameterWritePolicy
{
    /// <summary>申请人角色（发起环节的写入方）。</summary>
    public const string ApplicantRole = "APPLICANT";

    /// <summary>审批人角色（审批环节的写入方）。</summary>
    public const string ApproverRole = "APPROVER";

    /// <summary>管理员角色：两个环节都可越权写入，但必须真的持管理范围。</summary>
    public const string AdminRole = "ADMIN";

    /// <summary>
    /// 在参数规范里找出这次提交的键对应的定义。
    /// </summary>
    /// <remarks>
    /// 三种叫法都要认：对外 <see cref="ParameterDefinition.ApiName"/>（<c>param.days</c>）、
    /// 声明里改写过的 apiName、以及裸变量名（<c>days</c>）。
    /// 「换个名字就能绕过 <c>writableBy</c>」是这类校验最典型的漏法，
    /// 所以查询本身就要把别名算全，而不是在判定之后再去猜。
    /// </remarks>
    /// <param name="definitions">参数规范里的全部定义。</param>
    /// <param name="submittedName">请求里出现的键。</param>
    /// <param name="bareName">剥掉 <c>param.</c> 前缀后的裸名。</param>
    public static ParameterDefinition? Find(
        IReadOnlyList<ParameterDefinition> definitions,
        string submittedName,
        string bareName)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(submittedName);
        ArgumentNullException.ThrowIfNull(bareName);

        foreach (var definition in definitions)
        {
            if (string.Equals(definition.ApiName, submittedName, StringComparison.Ordinal)
                || string.Equals(definition.VariableName, bareName, StringComparison.Ordinal)
                || string.Equals(definition.VariableName, submittedName, StringComparison.Ordinal)
                // 声明了自定义 apiName 时，裸名仍然是同一个变量：两种叫法都落到同一条定义上
                || string.Equals(StripPrefix(definition.ApiName), bareName, StringComparison.Ordinal))
            {
                return definition;
            }
        }

        return null;
    }

    /// <summary>这个参数在本环节是否可写。</summary>
    /// <param name="definition">参数定义。</param>
    /// <param name="stage">环节。</param>
    /// <param name="callerIsAdmin">调用方是否持管理范围（<c>netbpm.admin</c> / <c>workflow:history:read:all</c> 等）。</param>
    public static bool IsWritable(
        ParameterDefinition definition,
        ParameterWriteStage stage,
        bool callerIsAdmin)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var declared = definition.WritableBy;

        if (declared is not { Count: > 0 })
        {
            // 未声明：发起时可写、之后只读（schema 文档的原始约定）
            return stage == ParameterWriteStage.Start;
        }

        if (callerIsAdmin && Contains(declared, AdminRole))
        {
            return true;
        }

        return stage == ParameterWriteStage.Start
            ? Contains(declared, ApplicantRole)
            : Contains(declared, ApproverRole);
    }

    /// <summary>给人看的「谁可以写」，用于 422 的 detail（让调用方一次就知道该找谁改）。</summary>
    /// <param name="definition">参数定义。</param>
    /// <param name="stage">环节。</param>
    public static string DescribeWriters(ParameterDefinition definition, ParameterWriteStage stage)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.WritableBy is not { Count: > 0 }
            ? (stage == ParameterWriteStage.Start ? "不限（仅发起时可写）" : "仅发起时可写")
            : string.Join(" / ", definition.WritableBy);
    }

    /// <summary>对外的参数名：优先 apiName，缺失时给裸名（错误文案不该出现空字符串）。</summary>
    /// <param name="definition">参数定义。</param>
    /// <param name="fallback">兜底名字。</param>
    public static string DisplayName(ParameterDefinition definition, string fallback)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return string.IsNullOrWhiteSpace(definition.ApiName) ? fallback : definition.ApiName;
    }

    private static bool Contains(IReadOnlyList<string> roles, string role)
    {
        foreach (var item in roles)
        {
            if (string.Equals(item, role, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripPrefix(string apiName) =>
        apiName.StartsWith("param.", StringComparison.Ordinal) ? apiName["param.".Length..] : apiName;
}

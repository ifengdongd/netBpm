using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Platform.Contracts;
using NetBpm.Workflow;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 实例读模型的共享判定规则：<b>状态</b>、<b>结论</b>、<b>状态文案</b>。
/// </summary>
/// <remarks>
/// <para>
/// 单独抽出来是因为这些规则同时被实例详情（<see cref="InstanceCatalog"/>）、实例列表
/// （<see cref="InstanceListCatalog"/>）使用。复制两份必然漂移——「列表显示已通过、点进去显示已办结」
/// 这类问题排查成本极高，且没人会怀疑是两处实现不一致。
/// </para>
/// <para>
/// 类内只有纯函数，不持有依赖：判定逻辑必须能被单测直接覆盖，不需要构造引擎。
/// </para>
/// </remarks>
internal static class InstanceReadRules
{
    /// <summary>挂起状态（<c>ACT_RU_EXECUTION.SUSPENSION_STATE_ = 2</c>）。</summary>
    internal const int SuspendedState = 2;

    /// <summary>
    /// 判定实例状态：<b>运行态执行优先</b>，执行已删才看历史的删除原因。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不是「历史是否有 EndTime」优先：历史被关闭（<c>EnableHistory=false</c>）时
    /// 历史查询恒为空，用历史当权威会把<b>运行中</b>的实例判成已结束。
    /// </para>
    /// <para>
    /// 引擎暂不产出 <see cref="InstanceStatus.Failed"/>：本子集没有「实例级异常挂起」的运行态，
    /// 委托解析失败 / 表达式求值失败走的是作业重试与死信，不落到实例状态上。
    /// </para>
    /// </remarks>
    /// <param name="execution">运行态根执行；实例结束后为 <see langword="null"/>。</param>
    /// <param name="historic">历史实例；历史关闭时为 <see langword="null"/>。</param>
    internal static InstanceStatus ResolveStatus(
        IExecutionEntity? execution,
        HistoricProcessInstance? historic)
    {
        if (execution is not null)
        {
            return execution.SuspensionState == SuspendedState
                ? InstanceStatus.Suspended
                : InstanceStatus.Running;
        }

        return string.IsNullOrWhiteSpace(historic?.DeleteReason)
            ? InstanceStatus.Completed
            : InstanceStatus.Terminated;
    }

    /// <summary>判定实例状态（列表用：只有「是否挂起」这个标量，没有执行实体）。</summary>
    /// <param name="hasExecution">运行态根执行是否仍存在。</param>
    /// <param name="suspensionState">运行态挂起状态（无执行时为 <see langword="null"/>）。</param>
    /// <param name="deleteReason">历史的删除原因（正常结束为 <see langword="null"/>）。</param>
    internal static InstanceStatus ResolveStatus(
        bool hasExecution,
        int? suspensionState,
        string? deleteReason)
    {
        if (hasExecution)
        {
            return suspensionState == SuspendedState
                ? InstanceStatus.Suspended
                : InstanceStatus.Running;
        }

        return string.IsNullOrWhiteSpace(deleteReason)
            ? InstanceStatus.Completed
            : InstanceStatus.Terminated;
    }

    /// <summary>
    /// 组装实例结论：<b>三级回落</b>（§4.7）。仅对<b>已结束</b>的实例给出结论。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 独立部署后没有宿主委托来声明结论（现状是宿主在服务任务里调
    /// <c>CompleteAsync(execution, WorkflowOutcomes.Approved)</c>），平台只能自己推导。三级依次是：
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b>流程显式声明</b>（<see cref="ConclusionSource.Declared"/>）：变量
    ///   <see cref="PlatformVariables.Outcome"/>，值写语义名或选项 id。最可信。
    ///   </description></item>
    ///   <item><description>
    ///   <b>最后一条审批决定</b>（<see cref="ConclusionSource.LastDecision"/>）：变量 <c>result</c>
    ///   经语义字典解析。覆盖单审 / 串签的绝大多数场景。
    ///   </description></item>
    ///   <item><description>
    ///   <b>兜底</b>（<see cref="ConclusionSource.Fallback"/>）：<see cref="ApprovalSemantic.Closed"/>。
    ///   流程走完了但没有任何审批决定（人工跳转到结束、被终止等）。
    ///   </description></item>
    /// </list>
    /// <para>
    /// 带上来历是刻意的：审计必须能区分「流程说通过了」与「平台只知道它结束了」。
    /// 把第三级伪装成第一级，等于给一个没人批过的单子盖上「已通过」。
    /// </para>
    /// </remarks>
    /// <param name="status">实例状态。</param>
    /// <param name="variables">实例变量快照（运行 + 历史合并）。</param>
    internal static InstanceConclusion? ResolveConclusion(
        InstanceStatus status,
        IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        if (status is not (InstanceStatus.Completed or InstanceStatus.Terminated))
        {
            return null;
        }

        // 一级：流程显式声明
        if (variables.TryGetValue(PlatformVariables.Outcome, out var declared)
            && VariableValues.AsString(declared) is { Length: > 0 } declaredText
            && ResolveDeclaredSemantic(declaredText) is { } declaredSemantic)
        {
            return new InstanceConclusion(
                Semantic: declaredSemantic,
                OptionId: null,
                Label: ConclusionLabel(declaredSemantic),
                Source: ConclusionSource.Declared);
        }

        // 二级：最后一条审批决定
        if (variables.TryGetValue(BaseModelVariables.Result, out var value)
            && VariableValues.AsString(value) is { Length: > 0 } optionId)
        {
            var semantic = ApprovalSemantics.Resolve(optionId);

            return new InstanceConclusion(
                Semantic: semantic,
                OptionId: optionId,
                Label: ConclusionLabel(semantic),
                Source: ConclusionSource.LastDecision);
        }

        // 三级：兜底（没有任何决定能代表结论）
        return new InstanceConclusion(
            Semantic: ApprovalSemantic.Closed,
            OptionId: null,
            Label: ConclusionLabel(ApprovalSemantic.Closed),
            Source: ConclusionSource.Fallback);
    }

    /// <summary>结论展示文案（平台级，供「已通过 / 已驳回」这类默认文案兜底）。</summary>
    /// <param name="semantic">审批语义。</param>
    internal static string ConclusionLabel(ApprovalSemantic semantic) => semantic switch
    {
        ApprovalSemantic.Approve => "已通过",
        ApprovalSemantic.Reject => "已驳回",
        ApprovalSemantic.Return => "已退回",
        ApprovalSemantic.Delegate => "已转办",
        ApprovalSemantic.Closed => "已办结",
        _ => "已办结",
    };

    /// <summary>由流程定义 id（<c>{key}:{version}:{id}</c>）解析出 key。</summary>
    /// <param name="processDefinitionId">流程定义 id。</param>
    internal static string ParseKey(string processDefinitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        var index = processDefinitionId.IndexOf(':', StringComparison.Ordinal);

        return index < 0 ? processDefinitionId : processDefinitionId[..index];
    }

    /// <summary>数据库读回的 <see cref="DateTime"/> 按 UTC 转 <see cref="DateTimeOffset"/>。</summary>
    /// <param name="value">数据库时间。</param>
    internal static DateTimeOffset? AsUtcOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    /// <summary>
    /// 解析流程显式声明的结论值：先按语义名匹配，再按选项 id 走默认字典。
    /// </summary>
    /// <remarks>
    /// 先比名字是因为 <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> 会把 <c>"1"</c> 解析成
    /// 枚举序号 <c>1</c>（= <see cref="ApprovalSemantic.Reject"/>）——那正好是最危险的误判：
    /// 流程声明「通过」，平台读成「驳回」。所以数字一律走 <see cref="ApprovalSemantics.Resolve"/>。
    /// </remarks>
    private static ApprovalSemantic? ResolveDeclaredSemantic(string text)
    {
        var trimmed = text.Trim();

        foreach (var semantic in Enum.GetValues<ApprovalSemantic>())
        {
            if (string.Equals(semantic.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return semantic;
            }
        }

        var resolved = ApprovalSemantics.Resolve(trimmed);

        // 未识别的值不猜：宁可回落到下一级，也不要凭空造一个结论
        return resolved == ApprovalSemantic.Custom
            && !string.Equals(trimmed, nameof(ApprovalSemantic.Custom), StringComparison.OrdinalIgnoreCase)
                ? null
                : resolved;
    }
}

using System.Globalization;

namespace NetBpm.Demo.ExternalTasks;

/// <summary>
/// topic=<c>demo.priceAudit</c>：价格审计。
/// <para>
/// 回执的 <c>auditPassed</c> 会推动用例流程里的排他网关 —— 典型「外部系统算完把结论写回流程」
/// 的用法：流程自己不做计算，只按外部结论走分支。
/// </para>
/// </summary>
public sealed class PriceAuditHandler(ILogger<PriceAuditHandler> logger) : IExternalTaskHandler
{
    /// <summary>主题名，BPMN 与用例断言都引用它，避免两边写死字符串。</summary>
    public const string TopicName = "demo.priceAudit";

    /// <inheritdoc />
    public string Topic => TopicName;

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, object?>?> ExecuteAsync(
        ExternalTaskInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var amount = ReadDouble(invocation.Payload, "amount");
        var passed = amount is > 0d;
        var taxAmount = Math.Round((amount ?? 0d) * 0.06d, 2);

        logger.LogInformation(
            "价格审计：任务 {TaskId} 实例 {ProcessInstanceId} 金额 {Amount} → 结论 {Passed}（税额 {TaxAmount}）",
            invocation.TaskId,
            invocation.ProcessInstanceId,
            amount,
            passed,
            taxAmount);

        return new ValueTask<IReadOnlyDictionary<string, object?>?>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auditPassed"] = passed,
                ["taxAmount"] = taxAmount,
                ["auditedBy"] = "demo-inproc-worker",
            });
    }

    /// <summary>
    /// 变量快照里的数字可能落成 double / long / int / string（引擎子集不支持 decimal，
    /// 见 <c>ProcessVariableTypes</c>），统一按 double 读。
    /// </summary>
    internal static double? ReadDouble(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            double value => value,
            float value => value,
            long value => value,
            int value => value,
            short value => value,
            decimal value => (double)value,
            string value when double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                => parsed,
            _ => null,
        };
    }
}

/// <summary>
/// topic=<c>demo.priceAuditArchive</c>：归档。
/// <para>
/// 它挂在「审计通过」分支上，只在 <see cref="PriceAuditHandler"/> 把 <c>auditPassed=true</c>
/// 写回流程后才会被登记 —— 所以用例用「有没有这条任务」来证明变量确实写回了。
/// </para>
/// </summary>
public sealed class PriceArchiveHandler(ILogger<PriceArchiveHandler> logger) : IExternalTaskHandler
{
    /// <summary>主题名。</summary>
    public const string TopicName = "demo.priceAuditArchive";

    /// <inheritdoc />
    public string Topic => TopicName;

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, object?>?> ExecuteAsync(
        ExternalTaskInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var taxAmount = PriceAuditHandler.ReadDouble(invocation.Payload, "taxAmount");
        var archiveNo = $"ARC-{invocation.ProcessInstanceId}";

        logger.LogInformation(
            "价格归档：任务 {TaskId} 实例 {ProcessInstanceId} 税额 {TaxAmount} → 归档号 {ArchiveNo}",
            invocation.TaskId,
            invocation.ProcessInstanceId,
            taxAmount,
            archiveNo);

        return new ValueTask<IReadOnlyDictionary<string, object?>?>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["archiveNo"] = archiveNo,
                ["archived"] = true,
            });
    }
}

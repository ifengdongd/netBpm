using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NetBpm.ExternalWorker;

/// <summary>
/// 一个 topic 的业务实现。
/// </summary>
/// <remarks>
/// 真实项目里这里就是「客户系统的那一段代码」：调自己的数据库、调第三方 API。
/// 返回值是要写回流程实例的变量 —— 引擎不关心你怎么算的，只认结果。
/// </remarks>
public interface IExternalTaskHandler
{
    /// <summary>关心的主题（与 BPMN 里的 <c>activiti:topic</c> 对应）。</summary>
    string Topic { get; }

    /// <summary>执行业务逻辑。</summary>
    /// <param name="task">领取到的任务（含变量快照）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>要写回流程实例的变量。</returns>
    /// <exception cref="ExternalTaskFailedException">业务口径的失败：会消耗服务端的重试额度。</exception>
    Task<Dictionary<string, object?>> ExecuteAsync(ExternalTaskDto task, CancellationToken ct);
}

/// <summary>
/// 演示：开票。读 <c>amount</c> 算出含税金额并生成发票号。
/// </summary>
/// <remarks>
/// <c>payload.simulateFailure = true</c> 时一律失败 —— 演示「失败不跳过节点 /
/// 重试耗尽进死信」这条语义需要一个<b>可控</b>的失败源，而不是随机抛异常
/// （随机失败会让演示结果无法复述）。
/// </remarks>
public sealed class InvoiceHandler : IExternalTaskHandler
{
    /// <inheritdoc />
    public string Topic => "invoice";

    /// <inheritdoc />
    public Task<Dictionary<string, object?>> ExecuteAsync(ExternalTaskDto task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (IsTrue(task.Payload, "simulateFailure"))
        {
            throw new ExternalTaskFailedException(
                "开票系统返回失败（演示用例：payload.simulateFailure=true）",
                details: "由流程变量显式触发，用于演示退避重试与死信");
        }

        var amount = ReadDecimal(task.Payload, "amount") ?? 0m;
        var total = Math.Round(amount * 1.06m, 2, MidpointRounding.AwayFromZero);

        return Task.FromResult(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["invoiceNo"] = $"INV-{DateTime.UtcNow:yyyyMMdd}-{task.Id}",
            ["invoiceAmount"] = amount,
            ["invoiceTotal"] = total,
            ["invoiceAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
    }

    private static bool IsTrue(Dictionary<string, object?> payload, string key) =>
        payload.TryGetValue(key, out var raw)
        && raw is not null
        && (string.Equals(raw.ToString(), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw.ToString(), "1", StringComparison.Ordinal));

    private static decimal? ReadDecimal(Dictionary<string, object?> payload, string key)
    {
        // 变量过了一遍 JSON：可能是 JsonElement，也可能是标量与字符串。
        // worker 侧宁容错，也不要因为类型不同就让整条流程卡住。
        if (!payload.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is System.Text.Json.JsonElement element)
        {
            return element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetDecimal(out var value)
                ? value
                : element.ValueKind == System.Text.Json.JsonValueKind.String
                    && decimal.TryParse(
                        element.GetString(),
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var parsed)
                    ? parsed
                    : null;
        }

        return decimal.TryParse(
            Convert.ToString(raw, CultureInfo.InvariantCulture),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var scalar)
            ? scalar
            : null;
    }
}

/// <summary>演示：通知（假出口 —— 只往日志写一行，避免演示依赖外部服务）。</summary>
public sealed class NotifyHandler : IExternalTaskHandler
{
    private readonly ILogger<NotifyHandler> _logger;

    /// <summary>构造。</summary>
    public NotifyHandler(ILogger<NotifyHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    public string Topic => "notify";

    /// <inheritdoc />
    public Task<Dictionary<string, object?>> ExecuteAsync(ExternalTaskDto task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        // 出口是假的，但写回流程的变量是真的 —— 后面的网关照样拿得到
        _logger.LogInformation(
            "已通知：{Activity}（实例 {InstanceId}，业务键 {BusinessKey}）",
            task.ActivityName,
            task.ProcessInstanceId,
            task.BusinessKey ?? "-");

        return Task.FromResult(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["notified"] = true,
            ["notifiedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
    }
}

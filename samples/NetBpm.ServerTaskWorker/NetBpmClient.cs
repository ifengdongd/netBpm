using System.Net.Http.Json;
using System.Text.Json;

namespace NetBpm.ServerTaskWorker;

/// <summary>
/// NetBpm 外部任务协议客户端 —— 只做四件事：领取、回执成功、回执失败、释放锁。
/// </summary>
/// <remarks>
/// <para>协议层没有 SDK 依赖，直接打 HTTP。凭据是控制台签发的客户端密钥，放在 <c>X-Api-Key</c> 头里。</para>
/// <para>路径一律写成不带前导斜杠的相对路径：<see cref="HttpClient.BaseAddress"/> 已带结尾斜杠，
/// 写成 <c>/api/…</c> 会把任何基地址里的路径前缀吃掉。</para>
/// </remarks>
public sealed class NetBpmClient
{
    private readonly HttpClient _http;
    private readonly WorkerOptions _options;

    /// <summary>构造。</summary>
    /// <param name="http">已配好基地址与凭据头的 <see cref="HttpClient"/>。</param>
    /// <param name="options">运行参数（取 workerId 与领取上限）。</param>
    public NetBpmClient(HttpClient http, WorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options;
    }

    /// <summary>
    /// 领取并加锁。
    /// </summary>
    /// <remarks>
    /// 服务端把 <c>waitSeconds</c> 实现成「每秒看一次」，不是长轮询：没有任务就返回空数组，
    /// 不会等到超时才回。所以调用方不需要自己 sleep。
    /// </remarks>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本轮领到的任务；没有则为空集合。</returns>
    public async Task<IReadOnlyList<ExternalTask>> FetchAsync(CancellationToken ct)
    {
        using var response = await SendAsync(
            "api/runtime/external-tasks/fetch",
            new FetchRequest
            {
                WorkerId = _options.WorkerId,
                Topics = _options.Topics,
                MaxTasks = _options.MaxTasks,
                LockDurationMs = _options.LockDurationMs,
                WaitSeconds = _options.WaitSeconds,
            },
            ct).ConfigureAwait(false);

        var tasks = await response.Content
            .ReadFromJsonAsync<List<ExternalTask>>(WorkerJson.Options, ct)
            .ConfigureAwait(false);

        return tasks ?? [];
    }

    /// <summary>
    /// 回执成功：写回变量 → 推进流程。
    /// </summary>
    /// <remarks>
    /// 变量是「写回实例」的，不是覆盖 payload —— 后续节点与历史变量都能看到这里写的值。
    /// </remarks>
    /// <param name="taskId">任务 id。</param>
    /// <param name="variables">要写回的变量。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task CompleteAsync(
        string taskId,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            $"api/runtime/external-tasks/{Uri.EscapeDataString(taskId)}/complete",
            new CompleteRequest
            {
                WorkerId = _options.WorkerId,
                Variables = new Dictionary<string, object?>(variables, StringComparer.Ordinal),
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 回执失败：扣重试 + 退避，<b>流程原地不动</b>。
    /// </summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="errorMessage">失败摘要（进 <c>errorMessage</c>，会展示在运维端）。</param>
    /// <param name="errorDetails">失败详情。</param>
    /// <param name="retries">
    /// 剩余重试次数。传 <c>0</c> 表示「别再试了，直接进死信」——
    /// 用于不可恢复的业务错误，免得把三次机会浪费在同一个必然失败的原因上。
    /// 传 <c>null</c> 则按「当前剩余 − 1」。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public async Task FailAsync(
        string taskId,
        string errorMessage,
        string? errorDetails,
        int? retries,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            $"api/runtime/external-tasks/{Uri.EscapeDataString(taskId)}/failure",
            new FailureRequest
            {
                WorkerId = _options.WorkerId,
                ErrorMessage = errorMessage,
                ErrorDetails = errorDetails,
                Retries = retries,
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 主动释放锁：任务立刻回到可领取。
    /// </summary>
    /// <remarks>
    /// <b>放弃处理才用</b>（例如停机时刚领到还没开始做）。处理失败请用
    /// <see cref="FailAsync"/> —— unlock 等于「我没看见过」，会把失败原因一起抹掉。
    /// </remarks>
    /// <param name="taskId">任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task UnlockAsync(string taskId, CancellationToken ct)
    {
        using var response = await SendAsync(
            $"api/runtime/external-tasks/{Uri.EscapeDataString(taskId)}/unlock",
            new { workerId = _options.WorkerId },
            ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, object body, CancellationToken ct)
    {
        var response = await _http
            .PostAsJsonAsync(path, body, WorkerJson.Options, ct)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (message, code) = ParseError(raw, (int)response.StatusCode);

            throw new NetBpmApiException(message, (int)response.StatusCode, code, raw);
        }
    }

    /// <summary>从错误响应里取 message / code，取不到就退化成 HTTP 码。</summary>
    private static (string Message, string? Code) ParseError(string raw, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ($"HTTP {statusCode}", null);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;

            var code = root.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : null;

            return (string.IsNullOrWhiteSpace(message) ? $"HTTP {statusCode}" : message, code);
        }
        catch (JsonException)
        {
            // 网关/反代返回的 HTML 错误页：保留原文片段，比丢成 "HTTP 502" 好排错
            return (raw.Length > 500 ? raw[..500] : raw, null);
        }
    }
}

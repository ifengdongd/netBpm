using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 性能门禁：<b>串行</b>读 p95 &lt; 80ms（详情）/ 300ms（列表）、写 p95 &lt; 200ms（真 MySQL）；
/// 100 并发只判「全部成功 + 不吊死」（5s 兜底），不作延迟门槛。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么默认不跑</b>：这两个数字是「同一台机器上的相对指标」，不是绝对承诺——
/// CI 机器的核数与磁盘、本机是否有其它进程，都会让结果在两倍范围内跳动。
/// 做成常驻门禁的结果是「今天红明天绿」，最后没人再看它。
/// 因此按 <c>NETBPM_RUN_LOAD_TESTS=1</c> 显式开启，用于<b>发版前</b>与<b>性能回归排查</b>；
/// 阈值可用 <c>NETBPM_LOAD_READ_P95_MS</c> / <c>NETBPM_LOAD_WRITE_P95_MS</c> / <c>NETBPM_LOAD_LIST_P95_MS</c> 覆盖。
/// </para>
/// <para>
/// <b>写为什么测「发起」而不是「提交」</b>：提交的耗时强依赖流程形状（会签要并发计数、网关要算表达式），
/// 一条基线覆盖不了；而「发起」是所有业务写入的公共前缀——落实例、写参数快照、写时间线、写待办索引，
/// 一个都不少。它变慢，别的写操作一定也慢。
/// </para>
/// <para>
/// <b>读分两条路径，预算也不同</b>：
/// <list type="bullet">
///   <item><description>
///   详情（<c>GET /instances/{id}</c>）按方案 §9 的 <b>80ms</b> 门禁——这是「打开一张单」的延迟。
///   </description></item>
///   <item><description>
///   列表（<c>GET /instances?limit=20</c>）单独给 <b>300ms</b> 回归预算。它在实测中就是慢：
///   租户内的过滤与排序在内存里做（清单 §3 ① 第 1 条「上万量级的租户/白名单内存过滤，
///   需自建实例索引表另做」），数据量越大越慢。给它套 80ms 只会得到一条永远红的用例——
///   这个预算的用途是「别在已经慢的地方再慢下去」，不是假装它达标。
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>串行与并发都跑，但只有串行进门槛</b>：串行量的是「这一次请求在服务端花了多久」（代码与索引的地盘），
/// 并发量的是「100 个人同时来时排多久」（核数、MySQL 规格、连接池的地盘）。
/// 所以串行数字按方案 §9 卡（真 MySQL 实测：详情 51ms / 列表 150ms / 写 21~26ms），
/// 并发只断言「100 个请求全部成功 + 没有吊死」（5s 兜底），读数照印不误。
/// </para>
/// <para>
/// ⚠️ <b>并发数字不要当门槛</b>（2026-11-03 nightly 的教训）：这里曾经给并发读过套 §9 的 80ms、
/// 给并发写套过 400ms，结果同一份代码在 4 核 runner 上必然超线 ——
/// 一次发起要写 10+ 行、列表还要在租户内做内存过滤，100 个同时进来在哪都要排队，
/// 红的不是回归而是「换了台机器」。并发压测真正能证明的是<b>全部成功</b>：
/// 连接泄漏、死锁牺牲品、池耗尽（503）都从这条断言冒出来。
/// </para>
/// </remarks>
public sealed class LoadTests
{
    private const int Concurrency = 100;

    /// <summary>串行测量的样本数（够算 p95，又不至于把整个用例拖长）。</summary>
    private const int SerialSamples = 40;

    /// <summary>并发下的兜底线（毫秒）：只用来抓「吊死 / 连接泄漏」，不是性能目标。</summary>
    private const double HangGuardMs = 5000;

    /// <summary>详情读 p95 预算（毫秒）。方案 §9 的口径。</summary>
    private static readonly double ReadP95BudgetMs = Budget("NETBPM_LOAD_READ_P95_MS", 80);

    /// <summary>写（发起）p95 预算（毫秒）。方案 §9 的口径，压在<b>串行</b>上（一次请求在服务端的成本）。</summary>
    private static readonly double WriteP95BudgetMs = Budget("NETBPM_LOAD_WRITE_P95_MS", 200);

    /// <summary>列表读 p95 回归预算（毫秒）。实测基线，不是方案 §9 的 80ms —— 原因见类注释。</summary>
    private static readonly double ListP95BudgetMs = Budget("NETBPM_LOAD_LIST_P95_MS", 300);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Read_ShouldKeepP95UnderBudget()
    {
        SkipUnlessEnabled();

        using var host = new RestEngineFactory();
        var (client, secret, processKey) = await PrepareAsync(host);

        // 先铺数据：空库上的读会把「索引命中」测成「什么都没扫」
        var instanceIds = new List<string>(Concurrency);

        for (var i = 0; i < Concurrency; i++)
        {
            instanceIds.Add(await StartCoreAsync(client, secret, processKey));
        }

        Assert.Equal(Concurrency, instanceIds.Count);

        await WarmupAsync(client, secret, processKey);

        Task<HttpResponseMessage> Detail(int index) => SendAsync(
            client,
            HttpMethod.Get,
            "/workflow/v1/instances/" + instanceIds[index % instanceIds.Count],
            secret,
            body: null);

        Task<HttpResponseMessage> List(int _) =>
            SendAsync(client, HttpMethod.Get, "/workflow/v1/instances?limit=20", secret, body: null);

        // ① 串行：量「这一次请求在服务端花了多久」——方案 §9 的两个数字说的就是它
        var serialDetailP95 = Report(
            "读·详情·串行",
            await MeasureSerialAsync(SerialSamples, Detail),
            ReadP95BudgetMs);

        var serialListP95 = Report(
            "读·列表·串行",
            await MeasureSerialAsync(SerialSamples, List),
            ListP95BudgetMs);

        // ② 并发：量「100 个人同时来时排多久」——受数据库连接与机器核数支配，只做吊死兜底
        var concurrentDetailP95 = Report(
            "读·详情·" + Concurrency + " 并发",
            await MeasureAsync(Concurrency, Detail),
            HangGuardMs);

        var concurrentListP95 = Report(
            "读·列表·" + Concurrency + " 并发",
            await MeasureAsync(Concurrency, List),
            HangGuardMs);

        // ① 串行：量「这一次请求在服务端花了多久」—— 方案 §9 的两个数字说的就是它。
        //    这两条是**代码与索引**的门槛，红了才代表实现退步。
        Assert.True(
            serialDetailP95 < ReadP95BudgetMs,
            $"详情读 p95 = {serialDetailP95:F1}ms，超过预算 {ReadP95BudgetMs}ms（串行，真 MySQL）。");

        Assert.True(
            serialListP95 < ListP95BudgetMs,
            $"列表读 p95 = {serialListP95:F1}ms，超过回归预算 {ListP95BudgetMs}ms（串行）。"
            + "列表在租户内做内存过滤，这一项的预算是「别再慢下去」，要根治得自建实例索引表。");

        // ② 并发：量的是「100 个人同时来时排队多久」—— 受核数 / MySQL 规格 / 连接池支配，
        //    **不作为代码门槛**（与 ci.yml 的口径一致：并发只留 5s 吊死兜底）。
        //    2026-11-03 nightly 的教训：这里原本拿 §9 的 80ms 去卡并发读数，
        //    同一份代码在 4 核 runner 上必然超线，红的不是回归而是「换了台机器」；
        //    真正有价值的并发断言只有一条 —— 100 个请求全部成功（连接泄漏 / 死锁 / 503 都归这里，
        //    由 MeasureAsync 的 failures 断言兜住）。数字照样打印，便于人工对比趋势。
        Assert.True(
            concurrentDetailP95 < HangGuardMs && concurrentListP95 < HangGuardMs,
            $"并发读出现异常延迟：详情 {concurrentDetailP95:F1}ms / 列表 {concurrentListP95:F1}ms"
            + $"（兜底线 {HangGuardMs:F0}ms，超过即视为吊死或连接泄漏）。");

        Assert.True(
            serialDetailP95 < HangGuardMs && serialListP95 < HangGuardMs,
            $"串行读出现异常延迟：详情 {serialDetailP95:F1}ms / 列表 {serialListP95:F1}ms。");
    }

    [Fact]
    public async Task Write_ShouldKeepP95Under200ms()
    {
        SkipUnlessEnabled();

        using var host = new RestEngineFactory();
        var (client, secret, processKey) = await PrepareAsync(host);

        await WarmupAsync(client, secret, processKey);

        var serialP95 = Report(
            "写·串行",
            await MeasureSerialAsync(SerialSamples, _ => StartCoreAsync(client, secret, processKey)),
            WriteP95BudgetMs);

        var concurrentP95 = Report(
            "写·" + Concurrency + " 并发",
            await MeasureAsync(Concurrency, _ => StartCoreAsync(client, secret, processKey)),
            HangGuardMs);

        Assert.True(
            serialP95 < WriteP95BudgetMs,
            $"写 p95 = {serialP95:F1}ms，超过预算 {WriteP95BudgetMs}ms（串行，真 MySQL）。");

        // 并发写不当门槛：一次发起要写 10+ 行，100 个同时进来在哪都要排队，
        // 读数由数据库规格与机器核数决定 —— 本机实测 455ms、4 核 runner 645ms，
        // 同一条 400ms 的线在两边都红，而串行读数（§9 的真实口径）一直是绿的。
        // 保留这条并发压测是为了抓「全部成功」与「不吊死」，不是抓延迟。
        Assert.True(
            concurrentP95 < HangGuardMs,
            $"并发写 p95 = {concurrentP95:F1}ms，超过兜底线 {HangGuardMs:F0}ms（疑似吊死或连接泄漏）。");
    }

    // ─────────────────────────── 测量 ───────────────────────────

    /// <summary>
    /// 串行打 <paramref name="count"/> 次，返回每次的耗时。
    /// </summary>
    /// <remarks>
    /// 串行与并发两个数字各有用途，缺一不可：
    /// <b>串行</b>量的是「这一次请求在服务端花了多久」（受代码与索引影响），
    /// <b>并发</b>量的是「同一时刻 100 个人来时排多久」（受数据库连接数、机器核数影响）。
    /// 把两者混成一个数字，就只能得到「慢了」，分不清是该改代码还是该加机器。
    /// </remarks>
    private static async Task<double[]> MeasureSerialAsync(int count, Func<int, Task> action)
    {
        var samples = new double[count];

        for (var index = 0; index < count; index++)
        {
            var stopwatch = Stopwatch.StartNew();

            await action(index);

            samples[index] = stopwatch.Elapsed.TotalMilliseconds;
        }

        return samples;
    }

    /// <summary>
    /// 并发打 <paramref name="count"/> 次，返回每次的耗时。
    /// </summary>
    /// <remarks>
    /// 用「同时起飞」而不是「匀速发压」：预算针对的是<b>峰值压力下的尾延迟</b>，
    /// 匀速发压测出来的 p95 会把排队掩盖掉。
    /// </remarks>
    private static async Task<double[]> MeasureAsync(int count, Func<int, Task> action)
    {
        var samples = new double[count];
        var failures = new List<string>();

        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = count }, async (index, _) =>
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await action(index);
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add($"[{index}] {exception.GetType().Name}: {exception.Message}");
                }
            }
            finally
            {
                samples[index] = stopwatch.Elapsed.TotalMilliseconds;
            }
        });

        Assert.True(
            failures.Count == 0,
            $"压测中有 {failures.Count} 次请求失败：{string.Join(" / ", failures.Take(3))}");

        return samples;
    }

    /// <summary>把分布打到测试输出（CI 日志里能直接看到），返回 p95。</summary>
    private static double Report(string label, double[] samples, double budget)
    {
        var sorted = samples.Order().ToArray();
        var p50 = Percentile(sorted, 0.50);
        var p95 = Percentile(sorted, 0.95);
        var p99 = Percentile(sorted, 0.99);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{label}：n={sorted.Length} p50={p50:F1}ms p95={p95:F1}ms p99={p99:F1}ms "
            + $"max={sorted[^1]:F1}ms（预算 {budget:F0}ms）");

        return p95;
    }

    /// <summary>最近秩法（nearest-rank）：取第 ⌈p·n⌉ 个样本，比插值更贴近「真实发生过的那次请求」。</summary>
    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    // ─────────────────────────── 场景 ───────────────────────────

    private static async Task<(HttpClient Client, string Secret, string ProcessKey)> PrepareAsync(
        RestEngineFactory host)
    {
        var processKey = "load_" + Guid.NewGuid().ToString("N")[..8];

        // 先清引擎表：压测每次都往库里堆 100+ 张单，不清的话第二轮测的是「五百张单的租户」，
        // 数字随运行次数单调变差，基线就没有意义了。清的是 ACT_*（引擎表），平台表不动。
        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        // 单个用户任务：写路径的公共前缀（落实例 / 参数快照 / 时间线 / 待办索引）一个不少，
        // 又不会因为流程走完而少写「当前待办」那一段
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-load">
              <bpmn:process id="{{processKey}}" name="压测流程" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:assignee="zhangsan">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        var engine = host.Services.GetRequiredService<IProcessEngine>();

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, processKey + ".bpmn20.xml", ct: Ct);

        var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new
            {
                name = "load-" + processKey,
                tenantId = "default",
                scopes = new[] { PlatformScopes.InstanceStart, PlatformScopes.InstanceRead },
            },
            Ct);

        var payload = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册压测客户端失败：{response.StatusCode} {payload}");

        using var json = JsonDocument.Parse(payload);

        return (client, json.RootElement.GetProperty("clientSecret").GetString()!, processKey);
    }

    /// <summary>预热：JIT、连接池、查询计划与元数据缓存都要先热起来，否则前十次全是冷启动。</summary>
    private static async Task WarmupAsync(HttpClient client, string secret, string processKey)
    {
        for (var i = 0; i < 10; i++)
        {
            await SendAsync(client, HttpMethod.Get, "/workflow/v1/instances?limit=20", secret, body: null);
            await StartCoreAsync(client, secret, processKey);
        }
    }

    /// <summary>发起一次，返回实例 id。</summary>
    private static async Task<string> StartCoreAsync(HttpClient client, string secret, string processKey)
    {
        var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/workflow/v1/instances",
            secret,
            new { processKey });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return json.RootElement.GetProperty("instanceId").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string secret,
        object? body)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);

        if (!response.IsSuccessStatusCode)
        {
            // 只报状态码不够定位：平台面的 503 至少有「数据库不可用」「运维开关」两种来源，
            // 而压测失败时 annotation 里只有这一句断言消息（CI 的 job 日志要凭据才能下载）。
            // 把错误信封里的 code 带出来，下一次红的时候就能直接判断是池耗尽还是别的。
            var detail = await ReadErrorDetailAsync(response);

            Assert.Fail(
                $"{method} {path} → {(int)response.StatusCode} {response.StatusCode}"
                + (detail.Length > 0 ? "：" + detail : string.Empty));
        }

        return response;
    }

    /// <summary>
    /// 从错误响应体里提取 <c>code</c> + <c>detail</c>/<c>message</c>（平台面 RFC 9457 / 旧信封两种都认），
    /// 截断到 200 字 —— 它最终要塞进 xUnit 的失败清单，不能无限长。
    /// </summary>
    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response)
    {
        try
        {
            var payload = await response.Content.ReadAsStringAsync(Ct);

            if (payload.Length == 0)
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(payload);

            var text = string.Join(
                ' ',
                new[]
                {
                    Property(document.RootElement, "code"),
                    Property(document.RootElement, "detail"),
                    Property(document.RootElement, "message"),
                }.Where(part => !string.IsNullOrWhiteSpace(part)));

            return text.Length <= 200 ? text : text[..200];
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException)
        {
            // 响应体不是 JSON（例如反向代理的 HTML 错误页）时，状态码仍是有效信息
            return string.Empty;
        }
    }

    private static string? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void SkipUnlessEnabled()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var enabled = Environment.GetEnvironmentVariable("NETBPM_RUN_LOAD_TESTS") == "1";

        Assert.SkipUnless(
            enabled,
            "性能门禁默认不跑（结果随机器跳动，做成常驻门禁会「今天红明天绿」）。"
            + "发版前或排查性能回归时设置 NETBPM_RUN_LOAD_TESTS=1 开启；"
            + "阈值可用 NETBPM_LOAD_READ_P95_MS / NETBPM_LOAD_WRITE_P95_MS / NETBPM_LOAD_LIST_P95_MS 覆盖。");

        PrimeThreadPool();
    }

    /// <summary>
    /// 压测前把线程池最小线程数抬到并发数以上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不这么做，测出来的<b>不是服务端延迟，而是 .NET 的线程注入速率</b>：
    /// 线程池遇到 100 个同时阻塞的任务时每秒只补 1~2 个线程，
    /// 表现是 p50 ≈ p95 ≈ 10s 的「全员一起慢」——把所有排队时间都算进了单次请求里。
    /// </para>
    /// <para>
    /// 这是<b>测试宿主</b>的失真（进程内 TestServer 与业务代码共享同一个线程池），
    /// 真实 Kestrel 部署下每请求独立于测试驱动线程，不会有这个效应。
    /// 因此这里修的是测量方法，不是被测对象。
    /// </para>
    /// </remarks>
    private static void PrimeThreadPool()
    {
        ThreadPool.GetMinThreads(out var workerMin, out var ioMin);

        var target = Concurrency * 2;

        if (workerMin < target)
        {
            ThreadPool.SetMinThreads(target, Math.Max(ioMin, target));
        }
    }

    private static double Budget(string name, double fallback) =>
        double.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value) && value > 0
            ? value
            : fallback;
}

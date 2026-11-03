using NetBpm.Engine.Jobs;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.ExternalTasks;

/// <summary>用例的一步（带断言的执行结果）。</summary>
/// <param name="Name">步骤名。</param>
/// <param name="Passed">断言是否通过。</param>
/// <param name="Detail">看到的事实 / 失败原因。</param>
public sealed record ExternalTaskCaseStep(string Name, bool Passed, string Detail);

/// <summary>用例报告：<see cref="Steps"/> 全绿才算 <see cref="Passed"/>。</summary>
public sealed record ExternalTaskCaseReport(
    bool Passed,
    string ProcessKey,
    string? ProcessInstanceId,
    IReadOnlyList<ExternalTaskCaseStep> Steps);

/// <summary>
/// 外部任务的「注册 + 调用」闭环用例：跑一遍 <c>processes/external-task-case.bpmn20.xml</c>，
/// 每一步都断言，返回分步报告给 <c>POST /api/external-task/case/run</c>。
/// <para>
/// 为什么不用 xUnit：这条用例要的<b>不是隔离的单元</b>，而是"这个宿主里注册的 worker 真的能把
/// 流程推完"。所以它跑在 Demo 宿主自己的引擎上（真库、真后台服务），一条 HTTP 请求就能告诉
/// 你外部任务这条链路此刻通不通。
/// </para>
/// </summary>
public sealed class ExternalTaskCaseRunner
{
    /// <summary>用例流程 key（= BPMN 里 process/@id）。</summary>
    public const string ProcessKey = "external_task_case";

    private const string DefinitionResourceName = "external-task-case.bpmn20.xml";

    /// <summary>等任务登记 / 等流程结束的上限。Demo 的 worker 轮询 1 秒，留足余量。</summary>
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly IProcessEngine _engine;
    private readonly IWorkflowService _workflowService;
    private readonly IExternalTaskBroker _broker;
    private readonly DemoExternalTaskWorker _worker;
    private readonly IReadOnlyList<IExternalTaskHandler> _handlers;
    private readonly ILogger<ExternalTaskCaseRunner> _logger;

    /// <summary>依赖全部由 DI 提供：引擎、broker、已注册的 worker 与 handler。</summary>
    public ExternalTaskCaseRunner(
        IProcessEngine engine,
        IWorkflowService workflowService,
        IExternalTaskBroker broker,
        DemoExternalTaskWorker worker,
        IEnumerable<IExternalTaskHandler> handlers,
        ILogger<ExternalTaskCaseRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(workflowService);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _workflowService = workflowService;
        _broker = broker;
        _worker = worker;
        _handlers = handlers.ToArray();
        _logger = logger;
    }

    /// <summary>跑一遍用例并返回分步结果（不抛异常：失败体现在 report 里）。</summary>
    public async Task<ExternalTaskCaseReport> RunAsync(CancellationToken ct = default)
    {
        var steps = new List<ExternalTaskCaseStep>();
        var topics = _handlers.Select(handler => handler.Topic).ToHashSet(StringComparer.Ordinal);

        // ① 注册：两个 topic 都有 handler，且 worker 订阅了它们
        var registered = topics.Contains(PriceAuditHandler.TopicName)
            && topics.Contains(PriceArchiveHandler.TopicName)
            && _worker.Topics.Contains(PriceAuditHandler.TopicName, StringComparer.Ordinal);

        steps.Add(new ExternalTaskCaseStep(
            "① handler 已注册（topic → 实现）",
            registered,
            $"已注册 topic：{(topics.Count == 0 ? "（无）" : string.Join(", ", topics))}；worker={DemoExternalTaskWorker.WorkerId}"));

        // ② 部署：只在没部署过时部署，避免每次跑用例都涨一个版本
        try
        {
            var definitionId = await EnsureDeployedAsync(ct).ConfigureAwait(false);
            steps.Add(new ExternalTaskCaseStep("② 用例流程已部署", true, definitionId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(new ExternalTaskCaseStep("② 用例流程已部署", false, ex.Message));

            return BuildReport(steps, processInstanceId: null);
        }

        // 暂停内置 worker：先看"登记出来、流程原地不动"的中间态，否则任务可能瞬间被领走
        _worker.Pause();

        string? instanceId = null;

        try
        {
            instanceId = await _engine
                .StartProcessInstanceByKeyAsync(
                    ProcessKey,
                    businessKey: $"CASE-{Guid.NewGuid():N}"[..13],
                    startUserId: "demo-case",
                    // ⚠️ 变量的值类型受引擎子集约束：只支持 string / int / long / double / bool / DateTime。
                    // decimal 会被拒（需要 Java 序列化布局），所以金额这里用 double。
                    variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["amount"] = 1000d },
                    ct: ct)
                .ConfigureAwait(false);

            steps.Add(new ExternalTaskCaseStep("③ 实例已发起", true, $"instanceId={instanceId}"));

            // ④ 登记：流程走到 svc_price_audit，外部任务出现且带变量快照
            var registeredTask = await WaitForTaskAsync(instanceId, PriceAuditHandler.TopicName, ct)
                .ConfigureAwait(false);

            steps.Add(new ExternalTaskCaseStep(
                "④ 外部任务已登记（topic=demo.priceAudit）",
                registeredTask is not null,
                registeredTask is null
                    ? $"等了 {RegistrationTimeout.TotalSeconds:0} 秒没看到登记行"
                    : $"taskId={registeredTask.Id}，status={registeredTask.Status}，payload.amount={ReadAmount(registeredTask)}"));

            // ⑤ 原地等待：登记不等于推进，实例必须还活着（这是外部任务最核心的语义）
            var historic = await _engine.FindHistoricProcessInstanceAsync(instanceId, ct).ConfigureAwait(false);
            var waiting = historic is { IsRunning: true };

            steps.Add(new ExternalTaskCaseStep(
                "⑤ 回执前流程原地等待（没有推进）",
                waiting,
                waiting ? "实例仍在运行" : "实例已结束 —— 外部任务被当成同步节点跳过了"));

            // ⑥ 调用：恢复 worker，它领取任务 → 执行 handler → 回执 → 网关按 auditPassed 走通过分支
            _worker.Resume();
            var ended = await WaitForEndAsync(instanceId, ct).ConfigureAwait(false);

            steps.Add(new ExternalTaskCaseStep(
                "⑥ 注册的 worker 领取并回执后流程结束",
                ended,
                ended ? $"等了不到 {CompletionTimeout.TotalSeconds:0} 秒" : $"等了 {CompletionTimeout.TotalSeconds:0} 秒流程仍未结束"));

            var tasks = await _engine
                .FindExternalTasksAsync(new ExternalTaskQuery(ProcessInstanceId: instanceId, MaxResults: 50), ct)
                .ConfigureAwait(false);

            var audit = tasks.FirstOrDefault(task => task.Topic == PriceAuditHandler.TopicName);
            steps.Add(new ExternalTaskCaseStep(
                "⑦ 第一条外部任务回执成功",
                audit?.Status == ExternalTaskStatus.Completed,
                audit is null ? "查不到登记行" : $"status={audit.Status}，workerId={audit.WorkerId ?? "（已释放）"}"));

            // ⑧ 变量写回的间接证据：只有 auditPassed=true 才会走「通过」分支并登记归档任务
            var archive = tasks.FirstOrDefault(task => task.Topic == PriceArchiveHandler.TopicName);
            steps.Add(new ExternalTaskCaseStep(
                "⑧ 回执变量推动了网关（出现归档任务）",
                archive is not null,
                archive is null
                    ? "没出现 topic=demo.priceAuditArchive 的任务 —— auditPassed 没有写回流程"
                    : $"taskId={archive.Id}，说明网关判定 auditPassed=true，走了「审计通过」分支"));

            steps.Add(new ExternalTaskCaseStep(
                "⑨ 第二条外部任务（归档）也回执成功",
                archive?.Status == ExternalTaskStatus.Completed,
                archive is null ? "归档任务不存在" : $"status={archive.Status}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(new ExternalTaskCaseStep("用例执行中断", false, ex.Message));
            _logger.LogWarning(ex, "外部任务用例执行中断");
        }
        finally
        {
            _worker.Resume();

            // 清理 COMPLETED 行：引擎目前不会随实例结束回收外部任务（删除实例也不会），
            // 反复跑用例不清理会一直堆在同一张表里
            if (instanceId is not null)
            {
                try
                {
                    var cleaned = await _broker.DeleteByProcessInstanceAsync(instanceId, ct).ConfigureAwait(false);
                    steps.Add(new ExternalTaskCaseStep("⑩ 清理本次用例的外部任务行", true, $"删除 {cleaned} 行"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    steps.Add(new ExternalTaskCaseStep("⑩ 清理本次用例的外部任务行", false, ex.Message));
                }
            }
        }

        return BuildReport(steps, instanceId);
    }

    /// <summary>按 key 解析最新定义；没有就部署一次（BPMN 随 Demo 一起发布在 processes/ 下）。</summary>
    private async ValueTask<string> EnsureDeployedAsync(CancellationToken ct)
    {
        var existing = await _workflowService.ResolveDefinitionIdAsync(ProcessKey, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "processes", DefinitionResourceName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到用例 BPMN：{path}（检查 csproj 里的 Content 复制）", path);
        }

        await using var stream = File.OpenRead(path);
        var definition = await _engine
            .DeployAsync(stream, DefinitionResourceName, "外部任务闭环用例", tenantId: null, ct)
            .ConfigureAwait(false);

        return definition.Id;
    }

    /// <summary>等某主题的任务登记出来（返回 null 表示超时）。</summary>
    private async ValueTask<ExternalTask?> WaitForTaskAsync(string instanceId, string topic, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + RegistrationTimeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var tasks = await _engine
                .FindExternalTasksAsync(
                    new ExternalTaskQuery(Topic: topic, ProcessInstanceId: instanceId, MaxResults: 10),
                    ct)
                .ConfigureAwait(false);

            if (tasks.Count > 0)
            {
                return tasks[0];
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>等实例结束（历史行的 EndTime 写上了）。</summary>
    private async ValueTask<bool> WaitForEndAsync(string instanceId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + CompletionTimeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var historic = await _engine.FindHistoricProcessInstanceAsync(instanceId, ct).ConfigureAwait(false);

            if (historic is { IsRunning: false })
            {
                return true;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static object? ReadAmount(ExternalTask task) =>
        task.Payload.TryGetValue("amount", out var amount) ? amount : null;

    private static ExternalTaskCaseReport BuildReport(
        List<ExternalTaskCaseStep> steps,
        string? processInstanceId) =>
        new(
            steps.Count > 0 && steps.All(step => step.Passed),
            ProcessKey,
            processInstanceId,
            steps);
}

namespace NetBpm.Engine.Jobs;

/// <summary>
/// 外部任务状态（存字符串词汇，不存枚举序号 —— 数据库里要能直接读懂）。
/// </summary>
public static class ExternalTaskStatus
{
    /// <summary>待领取（含 failure 退避后重新可领）。锁字段为空。</summary>
    public const string Pending = "PENDING";

    /// <summary>已被某个 worker 领走，锁未过期。</summary>
    public const string Locked = "LOCKED";

    /// <summary>已回执成功，流程已推进。</summary>
    public const string Completed = "COMPLETED";

    /// <summary>重试耗尽，停在原地等管理干预（可重投）。</summary>
    public const string DeadLetter = "DEAD_LETTER";
}

/// <summary>
/// 外部任务（<c>nb_external_task</c>）。对应 Camunda 的 External Task 模式，是
/// <b>引擎独立部署</b>下执行 <c>serviceTask</c> 的唯一手段。
/// <para>
/// 与 <see cref="AsyncJobEntity"/> 的分工：<b>异步作业是「引擎自己晚点做」，外部任务是「等别人做完」</b>。
/// 生命周期不同（作业到期即执行，外部任务要等回执）、重试语义不同（作业失败引擎重跑，
/// 外部任务失败要 worker 决定还剩几次），因此<b>不复用</b> <c>ACT_RU_JOB</c> —— 共用一套表会让两侧互相污染。
/// </para>
/// <para>
/// 只复用它俩的<b>租约模型</b>：<c>WorkerId</c> 对应 <c>LOCK_OWNER_</c>、
/// <c>LockExpiresAt</c> 对应 <c>LOCK_EXP_TIME_</c>，语义与 <c>NetBpm:Jobs:LeaseSeconds</c> 一致
/// —— 持有者崩溃后不再续租，到期自动被别的 worker 接管。
/// </para>
/// </summary>
public sealed class ExternalTask
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>流程实例 id。</summary>
    public string ProcessInstanceId { get; set; } = string.Empty;

    /// <summary>停留在该节点的执行 id（<b>推进流程靠它</b>，不能只存实例 id）。</summary>
    public string ExecutionId { get; set; } = string.Empty;

    /// <summary>流程定义 id。</summary>
    public string? ProcessDefinitionId { get; set; }

    /// <summary>流程定义 key。</summary>
    public string? ProcessDefinitionKey { get; set; }

    /// <summary>节点 id（<c>serviceTask/@id</c>）。流程推进后靠它对回当前元素。</summary>
    public string ActivityId { get; set; } = string.Empty;

    /// <summary>节点名（回显用，可空）。</summary>
    public string? ActivityName { get; set; }

    /// <summary>主题（<c>activiti:topic</c>），worker 按它订阅。</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>租户。</summary>
    public string? TenantId { get; set; }

    /// <summary>业务键（回显用，可空）。</summary>
    public string? BusinessKey { get; set; }

    /// <summary>
    /// 领取时一并交给 worker 的<b>变量快照</b>。
    /// <para>它是快照而不是实时视图：worker 拿到的是「领走那一刻」的值，
    /// 之后引擎侧的变量变化不会回灌 —— 否则 worker 与引擎会互相覆盖。</para>
    /// </summary>
    public IDictionary<string, object?> Payload { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>优先级，大的先领。</summary>
    public int Priority { get; set; }

    /// <summary>剩余重试次数。<c>0</c> 且再次失败即进死信。</summary>
    public int Retries { get; set; }

    /// <summary>状态（见 <see cref="ExternalTaskStatus"/>）。</summary>
    public string Status { get; set; } = ExternalTaskStatus.Pending;

    /// <summary>锁持有者（worker id）；无人持有时为 <see langword="null"/>。</summary>
    public string? WorkerId { get; set; }

    /// <summary>锁到期时间（UTC）。持有者不续租即到期，任务重新可被领取。</summary>
    public DateTime? LockExpiresAt { get; set; }

    /// <summary>登记时间（UTC）。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>回执成功时间（UTC）。</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// 下次可再被领取的时间（UTC）。failure 后按退避设定；<see langword="null"/> 表示立即可领。
    /// </summary>
    public DateTime? NextDueAt { get; set; }

    /// <summary>最近一次失败原因（摘要，回显用）。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>最近一次失败详情（堆栈 / 上下文，可空）。</summary>
    public string? ErrorDetails { get; set; }

    /// <summary>锁是否已过期（以 <paramref name="nowUtc"/> 为准）。</summary>
    public bool IsLockExpired(DateTime nowUtc) =>
        Status == ExternalTaskStatus.Locked && LockExpiresAt is { } expires && expires <= nowUtc;

    /// <summary>
    /// 当前是否可被领取：锁已过期（worker 崩溃）可被接管，或处于待领取且退避期已过。
    /// </summary>
    public bool IsAvailable(DateTime nowUtc)
    {
        if (IsLockExpired(nowUtc))
        {
            return true;
        }

        if (Status != ExternalTaskStatus.Pending)
        {
            return false;
        }

        return NextDueAt is not { } due || due <= nowUtc;
    }
}

/// <summary>
/// 外部任务的重试与退避策略（与 <see cref="AsyncJobRetry"/> 同款节奏，但计数方向相反：
/// 这里记的是<b>剩余</b>次数，job 记的是<b>已尝试</b>次数）。
/// </summary>
public static class ExternalTaskRetryPolicy
{
    /// <summary>默认重试次数（BPMN 未声明时）。</summary>
    public const int DefaultRetries = 3;

    /// <summary>退避上限：5 分钟，与异步作业一致。</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 第 <paramref name="attempt"/> 次失败后要等多久（指数退避：5s → 10s → 20s … 封顶 5min）。
    /// </summary>
    public static TimeSpan Backoff(int attempt)
    {
        var seconds = 5d * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));

        return capped;
    }

    /// <summary>
    /// 由「剩余次数」反推失败次数（用于算退避）。剩余次数被 worker 改小/改大时会偏，
    /// 但退避只决定节奏、不决定正确性，偏一点无妨。
    /// </summary>
    public static int AttemptOf(int remaining) => Math.Max(1, DefaultRetries - Math.Max(0, remaining));
}

/// <summary>
/// 外部任务选项（锁时长 / 默认重试次数）。宿主按配置注入，内存与持久化实现共用。
/// </summary>
public sealed class ExternalTaskOptions
{
    /// <summary>一次领取的锁时长（默认 5 分钟，与 <c>NetBpm:Jobs:LeaseSeconds</c> 同量级）。</summary>
    public TimeSpan DefaultLockDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>登记时的默认剩余重试次数。</summary>
    public int DefaultRetries { get; set; } = ExternalTaskRetryPolicy.DefaultRetries;
}

/// <summary>worker 订阅的一个主题（可单独指定锁时长）。</summary>
/// <param name="Topic">主题名。</param>
/// <param name="LockDuration">该主题的锁时长；为空则用请求级或全局默认值。</param>
public sealed record ExternalTaskTopicRequest(string Topic, TimeSpan? LockDuration = null);

/// <summary>
/// 领取请求（fetch &amp; lock）。
/// </summary>
/// <param name="WorkerId">worker 标识（锁持有者），必须非空 —— 回执时要对得上。</param>
/// <param name="Topics">订阅的主题列表。</param>
/// <param name="MaxTasks">本次最多领几条。</param>
/// <param name="LockDuration">锁时长；为空用全局默认（<c>NetBpm:ExternalTasks:LockSeconds</c>）。</param>
/// <param name="TenantId">
/// 租户过滤。<b>不能为空就放行</b>：外部任务带租户，A 租户的 worker 领到 B 租户的任务
/// 等于把别人的活干在别人的流程上。未启用租户时传 <c>default</c> 即可。
/// </param>
public sealed record ExternalTaskFetchRequest(
    string WorkerId,
    IReadOnlyList<ExternalTaskTopicRequest> Topics,
    int MaxTasks = 10,
    TimeSpan? LockDuration = null,
    string? TenantId = null);

/// <summary>
/// 失败回执。
/// </summary>
/// <param name="ErrorMessage">失败原因摘要。</param>
/// <param name="ErrorDetails">失败详情。</param>
/// <param name="Retries">
/// worker 指定的<b>剩余</b>重试次数。<see langword="null"/> 表示沿用「当前剩余 − 1」；
/// 结果 ≤ 0 时进死信（停在原地等管理干预，不再自动重投）。
/// </param>
/// <param name="RetryAfter">多久后可再领；为空走默认指数退避。</param>
public sealed record ExternalTaskFailure(
    string? ErrorMessage = null,
    string? ErrorDetails = null,
    int? Retries = null,
    DateTime? RetryAfter = null);

/// <summary>外部任务查询（管理端列表 / 死信重投）。</summary>
/// <param name="Topic">主题过滤。</param>
/// <param name="ProcessInstanceId">流程实例过滤。</param>
/// <param name="Status">状态过滤（见 <see cref="ExternalTaskStatus"/>）。</param>
/// <param name="WorkerId">锁持有者过滤。</param>
/// <param name="OnlyDue">只返回当前可领取的（未被锁或锁已过期）。</param>
/// <param name="MaxResults">返回条数上限。</param>
/// <param name="TenantId">租户过滤（与 <see cref="ExternalTaskFetchRequest.TenantId"/> 同口径）。</param>
public sealed record ExternalTaskQuery(
    string? Topic = null,
    string? ProcessInstanceId = null,
    string? Status = null,
    string? WorkerId = null,
    bool OnlyDue = false,
    int MaxResults = 100,
    string? TenantId = null);

/// <summary>
/// 外部任务存储。
/// <para>
/// ⚠️ 与 <see cref="IAsyncJobManager"/> 同属「作业」范畴但**不复用**：见 <see cref="ExternalTask"/> 的说明。
/// 这里只借用了它的租约语义。
/// </para>
/// <para>
/// 未注入时，标记了 <c>activiti:topic</c> 的流程<b>部署能过、执行会显式报错</b>
/// （而不是静默退化成同步跳过）—— 与 <see cref="IAsyncJobManager"/> 的约定一致。
/// </para>
/// </summary>
public interface IExternalTaskBroker
{
    /// <summary>登记一条外部任务（行为类执行到节点时调用）。</summary>
    ValueTask<ExternalTask> CreateAsync(ExternalTask task, CancellationToken ct = default);

    /// <summary>按 id 查；不存在返回 <see langword="null"/>。</summary>
    ValueTask<ExternalTask?> FindByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// 领取并加锁（fetch &amp; lock）：按主题取走最多 <c>MaxTasks</c> 条可领取任务，
    /// 写入 <c>WorkerId</c> / <c>LockExpiresAt</c> 并置为 <see cref="ExternalTaskStatus.Locked"/>。
    /// <para>锁过期的任务视为可领取 —— worker 崩溃后不需要有人来「解锁」。</para>
    /// </summary>
    ValueTask<IReadOnlyList<ExternalTask>> FetchAndLockAsync(
        ExternalTaskFetchRequest request,
        DateTime nowUtc,
        CancellationToken ct = default);

    /// <summary>
    /// 标记完成。锁校验失败（不是持有者 / 锁已过期 / 已完成）返回 <see langword="false"/>。
    /// <para>只改状态，<b>不推进流程</b> —— 推进由命令层完成（见 <c>CompleteExternalTaskCommand</c>）。</para>
    /// </summary>
    ValueTask<bool> CompleteAsync(string id, string workerId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>失败回执：扣重试次数、写错误信息、按退避设定 <c>NextDueAt</c>；耗尽进死信。</summary>
    ValueTask<bool> FailureAsync(
        string id,
        string workerId,
        ExternalTaskFailure failure,
        DateTime nowUtc,
        CancellationToken ct = default);

    /// <summary>续租（worker 处理时间超过初始锁时长时使用）。</summary>
    ValueTask<bool> ExtendLockAsync(
        string id,
        string workerId,
        TimeSpan lockDuration,
        DateTime nowUtc,
        CancellationToken ct = default);

    /// <summary>主动释放锁（worker 放弃处理），任务回到 <see cref="ExternalTaskStatus.Pending"/>。</summary>
    ValueTask<bool> UnlockAsync(string id, string workerId, CancellationToken ct = default);

    /// <summary>按条件查询。</summary>
    ValueTask<IReadOnlyList<ExternalTask>> FindAsync(ExternalTaskQuery query, CancellationToken ct = default);

    /// <summary>死信重投：回到 <see cref="ExternalTaskStatus.Pending"/> 并恢复重试次数。</summary>
    ValueTask<bool> RetryDeadLetterAsync(string id, int retries, CancellationToken ct = default);

    /// <summary>实例删除 / 终止时清理（流程已不存在，留着是孤儿数据）。</summary>
    ValueTask<int> DeleteByProcessInstanceAsync(string processInstanceId, CancellationToken ct = default);
}

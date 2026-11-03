namespace NetBpm.Platform.Contracts;

/// <summary>
/// 对外错误码总表（与 <c>docs/platform-api-refactoring-plan.md</c> 附录 B 一一对应）。
/// </summary>
/// <remarks>
/// 错误码是<b>稳定契约</b>：调用方可以按它分支，所以一旦发布就只能新增、不能改字符串、不能改语义。
/// 命名格式固定为 <c>域.原因</c>（小写下划线），响应体见 <see cref="PlatformProblem"/>。
/// </remarks>
public static class ErrorCodes
{
    /// <summary>未携带任何凭据。<c>401</c></summary>
    /// <summary>401：凭据缺失、无效、过期或被停用（不细分，避免给探测者方向）。</summary>
    public const string AuthUnauthorized = "auth.unauthorized";

    public const string AuthMissingCredential = "auth.missing_credential";

    /// <summary>令牌签名 / 过期 / 受众不符。<c>401</c></summary>
    public const string AuthInvalidToken = "auth.invalid_token";

    /// <summary>scope 不足。<c>403</c></summary>
    public const string AuthInsufficientScope = "auth.insufficient_scope";

    /// <summary>纯服务令牌调用了需要真人主体的接口（提交审批）。<c>403</c></summary>
    public const string AuthSubjectRequired = "auth.subject_required";

    /// <summary>客户端已被停用或密钥已吊销。<c>401</c></summary>
    public const string AuthClientDisabled = "auth.client_disabled";

    /// <summary>跨租户访问。<b>按不存在处理</b>，返回 <c>404</c> 而不是 403，避免泄露存在性。</summary>
    public const string TenantMismatch = "tenant.mismatch";

    /// <summary>流程 key 不存在，或该客户端未被授权使用此流程。<c>404</c></summary>
    public const string ProcessNotFound = "process.not_found";

    /// <summary>流程存在但没有任何生效定义。<c>409</c></summary>
    public const string ProcessNotDeployed = "process.not_deployed";

    /// <summary>流程定义已挂起，不能发起或推进。<c>409</c></summary>
    public const string ProcessSuspended = "process.suspended";

    /// <summary>版本不存在。<c>404</c></summary>
    public const string ProcessVersionNotFound = "process.version_not_found";

    /// <summary>实例不存在（或不属于当前租户 / 客户端）。<c>404</c></summary>
    public const string InstanceNotFound = "instance.not_found";

    /// <summary>实例已结束、已终止或已挂起，不能继续推进。<c>409</c></summary>
    public const string InstanceNotRunning = "instance.not_running";

    /// <summary>任务不存在（常见原因：已被他人办结）。<c>404</c></summary>
    public const string TaskNotFound = "task.not_found";

    /// <summary>任务已被办结（重复提交）。<c>409</c></summary>
    public const string TaskAlreadyCompleted = "task.already_completed";

    /// <summary>当前主体不是该任务的办理人 / 候选人也未认领。<c>403</c></summary>
    public const string TaskNotAssignee = "task.not_assignee";

    /// <summary>该任务已被他人认领。<c>409</c></summary>
    public const string TaskAlreadyClaimed = "task.already_claimed";

    /// <summary>选项 id 不属于该节点的选项集。<c>422</c></summary>
    public const string DecisionNotInOptionSet = "decision.not_in_option_set";

    /// <summary>调用方声明的 semantic 与该选项的字典定义不一致。<c>422</c></summary>
    public const string DecisionSemanticMismatch = "decision.semantic_mismatch";

    /// <summary>该选项要求填写审批意见。<c>422</c></summary>
    public const string DecisionCommentRequired = "decision.comment_required";

    /// <summary>该选项要求指定目标节点（退回 / 跳转语义）。<c>422</c></summary>
    public const string DecisionTargetNodeRequired = "decision.target_node_required";

    /// <summary>指定的目标节点不在允许范围内。<c>422</c></summary>
    public const string DecisionTargetNodeInvalid = "decision.target_node_invalid";

    /// <summary>必填参数缺失。<c>422</c></summary>
    public const string ParameterRequiredMissing = "parameter.required_missing";

    /// <summary>参数类型或约束不符。<c>422</c></summary>
    public const string ParameterTypeInvalid = "parameter.type_invalid";

    /// <summary>参数在该环节不可写。<c>422</c></summary>
    public const string ParameterReadOnly = "parameter.readonly";

    /// <summary>参数名与保留命名空间（<c>sys.</c> 等）冲突。<c>400</c></summary>
    public const string ParameterConflictWithReserved = "parameter.conflict_with_reserved";

    /// <summary>
    /// 参数规范读不出来，本次提交 / 发起未被受理。<c>503</c>
    /// <para>
    /// 与 <see cref="ParameterReadOnly"/> 分开：那里是「这个字段在这一环节不可写」，
    /// 这里是「服务端此刻无法判断它可不可写」。前者调用方应改请求，后者只需重试。
    /// </para>
    /// <para>
    /// 存在这个码的理由：写权限校验依赖参数规范，读不到规范时<b>不能</b>按「无 schema 放行」降级——
    /// 那等于在存储故障期间静默关掉 <c>writableBy</c>，而响应仍是 200，谁都不会发现。
    /// </para>
    /// </summary>
    public const string ParameterSchemaUnavailable = "parameter.schema_unavailable";

    /// <summary>乐观并发：<c>expectedRevision</c> 与当前值不符。<c>409</c></summary>
    public const string ConcurrencyRevisionMismatch = "concurrency.revision_mismatch";

    /// <summary>乐观并发：<c>If-Match</c> 与当前 ETag 不符。<c>412</c></summary>
    public const string ConcurrencyEtagMismatch = "concurrency.etag_mismatch";

    /// <summary>幂等键被复用但请求体不同。<c>409</c></summary>
    public const string IdempotencyKeyReused = "idempotency.key_reused";

    /// <summary>
    /// 相同幂等键的首次请求仍在处理中。<c>409</c>
    /// <para>
    /// 与 <see cref="IdempotencyKeyReused"/> 分开：这里是「稍后可以用同一个键重试」，
    /// 那里是「这个键已经属于别的请求，换一个或用原来的请求」。合成一个码会让调用方做错决定。
    /// </para>
    /// </summary>
    public const string IdempotencyInFlight = "idempotency.in_flight";

    /// <summary>
    /// EL 表达式求值失败（典型：<c>assignee="${applyUser}"</c> 引用了不存在的变量）。<c>422</c>
    /// <para>
    /// 不走 500：异常消息带着表达式原文与未解析的标识符，是建模/调用方能直接修的线索；
    /// 按「未预期异常」处理会把普通笔误伪装成服务端故障。
    /// </para>
    /// </summary>
    public const string EngineExpressionError = "engine.expression_error";

    /// <summary>
    /// 委托解析失败。<c>500</c>
    /// <para>独立部署下正常流程<b>不应该</b>出现此码——应走 <see cref="ExternalTaskNoWorker"/>。</para>
    /// </summary>
    public const string EngineDelegateMissing = "engine.delegate_missing";

    /// <summary>外部任务锁已过期，任务被回收给其它 worker。<c>409</c></summary>
    public const string ExternalTaskLockExpired = "external_task.lock_expired";

    /// <summary>没有 worker 领取该 topic 的外部任务（超时）。<c>503</c></summary>
    public const string ExternalTaskNoWorker = "external_task.no_worker";

    /// <summary>超出配额。<c>429</c></summary>
    public const string RateLimited = "rate_limited";

    /// <summary>请求体结构不合法（JSON 层）。<c>400</c></summary>
    public const string RequestMalformed = "request.malformed";

    /// <summary>服务端未预期异常（响应里不带堆栈，只带 <c>traceId</c>）。<c>500</c></summary>
    public const string InternalError = "internal.error";

    /// <summary>客户端不存在（管理面按 id 查询时）。<c>404</c></summary>
    public const string ClientNotFound = "client.not_found";

    /// <summary>404：事件订阅不存在，或不属于当前凭据的租户。</summary>
    public const string EventSubscriptionNotFound = "event_subscription.not_found";

    /// <summary>404：该事件对该订阅没有投递记录。</summary>
    public const string EventDeliveryNotFound = "event_delivery.not_found";

    /// <summary>409：投递不在死信状态，无需重投。</summary>
    public const string EventDeliveryNotDeadLetter = "event_delivery.not_dead_letter";

    /// <summary>授予了未定义的 scope（拼错或跨版本）。<c>422</c></summary>
    public const string ClientScopeUnknown = "client.scope_unknown";

    /// <summary>授予高危 scope 但未显式确认。<c>422</c></summary>
    public const string ClientScopeNeedsConfirmation = "client.scope_needs_confirmation";

    /// <summary>租户不存在（注册客户端时的所属租户）。<c>404</c></summary>
    public const string TenantNotFound = "tenant.not_found";

    /// <summary>登记租户时缺少名称。<c>400</c></summary>
    public const string TenantNameRequired = "tenant.name_required";

    /// <summary>试图停用当前凭据所属租户（停用后无法自行恢复）。<c>400</c></summary>
    public const string TenantSelfSuspend = "tenant.self_suspend_forbidden";

    /// <summary>
    /// 凭据绑定的租户与目标的租户不同，<b>写操作</b>被拒。<c>403</c>
    /// <para>
    /// 与 <see cref="TenantMismatch"/> 分工：读操作跨租户按不存在（404，不泄露存在性），
    /// 写操作的租户就在请求里、调用方本来就知道它存在，再假装「不存在」只会让人以为自己拼错了 id。
    /// </para>
    /// </summary>
    public const string TenantForbidden = "tenant.forbidden";

    /// <summary>注册客户端时缺少系统名称。<c>400</c></summary>
    public const string ClientNameRequired = "client.name_required";

    /// <summary>需要请求体但收到空（或 JSON 为空对象）。<c>400</c></summary>
    public const string RequestBodyRequired = "request.body_required";

    /// <summary>
    /// 目录覆盖请求里没有任何覆盖字段。<c>422</c>
    /// <para>
    /// 空覆盖与「没有覆盖行」在合并结果上等价，允许它只会往表里堆一行什么都不做的记录；
    /// 要清除覆盖应使用 <c>DELETE</c>。
    /// </para>
    /// </summary>
    public const string CatalogOverrideEmpty = "catalog.override_empty";

    /// <summary>
    /// 流程存在，但没有目录覆盖行。<c>404</c>
    /// <para>
    /// 与 <see cref="ProcessNotFound"/> 分开：前者是「key 打错了」，后者是「没精修过，PUT 一次即可」。
    /// 合成一个码会让管理端把「打错 key」也当成「建一条就行」。
    /// </para>
    /// </summary>
    public const string CatalogOverrideNotFound = "catalog.override_not_found";

    /// <summary>
    /// 试图把 <see cref="ApprovalSemantic.Closed"/> 写进字典。<c>422</c>
    /// <para>
    /// 该语义只由平台在「结论三级回落」的最后一级产生（流程结束但没有任何审批决定）。
    /// 允许字典声明它，等于允许人工伪造平台专属结论——审计上就分不清
    /// 「流程说通过了」与「平台只知道它结束了」。
    /// </para>
    /// </summary>
    public const string DictionarySemanticReserved = "dictionary.semantic_reserved";

    /// <summary>
    /// 字典请求里的 <c>nodeId</c> 不是该流程定义里的用户任务。<c>422</c>
    /// <para>
    /// 按 422 而不是 404：路径上的资源（流程）是存在的，错的是请求体的字段值。
    /// </para>
    /// </summary>
    public const string DictionaryNodeNotFound = "dictionary.node_not_found";

    /// <summary>
    /// 该选项不在流程定义声明的选项集里。<c>422</c>
    /// <para>
    /// 这类配置永远不会被读到（解析时只按 BPMN 声明逐项查字典），属于「配好了但不工作」，
    /// 比没配更难发现，所以在写入时就拒掉。
    /// </para>
    /// </summary>
    public const string DictionaryOptionNotDeclared = "dictionary.option_not_declared";

    /// <summary>
    /// <c>allowedTargetNodes</c> 里有流程定义中不存在的节点。<c>422</c>
    /// </summary>
    public const string DictionaryTargetNodeUnknown = "dictionary.target_node_unknown";

    /// <summary>
    /// 要删除的字典行不存在。<c>404</c>
    /// </summary>
    public const string DictionaryEntryNotFound = "dictionary.entry_not_found";
}

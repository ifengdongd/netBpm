namespace NetBpm.Platform.Contracts;

/// <summary>
/// 审批语义字典的一行（<c>GET /admin/v1/dictionaries</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 字典回答的是「这个审批选项在<b>业务上</b>算什么」：BPMN 里只有
/// <c>activiti:value="1"</c>，而「1 是同意还是驳回」既不能写死在引擎里，也不能让每个调用方自己猜。
/// 这一行就是那次翻译的<b>可管理形态</b>。
/// </para>
/// <para>
/// 🔑 管理端看到的是「库里实际存了什么」，而不是 <c>IApprovalVocabulary</c> 解析后的合并结果——
/// 后者把「默认字典 + 流程声明 + 库里覆盖」三层压成一个答案，管理端分不清某一行是平台默认还是本流程配的。
/// 与 <c>/workflow/v1</c> 的分工同理（业务面拿合并结果，管理面拿源数据）。
/// </para>
/// </remarks>
/// <param name="TenantId">租户。</param>
/// <param name="ProcessKey">流程 key（字典的粒度是「某流程的审批口径」）。</param>
/// <param name="NodeId">节点级映射的节点 id；<see langword="null"/> 表示流程级默认。</param>
/// <param name="OptionId">流程里声明的选项值（<c>activiti:value</c>）。</param>
/// <param name="Semantic">语义（同意 / 驳回 / 退回 / 自定义）。</param>
/// <param name="OutcomeClass">结论归类（正向 / 负向 / 中性），决定通过率怎么算。</param>
/// <param name="Label">展示名（为空时用语义的默认文案）。</param>
/// <param name="RequiresComment">提交该选项是否必须写审批意见。</param>
/// <param name="RequiresTargetNode">提交该选项是否必须指定目标节点。</param>
/// <param name="AllowedTargetNodes">可选目标节点白名单；空数组表示按流程定义默认。</param>
/// <param name="Attributes">流程自定义附加属性（原样回显）。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">更新时间。</param>
public sealed record ApprovalDictionaryEntryView(
    string TenantId,
    string ProcessKey,
    string? NodeId,
    string OptionId,
    ApprovalSemantic Semantic,
    ApprovalOutcomeClass OutcomeClass,
    string? Label,
    bool RequiresComment,
    bool RequiresTargetNode,
    IReadOnlyList<string> AllowedTargetNodes,
    IReadOnlyDictionary<string, string> Attributes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 管理动作审计的一条记录（<c>GET /admin/v1/audit</c>）。
/// </summary>
/// <remarks>
/// 审计与业务日志是两件事：业务日志给排障用（可以采样、可以过期），
/// 审计给合规用（谁在什么时候把哪条流程下线了，必须能回溯）。因此这里是<b>完整流水</b>，
/// 不聚合、不采样、不因「动作太频繁」而丢。
/// </remarks>
/// <param name="Id">记录 id。</param>
/// <param name="TenantId">租户（跨租户动作可能为空）。</param>
/// <param name="Actor">操作人（真人主体 id 或客户端 id）。</param>
/// <param name="AuthMode">认证方式（令牌 / 客户端密钥 / 头身份）。</param>
/// <param name="Action">动作标识（如 <c>process_catalog.upsert</c>）。</param>
/// <param name="TargetType">目标类型（如 <c>process_catalog</c>）。</param>
/// <param name="TargetId">目标 id。</param>
/// <param name="ProcessInstanceId">关联的流程实例 id。</param>
/// <param name="Detail">
/// 动作明细，<b>原样 JSON 文本</b>（不在这里解成对象：不同动作的明细形状不同，
/// 解成统一对象反而会丢掉各动作自己的字段）。
/// </param>
/// <param name="Success">动作是否成功。</param>
/// <param name="Error">失败原因（成功时为 <see langword="null"/>）。</param>
/// <param name="CreatedAt">写入时间。</param>
public sealed record AdminAuditEntryView(
    string Id,
    string? TenantId,
    string? Actor,
    string AuthMode,
    string Action,
    string TargetType,
    string TargetId,
    string? ProcessInstanceId,
    string? Detail,
    bool Success,
    string? Error,
    DateTimeOffset CreatedAt);

/// <summary>
/// 审计分页结果。
/// </summary>
/// <remarks>
/// 用 offset 分页而不是游标：审计是「按时间段往回翻」的读法，没有列表那种「翻页期间还在新增」的稳定性要求，
/// 而 offset 能直接回答「一共多少条」——合规核对时这个数字比游标更有用。
/// </remarks>
/// <param name="Items">当前页。</param>
/// <param name="Total">满足条件的总数。</param>
/// <param name="First">跳过的条数（回显请求值，便于前端算页码）。</param>
/// <param name="Max">页大小（回显<b>夹取后</b>的值，前端据此判断有没有被降级）。</param>
public sealed record AdminAuditPageView(
    IReadOnlyList<AdminAuditEntryView> Items,
    int Total,
    int First,
    int Max);

/// <summary>按动作聚合的审计计数。</summary>
/// <param name="Action">动作标识。</param>
/// <param name="Count">条数。</param>
public sealed record AdminAuditActionCount(string Action, int Count);

/// <summary>
/// 审计筛选项（控制台下拉数据源，<c>GET /admin/v1/audit/facets</c>）。
/// </summary>
/// <remarks>
/// 单独一个端点而不是塞进分页响应：下拉数据的变化频率远低于流水，
/// 每次翻页都带一遍会把这几十个字符串重复传满整个翻页过程。
/// </remarks>
/// <param name="Actions">出现过的动作标识。</param>
/// <param name="TargetTypes">出现过的目标类型。</param>
/// <param name="Actors">出现过的操作人（最多 200 个，避免大表下拉过长）。</param>
/// <param name="Counts">按动作聚合的条数（默认统计最近 30 天）。</param>
public sealed record AdminAuditFacetsView(
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> TargetTypes,
    IReadOnlyList<string> Actors,
    IReadOnlyList<AdminAuditActionCount> Counts);

/// <summary>
/// 租户配额（<c>GET /admin/v1/quotas</c>）。
/// </summary>
/// <remarks>
/// 配额按 <b>client</b> 分区生效：租户上的值是「这个租户下客户端的默认值」，
/// 客户端上没有单独配时才继承它。因此两个数都要回显——只看租户值会以为某个客户端被限到了租户默认。
/// </remarks>
/// <param name="TenantId">租户 id。</param>
/// <param name="Name">租户名。</param>
/// <param name="Status">状态文本（<c>ACTIVE</c> / <c>SUSPENDED</c>）。</param>
/// <param name="QuotaPerMinute">默认每分钟配额。</param>
/// <param name="ClientCount">该租户下的客户端数。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record TenantQuotaView(
    string TenantId,
    string Name,
    string Status,
    int QuotaPerMinute,
    int ClientCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// 客户端配额（<c>GET /admin/v1/quotas</c>）。
/// </summary>
/// <remarks>
/// <see cref="QuotaPerMinute"/> 是<b>客户端行上存的值</b>：注册时若没单独指定会填入租户默认，
/// 所以正常情况它就是实际生效值。只有「先注册、后把租户默认调小」的客户端会与租户值不一致——
/// 这也是管理端要同时看两个层级的原因（见 <see cref="TenantQuotaView"/>）。
/// </remarks>
/// <param name="TenantId">租户。</param>
/// <param name="ClientId">客户端 id。</param>
/// <param name="Name">客户端名。</param>
/// <param name="Status">状态文本（<c>PENDING</c> / <c>ACTIVE</c> / <c>SUSPENDED</c> / <c>REVOKED</c>）。</param>
/// <param name="QuotaPerMinute">实际生效的每分钟配额。</param>
/// <param name="Scopes">已授予的 scope。</param>
/// <param name="AllowedProcessKeys">流程白名单（空 = 不限制）。</param>
/// <param name="SecretExpiresAt">密钥到期时间（轮换宽限期的终点）。</param>
public sealed record ClientQuotaView(
    string TenantId,
    string ClientId,
    string Name,
    string Status,
    int QuotaPerMinute,
    IReadOnlyCollection<string> Scopes,
    IReadOnlyCollection<string> AllowedProcessKeys,
    DateTimeOffset? SecretExpiresAt);

/// <summary>
/// 配额总览（<c>GET /admin/v1/quotas</c>）。
/// </summary>
/// <remarks>
/// 一次返回两个层级：管理端的用法是「先看租户有没有配错，再点进来看是哪几个客户端吃满了」，
/// 分两个请求只会让首屏闪两下。
/// </remarks>
/// <param name="Tenants">租户及其默认配额。</param>
/// <param name="Clients">客户端的实际生效配额。</param>
/// <param name="RateLimitEnabled">
/// 限流是否启用。为 <see langword="false"/> 时上面的数是「声明值」，不会产生 429——
/// 管理端必须能区分这两种状态，否则会把「配额配得很大」误读成「已经放开了」。
/// </param>
/// <param name="DefaultPerMinute">未配置配额时的兜底每分钟上限（<c>NetBpm:RateLimit:ApiPerMinute</c>）。</param>
/// <param name="TenantsNextCursor">
/// 租户列表的下一页游标；<see langword="null"/> 表示这一页已经是最后一页。
/// <para>
/// 两个列表<b>各自分页</b>（游标与 limit 互不影响）：租户是「配得对不对」的参照，客户端是「谁吃满了」的主体，
/// 把它们塞进一个游标会让翻页语义变得没人能解释清楚。管理端按需分别「加载更多」。
/// </para>
/// </param>
/// <param name="ClientsNextCursor">客户端列表的下一页游标；<see langword="null"/> 表示没有更多。</param>
public sealed record QuotaOverviewView(
    IReadOnlyList<TenantQuotaView> Tenants,
    IReadOnlyList<ClientQuotaView> Clients,
    bool RateLimitEnabled,
    int DefaultPerMinute,
    string? TenantsNextCursor = null,
    string? ClientsNextCursor = null);

using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 一次请求的调用形态。
/// </summary>
/// <remarks>
/// 中台形态下，「谁在调」与「以谁的身份调」是两件事，必须分开表达；
/// 只有把两者都记进审批记录，事后才能回答「这条审批是哪个系统、替哪个真人提交的」。
/// </remarks>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<CallerMode>))]
public enum CallerMode
{
    /// <summary>用户直调：调用方系统把最终用户的令牌透传过来（<c>sub</c> 就是操作人）。</summary>
    UserToken = 0,

    /// <summary>
    /// 服务代用户：服务令牌通过 OAuth2 Token Exchange（RFC 8693）换取用户令牌，
    /// 令牌里同时有 <c>sub</c>（真人）与 <c>act.sub</c>（发起换取的服务）。
    /// <b>这是推荐的接入形态。</b>
    /// </summary>
    OnBehalfOf = 1,

    /// <summary>
    /// 纯服务：<c>client_credentials</c>，没有真人主体。
    /// 只能做只读与系统动作，<b>禁止提交审批</b>（见 <see cref="CallerIdentity.CanSubmitApproval"/>）。
    /// </summary>
    ClientCredentials = 2,
}

/// <summary>
/// 当前请求的身份三元组：<c>caller</c>（哪个系统）· <c>subject</c>（哪个人）· <c>tenant</c>（哪个租户）。
/// </summary>
/// <remarks>
/// <para>
/// 三个值<b>只能来自已签名令牌</b>，不接受请求头声明——请求头可被任何持有效令牌的调用方伪造，
/// 一旦用它做数据隔离，隔离就形同虚设（现有实现只在 JWT 分支忽略了请求头，见改造点 M4）。
/// </para>
/// <para>
/// 审计落点：审批记录的办理人写 <see cref="SubjectId"/>，同时并列写 <see cref="CallerId"/>，
/// 这样「系统批量代批」与「真人自己批」在审计报表里可区分。
/// </para>
/// </remarks>
/// <param name="CallerId">调用系统（OAuth 客户端 id，令牌 <c>azp</c> / <c>client_id</c>）。</param>
/// <param name="SubjectId">实际操作人（令牌 <c>sub</c>）；纯服务形态为 <see langword="null"/>。</param>
/// <param name="TenantId">租户 id，数据隔离的唯一依据。</param>
/// <param name="Mode">调用形态。</param>
/// <param name="Scopes">令牌携带的 scope 集合。</param>
/// <param name="Groups">令牌携带的组集合（用于候选组匹配）。</param>
public sealed record CallerIdentity(
    string CallerId,
    string? SubjectId,
    string TenantId,
    CallerMode Mode,
    IReadOnlyCollection<string> Scopes,
    IReadOnlyCollection<string> Groups)
{
    /// <summary>是否属于「服务代用户」形态（两个身份都在，且不是同一个）。</summary>
    public bool IsOnBehalfOf =>
        SubjectId is not null
        && !string.Equals(CallerId, SubjectId, StringComparison.Ordinal);

    /// <summary>
    /// 是否允许提交审批。
    /// </summary>
    /// <remarks>
    /// 判据是「有没有真人主体」，不是「有没有 scope」：
    /// 纯服务令牌即便被授予了 <see cref="PlatformScopes.TaskSubmit"/>，也必须拒绝——
    /// 审批结论必须能追到一个人，否则合规审计无法通过。
    /// </remarks>
    public bool CanSubmitApproval => SubjectId is not null && Mode != CallerMode.ClientCredentials;

    /// <summary>是否具备指定 scope（按 <see cref="PlatformScopes.IsSatisfiedBy"/> 的规则判定）。</summary>
    /// <param name="scope">需要的 scope。</param>
    public bool HasScope(string scope) => PlatformScopes.IsSatisfiedBy([.. Scopes], scope);
}

/// <summary>
/// 客户端状态（管理面）。
/// </summary>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<ClientStatus>))]
public enum ClientStatus
{
    /// <summary>已注册，尚未启用（可以发密钥，但调用一律 401）。</summary>
    Pending = 0,

    /// <summary>正常可用。</summary>
    Active = 1,

    /// <summary>暂停（保留密钥，调用一律 401，用于临时止血）。</summary>
    Suspended = 2,

    /// <summary>已注销（密钥全部吊销，记录保留以便审计回查）。</summary>
    Revoked = 3,
}

/// <summary>
/// 接入系统（OAuth 客户端）的注册信息。
/// </summary>
/// <remarks>
/// <para>
/// 中台形态下 <b>不是「一个全局 API Key」</b>，而是「一系统一客户端」：
/// 配额、scope、可用流程、审计全部按客户端维度记账。否则 A 系统的用量会打到 B 系统头上，
/// 出故障时也无法按系统停服。
/// </para>
/// <para>
/// <see cref="AllowedProcessKeys"/> 是中台的核心隔离手段之一：同一套引擎上，
/// 招聘系统只能看到 <c>leave_process</c>，采购系统只能看到 <c>order_process</c>，
/// 未授权的流程 key 一律按<b>不存在</b>（404）处理。
/// </para>
/// </remarks>
/// <param name="ClientId">客户端 id（全局唯一，出现在令牌 <c>azp</c>）。</param>
/// <param name="Name">系统名称（进审计与运维看板）。</param>
/// <param name="TenantId">所属租户；通常一个客户端只服务一个租户。</param>
/// <param name="Status">状态。</param>
/// <param name="Scopes">授予的 scope 集合。</param>
/// <param name="AllowedProcessKeys">可用流程 key 白名单；为空表示该租户下全部流程（仅管理类客户端应这样配）。</param>
/// <param name="QuotaPerMinute">每分钟请求配额（按客户端分区限流）。</param>
/// <param name="CreatedAt">注册时间。</param>
/// <param name="SecretExpiresAt">当前密钥的过期时间，用于强制轮换。</param>
public sealed record ClientDescriptor(
    string ClientId,
    string Name,
    string TenantId,
    ClientStatus Status,
    IReadOnlyCollection<string> Scopes,
    IReadOnlyCollection<string> AllowedProcessKeys,
    int QuotaPerMinute,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SecretExpiresAt)
{
    /// <summary>是否可以正常调用。</summary>
    public bool IsUsable => Status == ClientStatus.Active;

    /// <summary>是否被授权使用指定流程（白名单为空视为全部授权）。</summary>
    /// <param name="processKey">流程 key。</param>
    public bool CanUseProcess(string processKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(processKey);

        return AllowedProcessKeys.Count == 0
            || AllowedProcessKeys.Contains(processKey, StringComparer.Ordinal);
    }
}

/// <summary>
/// 新签发（或轮换）出来的客户端密钥。
/// </summary>
/// <remarks>
/// <b>明文只在这里出现一次</b>，服务端只保存 SHA-256 摘要。响应结束后无法再次取得，
/// 丢失只能重新轮换——这是有意为之，避免「密钥可被运维读出来」。
/// </remarks>
/// <param name="ClientId">客户端 id。</param>
/// <param name="ClientSecret">明文密钥。</param>
/// <param name="SecretExpiresAt">过期时间。</param>
/// <param name="PreviousSecretValidUntil">
/// 轮换过渡期：旧密钥在此时间之前仍然有效（默认 7 天），让调用方有时间改配置。
/// </param>
public sealed record IssuedClientSecret(
    string ClientId,
    string ClientSecret,
    DateTimeOffset SecretExpiresAt,
    DateTimeOffset? PreviousSecretValidUntil);

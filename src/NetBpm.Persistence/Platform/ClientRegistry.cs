using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 客户端注册表的可恢复错误（带稳定错误码，由 API 层映射成 RFC 9457 响应）。
/// </summary>
/// <remarks>
/// 只表达「调用方给错了」这一类问题（未知 scope / 未确认高危 scope / 租户不存在）；
/// 查不到客户端这类可空结果一律用返回值表达，不靠异常控制流。
/// </remarks>
public sealed class ClientRegistryException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="code">稳定错误码（见 <see cref="ErrorCodes"/>）。</param>
    /// <param name="message">面向运维的说明（不含密钥等敏感信息）。</param>
    public ClientRegistryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>稳定错误码。</summary>
    public string Code { get; }
}

/// <summary>客户端注册表的默认取值。</summary>
public sealed record ClientRegistryOptions
{
    /// <summary>新签发密钥的有效期（天）。默认 90。</summary>
    public int SecretValidDays { get; init; } = 90;

    /// <summary>轮换时上一代密钥的宽限期（天）。默认 7。</summary>
    public int RotationGraceDays { get; init; } = 7;

    /// <summary>
    /// 新注册客户端的初始状态。默认 <see cref="ClientStatus.Active"/>。
    /// </summary>
    /// <remarks>
    /// 默认「注册即可用」的理由：注册本身是管理员发起的动作，已经是人工审批点，
    /// 再要求一次「激活」只会让接入方在第一次调用时收到 401 却不知道为什么。
    /// 需要「申请 → 审批 → 启用」的部署把它配成 <see cref="ClientStatus.Pending"/> 即可。
    /// </remarks>
    public ClientStatus InitialStatus { get; init; } = ClientStatus.Active;
}

/// <summary>密钥校验结果。</summary>
public enum ClientAuthenticationStatus
{
    /// <summary>通过。</summary>
    Success = 0,

    /// <summary>
    /// 客户端不存在，或密钥格式不认识。
    /// <para>两种情况对外都是 <c>auth.invalid_token</c>，不做区分（区分为枚举客户端提供了便利）。</para>
    /// </summary>
    UnknownClient = 1,

    /// <summary>密钥与当前 / 上一代摘要都不匹配。</summary>
    SecretMismatch = 2,

    /// <summary>密钥匹配但已过期（含宽限期已过）。</summary>
    SecretExpired = 3,

    /// <summary>客户端被暂停或注销（<c>PENDING</c> 也走这条：已发密钥但尚未启用）。</summary>
    ClientNotActive = 4,

    /// <summary>所属租户被停用。</summary>
    TenantSuspended = 5,
}

/// <summary>密钥校验结果与命中的客户端。</summary>
/// <param name="Status">结果。</param>
/// <param name="Client">通过时的客户端快照；未通过为 <see langword="null"/>。</param>
public sealed record ClientAuthenticationResult(
    ClientAuthenticationStatus Status,
    ClientDescriptor? Client)
{
    /// <summary>是否通过。</summary>
    public bool Succeeded => Status == ClientAuthenticationStatus.Success && Client is not null;

    /// <summary>构造一个失败结果。</summary>
    /// <param name="status">失败原因。</param>
    public static ClientAuthenticationResult Fail(ClientAuthenticationStatus status) => new(status, null);
}

/// <summary>
/// 客户端只读视图（配额总览这类读端点用）。
/// </summary>
/// <remarks>
/// 与 <see cref="ITenantDirectory"/> 同一思路：只读端点不该因为「要列一下客户端」
/// 就拿到注册 / 轮换 / 注销的能力。同一个实例的两个接口，从类型上区分能做到什么。
/// </remarks>
public interface IClientDirectory
{
    /// <summary>按 id 查客户端（不存在返回 <see langword="null"/>）。</summary>
    /// <param name="clientId">客户端 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ClientDescriptor?> FindAsync(string clientId, CancellationToken ct = default);

    /// <summary>列出客户端；<paramref name="tenantId"/> 为空表示跨租户（管理面）。</summary>
    /// <param name="tenantId">租户过滤。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ClientDescriptor>> ListAsync(
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// 按 <c>(tenant_id, client_id)</c> 升序分页（keyset：只返回严格排在游标之后的行）。
    /// </summary>
    /// <remarks>
    /// 配额总览要「一屏看全网」：客户端数是租户数 × 系统数的乘积，全量拉回再切片在接入方一多就打到内存与连接上。
    /// 游标用 <c>(tenant_id, client_id)</c> 而不是 offset —— 注册 / 注销随时发生，offset 会漏行。
    /// 默认实现是给测试替身兜底的（先取全量再切），库实现走主键与 <c>ix_nb_api_client_tenant</c>。
    /// </remarks>
    /// <param name="tenantId">租户过滤；<see langword="null"/> 表示跨租户。</param>
    /// <param name="afterTenantId">上一页最后一条的租户 id；与 <paramref name="afterClientId"/> 同时为空表示第一页。</param>
    /// <param name="afterClientId">上一页最后一条的客户端 id。</param>
    /// <param name="limit">最多返回条数。</param>
    /// <param name="ct">取消令牌。</param>
    async ValueTask<IReadOnlyList<ClientDescriptor>> ListPageAsync(
        string? tenantId,
        string? afterTenantId,
        string? afterClientId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var all = await ListAsync(tenantId, ct).ConfigureAwait(false);

        // 必须自己按 (tenant_id, client_id) 排：ListAsync 的默认顺序是 created_at，
        // 直接 Take 会得到「不属于这一页」的行，翻页就有漏行的可能。
        var ordered = all
            .OrderBy(client => client.TenantId, StringComparer.Ordinal)
            .ThenBy(client => client.ClientId, StringComparer.Ordinal);

        if (string.IsNullOrEmpty(afterTenantId) && string.IsNullOrEmpty(afterClientId))
        {
            return [.. ordered.Take(limit)];
        }

        return
        [
            .. ordered
                .Where(client => IsAfter(client, afterTenantId, afterClientId))
                .Take(limit),
        ];
    }

    /// <summary>每个租户的客户端数量（配额总览的「租户行」用）。</summary>
    /// <param name="tenantId">租户过滤；<see langword="null"/> 表示跨租户汇总。</param>
    /// <param name="ct">取消令牌。</param>
    async ValueTask<IReadOnlyDictionary<string, int>> CountByTenantAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var all = await ListAsync(tenantId, ct).ConfigureAwait(false);

        return all
            .GroupBy(client => client.TenantId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    /// <summary>游标比较：<c>(tenant_id, client_id)</c> 字典序。</summary>
    /// <param name="client">候选行。</param>
    /// <param name="afterTenantId">游标租户。</param>
    /// <param name="afterClientId">游标客户端。</param>
    private static bool IsAfter(ClientDescriptor client, string? afterTenantId, string? afterClientId)
    {
        var cursorTenant = afterTenantId ?? string.Empty;
        var byTenant = string.CompareOrdinal(client.TenantId, cursorTenant);

        return byTenant != 0
            ? byTenant > 0
            : string.CompareOrdinal(client.ClientId, afterClientId ?? string.Empty) > 0;
    }
}

/// <summary>
/// 接入客户端注册表：签发密钥、鉴权、轮换、流程白名单。
/// </summary>
/// <remarks>
/// <para>
/// 一层职责：把「一系统一客户端一密钥」落成可执行的标准。
/// 配额与限流、审计记账不在这里（它们依赖运行时状态，属于接入层）。
/// </para>
/// <para>
/// 两条不可协商的约定：
/// ① 明文密钥只在 <see cref="RegisterAsync"/> / <see cref="RotateSecretAsync"/> 的返回值里出现一次，
///    任何读取接口都不返回它，也不返回可反推的尾部（只有 <see cref="ClientSecretFormat.Describe"/> 供日志脱敏）；
/// ② 状态非 <see cref="ClientStatus.Active"/> 的客户端一律鉴权失败，即使密钥本身正确。
/// </para>
/// </remarks>
public interface IClientRegistry : IClientDirectory
{
    /// <summary>注册一个新客户端并签发首个密钥。</summary>
    /// <param name="request">注册请求。</param>
    /// <param name="allowPrivilegedScopes">
    /// 是否允许授予高危 scope（终止实例 / 代提交 / 各类 <c>workflow:admin:*</c>）。
    /// 默认 <see langword="false"/>：高危权限必须由调用方显式确认，不能跟着一串 scope 顺带发出去。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ClientRegistryException">scope 未定义 / 高危 scope 未确认 / 租户不存在。</exception>
    ValueTask<IssuedClientSecret> RegisterAsync(
        RegisterClientRequest request,
        bool allowPrivilegedScopes = false,
        CancellationToken ct = default);

    /// <summary>
    /// 局部更新（<see langword="null"/> = 不改；空集合 = 清空）。
    /// </summary>
    /// <param name="clientId">客户端 id。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="allowPrivilegedScopes">是否允许把高危 scope 加进集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ClientRegistryException">scope 未定义 / 高危 scope 未确认。</exception>
    ValueTask<ClientDescriptor?> UpdateAsync(
        string clientId,
        UpdateClientRequest request,
        bool allowPrivilegedScopes = false,
        CancellationToken ct = default);

    /// <summary>
    /// 轮换密钥：签发新密钥，旧密钥进入宽限期（默认 7 天）。
    /// </summary>
    /// <remarks>
    /// 宽限期只保留<b>一代</b>：连续轮换两次会让更早的那把立即失效。
    /// 这是为了不出现「三代密钥同时有效」的审计盲区。
    /// </remarks>
    /// <param name="clientId">客户端 id。</param>
    /// <param name="validDays">新密钥有效期（天）；为空取 <see cref="ClientRegistryOptions.SecretValidDays"/>。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ClientRegistryException">客户端已注销（注销后不再签发新密钥）。</exception>
    ValueTask<IssuedClientSecret?> RotateSecretAsync(
        string clientId,
        int? validDays = null,
        CancellationToken ct = default);

    /// <summary>注销客户端（状态置 <see cref="ClientStatus.Revoked"/>，密钥立即失效）。</summary>
    /// <param name="clientId">客户端 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<bool> RevokeAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// 用密钥原文鉴权。
    /// </summary>
    /// <param name="presentedSecret">请求携带的密钥原文。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ClientAuthenticationResult> AuthenticateAsync(
        string presentedSecret,
        CancellationToken ct = default);

    /// <summary>
    /// 客户端是否可以使用该流程（白名单为空视为全部授权）。
    /// </summary>
    /// <param name="clientId">客户端 id。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<bool> CanUseProcessAsync(
        string clientId,
        string processKey,
        CancellationToken ct = default);
}

/// <summary>scope 的解析、规范与高危判定。</summary>
internal static class ClientScopes
{
    /// <summary>
    /// 高危 scope：终止实例、代他人提交、以及全部 <c>workflow:admin:*</c>。
    /// </summary>
    /// <remarks>
    /// 这里的划分与 <see cref="PlatformScopes.IsSatisfiedBy"/> 的「不做前缀通配」是同一套风险观：
    /// 能用 <c>workflow:*</c> 顺手拿到的，都是常规业务能力；拿不到的，都必须单独一句话。
    /// </remarks>
    internal static readonly string[] Privileged =
    [
        PlatformScopes.InstanceTerminate,
        PlatformScopes.TaskSubmitOnBehalf,
        PlatformScopes.AdminProcess,
        PlatformScopes.AdminInstance,
        PlatformScopes.AdminVariables,
        PlatformScopes.AdminClient,
    ];

    internal static string Format(IEnumerable<string> scopes) =>
        string.Join(' ', scopes.OrderBy(scope => scope, StringComparer.Ordinal));

    internal static string[] Parse(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>校验并规范化 scope 集合（去重 + 排序，保证「同集合 → 同字符串」）。</summary>
    internal static string[] Validate(IReadOnlyCollection<string>? requested, bool allowPrivilegedScopes)
    {
        if (requested is null || requested.Count == 0)
        {
            return [];
        }

        var normalized = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var scope in requested)
        {
            var value = scope?.Trim() ?? string.Empty;

            if (!PlatformScopes.All.Contains(value, StringComparer.Ordinal))
            {
                throw new ClientRegistryException(
                    ErrorCodes.ClientScopeUnknown,
                    $"未定义的 scope：'{value}'。可用取值见 PlatformScopes.All。");
            }

            normalized.Add(value);
        }

        if (!allowPrivilegedScopes)
        {
            var privileged = normalized.FirstOrDefault(scope => Privileged.Contains(scope, StringComparer.Ordinal));

            if (privileged is not null)
            {
                throw new ClientRegistryException(
                    ErrorCodes.ClientScopeNeedsConfirmation,
                    $"'{privileged}' 属于高危 scope：需显式确认后才可授予（allowPrivilegedScopes=true）。");
            }
        }

        return [.. normalized];
    }
}

/// <summary>客户端状态列与对外词汇的互转。</summary>
internal static class ClientStatusText
{
    internal static string ToText(ClientStatus status) => status switch
    {
        ClientStatus.Active => "ACTIVE",
        ClientStatus.Suspended => "SUSPENDED",
        ClientStatus.Revoked => "REVOKED",
        _ => "PENDING",
    };

    /// <summary>解析状态列；不认识的值按 <see cref="ClientStatus.Pending"/> 处理（失败关闭）。</summary>
    internal static ClientStatus Parse(string? text) =>
        Enum.TryParse<ClientStatus>(text, ignoreCase: true, out var status)
            ? status
            : ClientStatus.Pending;
}

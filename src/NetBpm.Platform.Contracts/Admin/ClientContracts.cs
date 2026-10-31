namespace NetBpm.Platform.Contracts;

/// <summary>
/// 租户（管理面，<c>GET /admin/v1/tenants</c>）。
/// </summary>
/// <remarks>
/// 用 <see cref="Active"/> 而不是枚举，是为了不引入第二套「状态词汇」：
/// 租户只有「能用 / 不能用」两种事实，多一个枚举就多一份要与客户端状态对齐的映射。
/// </remarks>
/// <param name="TenantId">租户 id。</param>
/// <param name="Name">名称。</param>
/// <param name="Active">是否可用（停用后其下全部客户端鉴权失败，但数据不动）。</param>
/// <param name="QuotaPerMinute">默认每分钟配额（客户端未单独配置时取此值）。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record TenantView(
    string TenantId,
    string Name,
    bool Active,
    int QuotaPerMinute,
    DateTimeOffset CreatedAt);

/// <summary>
/// 登记租户（管理面，<c>PUT /admin/v1/tenants/{tenantId}</c>）。
/// </summary>
/// <remarks>
/// 幂等：已存在则原样返回，<b>不覆盖</b>名称与配额——否则每次启动引导都会把运维手工调过的值打回默认。
/// </remarks>
/// <param name="Name">名称。</param>
/// <param name="QuotaPerMinute">默认每分钟配额；为空取平台默认值。</param>
public sealed record RegisterTenantRequest(string Name, int? QuotaPerMinute = null);

/// <summary>
/// 修改租户状态（管理面，<c>PUT /admin/v1/tenants/{tenantId}/status</c>）。
/// </summary>
/// <remarks>
/// 停用租户会让其下<b>全部客户端</b>鉴权失败，是做「整租户止血」的开关；
/// 数据一行不动，所以是可恢复操作。
/// </remarks>
/// <param name="Active">是否可用。</param>
public sealed record SetTenantStatusRequest(bool Active);

/// <summary>
/// 注册接入系统（管理面，<c>POST /admin/v1/clients</c>）。
/// </summary>
/// <remarks>
/// 接入流程的第一步：平台管理员按本请求为一个业务系统登记客户端、分配 scope 与可用流程，
/// 响应返回 <see cref="IssuedClientSecret"/>（明文只此一次）。
/// 之后该系统的所有调用都以 <see cref="ClientDescriptor.ClientId"/> 记账。
/// </remarks>
/// <param name="Name">系统名称（进审计与运维看板）。</param>
/// <param name="TenantId">所属租户。</param>
/// <param name="Scopes">授予的 scope；管理类 scope 会被服务端单独确认。</param>
/// <param name="AllowedProcessKeys">可用流程白名单；为空表示全部（不推荐）。</param>
/// <param name="QuotaPerMinute">每分钟请求配额；为空则取租户默认值。</param>
/// <param name="SecretValidDays">密钥有效期（天）；为空取默认 90 天。</param>
public sealed record RegisterClientRequest(
    string Name,
    string TenantId,
    IReadOnlyCollection<string> Scopes,
    IReadOnlyCollection<string>? AllowedProcessKeys = null,
    int? QuotaPerMinute = null,
    int? SecretValidDays = null);

/// <summary>
/// 修改接入系统（管理面，<c>PATCH /admin/v1/clients/{clientId}</c>）。
/// </summary>
/// <remarks>
/// 所有字段为 <see langword="null"/> 表示「不修改」；给空集合表示「清空」。
/// 这是刻意不做 <c>PUT</c> 全量覆盖的：漏传一个字段就把 scope 清空，是多系统接入时最常见的事故。
/// </remarks>
/// <param name="Name">新名称。</param>
/// <param name="Status">新状态（暂停 / 启用 / 注销）。</param>
/// <param name="Scopes">新 scope 集合。</param>
/// <param name="AllowedProcessKeys">新流程白名单。</param>
/// <param name="QuotaPerMinute">新配额。</param>
public sealed record UpdateClientRequest(
    string? Name = null,
    ClientStatus? Status = null,
    IReadOnlyCollection<string>? Scopes = null,
    IReadOnlyCollection<string>? AllowedProcessKeys = null,
    int? QuotaPerMinute = null);

/// <summary>
/// 客户端用量（配额与限流看板）。
/// </summary>
/// <param name="ClientId">客户端 id。</param>
/// <param name="WindowStart">统计窗口起点。</param>
/// <param name="Requests">窗口内请求数。</param>
/// <param name="Rejected">其中被限流的请求数。</param>
/// <param name="QuotaPerMinute">当前配额。</param>
public sealed record ClientUsage(
    string ClientId,
    DateTimeOffset WindowStart,
    long Requests,
    long Rejected,
    int QuotaPerMinute);

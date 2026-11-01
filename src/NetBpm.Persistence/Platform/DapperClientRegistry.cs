using Dapper;
using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 数据库客户端注册表。表结构见 <c>db/{dialect}/netbpm_init.sql</c> 的
/// <c>nb_api_client</c> 与 <c>nb_client_process_grant</c>。
/// </summary>
/// <remarks>
/// <para>
/// 鉴权是热路径，因此只做「按主键查一行 + 最多两次定长比较 + 一次白名单查询」，
/// 且 <c>last_used_at</c> 按分钟节流回写（一分钟内的重复调用不产生任何写语句）。
/// </para>
/// <para>
/// 白名单查询在鉴权路径上是必要的：调用方要不要放行这条流程，取决于它。
/// 若以后成为瓶颈，应在接入层加一个短 TTL 的客户端缓存（而不是把白名单塞进令牌）。
/// </para>
/// </remarks>
public sealed class DapperClientRegistry : IClientRegistry
{
    private const string ClientColumns =
        "id as Id, tenant_id as TenantId, name as Name, status as Status, scopes as Scopes, " +
        "quota_per_minute as QuotaPerMinute, secret_hash as SecretHash, " +
        "secret_expires_at as SecretExpiresAt, previous_secret_hash as PreviousSecretHash, " +
        "previous_secret_expires_at as PreviousSecretExpiresAt, created_at as CreatedAt, " +
        "updated_at as UpdatedAt, last_used_at as LastUsedAt";

    private const string GrantColumns = "client_id as ClientId, process_key as ProcessKey";

    private readonly IDbConnectionFactory _connections;
    private readonly ITenantDirectory _tenants;
    private readonly ClientRegistryOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>构造。</summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="tenants">租户只读视图（注册时校验归属、鉴权时判停用）。</param>
    /// <param name="options">默认取值。</param>
    /// <param name="clock">时钟（测试可替换）。</param>
    public DapperClientRegistry(
        IDbConnectionFactory connections,
        ITenantDirectory tenants,
        ClientRegistryOptions? options = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(tenants);

        _connections = connections;
        _tenants = tenants;
        _options = options ?? new ClientRegistryOptions();
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<IssuedClientSecret> RegisterAsync(
        RegisterClientRequest request,
        bool allowPrivilegedScopes = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = Required(request.Name, "name");
        var tenantId = Required(request.TenantId, "tenantId");
        var scopes = ClientScopes.Validate(request.Scopes, allowPrivilegedScopes);
        var processKeys = NormalizeProcessKeys(request.AllowedProcessKeys);

        var tenant = await _tenants.FindAsync(tenantId, ct).ConfigureAwait(false)
            ?? throw new ClientRegistryException(
                ErrorCodes.TenantNotFound,
                $"租户 '{tenantId}' 不存在：请先登记租户（nb_tenant）再注册客户端。");

        var quota = request.QuotaPerMinute is { } requestedQuota && requestedQuota > 0
            ? requestedQuota
            : tenant.QuotaPerMinute;
        var validDays = request.SecretValidDays is { } requestedDays && requestedDays > 0
            ? requestedDays
            : _options.SecretValidDays;
        var now = _clock.GetUtcNow();
        var expiresAt = now.AddDays(validDays);

        var (clientId, secret, hash) = IssueSecret();

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into nb_api_client
                (id, tenant_id, name, status, scopes, quota_per_minute, secret_hash, secret_expires_at,
                 previous_secret_hash, previous_secret_expires_at, created_at, updated_at, last_used_at)
            values
                (@clientId, @tenantId, @name, @status, @scopes, @quota, @hash, @expiresAt,
                 null, null, @now, @now, null)
            """,
            new
            {
                clientId,
                tenantId,
                name,
                status = ClientStatusText.ToText(_options.InitialStatus),
                scopes = ClientScopes.Format(scopes),
                quota,
                hash,
                expiresAt = expiresAt.UtcDateTime,
                now = now.UtcDateTime,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await InsertGrantsAsync(connection, transaction, clientId, processKeys, now.UtcDateTime, null, ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new IssuedClientSecret(clientId, secret, expiresAt, null);
    }

    /// <inheritdoc />
    public async ValueTask<ClientDescriptor?> FindAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        await using var connection = _connections.CreateConnection();

        var row = await QueryClientAsync(connection, clientId.Trim(), ct).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var grants = await LoadGrantsAsync(connection, row.Id, ct).ConfigureAwait(false);

        return row.ToDescriptor(grants);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClientDescriptor>> ListAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = (await connection.QueryAsync<ClientRow>(new CommandDefinition(
            $"select {ClientColumns} from nb_api_client " +
            "where (@tenantId is null or tenant_id = @tenantId) order by created_at, id",
            new { tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim() },
            cancellationToken: ct)).ConfigureAwait(false)).ToList();

        if (rows.Count == 0)
        {
            return [];
        }

        // 一次取全部白名单，避免「N 个客户端 × 1 次查询」
        var ids = rows.Select(row => row.Id).ToArray();

        var grantRows = await connection.QueryAsync<GrantRow>(new CommandDefinition(
            $"select {GrantColumns} from nb_client_process_grant where client_id in @ids " +
            "order by client_id, process_key",
            new { ids },
            cancellationToken: ct)).ConfigureAwait(false);

        var lookup = grantRows
            .GroupBy(grant => grant.ClientId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(grant => grant.ProcessKey).ToArray(),
                StringComparer.Ordinal);

        return
        [
            .. rows.Select(row => row.ToDescriptor(
                lookup.TryGetValue(row.Id, out var grants) ? grants : [])),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClientDescriptor>> ListPageAsync(
        string? tenantId,
        string? afterTenantId,
        string? afterClientId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = _connections.CreateConnection();

        var parameters = new DynamicParameters();
        var conditions = new List<string> { "(@tenantId is null or tenant_id = @tenantId)" };
        parameters.Add("tenantId", string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim());

        if (!string.IsNullOrEmpty(afterTenantId) || !string.IsNullOrEmpty(afterClientId))
        {
            // keyset：先比租户再比客户端 id（与 order by 完全一致，翻页不重不漏）
            conditions.Add("(tenant_id > @afterTenant or (tenant_id = @afterTenant and id > @afterClient))");
            parameters.Add("afterTenant", afterTenantId ?? string.Empty);
            parameters.Add("afterClient", afterClientId ?? string.Empty);
        }

        var paging = SqlPaging.Build(_connections.DatabaseType, 0, limit, parameters);

        var rows = (await connection.QueryAsync<ClientRow>(new CommandDefinition(
            $"select {ClientColumns} from nb_api_client where {string.Join(" and ", conditions)} "
            + $"order by tenant_id, id{paging}",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false)).ToList();

        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(row => row.Id).ToArray();

        var grantRows = await connection.QueryAsync<GrantRow>(new CommandDefinition(
            $"select {GrantColumns} from nb_client_process_grant where client_id in @ids " +
            "order by client_id, process_key",
            new { ids },
            cancellationToken: ct)).ConfigureAwait(false);

        var lookup = grantRows
            .GroupBy(grant => grant.ClientId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(grant => grant.ProcessKey).ToArray(),
                StringComparer.Ordinal);

        return
        [
            .. rows.Select(row => row.ToDescriptor(
                lookup.TryGetValue(row.Id, out var grants) ? grants : [])),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, int>> CountByTenantAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        // 一条 group by 代替「每行租户再查一次 count」：配额总览的租户行最多一页，
        // 但按租户逐个 count 是 N 次往返，接入方一多就是翻页卡顿。
        var rows = await connection.QueryAsync<TenantCountRow>(new CommandDefinition(
            """
            select tenant_id as TenantId, count(*) as Total
            from nb_api_client
            where (@tenantId is null or tenant_id = @tenantId)
            group by tenant_id
            """,
            new { tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim() },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToDictionary(row => row.TenantId, row => row.Total, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async ValueTask<ClientDescriptor?> UpdateAsync(
        string clientId,
        UpdateClientRequest request,
        bool allowPrivilegedScopes = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        await using var connection = _connections.CreateConnection();

        var row = await QueryClientAsync(connection, clientId.Trim(), ct).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var name = request.Name is null ? row.Name : Required(request.Name, "name");
        var status = request.Status ?? ClientStatusText.Parse(row.Status);
        var scopes = request.Scopes is null
            ? ClientScopes.Parse(row.Scopes)
            : ClientScopes.Validate(request.Scopes, allowPrivilegedScopes);
        var quota = request.QuotaPerMinute ?? row.QuotaPerMinute;
        var processKeys = request.AllowedProcessKeys is null
            ? null
            : NormalizeProcessKeys(request.AllowedProcessKeys);
        var now = _clock.GetUtcNow().UtcDateTime;

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update nb_api_client
            set name = @name, status = @status, scopes = @scopes, quota_per_minute = @quota,
                updated_at = @now
            where id = @clientId
            """,
            new
            {
                clientId = row.Id,
                name,
                status = ClientStatusText.ToText(status),
                scopes = ClientScopes.Format(scopes),
                quota,
                now,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (processKeys is not null)
        {
            // 白名单用「整体替换」而不是增量：授权集合是管理面的一次决策，
            // 增量接口会让「漏删一条」变成长期越权。
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from nb_client_process_grant where client_id = @clientId",
                new { clientId = row.Id },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

            await InsertGrantsAsync(connection, transaction, row.Id, processKeys, now, null, ct)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        var grants = await LoadGrantsAsync(connection, row.Id, ct).ConfigureAwait(false);

        return row.ToDescriptor(grants, name, status, scopes, quota);
    }

    /// <inheritdoc />
    public async ValueTask<IssuedClientSecret?> RotateSecretAsync(
        string clientId,
        int? validDays = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        await using var connection = _connections.CreateConnection();

        var row = await QueryClientAsync(connection, clientId.Trim(), ct).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        if (ClientStatusText.Parse(row.Status) == ClientStatus.Revoked)
        {
            throw new ClientRegistryException(
                ErrorCodes.AuthClientDisabled,
                $"客户端 '{row.Id}' 已注销：不再签发新密钥（需要恢复请重新注册）。");
        }

        var days = validDays is { } requested && requested > 0 ? requested : _options.SecretValidDays;
        var now = _clock.GetUtcNow();
        var expiresAt = now.AddDays(days);
        var graceUntil = now.AddDays(_options.RotationGraceDays);

        var (_, secret, hash) = IssueSecret(row.Id);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update nb_api_client
            set previous_secret_hash = secret_hash,
                previous_secret_expires_at = @graceUntil,
                secret_hash = @hash,
                secret_expires_at = @expiresAt,
                updated_at = @now
            where id = @clientId
            """,
            new
            {
                clientId = row.Id,
                graceUntil = graceUntil.UtcDateTime,
                hash,
                expiresAt = expiresAt.UtcDateTime,
                now = now.UtcDateTime,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return new IssuedClientSecret(row.Id, secret, expiresAt, graceUntil);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RevokeAsync(string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "update nb_api_client set status = @status, updated_at = @now where id = @clientId",
            new
            {
                clientId = clientId.Trim(),
                status = ClientStatusText.ToText(ClientStatus.Revoked),
                now = _clock.GetUtcNow().UtcDateTime,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async ValueTask<ClientAuthenticationResult> AuthenticateAsync(
        string presentedSecret,
        CancellationToken ct = default)
    {
        // 格式不认识 = 客户端 id 都解析不出来，与「id 不存在」对外同等处理
        if (!ClientSecretFormat.TryParse(presentedSecret, out var clientId, out _))
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.UnknownClient);
        }

        await using var connection = _connections.CreateConnection();

        var row = await QueryClientAsync(connection, clientId, ct).ConfigureAwait(false);

        if (row is null)
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.UnknownClient);
        }

        var now = _clock.GetUtcNow();

        var matchedCurrent = ClientSecretFormat.Verify(presentedSecret, row.SecretHash);
        var matchedPrevious = !matchedCurrent
            && ClientSecretFormat.Verify(presentedSecret, row.PreviousSecretHash);

        if (!matchedCurrent && !matchedPrevious)
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.SecretMismatch);
        }

        // 先验密钥再看状态：否则拿一把瞎猜的密钥就能探出「这个客户端被停用了」
        if (IsExpired(matchedCurrent ? row.SecretExpiresAt : row.PreviousSecretExpiresAt, now))
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.SecretExpired);
        }

        var status = ClientStatusText.Parse(row.Status);

        if (status != ClientStatus.Active)
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.ClientNotActive);
        }

        var tenant = await _tenants.FindAsync(row.TenantId, ct).ConfigureAwait(false);

        // 租户不存在（被清理）与租户停用同等处理：不允许在孤儿数据上继续调用
        if (tenant is null || !tenant.IsUsable)
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.TenantSuspended);
        }

        await TouchAsync(connection, row, now, ct).ConfigureAwait(false);

        var grants = await LoadGrantsAsync(connection, row.Id, ct).ConfigureAwait(false);

        return new ClientAuthenticationResult(
            ClientAuthenticationStatus.Success,
            row.ToDescriptor(grants));
    }

    /// <inheritdoc />
    public async ValueTask<bool> CanUseProcessAsync(
        string clientId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        await using var connection = _connections.CreateConnection();

        var counts = await connection.QuerySingleAsync<GrantCount>(new CommandDefinition(
            """
            select count(*) as Total,
                   coalesce(sum(case when process_key = @processKey then 1 else 0 end), 0) as Matched
            from nb_client_process_grant
            where client_id = @clientId
            """,
            new { clientId = clientId.Trim(), processKey = processKey.Trim() },
            cancellationToken: ct)).ConfigureAwait(false);

        // 没有授权行 = 不限流程（建库注释里写明了「不推荐」，但语义必须明确）
        return counts.Total == 0 || counts.Matched > 0;
    }

    private static bool IsExpired(DateTime? expiresAt, DateTimeOffset now) =>
        expiresAt is { } actual && actual < now.UtcDateTime;

    private static (string ClientId, string Secret, string Hash) IssueSecret(string? clientId = null)
    {
        var id = string.IsNullOrEmpty(clientId) ? ClientSecretFormat.NewClientId() : clientId;
        var secret = ClientSecretFormat.Compose(id, ClientSecretFormat.NewToken());

        return (id, secret, ClientSecretFormat.Hash(secret));
    }

    private static string Required(string? value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("不能为空。", paramName)
            : value.Trim();

    private static string[]? NormalizeProcessKeys(IReadOnlyCollection<string>? keys)
    {
        if (keys is null)
        {
            return null;
        }

        var normalized = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("流程白名单里不能有空值。", nameof(keys));
            }

            normalized.Add(key.Trim());
        }

        return [.. normalized];
    }

    private static async Task<ClientRow?> QueryClientAsync(
        System.Data.Common.DbConnection connection,
        string clientId,
        CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<ClientRow>(new CommandDefinition(
            $"select {ClientColumns} from nb_api_client where id = @clientId",
            new { clientId },
            cancellationToken: ct)).ConfigureAwait(false);

    private static async Task<string[]> LoadGrantsAsync(
        System.Data.Common.DbConnection connection,
        string clientId,
        CancellationToken ct) =>
        [.. await connection.QueryAsync<string>(new CommandDefinition(
            "select process_key from nb_client_process_grant where client_id = @clientId " +
            "order by process_key",
            new { clientId },
            cancellationToken: ct)).ConfigureAwait(false)];

    private static async Task InsertGrantsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string clientId,
        string[]? processKeys,
        DateTime grantedAt,
        string? grantedBy,
        CancellationToken ct)
    {
        if (processKeys is null || processKeys.Length == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into nb_client_process_grant (client_id, process_key, granted_at, granted_by)
            values (@clientId, @processKey, @grantedAt, @grantedBy)
            """,
            processKeys.Select(key => new { clientId, processKey = key, grantedAt, grantedBy }),
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>last_used_at</c> 按分钟节流回写：一分钟内的重复调用不再产生写语句。
    /// </summary>
    private static async Task TouchAsync(
        System.Data.Common.DbConnection connection,
        ClientRow row,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (row.LastUsedAt is { } lastUsed && lastUsed > now.UtcDateTime.AddMinutes(-1))
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update nb_api_client set last_used_at = @now where id = @clientId",
            new { clientId = row.Id, now = now.UtcDateTime },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>按租户聚合的客户端数量（配额总览用）。</summary>
    private sealed class TenantCountRow
    {
        public string TenantId { get; set; } = string.Empty;

        public int Total { get; set; }
    }

    private sealed class ClientRow
    {
        public string Id { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Status { get; set; }

        public string? Scopes { get; set; }

        public int QuotaPerMinute { get; set; }

        public string SecretHash { get; set; } = string.Empty;

        public DateTime? SecretExpiresAt { get; set; }

        public string? PreviousSecretHash { get; set; }

        public DateTime? PreviousSecretExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public DateTime? LastUsedAt { get; set; }

        /// <summary>
        /// 投影成对外契约；<paramref name="name"/> 等四个可选参数用于「刚更新完、还没重读」的场景。
        /// </summary>
        internal ClientDescriptor ToDescriptor(
            IReadOnlyCollection<string> grants,
            string? name = null,
            ClientStatus? status = null,
            IReadOnlyCollection<string>? scopes = null,
            int? quotaPerMinute = null) => new(
            Id,
            name ?? Name,
            TenantId,
            status ?? ClientStatusText.Parse(Status),
            scopes ?? ClientScopes.Parse(Scopes),
            grants,
            quotaPerMinute ?? QuotaPerMinute,
            PlatformTimestamps.ToOffset(CreatedAt),
            PlatformTimestamps.ToOffset(SecretExpiresAt));
    }

    private sealed class GrantRow
    {
        public string ClientId { get; set; } = string.Empty;

        public string ProcessKey { get; set; } = string.Empty;
    }

    private sealed class GrantCount
    {
        public long Total { get; set; }

        public long Matched { get; set; }
    }
}

using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 内存客户端注册表：无库（in-memory）模式与单元测试使用。
/// </summary>
/// <remarks>
/// 语义与 <see cref="DapperClientRegistry"/> 逐条对齐（包括「先验密钥再看状态」「宽限期只有一代」
/// 「白名单整体替换」），否则单测通过而生产失败——那比没有单测更糟。
/// 唯一差别是它不落库、进程重启即失效。
/// </remarks>
public sealed class InMemoryClientRegistry : IClientRegistry
{
    private readonly Dictionary<string, Record> _clients = new(StringComparer.Ordinal);
    private readonly ITenantDirectory _tenants;
    private readonly ClientRegistryOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    /// <summary>构造。</summary>
    /// <param name="tenants">租户只读视图（注册校验归属、鉴权判停用）。</param>
    /// <param name="options">默认取值。</param>
    /// <param name="clock">时钟（测试可替换）。</param>
    public InMemoryClientRegistry(
        ITenantDirectory tenants,
        ClientRegistryOptions? options = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tenants);

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

        var clientId = ClientSecretFormat.NewClientId();
        var secret = ClientSecretFormat.Compose(clientId, ClientSecretFormat.NewToken());

        lock (_gate)
        {
            _clients[clientId] = new Record
            {
                Id = clientId,
                TenantId = tenantId,
                Name = name,
                Status = _options.InitialStatus,
                Scopes = [.. scopes],
                QuotaPerMinute = quota,
                SecretHash = ClientSecretFormat.Hash(secret),
                SecretExpiresAt = expiresAt,
                CreatedAt = now,
                UpdatedAt = now,
                Grants = new HashSet<string>(processKeys ?? [], StringComparer.Ordinal),
            };
        }

        return new IssuedClientSecret(clientId, secret, expiresAt, null);
    }

    /// <inheritdoc />
    public ValueTask<ClientDescriptor?> FindAsync(string clientId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _clients.TryGetValue(clientId?.Trim() ?? string.Empty, out var record)
                    ? record.ToDescriptor()
                    : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClientDescriptor>> ListAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();

        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<ClientDescriptor>>(
            [
                .. _clients.Values
                    .Where(record => filter is null || string.Equals(record.TenantId, filter, StringComparison.Ordinal))
                    .OrderBy(record => record.CreatedAt)
                    .ThenBy(record => record.Id, StringComparer.Ordinal)
                    .Select(record => record.ToDescriptor()),
            ]);
        }
    }

    /// <inheritdoc />
    public ValueTask<ClientDescriptor?> UpdateAsync(
        string clientId,
        UpdateClientRequest request,
        bool allowPrivilegedScopes = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        lock (_gate)
        {
            if (!_clients.TryGetValue(clientId.Trim(), out var record))
            {
                return ValueTask.FromResult<ClientDescriptor?>(null);
            }

            record.Name = request.Name is null ? record.Name : Required(request.Name, "name");
            record.Status = request.Status ?? record.Status;

            if (request.Scopes is not null)
            {
                record.Scopes = [.. ClientScopes.Validate(request.Scopes, allowPrivilegedScopes)];
            }

            record.QuotaPerMinute = request.QuotaPerMinute ?? record.QuotaPerMinute;

            if (request.AllowedProcessKeys is not null)
            {
                record.Grants = new HashSet<string>(
                    NormalizeProcessKeys(request.AllowedProcessKeys)!,
                    StringComparer.Ordinal);
            }

            record.UpdatedAt = _clock.GetUtcNow();

            return ValueTask.FromResult<ClientDescriptor?>(record.ToDescriptor());
        }
    }

    /// <inheritdoc />
    public ValueTask<IssuedClientSecret?> RotateSecretAsync(
        string clientId,
        int? validDays = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        lock (_gate)
        {
            if (!_clients.TryGetValue(clientId.Trim(), out var record))
            {
                return ValueTask.FromResult<IssuedClientSecret?>(null);
            }

            if (record.Status == ClientStatus.Revoked)
            {
                throw new ClientRegistryException(
                    ErrorCodes.AuthClientDisabled,
                    $"客户端 '{record.Id}' 已注销：不再签发新密钥（需要恢复请重新注册）。");
            }

            var days = validDays is { } requested && requested > 0 ? requested : _options.SecretValidDays;
            var now = _clock.GetUtcNow();
            var expiresAt = now.AddDays(days);
            var graceUntil = now.AddDays(_options.RotationGraceDays);

            var secret = ClientSecretFormat.Compose(record.Id, ClientSecretFormat.NewToken());

            record.PreviousSecretHash = record.SecretHash;
            record.PreviousSecretExpiresAt = graceUntil;
            record.SecretHash = ClientSecretFormat.Hash(secret);
            record.SecretExpiresAt = expiresAt;
            record.UpdatedAt = now;

            return ValueTask.FromResult<IssuedClientSecret?>(
                new IssuedClientSecret(record.Id, secret, expiresAt, graceUntil));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RevokeAsync(string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        lock (_gate)
        {
            if (!_clients.TryGetValue(clientId.Trim(), out var record))
            {
                return ValueTask.FromResult(false);
            }

            record.Status = ClientStatus.Revoked;
            record.UpdatedAt = _clock.GetUtcNow();

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ClientAuthenticationResult> AuthenticateAsync(
        string presentedSecret,
        CancellationToken ct = default)
    {
        if (!ClientSecretFormat.TryParse(presentedSecret, out var clientId, out _))
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.UnknownClient);
        }

        Record? record;

        lock (_gate)
        {
            _clients.TryGetValue(clientId, out record);
        }

        if (record is null)
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.UnknownClient);
        }

        var now = _clock.GetUtcNow();

        bool matchedCurrent;
        bool matchedPrevious;
        ClientDescriptor snapshot;

        lock (_gate)
        {
            matchedCurrent = ClientSecretFormat.Verify(presentedSecret, record.SecretHash);
            matchedPrevious = !matchedCurrent
                && ClientSecretFormat.Verify(presentedSecret, record.PreviousSecretHash);

            if (!matchedCurrent && !matchedPrevious)
            {
                return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.SecretMismatch);
            }

            var expiresAt = matchedCurrent ? record.SecretExpiresAt : record.PreviousSecretExpiresAt;

            if (expiresAt is { } actual && actual < now)
            {
                return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.SecretExpired);
            }

            if (record.Status != ClientStatus.Active)
            {
                return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.ClientNotActive);
            }

            snapshot = record.ToDescriptor();
        }

        var tenant = await _tenants.FindAsync(snapshot.TenantId, ct).ConfigureAwait(false);

        if (tenant is null || !tenant.IsUsable)
        {
            return ClientAuthenticationResult.Fail(ClientAuthenticationStatus.TenantSuspended);
        }

        lock (_gate)
        {
            if (record.LastUsedAt is null || record.LastUsedAt < now.AddMinutes(-1))
            {
                record.LastUsedAt = now;
            }
        }

        return new ClientAuthenticationResult(ClientAuthenticationStatus.Success, snapshot);
    }

    /// <inheritdoc />
    public ValueTask<bool> CanUseProcessAsync(
        string clientId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        lock (_gate)
        {
            if (!_clients.TryGetValue(clientId.Trim(), out var record))
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(
                record.Grants.Count == 0 || record.Grants.Contains(processKey.Trim()));
        }
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

    private sealed class Record
    {
        public string Id { get; init; } = string.Empty;

        public string TenantId { get; init; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public ClientStatus Status { get; set; }

        public List<string> Scopes { get; set; } = [];

        public int QuotaPerMinute { get; set; }

        public string SecretHash { get; set; } = string.Empty;

        public DateTimeOffset? SecretExpiresAt { get; set; }

        public string? PreviousSecretHash { get; set; }

        public DateTimeOffset? PreviousSecretExpiresAt { get; set; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? LastUsedAt { get; set; }

        public HashSet<string> Grants { get; set; } = new(StringComparer.Ordinal);

        public ClientDescriptor ToDescriptor() => new(
            Id,
            Name,
            TenantId,
            Status,
            [.. Scopes],
            [.. Grants.OrderBy(key => key, StringComparer.Ordinal)],
            QuotaPerMinute,
            CreatedAt,
            SecretExpiresAt);
    }
}

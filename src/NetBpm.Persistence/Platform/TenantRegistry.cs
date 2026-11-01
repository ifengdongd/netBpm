using Dapper;

namespace NetBpm.Persistence.Platform;

/// <summary>租户状态（库内以对外词汇 <c>ACTIVE</c> / <c>SUSPENDED</c> 存，不存枚举序号）。</summary>
public enum TenantStatus
{
    /// <summary>可用。</summary>
    Active = 0,

    /// <summary>停用：其下客户端鉴权一律失败，但数据不动（可恢复）。</summary>
    Suspended = 1,
}

/// <summary>租户（<c>nb_tenant</c> 表的一行）。</summary>
/// <param name="TenantId">租户 id。</param>
/// <param name="Name">名称。</param>
/// <param name="Status">状态。</param>
/// <param name="QuotaPerMinute">默认每分钟配额（客户端未单独配置时取此值）。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record TenantDescriptor(
    string TenantId,
    string Name,
    TenantStatus Status,
    int QuotaPerMinute,
    DateTimeOffset CreatedAt)
{
    /// <summary>是否可用（停用租户下的客户端一律不得调用）。</summary>
    public bool IsUsable => Status == TenantStatus.Active;
}

/// <summary>登记（或确认已存在）一个租户。用于首次部署的幂等引导。</summary>
/// <param name="TenantId">租户 id（一期固定 <c>default</c>）。</param>
/// <param name="Name">名称。</param>
/// <param name="QuotaPerMinute">默认配额。</param>
public sealed record TenantRegistration(string TenantId, string Name, int QuotaPerMinute = 600);

/// <summary>
/// 租户只读视图（鉴权只需要「这个租户在不在、是否停用、默认配额多少」）。
/// </summary>
/// <remarks>
/// 单独抽出只读接口是为了让鉴权路径不依赖可写的注册表：
/// 独立服务里鉴权组件只被注入只读依赖，从代码上就写不了租户。
/// </remarks>
public interface ITenantDirectory
{
    /// <summary>按 id 找租户（找不到返回 <see langword="null"/>）。</summary>
    /// <param name="tenantId">租户 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<TenantDescriptor?> FindAsync(string tenantId, CancellationToken ct = default);

    /// <summary>全部租户（按 id 排序）。</summary>
    /// <remarks>
    /// 放在只读接口上而非写接口：配额总览要列全部租户，但它不该因此拿到
    /// <c>EnsureAsync</c> / <c>SetStatusAsync</c>（能改租户状态的依赖不该出现在读端点上）。
    /// </remarks>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<TenantDescriptor>> ListAsync(CancellationToken ct = default);
}

/// <summary>租户注册表（管理面与首次部署引导使用）。</summary>
public interface ITenantRegistry : ITenantDirectory
{
    /// <summary>
    /// 幂等登记租户：已存在则原样返回（<b>不</b>覆盖名称与配额），不存在才插入。
    /// </summary>
    /// <remarks>
    /// 刻意不做 upsert：启动引导每次都会调用它，若覆盖写，重启一次就会把运维手工调过的配额打回默认值。
    /// </remarks>
    /// <param name="registration">登记信息。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<TenantDescriptor> EnsureAsync(TenantRegistration registration, CancellationToken ct = default);

    /// <summary>改状态；租户不存在返回 <see langword="false"/>。</summary>
    /// <param name="tenantId">租户 id。</param>
    /// <param name="status">新状态。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<bool> SetStatusAsync(string tenantId, TenantStatus status, CancellationToken ct = default);
}

/// <summary>状态列与对外词汇的互转（两方言共用一套文本值）。</summary>
internal static class TenantStatusText
{
    internal const string Active = "ACTIVE";
    internal const string Suspended = "SUSPENDED";

    internal static string ToText(TenantStatus status) =>
        status == TenantStatus.Suspended ? Suspended : Active;

    internal static TenantStatus Parse(string? text) =>
        string.Equals(text, Suspended, StringComparison.OrdinalIgnoreCase)
            ? TenantStatus.Suspended
            : TenantStatus.Active;
}

/// <summary>
/// 数据库租户注册表。表结构见 <c>db/{dialect}/netbpm_init.sql</c> 的 <c>nb_tenant</c>。
/// </summary>
public sealed class DapperTenantRegistry : ITenantRegistry
{
    private const string Columns =
        "id as TenantId, name as Name, status as Status, quota_per_minute as QuotaPerMinute, " +
        "created_at as CreatedAt";

    private readonly IDbConnectionFactory _connections;
    private readonly TimeProvider _clock;

    /// <summary>构造。</summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="clock">时钟（测试可替换）。</param>
    public DapperTenantRegistry(IDbConnectionFactory connections, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<TenantDescriptor?> FindAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        await using var connection = _connections.CreateConnection();

        var row = await connection.QuerySingleOrDefaultAsync<TenantRow>(new CommandDefinition(
            $"select {Columns} from nb_tenant where id = @tenantId",
            new { tenantId = tenantId.Trim() },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public async ValueTask<TenantDescriptor> EnsureAsync(
        TenantRegistration registration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var tenantId = Require(registration.TenantId, nameof(registration));
        var name = Require(registration.Name, nameof(registration));

        var existing = await FindAsync(tenantId, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        await using var connection = _connections.CreateConnection();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into nb_tenant (id, name, status, quota_per_minute, created_at, updated_at)
                values (@tenantId, @name, @status, @quota, @now, @now)
                """,
                new
                {
                    tenantId,
                    name,
                    status = TenantStatusText.Active,
                    quota = registration.QuotaPerMinute <= 0 ? 600 : registration.QuotaPerMinute,
                    now,
                },
                cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (Exception ex) when (DatabaseErrors.IsUniqueViolation(ex))
        {
            // 并发引导（多副本同时启动）时另一方已插入：按「已存在」处理，不覆盖
        }

        return await FindAsync(tenantId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"租户 '{tenantId}' 写入后读取不到。");
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TenantDescriptor>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<TenantRow>(new CommandDefinition(
            $"select {Columns} from nb_tenant order by id",
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetStatusAsync(
        string tenantId,
        TenantStatus status,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "update nb_tenant set status = @status, updated_at = @now where id = @tenantId",
            new
            {
                tenantId = Require(tenantId, nameof(tenantId)),
                status = TenantStatusText.ToText(status),
                now = _clock.GetUtcNow().UtcDateTime,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    private static TenantDescriptor Map(TenantRow row) => new(
        row.TenantId,
        row.Name,
        TenantStatusText.Parse(row.Status),
        row.QuotaPerMinute,
        PlatformTimestamps.ToOffset(row.CreatedAt));

    private static string Require(string? value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("不能为空。", paramName)
            : value.Trim();

    private sealed class TenantRow
    {
        public string TenantId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? Status { get; set; }

        public int QuotaPerMinute { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}

/// <summary>
/// 内存租户注册表：无库（in-memory）模式与单元测试使用。
/// </summary>
/// <remarks>
/// 默认<b>空表</b>：不预置任何租户，注册客户端时租户不存在就是失败。
/// 「悄悄接受任意租户」会让租户拼写错误一路带到生产，宁可让第一次调用就报错。
/// </remarks>
public sealed class InMemoryTenantRegistry : ITenantRegistry
{
    private readonly Dictionary<string, TenantDescriptor> _tenants = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;

    /// <summary>构造。</summary>
    /// <param name="seed">预置租户（可为空）。</param>
    /// <param name="clock">时钟（测试可替换）。</param>
    public InMemoryTenantRegistry(IEnumerable<TenantDescriptor>? seed = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;

        if (seed is null)
        {
            return;
        }

        foreach (var tenant in seed)
        {
            _tenants[tenant.TenantId] = tenant;
        }
    }

    /// <inheritdoc />
    public ValueTask<TenantDescriptor?> FindAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _tenants.TryGetValue(tenantId?.Trim() ?? string.Empty, out var tenant) ? tenant : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<TenantDescriptor> EnsureAsync(
        TenantRegistration registration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var tenantId = string.IsNullOrWhiteSpace(registration.TenantId)
            ? throw new ArgumentException("不能为空。", nameof(registration))
            : registration.TenantId.Trim();

        lock (_gate)
        {
            if (_tenants.TryGetValue(tenantId, out var existing))
            {
                return ValueTask.FromResult(existing);
            }

            var created = new TenantDescriptor(
                tenantId,
                string.IsNullOrWhiteSpace(registration.Name) ? tenantId : registration.Name.Trim(),
                TenantStatus.Active,
                registration.QuotaPerMinute <= 0 ? 600 : registration.QuotaPerMinute,
                _clock.GetUtcNow());

            _tenants[tenantId] = created;

            return ValueTask.FromResult(created);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TenantDescriptor>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<TenantDescriptor>>(
                [.. _tenants.Values.OrderBy(tenant => tenant.TenantId, StringComparer.Ordinal)]);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SetStatusAsync(
        string tenantId,
        TenantStatus status,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_tenants.TryGetValue(tenantId?.Trim() ?? string.Empty, out var tenant))
            {
                return ValueTask.FromResult(false);
            }

            _tenants[tenant.TenantId] = tenant with { Status = status };

            return ValueTask.FromResult(true);
        }
    }
}

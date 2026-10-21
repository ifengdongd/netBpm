using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace NetBpm.Persistence.Admin;

/// <summary>
/// 控制台账号的口令散列（PBKDF2-HMACSHA256）。
/// <para>
/// 存储格式：<c>pbkdf2$&lt;迭代次数&gt;$&lt;salt(base64)&gt;$&lt;hash(base64)&gt;</c>。
/// 迭代次数写进散列串，便于以后只调参数就能平滑升级（老串按自己的参数校验）。
/// 明文口令既不落库也不写审计 —— 登录失败只记「失败」，不记输入了什么。
/// </para>
/// </summary>
public static class PasswordHasher
{
    /// <summary>默认迭代次数（OWASP 对 PBKDF2-HMACSHA256 的下限建议为 600k，这里取兼容与性能的折中）。</summary>
    public const int DefaultIterations = 210_000;

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Scheme = "pbkdf2";

    /// <summary>生成口令散列串。</summary>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Pbkdf2(password, salt, iterations);

        return string.Join(
            '$',
            Scheme,
            iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>校验口令是否匹配散列串；格式不认识时返回 false（不抛异常，避免探测差异）。</summary>
    public static bool Verify(string password, string? stored)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var parts = stored.Split('$');

        if (parts.Length != 4 || !string.Equals(parts[0], Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Pbkdf2(password, salt, iterations);

        // 定长比较：不因「前几个字节就不同」提前返回，避免计时侧信道
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>口令强度下限：控制台账号是管理入口，弱口令等于把管理面交出去。</summary>
    public static bool IsStrongEnough(string password) =>
        !string.IsNullOrWhiteSpace(password) && password.Length >= 8;

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSize);
}

/// <summary>控制台账号（<c>admin_user</c> 表的一行）。</summary>
public sealed record AdminUser(
    string Id,
    string Username,
    string PasswordHash,
    IReadOnlyList<string> Scopes,
    string? DisplayName = null,
    string? TenantId = null,
    bool Enabled = true,
    int FailedAttempts = 0,
    DateTime? LockedUntil = null,
    DateTime? LastLoginAt = null,
    DateTime CreatedAt = default,
    DateTime UpdatedAt = default);

/// <summary>账号存储。数据库实现用于生产，内存实现供无库（in-memory）模式与开发使用。</summary>
public interface IAdminUserStore
{
    /// <summary>按登录名查账号（找不到返回 null）。</summary>
    ValueTask<AdminUser?> FindByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>登录成功：清零失败计数、写上次登录时间。</summary>
    ValueTask MarkLoginSuccessAsync(string id, DateTime loggedInAt, CancellationToken ct = default);

    /// <summary>
    /// 登录失败：累加失败计数；达到 <paramref name="maxFailures"/> 时锁定到 <paramref name="lockUntil"/>。
    /// </summary>
    ValueTask MarkLoginFailureAsync(
        string id,
        int maxFailures,
        DateTime? lockUntil,
        CancellationToken ct = default);

    /// <summary>建账号；登录名已存在返回 false。</summary>
    ValueTask<bool> CreateAsync(AdminUser user, CancellationToken ct = default);

    /// <summary>改口令（散列后落库）。账号不存在返回 false。</summary>
    ValueTask<bool> SetPasswordAsync(string id, string passwordHash, CancellationToken ct = default);

    /// <summary>启用 / 停用账号。</summary>
    ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);

    /// <summary>账号列表（按登录名排序；供运维工具查看）。</summary>
    ValueTask<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default);
}

/// <summary>
/// 数据库账号存储。表结构见 <c>db/{dialect}/netbpm_init.sql</c> 的 <c>admin_user</c>。
/// </summary>
public sealed class DapperAdminUserStore : IAdminUserStore
{
    private const string Columns =
        "id, username, display_name as DisplayName, password_hash as PasswordHash, scopes, " +
        "tenant_id as TenantId, enabled, failed_attempts as FailedAttempts, " +
        "locked_until as LockedUntil, last_login_at as LastLoginAt, created_at as CreatedAt, " +
        "updated_at as UpdatedAt";

    private readonly IDbConnectionFactory _connections;

    public DapperAdminUserStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<AdminUser?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        await using var connection = _connections.CreateConnection();

        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            $"select {Columns} from admin_user where username = @username",
            new { username = username.Trim() },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public async ValueTask MarkLoginSuccessAsync(string id, DateTime loggedInAt, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            "update admin_user set failed_attempts = 0, locked_until = null, " +
            "last_login_at = @loggedInAt, updated_at = @updatedAt where id = @id",
            new { id, loggedInAt, updatedAt = DateTime.UtcNow },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkLoginFailureAsync(
        string id,
        int maxFailures,
        DateTime? lockUntil,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        // 一次语句完成「累加 + 到阈值才锁定」：避免读-改-写之间被并发登录绕过
        await connection.ExecuteAsync(new CommandDefinition(
            "update admin_user set failed_attempts = failed_attempts + 1, " +
            "locked_until = case when failed_attempts + 1 >= @maxFailures then @lockUntil else locked_until end, " +
            "updated_at = @updatedAt where id = @id",
            new { id, maxFailures, lockUntil, updatedAt = DateTime.UtcNow },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> CreateAsync(AdminUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = _connections.CreateConnection();

        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "select count(*) from admin_user where username = @username",
            new { username = user.Username },
            cancellationToken: ct)).ConfigureAwait(false);

        if (exists > 0)
        {
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into admin_user
                (id, username, display_name, password_hash, scopes, tenant_id, enabled,
                 failed_attempts, locked_until, last_login_at, created_at, updated_at)
            values
                (@Id, @Username, @DisplayName, @PasswordHash, @Scopes, @TenantId, @Enabled,
                 0, null, null, @CreatedAt, @UpdatedAt)
            """,
            new
            {
                user.Id,
                user.Username,
                user.DisplayName,
                user.PasswordHash,
                Scopes = string.Join(' ', user.Scopes),
                user.TenantId,
                Enabled = user.Enabled ? 1 : 0,
                user.CreatedAt,
                user.UpdatedAt,
            },
            cancellationToken: ct)).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetPasswordAsync(string id, string passwordHash, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "update admin_user set password_hash = @passwordHash, failed_attempts = 0, " +
            "locked_until = null, updated_at = @updatedAt where id = @id",
            new { id, passwordHash, updatedAt = DateTime.UtcNow },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "update admin_user set enabled = @enabled, updated_at = @updatedAt where id = @id",
            new { id, enabled = enabled ? 1 : 0, updatedAt = DateTime.UtcNow },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<UserRow>(new CommandDefinition(
            $"select {Columns} from admin_user order by username",
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(Map)];
    }

    private static AdminUser Map(UserRow row) => new(
        row.Id,
        row.Username,
        row.PasswordHash,
        SplitScopes(row.Scopes),
        row.DisplayName,
        row.TenantId,
        row.Enabled != 0,
        row.FailedAttempts,
        row.LockedUntil,
        row.LastLoginAt,
        row.CreatedAt,
        row.UpdatedAt);

    private static string[] SplitScopes(string? scopes) =>
        string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class UserRow
    {
        public string Id { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        public string? DisplayName { get; set; }

        public string PasswordHash { get; set; } = string.Empty;

        public string? Scopes { get; set; }

        public string? TenantId { get; set; }

        public int Enabled { get; set; }

        public int FailedAttempts { get; set; }

        public DateTime? LockedUntil { get; set; }

        public DateTime? LastLoginAt { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}

/// <summary>
/// 内存账号存储：无数据库（in-memory）模式与开发用的兜底实现，进程重启即失效。
/// <para>
/// 只在 <c>NetBpm:Security:DevUsers</c> 显式配置账号时才有内容 —— 不配置就是空表，
/// 登录一律失败（而不是悄悄放行）。
/// </para>
/// </summary>
public sealed class InMemoryAdminUserStore : IAdminUserStore
{
    private readonly Dictionary<string, AdminUser> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>用一批账号初始化（配置里的开发账号）。</summary>
    public InMemoryAdminUserStore(IEnumerable<AdminUser>? seed = null)
    {
        if (seed is null)
        {
            return;
        }

        foreach (var user in seed)
        {
            _users[user.Username] = user;
        }
    }

    /// <inheritdoc />
    public ValueTask<AdminUser?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _users.TryGetValue(username.Trim(), out var user) ? user : null);
        }
    }

    /// <inheritdoc />
    public ValueTask MarkLoginSuccessAsync(string id, DateTime loggedInAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = _users.Values.FirstOrDefault(user => user.Id == id)?.Username;

            if (key is not null)
            {
                var user = _users[key];
                _users[key] = user with { FailedAttempts = 0, LockedUntil = null, LastLoginAt = loggedInAt };
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkLoginFailureAsync(
        string id,
        int maxFailures,
        DateTime? lockUntil,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = _users.Values.FirstOrDefault(user => user.Id == id)?.Username;

            if (key is null)
            {
                return ValueTask.CompletedTask;
            }

            var user = _users[key];
            var attempts = user.FailedAttempts + 1;

            _users[key] = user with
            {
                FailedAttempts = attempts,
                LockedUntil = attempts >= maxFailures ? lockUntil : user.LockedUntil,
            };
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> CreateAsync(AdminUser user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_gate)
        {
            if (_users.ContainsKey(user.Username))
            {
                return ValueTask.FromResult(false);
            }

            _users[user.Username] = user;

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SetPasswordAsync(string id, string passwordHash, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = _users.Values.FirstOrDefault(user => user.Id == id)?.Username;

            if (key is null)
            {
                return ValueTask.FromResult(false);
            }

            _users[key] = _users[key] with { PasswordHash = passwordHash, FailedAttempts = 0, LockedUntil = null };

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = _users.Values.FirstOrDefault(user => user.Id == id)?.Username;

            if (key is null)
            {
                return ValueTask.FromResult(false);
            }

            _users[key] = _users[key] with { Enabled = enabled };

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<AdminUser>>(
                [.. _users.Values.OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)]);
        }
    }
}

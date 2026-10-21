using Dapper;
using NetBpm.Persistence;
using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary>
/// <c>DapperAdminUserStore</c> 在<b>真实数据库</b>上的行为（MySQL / SqlServer 各跑一遍）。
/// <para>
/// 内存实现由单元测试覆盖；这里专门盯那部分「只有真库才暴露」的差异：
/// 列名映射（<c>display_name → DisplayName</c>）、<c>enabled</c> 的 0/1 ↔ bool、
/// 以及失败计数的<b>单语句原子更新</b>（读-改-写会被并发登录绕过）。
/// </para>
/// <para>方言不可用（无 MySQL / 无 Docker）时动态跳过，与既有双库测试同策略。</para>
/// </summary>
public sealed class AdminUserStoreDatabaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task Store_ShouldRoundTripAccountOnRealDatabase(string dialect)
    {
        var (store, username, cleanup) = await PrepareAsync(dialect);

        try
        {
            const string password = "NetBpm@2026";
            var id = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;

            Assert.True(await store.CreateAsync(new AdminUser(
                id, username, PasswordHasher.Hash(password), ["netbpm.admin"],
                "测试账号", null, true, 0, null, null, now, now)));

            // 登录名唯一：重复建号返回 false（脚本对 username 建了唯一索引）
            Assert.False(await store.CreateAsync(new AdminUser(
                Guid.NewGuid().ToString("N"), username, PasswordHasher.Hash(password), [],
                null, null, true, 0, null, null, now, now)));

            var found = await store.FindByUsernameAsync(username);

            Assert.NotNull(found);
            Assert.Equal(id, found!.Id);
            Assert.Equal("测试账号", found.DisplayName);
            Assert.Equal(["netbpm.admin"], found.Scopes);
            Assert.True(found.Enabled);
            Assert.True(PasswordHasher.Verify(password, found.PasswordHash));
            Assert.False(PasswordHasher.Verify("wrong", found.PasswordHash));

            // ListAsync 也能看到（列映射与排序）
            Assert.Contains(await store.ListAsync(), user => user.Username == username);
        }
        finally
        {
            await cleanup();
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task Store_FailureCounting_ShouldLockAndResetOnRealDatabase(string dialect)
    {
        var (store, username, cleanup) = await PrepareAsync(dialect);

        try
        {
            var now = DateTime.UtcNow;
            var user = new AdminUser(
                Guid.NewGuid().ToString("N"), username, PasswordHasher.Hash("NetBpm@2026"),
                ["netbpm.admin"], null, null, true, 0, null, null, now, now);

            await store.CreateAsync(user);

            var lockUntil = DateTime.UtcNow.AddMinutes(10);

            // 未达阈值：只累加，不锁定
            await store.MarkLoginFailureAsync(user.Id, 3, lockUntil);

            var afterOne = await store.FindByUsernameAsync(username);

            Assert.NotNull(afterOne);
            Assert.Equal(1, afterOne!.FailedAttempts);
            Assert.Null(afterOne.LockedUntil);

            // 达到阈值：同一条 update 里既累加又锁定（不存在读-改-写的竞态窗口）
            await store.MarkLoginFailureAsync(user.Id, 3, lockUntil);
            await store.MarkLoginFailureAsync(user.Id, 3, lockUntil);

            var locked = await store.FindByUsernameAsync(username);

            Assert.NotNull(locked);
            Assert.Equal(3, locked!.FailedAttempts);
            Assert.NotNull(locked.LockedUntil);

            // 登录成功：清零失败计数与锁定，并写上次登录时间
            var loggedInAt = DateTime.UtcNow;
            await store.MarkLoginSuccessAsync(user.Id, loggedInAt);

            var afterLogin = await store.FindByUsernameAsync(username);

            Assert.NotNull(afterLogin);
            Assert.Equal(0, afterLogin!.FailedAttempts);
            Assert.Null(afterLogin.LockedUntil);
            Assert.NotNull(afterLogin.LastLoginAt);
        }
        finally
        {
            await cleanup();
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task Store_SetPasswordAndEnabled_ShouldPersist(string dialect)
    {
        var (store, username, cleanup) = await PrepareAsync(dialect);

        try
        {
            var now = DateTime.UtcNow;
            var user = new AdminUser(
                Guid.NewGuid().ToString("N"), username, PasswordHasher.Hash("Old@Password1"),
                ["netbpm.admin"], null, null, true, 0, null, null, now, now);

            await store.CreateAsync(user);

            Assert.True(await store.SetPasswordAsync(user.Id, PasswordHasher.Hash("New@Password2")));
            Assert.True(await store.SetEnabledAsync(user.Id, false));
            Assert.False(await store.SetEnabledAsync("no-such-id", true));

            var after = await store.FindByUsernameAsync(username);

            Assert.NotNull(after);
            Assert.False(after!.Enabled);
            Assert.True(PasswordHasher.Verify("New@Password2", after.PasswordHash));
            Assert.False(PasswordHasher.Verify("Old@Password1", after.PasswordHash));
        }
        finally
        {
            await cleanup();
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task Store_FindMissingUsername_ShouldReturnNull(string dialect)
    {
        var (store, _, cleanup) = await PrepareAsync(dialect);

        try
        {
            Assert.Null(await store.FindByUsernameAsync($"no-such-{Guid.NewGuid():N}"));
            Assert.Null(await store.FindByUsernameAsync("   "));
        }
        finally
        {
            await cleanup();
        }
    }

    /// <summary>建好表、给出一个唯一登录名与「删掉本用例数据」的清理动作。</summary>
    private static async Task<(IAdminUserStore Store, string Username, Func<Task> Cleanup)> PrepareAsync(
        string dialect)
    {
        var spec = dialect switch
        {
            "sqlserver" => TestDatabases.SqlServer,
            "mysql" => TestDatabases.MySql,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "未知方言"),
        };

        Assert.SkipUnless(
            spec is not null,
            $"方言 {dialect} 不可用（NETBPM_MYSQL_CONNECTION / NETBPM_SQLSERVER_CONNECTION，或本机 Docker）。");

        TestDatabases.EnsureSchema(spec!);

        var connections = new DbConnectionFactory(spec!.Type, spec.ConnectionString);
        var username = $"store-{Guid.NewGuid().ToString("N")[..12]}";

        async Task Cleanup()
        {
            await using var connection = connections.CreateConnection();

            await connection.ExecuteAsync(new CommandDefinition(
                "delete from admin_user where username = @username",
                new { username },
                cancellationToken: Ct));
        }

        await Cleanup();

        return (new DapperAdminUserStore(connections), username, Cleanup);
    }
}

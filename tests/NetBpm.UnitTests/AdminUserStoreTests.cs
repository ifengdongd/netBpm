using NetBpm.Persistence.Admin;

namespace NetBpm.UnitTests;

/// <summary>
/// 控制台账号的口令散列与内存账号存储（登录接口的底层两件套）。
/// <para>
/// 数据库实现（<see cref="DapperAdminUserStore"/>）由集成测试覆盖；
/// 这里只测不需要数据库的纯逻辑：散列格式、定长校验、失败计数与锁定。
/// </para>
/// </summary>
public sealed class AdminUserStoreTests
{
    [Fact]
    public void Hash_ShouldEmbedSchemeIterationsAndSalt()
    {
        var stored = PasswordHasher.Hash("NetBpm@2026");

        var parts = stored.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2", parts[0]);
        Assert.Equal(PasswordHasher.DefaultIterations.ToString(System.Globalization.CultureInfo.InvariantCulture), parts[1]);
        Assert.NotEqual(string.Empty, parts[2]);
        Assert.NotEqual(string.Empty, parts[3]);
    }

    [Fact]
    public void Hash_TwiceForSamePassword_ShouldDifferBySalt()
    {
        Assert.NotEqual(PasswordHasher.Hash("same-password"), PasswordHasher.Hash("same-password"));
    }

    [Fact]
    public void Verify_ShouldAcceptCorrectAndRejectWrongPassword()
    {
        var stored = PasswordHasher.Hash("NetBpm@2026");

        Assert.True(PasswordHasher.Verify("NetBpm@2026", stored));
        Assert.False(PasswordHasher.Verify("netbpm@2026", stored));
        Assert.False(PasswordHasher.Verify("NetBpm@2026 ", stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plain-text-password")]
    [InlineData("pbkdf2$abc$salt$hash")]
    [InlineData("bcrypt$210000$c2FsdA$aGFzaA")]
    [InlineData("pbkdf2$0$c2FsdA$aGFzaA")]
    public void Verify_OnUnrecognizedFormat_ShouldReturnFalseInsteadOfThrowing(string? stored)
    {
        Assert.False(PasswordHasher.Verify("whatever", stored));
    }

    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsStrongEnough_ShouldRequireEightCharacters(string password, bool expected)
    {
        Assert.Equal(expected, PasswordHasher.IsStrongEnough(password));
    }

    [Fact]
    public async Task Store_ShouldFindCreateAndList()
    {
        var store = new InMemoryAdminUserStore();

        Assert.Null(await store.FindByUsernameAsync("admin"));

        Assert.True(await store.CreateAsync(User("admin")));
        Assert.False(await store.CreateAsync(User("admin")));

        var found = await store.FindByUsernameAsync(" admin ");

        Assert.NotNull(found);
        Assert.Equal("admin", found!.Username);
        Assert.Equal(["netbpm.admin"], found.Scopes);

        var all = await store.ListAsync();

        Assert.Single(all);
    }

    [Fact]
    public async Task Store_Failures_ShouldLockAtThreshold()
    {
        var store = new InMemoryAdminUserStore();
        await store.CreateAsync(User("admin"));

        var user = (await store.FindByUsernameAsync("admin"))!;
        var lockUntil = DateTime.UtcNow.AddMinutes(10);

        // 未到阈值：只累加，不锁定
        for (var i = 1; i < 5; i++)
        {
            await store.MarkLoginFailureAsync(user.Id, 5, lockUntil);

            var current = (await store.FindByUsernameAsync("admin"))!;
            Assert.Equal(i, current.FailedAttempts);
            Assert.Null(current.LockedUntil);
        }

        await store.MarkLoginFailureAsync(user.Id, 5, lockUntil);

        var locked = (await store.FindByUsernameAsync("admin"))!;
        Assert.Equal(5, locked.FailedAttempts);
        Assert.Equal(lockUntil, locked.LockedUntil);
    }

    [Fact]
    public async Task Store_LoginSuccess_ShouldResetFailuresAndLock()
    {
        var store = new InMemoryAdminUserStore();
        await store.CreateAsync(User("admin"));

        var user = (await store.FindByUsernameAsync("admin"))!;
        await store.MarkLoginFailureAsync(user.Id, 5, DateTime.UtcNow.AddMinutes(10));

        var loggedInAt = DateTime.UtcNow;
        await store.MarkLoginSuccessAsync(user.Id, loggedInAt);

        var after = (await store.FindByUsernameAsync("admin"))!;
        Assert.Equal(0, after.FailedAttempts);
        Assert.Null(after.LockedUntil);
        Assert.Equal(loggedInAt, after.LastLoginAt);
    }

    [Fact]
    public async Task Store_SetPasswordAndEnabled_ShouldUpdateExistingAccountOnly()
    {
        var store = new InMemoryAdminUserStore();
        await store.CreateAsync(User("admin"));

        var user = (await store.FindByUsernameAsync("admin"))!;

        Assert.True(await store.SetPasswordAsync(user.Id, PasswordHasher.Hash("New@Password2026")));
        Assert.True(await store.SetEnabledAsync(user.Id, false));
        Assert.False(await store.SetPasswordAsync("missing", "hash"));

        var after = (await store.FindByUsernameAsync("admin"))!;

        Assert.False(after.Enabled);
        Assert.True(PasswordHasher.Verify("New@Password2026", after.PasswordHash));
        Assert.False(PasswordHasher.Verify("NetBpm@2026", after.PasswordHash));
    }

    private static AdminUser User(string username) => new(
        Guid.NewGuid().ToString("N"),
        username,
        PasswordHasher.Hash("NetBpm@2026"),
        ["netbpm.admin"],
        "管理员");
}

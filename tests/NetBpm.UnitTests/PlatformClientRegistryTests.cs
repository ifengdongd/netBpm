using System.Globalization;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;

namespace NetBpm.UnitTests;

/// <summary>
/// 平台接入的客户端注册表（<see cref="InMemoryClientRegistry"/> 与密钥格式）。
/// </summary>
/// <remarks>
/// 这些用例守的是「接入标准」，不是实现细节：
/// 密钥只发一次、宽限期只有一代、白名单空集=全部、状态非 ACTIVE 一律拒绝、
/// 高危 scope 必须显式确认。语义若与 <see cref="DapperClientRegistry"/> 出现分叉，
/// 说明标准没定义清楚，先改标准再改两处实现。
/// </remarks>
public sealed class PlatformClientRegistryTests
{
    private const string ClientName = "OA 门户";

    [Fact]
    public async Task Register_IssuesParsableSecret_ThatAuthenticates()
    {
        var harness = await Harness.CreateAsync();

        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));

        Assert.StartsWith("nbc.cl_", issued.ClientSecret, StringComparison.Ordinal);
        Assert.True(ClientSecretFormat.TryParse(issued.ClientSecret, out var clientId, out _));
        Assert.Equal(issued.ClientId, clientId);
        Assert.Null(issued.PreviousSecretValidUntil);

        var result = await harness.Registry.AuthenticateAsync(issued.ClientSecret);

        Assert.True(result.Succeeded);
        Assert.Equal(ClientStatus.Active, result.Client!.Status);
        Assert.Equal(ClientName, result.Client.Name);
        Assert.Equal(["workflow:task:read"], result.Client.Scopes);
    }

    [Fact]
    public async Task Register_RequiresExistingTenant()
    {
        var harness = await Harness.CreateAsync();

        var error = await Assert.ThrowsAsync<ClientRegistryException>(async () =>
            await harness.Registry.RegisterAsync(
                new RegisterClientRequest(ClientName, "tenant-typo", [PlatformScopes.TaskRead])));

        Assert.Equal(ErrorCodes.TenantNotFound, error.Code);
    }

    [Fact]
    public async Task Register_RejectsUnknownScope()
    {
        var harness = await Harness.CreateAsync();

        var error = await Assert.ThrowsAsync<ClientRegistryException>(async () =>
            await harness.Registry.RegisterAsync(
                new RegisterClientRequest(ClientName, "default", ["workflow:taks:read"])));

        Assert.Equal(ErrorCodes.ClientScopeUnknown, error.Code);
    }

    [Fact]
    public async Task Register_RequiresConfirmation_ForPrivilegedScopes()
    {
        var harness = await Harness.CreateAsync();

        var error = await Assert.ThrowsAsync<ClientRegistryException>(async () =>
            await harness.Registry.RegisterAsync(
                new RegisterClientRequest(
                    ClientName,
                    "default",
                    [PlatformScopes.TaskRead, PlatformScopes.AdminInstance])));

        Assert.Equal(ErrorCodes.ClientScopeNeedsConfirmation, error.Code);

        // 显式确认后才放行：高危 scope 不能跟着一串业务 scope 顺带发出去
        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(
                ClientName,
                "default",
                [PlatformScopes.TaskRead, PlatformScopes.AdminInstance]),
            allowPrivilegedScopes: true);

        var client = await harness.Registry.FindAsync(issued.ClientId);

        Assert.Contains(PlatformScopes.AdminInstance, client!.Scopes);
    }

    [Fact]
    public async Task Authenticate_DistinguishesUnknownClient_FromWrongSecret()
    {
        var harness = await Harness.CreateAsync();
        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));

        var malformed = await harness.Registry.AuthenticateAsync("not-a-secret");
        var unknownClient = await harness.Registry.AuthenticateAsync(
            ClientSecretFormat.Compose("cl_0000000000000000", ClientSecretFormat.NewToken()));
        var wrongSecret = await harness.Registry.AuthenticateAsync(
            ClientSecretFormat.Compose(issued.ClientId, ClientSecretFormat.NewToken()));

        Assert.Equal(ClientAuthenticationStatus.UnknownClient, malformed.Status);
        Assert.Equal(ClientAuthenticationStatus.UnknownClient, unknownClient.Status);
        Assert.Equal(ClientAuthenticationStatus.SecretMismatch, wrongSecret.Status);
        Assert.All([malformed, unknownClient, wrongSecret], result => Assert.False(result.Succeeded));
    }

    [Fact]
    public async Task Authenticate_RejectsInactiveClient_EvenWithCorrectSecret()
    {
        var harness = await Harness.CreateAsync();
        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));

        await harness.Registry.UpdateAsync(
            issued.ClientId,
            new UpdateClientRequest(Status: ClientStatus.Suspended));

        var result = await harness.Registry.AuthenticateAsync(issued.ClientSecret);

        Assert.Equal(ClientAuthenticationStatus.ClientNotActive, result.Status);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task Authenticate_RejectsSuspendedTenant()
    {
        var harness = await Harness.CreateAsync();
        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));

        await harness.Tenants.SetStatusAsync("default", TenantStatus.Suspended);

        var result = await harness.Registry.AuthenticateAsync(issued.ClientSecret);

        Assert.Equal(ClientAuthenticationStatus.TenantSuspended, result.Status);
    }

    [Fact]
    public async Task Rotate_KeepsPreviousSecretUsable_OnlyWithinGrace()
    {
        var harness = await Harness.CreateAsync();
        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));

        var rotated = await harness.Registry.RotateSecretAsync(issued.ClientId);

        Assert.NotNull(rotated);
        Assert.NotEqual(issued.ClientSecret, rotated.ClientSecret);
        Assert.Equal(
            harness.Clock.GetUtcNow().AddDays(7),
            rotated.PreviousSecretValidUntil!.Value);

        // 宽限期内：新旧两把都能用（调用方可以灰度改配置而不是停机）
        Assert.True((await harness.Registry.AuthenticateAsync(issued.ClientSecret)).Succeeded);
        Assert.True((await harness.Registry.AuthenticateAsync(rotated.ClientSecret)).Succeeded);

        harness.Clock.Advance(TimeSpan.FromDays(8));

        Assert.Equal(
            ClientAuthenticationStatus.SecretExpired,
            (await harness.Registry.AuthenticateAsync(issued.ClientSecret)).Status);
        Assert.True((await harness.Registry.AuthenticateAsync(rotated.ClientSecret)).Succeeded);
    }

    [Fact]
    public async Task Rotate_Twice_InvalidatesTheOlderGeneration()
    {
        var harness = await Harness.CreateAsync();
        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));

        var second = await harness.Registry.RotateSecretAsync(issued.ClientId);
        var third = await harness.Registry.RotateSecretAsync(issued.ClientId);

        // 宽限期只保留「紧邻的上一代」：第一代立即失效，不会出现「三代密钥同时可用」的审计盲区。
        // 注意第二代仍在宽限期内（它就是第三代的上一代）——这正是给调用方灰度换配置的窗口。
        Assert.Equal(
            ClientAuthenticationStatus.SecretMismatch,
            (await harness.Registry.AuthenticateAsync(issued.ClientSecret)).Status);
        Assert.True((await harness.Registry.AuthenticateAsync(second!.ClientSecret)).Succeeded);
        Assert.True((await harness.Registry.AuthenticateAsync(third!.ClientSecret)).Succeeded);
    }

    [Fact]
    public async Task RevokedClient_CannotRotate()
    {
        var harness = await Harness.CreateAsync();
        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));

        await harness.Registry.RevokeAsync(issued.ClientId);

        Assert.Equal(
            ClientAuthenticationStatus.ClientNotActive,
            (await harness.Registry.AuthenticateAsync(issued.ClientSecret)).Status);

        var error = await Assert.ThrowsAsync<ClientRegistryException>(async () =>
            await harness.Registry.RotateSecretAsync(issued.ClientId));

        Assert.Equal(ErrorCodes.AuthClientDisabled, error.Code);
    }

    [Fact]
    public async Task ProcessWhitelist_EmptyMeansAll_NonEmptyMeansListed()
    {
        var harness = await Harness.CreateAsync();

        var unrestricted = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));
        var restricted = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(
                ClientName,
                "default",
                [PlatformScopes.TaskRead],
                AllowedProcessKeys: ["leave_process"]));

        Assert.True(await harness.Registry.CanUseProcessAsync(unrestricted.ClientId, "order_process"));
        Assert.True(await harness.Registry.CanUseProcessAsync(restricted.ClientId, "leave_process"));
        Assert.False(await harness.Registry.CanUseProcessAsync(restricted.ClientId, "order_process"));
    }

    [Fact]
    public async Task Update_ReplacesWhitelist_AndKeepsUntouchedFields()
    {
        var harness = await Harness.CreateAsync();
        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(
                ClientName,
                "default",
                [PlatformScopes.TaskRead],
                AllowedProcessKeys: ["leave_process", "order_process"]));

        var updated = await harness.Registry.UpdateAsync(
            issued.ClientId,
            new UpdateClientRequest(AllowedProcessKeys: ["leave_process"]));

        Assert.NotNull(updated);
        Assert.Equal(ClientName, updated.Name);
        Assert.Equal(["leave_process"], updated.AllowedProcessKeys);
        Assert.False(await harness.Registry.CanUseProcessAsync(issued.ClientId, "order_process"));
    }

    [Fact]
    public async Task Register_DefaultsQuotaToTenantQuota()
    {
        var harness = await Harness.CreateAsync(tenantQuota: 1200);

        var issued = await harness.Registry.RegisterAsync(
            new RegisterClientRequest(ClientName, "default", [PlatformScopes.TaskRead]));

        var client = await harness.Registry.FindAsync(issued.ClientId);

        Assert.Equal(1200, client!.QuotaPerMinute);
    }

    /// <summary>用例基座：内存租户表（预置 <c>default</c>）+ 可控时钟 + 内存注册表。</summary>
    private sealed record Harness(
        InMemoryClientRegistry Registry,
        InMemoryTenantRegistry Tenants,
        TestClock Clock)
    {
        internal static async Task<Harness> CreateAsync(int tenantQuota = 600)
        {
            var clock = new TestClock(DateTimeOffset.Parse(
                "2026-10-30T02:00:00Z",
                CultureInfo.InvariantCulture));

            var tenants = new InMemoryTenantRegistry(clock: clock);
            await tenants.EnsureAsync(new TenantRegistration("default", "默认租户", tenantQuota));

            return new Harness(new InMemoryClientRegistry(tenants, clock: clock), tenants, clock);
        }
    }

    /// <summary>可手动推进的时钟（不引第三方包：只需要 <see cref="GetUtcNow"/>）。</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

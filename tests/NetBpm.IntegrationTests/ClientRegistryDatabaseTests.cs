using Dapper;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// <see cref="DapperClientRegistry"/> 在<b>真实数据库</b>上的行为（MySQL / SqlServer 各跑一遍）。
/// </summary>
/// <remarks>
/// 内存实现的语义由单元测试覆盖；这里专门盯「只有真库才暴露」的部分：
/// ① <c>secret_hash</c> 只存摘要、明文任何列都找不到；
/// ② 状态列存的是对外词汇（<c>ACTIVE</c> / <c>SUSPENDED</c>），不是枚举序号；
/// ③ 白名单是「整体替换」而不是追加；
/// ④ 宽限期到点后旧密钥确实失效（时间由 SQL 直接推，不依赖等待）。
/// </remarks>
public sealed class ClientRegistryDatabaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task Register_ShouldStoreOnlyDigest_AndAuthenticate(string dialect)
    {
        var harness = await PrepareAsync(dialect);

        try
        {
            var issued = await harness.Registry.RegisterAsync(
                new RegisterClientRequest(
                    "集成测试客户端",
                    harness.TenantId,
                    [PlatformScopes.InstanceRead, PlatformScopes.TaskRead],
                    AllowedProcessKeys: ["leave_process"]));

            await using var connection = harness.Connections.CreateConnection();

            var row = await connection.QuerySingleAsync<ClientRow>(new CommandDefinition(
                "select status as Status, secret_hash as SecretHash, scopes as Scopes " +
                "from nb_api_client where id = @id",
                new { id = issued.ClientId },
                cancellationToken: Ct));

            Assert.Equal("ACTIVE", row.Status);
            Assert.Equal(ClientSecretFormat.Hash(issued.ClientSecret), row.SecretHash);
            Assert.DoesNotContain(issued.ClientSecret, row.SecretHash, StringComparison.Ordinal);

            // 空格分隔且按序规范化：同集合永远落成同一个字符串（可对拍）
            Assert.Equal("workflow:instance:read workflow:task:read", row.Scopes);

            var authentication = await harness.Registry.AuthenticateAsync(issued.ClientSecret);

            Assert.True(authentication.Succeeded);
            Assert.Equal(harness.TenantId, authentication.Client!.TenantId);
            Assert.Equal(["leave_process"], authentication.Client.AllowedProcessKeys);
            Assert.True(await harness.Registry.CanUseProcessAsync(issued.ClientId, "leave_process"));
            Assert.False(await harness.Registry.CanUseProcessAsync(issued.ClientId, "order_process"));

            // last_used_at 在首次成功鉴权后回写（按分钟节流，但第一次必然写）
            var lastUsed = await connection.ExecuteScalarAsync<DateTime?>(
                new CommandDefinition(
                    "select last_used_at from nb_api_client where id = @id",
                    new { id = issued.ClientId },
                    cancellationToken: Ct));

            Assert.NotNull(lastUsed);
        }
        finally
        {
            await harness.Cleanup();
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task Rotate_ShouldExpirePreviousSecret_WhenGraceWindowPasses(string dialect)
    {
        var harness = await PrepareAsync(dialect);

        try
        {
            var issued = await harness.Registry.RegisterAsync(
                new RegisterClientRequest("集成测试客户端", harness.TenantId, [PlatformScopes.TaskRead]));

            var rotated = await harness.Registry.RotateSecretAsync(issued.ClientId);

            Assert.NotNull(rotated);
            Assert.True((await harness.Registry.AuthenticateAsync(issued.ClientSecret)).Succeeded);

            // 把宽限期推到过去：等价于「7 天后再来」，但不真的等
            await using var connection = harness.Connections.CreateConnection();

            await connection.ExecuteAsync(new CommandDefinition(
                "update nb_api_client set previous_secret_expires_at = @past where id = @id",
                new { past = DateTime.UtcNow.AddDays(-1), id = issued.ClientId },
                cancellationToken: Ct));

            Assert.Equal(
                ClientAuthenticationStatus.SecretExpired,
                (await harness.Registry.AuthenticateAsync(issued.ClientSecret)).Status);
            Assert.True((await harness.Registry.AuthenticateAsync(rotated!.ClientSecret)).Succeeded);
        }
        finally
        {
            await harness.Cleanup();
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task Update_ShouldReplaceWhitelist_AndMirrorStatusText(string dialect)
    {
        var harness = await PrepareAsync(dialect);

        try
        {
            var issued = await harness.Registry.RegisterAsync(
                new RegisterClientRequest(
                    "集成测试客户端",
                    harness.TenantId,
                    [PlatformScopes.TaskRead],
                    AllowedProcessKeys: ["leave_process", "order_process"]));

            await harness.Registry.UpdateAsync(
                issued.ClientId,
                new UpdateClientRequest(
                    AllowedProcessKeys: ["order_process"],
                    Status: ClientStatus.Suspended));

            await using var connection = harness.Connections.CreateConnection();

            var grants = await connection.QueryAsync<string>(new CommandDefinition(
                "select process_key from nb_client_process_grant where client_id = @id order by process_key",
                new { id = issued.ClientId },
                cancellationToken: Ct));

            // 整体替换：被移除的 leave_process 必须真的消失（增量接口会让「漏删」变成长期越权）
            Assert.Equal(["order_process"], grants);

            var status = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "select status from nb_api_client where id = @id",
                new { id = issued.ClientId },
                cancellationToken: Ct));

            Assert.Equal("SUSPENDED", status);
            Assert.Equal(
                ClientAuthenticationStatus.ClientNotActive,
                (await harness.Registry.AuthenticateAsync(issued.ClientSecret)).Status);
        }
        finally
        {
            await harness.Cleanup();
        }
    }

    /// <summary>建租户、给注册表与清理动作（清理按租户整片删，互不干扰）。</summary>
    private static async Task<Harness> PrepareAsync(string dialect)
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
        var tenants = new DapperTenantRegistry(connections);
        var tenantId = "t-" + Guid.NewGuid().ToString("N")[..12];

        await tenants.EnsureAsync(new TenantRegistration(tenantId, "集成测试租户"), Ct);

        return new Harness(
            new DapperClientRegistry(connections, tenants),
            connections,
            tenantId,
            async () =>
            {
                await using var connection = connections.CreateConnection();

                await connection.ExecuteAsync(new CommandDefinition(
                    "delete from nb_client_process_grant where client_id in " +
                    "(select id from nb_api_client where tenant_id = @tenantId)",
                    new { tenantId },
                    cancellationToken: Ct));

                await connection.ExecuteAsync(new CommandDefinition(
                    "delete from nb_api_client where tenant_id = @tenantId",
                    new { tenantId },
                    cancellationToken: Ct));

                await connection.ExecuteAsync(new CommandDefinition(
                    "delete from nb_tenant where id = @tenantId",
                    new { tenantId },
                    cancellationToken: Ct));
            });
    }

    /// <summary>直接读库校验用的行（Dapper 不做下划线→驼峰映射，SQL 里显式起别名）。</summary>
    private sealed class ClientRow
    {
        public string Status { get; set; } = string.Empty;

        public string SecretHash { get; set; } = string.Empty;

        public string Scopes { get; set; } = string.Empty;
    }

    private sealed record Harness(
        DapperClientRegistry Registry,
        IDbConnectionFactory Connections,
        string TenantId,
        Func<Task> Cleanup);
}

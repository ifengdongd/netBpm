using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence.Admin;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Diagnostics;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 审计写入失败必须<b>可发现</b>：业务保持 fail-open，但失败要计进
/// <c>netbpm.admin.audit.write_failures</c>（配 page 级告警）。
/// </summary>
/// <remarks>
/// <para>
/// fail-open 的写法本身是对的（审计表故障不该让运维动作全部不可用），但它有一个前提：
/// <b>有人会发现</b>。此前失败只写一条 warning，而「审计表不可用」通常意味着日志链路也在同一片故障里，
/// 结果是「动作发生了、审计没写、没人知道」。
/// </para>
/// <para>
/// 这条用例守两件事：动作照常成功（不把 fail-open 改成 fail-closed），以及失败真的进了指标
/// （否则告警规则挂在一个永远不会增长的计数器上，等于没有告警）。
/// </para>
/// </remarks>
public sealed class AdminAuditFailureTests
{
    private const string DefaultTenant = "default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AuditFailure_ShouldKeepActionSucceeding_AndCountMetric()
    {
        long failures = 0;

        // 先触达静态类型：Counter 在 AdminDiagnostics 首次访问时创建，listener 只能订阅已存在的 instrument。
        _ = AdminDiagnostics.AuditWriteFailures;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AdminDiagnostics.MeterName
                    && instrument.Name == "netbpm.admin.audit.write_failures")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref failures, value));
        listener.Start();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var (secret, clientId) = await RegisterAsync(client, [PlatformScopes.AdminClient]);

        // 用同租户的写动作（跨租户登记会被 403，那是另一条边界，不该混进本用例）
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            new Uri($"/admin/v1/clients/{clientId}", UriKind.Relative))
        {
            Content = new JsonContent("""{"name":"audit-failure-renamed"}"""),
        };

        request.Headers.Add("X-Api-Key", secret);

        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        // 1) fail-open 不变：审计写不进去，管理动作照常成功
        Assert.True(
            response.IsSuccessStatusCode,
            $"审计失败不应该阻断管理动作，实际 {response.StatusCode}：{body}");

        // 2) 失败进了指标（指标是告警规则的唯一依据）
        Assert.True(
            failures >= 1,
            "审计写入失败必须计进 netbpm.admin.audit.write_failures；否则 fail-open 等于静默关闭审计。");
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    /// <summary>只让审计写入失败，查询 / 清理照常（模拟「审计表不可写但服务还在跑」）。</summary>
    private sealed class FailingAuditStore : IAdminAuditStore
    {
        private readonly IAdminAuditStore _inner = new InMemoryAdminAuditStore();

        public ValueTask RecordAsync(AdminAuditEntry entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("模拟故障：admin_audit_log 不可写。");

        public ValueTask<AdminAuditPage> QueryAsync(AdminAuditQuery query, CancellationToken ct = default) =>
            _inner.QueryAsync(query, ct);

        public ValueTask<IReadOnlyList<AdminAuditSummary>> SummarizeAsync(
            string? tenantId,
            DateTime since,
            CancellationToken ct = default) => _inner.SummarizeAsync(tenantId, since, ct);

        public ValueTask<AdminAuditOptions> GetOptionsAsync(
            string? tenantId,
            CancellationToken ct = default) => _inner.GetOptionsAsync(tenantId, ct);

        public ValueTask<int> PurgeAsync(
            string? tenantId,
            DateTime before,
            CancellationToken ct = default) => _inner.PurgeAsync(tenantId, before, ct);
    }

    private static WebApplicationFactory<Program> BuildHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:RequireCredential", "false");
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);

            builder.ConfigureServices(services =>
                services.AddScoped<IAdminAuditStore>(_ => new FailingAuditStore()));
        });

    private static async Task<(string Secret, string ClientId)> RegisterAsync(HttpClient client, string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new { name = "audit-failure", tenantId = DefaultTenant, scopes },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return (
            json.RootElement.GetProperty("clientSecret").GetString()!,
            json.RootElement.GetProperty("clientId").GetString()!);
    }

    /// <summary>避免依赖 <c>System.Net.Http.Json</c> 的类型推断，直接给 UTF-8 JSON。</summary>
    private sealed class JsonContent : StringContent
    {
        public JsonContent(string json)
            : base(json, System.Text.Encoding.UTF8, "application/json")
        {
        }
    }
}

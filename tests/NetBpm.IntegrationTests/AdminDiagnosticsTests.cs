using System.Diagnostics.Metrics;
using NetBpm.Persistence.Admin;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Security;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 管理治理指标的语义：RBAC 拒绝、审计留痕两个计数器的增量与标签。
/// <para>
/// Meter 是静态单例 —— 用 <see cref="MeterListener"/> 订阅同一个 Meter 名收集增量；
/// 每个用例只统计自己存活期间发生的测量，互不串扰。纯内存测试，不依赖数据库。
/// </para>
/// </summary>
public sealed class AdminDiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Ensure_ShouldCountDenied_ByScopeAndAuthMode()
    {
        using var collector = new MetricCollector();

        var context = new RequestContext
        {
            AuthMode = RequestAuthMode.Jwt,
            Scopes = new HashSet<string>(StringComparer.Ordinal),
        };

        Assert.Throws<ForbiddenException>(() =>
            AdminAuthorization.Ensure(context, TaskScopes.AdminInstanceJump, "跳转流程实例"));

        Assert.Equal(1, collector.TotalWhere(
            "netbpm.admin.authorization.denied",
            AdminDiagnostics.TagScope,
            TaskScopes.AdminInstanceJump));
        Assert.Equal(1, collector.TotalWhere(
            "netbpm.admin.authorization.denied",
            AdminDiagnostics.TagAuthMode,
            "jwt"));
    }

    [Fact]
    public void Ensure_ShouldNotCount_WhenAllowed()
    {
        using var collector = new MetricCollector();

        // API Key 是可信服务：放行且不计数
        AdminAuthorization.Ensure(
            new RequestContext { AuthMode = RequestAuthMode.ApiKey },
            TaskScopes.AdminInstanceJump,
            "跳转流程实例");

        // JWT 持有 scope：放行且不计数
        AdminAuthorization.Ensure(
            new RequestContext
            {
                AuthMode = RequestAuthMode.Jwt,
                Scopes = new HashSet<string>([TaskScopes.AdminInstanceJump], StringComparer.Ordinal),
            },
            TaskScopes.AdminInstanceJump,
            "跳转流程实例");

        // 超级范围覆盖：放行且不计数
        AdminAuthorization.Ensure(
            new RequestContext
            {
                AuthMode = RequestAuthMode.Jwt,
                Scopes = new HashSet<string>([TaskScopes.Admin], StringComparer.Ordinal),
            },
            TaskScopes.AdminModelWrite,
            "保存流程模型");

        Assert.Equal(0, collector.TotalWhere(
            "netbpm.admin.authorization.denied",
            AdminDiagnostics.TagScope,
            TaskScopes.AdminInstanceJump));
    }

    [Fact]
    public async Task Auditor_ShouldCountAudit_ByActionAndSuccess()
    {
        using var collector = new MetricCollector();
        var auditor = new AdminActionAuditor(new InMemoryAdminAuditStore(), new NoopLogger());

        await auditor.RecordAsync(
            new RequestContext { AuthMode = RequestAuthMode.Jwt, UserId = "ops" },
            AdminAuditActions.TaskReassign,
            AdminAuditTargets.Task,
            "task-1",
            ct: Ct);

        await auditor.RecordAsync(
            new RequestContext { AuthMode = RequestAuthMode.ApiKey },
            AdminAuditActions.InstanceJump,
            AdminAuditTargets.ProcessInstance,
            "pi-1",
            success: false,
            error: "boom",
            ct: Ct);

        Assert.Equal(1, collector.TotalWhere(
            "netbpm.admin.audit.recorded",
            AdminDiagnostics.TagAction,
            AdminAuditActions.TaskReassign));
        Assert.Equal(1, collector.TotalWhere(
            "netbpm.admin.audit.recorded",
            AdminDiagnostics.TagSuccess,
            "true"));
        Assert.Equal(1, collector.TotalWhere(
            "netbpm.admin.audit.recorded",
            AdminDiagnostics.TagAction,
            AdminAuditActions.InstanceJump));
        Assert.Equal(1, collector.TotalWhere(
            "netbpm.admin.audit.recorded",
            AdminDiagnostics.TagSuccess,
            "false"));
    }

    [Fact]
    public async Task Auditor_ShouldCountEvenWhenStoreFails()
    {
        using var collector = new MetricCollector();
        var auditor = new AdminActionAuditor(new FailingStore(), new NoopLogger());

        // 审计存储失败不影响业务（warning），但动作计数已经发生
        await auditor.RecordAsync(
            new RequestContext { AuthMode = RequestAuthMode.Jwt },
            AdminAuditActions.VariableWrite,
            AdminAuditTargets.Variable,
            "pi-1:amount",
            ct: Ct);

        Assert.Equal(1, collector.TotalWhere(
            "netbpm.admin.audit.recorded",
            AdminDiagnostics.TagAction,
            AdminAuditActions.VariableWrite));
    }

    private sealed class NoopLogger : Microsoft.Extensions.Logging.ILogger<AdminActionAuditor>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class FailingStore : IAdminAuditStore
    {
        public ValueTask RecordAsync(AdminAuditEntry entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("审计表不可用");

        public ValueTask<AdminAuditPage> QueryAsync(AdminAuditQuery query, CancellationToken ct = default) =>
            ValueTask.FromResult(new AdminAuditPage([], 0));

        public ValueTask<IReadOnlyList<AdminAuditSummary>> SummarizeAsync(
            string? tenantId,
            DateTime since,
            CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<AdminAuditSummary>>([]);

        public ValueTask<int> PurgeAsync(string? tenantId, DateTime before, CancellationToken ct = default) =>
            ValueTask.FromResult(0);
    }

    /// <summary>订阅 <see cref="AdminDiagnostics.MeterName"/>，按「指标名 + 标签」累计增量。</summary>
    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);

        public MetricCollector()
        {
            // .NET 10：InstrumentPublished 的第二参数是 MeterListener 本身，
            // 用 listener.EnableMeasurementEvents(instrument) 启用订阅
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AdminDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.Start();
        }

        public long TotalWhere(string instrumentName, string tagKey, string tagValue) =>
            _values
                .Where(entry =>
                    entry.Key.StartsWith($"{instrumentName}|", StringComparison.Ordinal)
                    && entry.Key.Contains($"{tagKey}={tagValue}", StringComparison.Ordinal))
                .Sum(entry => entry.Value);

        private void OnMeasurement(
            Instrument instrument,
            long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            var parts = new List<string>();

            foreach (var tag in tags)
            {
                parts.Add($"{tag.Key}={tag.Value}");
            }

            parts.Sort(StringComparer.Ordinal);

            var key = $"{instrument.Name}|{string.Join(",", parts)}";
            _values[key] = _values.GetValueOrDefault(key) + measurement;
        }

        public void Dispose() => _listener.Dispose();
    }
}

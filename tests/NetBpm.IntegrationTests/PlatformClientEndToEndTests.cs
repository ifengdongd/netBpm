using System.Net;
using NetBpm.Platform.Client;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 官方 SDK 打真实宿主：一条完整链路（查目录 → 发起 → 查参数 / 修订 → 办待办 → 查历史 → 终止）。
/// </summary>
/// <remarks>
/// <para>
/// 这组用例的价值不在「覆盖了什么接口」，而在<b>用接入方的方式走一遍</b>：
/// 全程只用 <see cref="NetBpmPlatformClient"/>，不碰 <c>HttpClient</c>、不手拼 URL、不手工反序列化。
/// SDK 里任何一处路径拼错 / 模型对不上 / 错误映射失灵，这里都会红。
/// </para>
/// <para>
/// 与单测（<c>NetBpm.UnitTests.PlatformClientTests</c>）的分工：单测盯着「发出去的请求长什么样」，
/// 这里盯着「服务端真的认这套请求」。两边都绿才算 SDK 可用。
/// </para>
/// </remarks>
public sealed class PlatformClientEndToEndTests
{
    private const string ProcessKey = ReadEndpointHarness.ProcessKey;

    /// <summary>接入方实际会申请的 scope（含代客提交与终止：本用例要演一遍撤销）。</summary>
    private static string[] Scopes =>
    [
        PlatformScopes.ProcessRead,
        PlatformScopes.InstanceRead,
        PlatformScopes.InstanceStart,
        PlatformScopes.InstanceTerminate,
        PlatformScopes.TaskRead,
        PlatformScopes.TaskSubmit,
        PlatformScopes.TaskSubmitOnBehalf,
        PlatformScopes.TaskAssign,
        PlatformScopes.HistoryRead,
        PlatformScopes.AdminProcess,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sdk_ShouldWalkTheWholeApprovalPath()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var http = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(http, "sdk-e2e", Scopes);

        using var sdk = CreateClient(http, secret, subject: "lisi");

        // ① 目录：接入第一步
        var processes = await sdk.Processes.ListAsync(ct: Ct);

        var descriptor = Assert.Single(processes.Items, item => item.ProcessKey == ProcessKey);

        Assert.Equal(1, descriptor.DeployedVersion);
        Assert.True(descriptor.Capabilities.CanStart);

        var schema = await sdk.Processes.GetParameterSchemaAsync(ProcessKey, Ct);

        Assert.Contains(schema.Parameters, parameter => parameter.ApiName == "param.days");

        var form = await sdk.Processes.GetFormSchemaAsync(ProcessKey, ct: Ct);

        Assert.Equal(2, form.Activities.Count);

        var svg = await sdk.Processes.GetDiagramSvgAsync(ProcessKey, ct: Ct);

        Assert.Contains("<svg", svg, StringComparison.Ordinal);

        // ② 发起（幂等键 = 业务单号 + 动作）
        var started = await sdk.Instances.StartAsync(
            new StartInstanceRequest(
                ProcessKey,
                BusinessKey: "BX-SDK-001",
                Parameters: new Dictionary<string, string?>(StringComparer.Ordinal) { ["param.days"] = "3" }),
            idempotencyKey: "BX-SDK-001:start",
            ct: Ct);

        Assert.Equal("BX-SDK-001", started.BusinessKey);
        Assert.NotNull(started.Instance);
        Assert.Equal(InstanceStatus.Running, started.Instance!.Status);

        // 同一个幂等键重放：不能多出一张单
        var replay = await sdk.Instances.StartAsync(
            new StartInstanceRequest(
                ProcessKey,
                BusinessKey: "BX-SDK-001",
                Parameters: new Dictionary<string, string?>(StringComparer.Ordinal) { ["param.days"] = "3" }),
            idempotencyKey: "BX-SDK-001:start",
            ct: Ct);

        Assert.Equal(started.InstanceId, replay.InstanceId);

        var instanceId = started.InstanceId;

        // ③ 参数与修订历史
        var parameters = await sdk.Instances.GetParametersAsync(instanceId, Ct);
        var days = Assert.Single(parameters.Parameters, row => row.ApiName == "param.days");

        Assert.Equal("3", days.Value);

        var revisions = await sdk.Instances.GetParameterHistoryAsync(instanceId, ct: Ct);

        var first = Assert.Single(revisions);

        Assert.Equal(1, first.Revision);
        Assert.Equal("3", first.Values["param.days"]);

        // ④ 待办 → 提交
        var tasks = await sdk.Tasks.ListAsync(new TaskQuery(TaskScope.Mine), Ct);
        var task = Assert.Single(tasks.Items);

        Assert.Equal("申请", task.ActivityName);

        var submitted = await sdk.Tasks.SubmitAsync(
            task.TaskId,
            new SubmitTaskRequest("1", Comment: "同意（SDK 端到端）"),
            idempotencyKey: task.TaskId + ":submit",
            ct: Ct);

        Assert.Equal("task_approve", Assert.Single(submitted.NextTasks).ActivityId);
        Assert.NotEmpty(submitted.Events);

        // 表单快照与附件回放：提交过就有快照（没附件就是空数组，不是 404）
        var attachments = await sdk.Tasks.GetAttachmentsAsync(task.TaskId, Ct);

        Assert.Empty(attachments);

        // ⑤ 时间线：写时定稿，能按单回顾
        var history = await sdk.Instances.GetHistoryAsync(instanceId, Ct);

        Assert.NotEmpty(history.Entries);
        Assert.Contains(history.Entries, entry => entry.Kind == TimelineEntryKind.TaskCompleted);

        // ⑥ 撤销（终止要原因，进审批记录）
        var terminated = await sdk.Instances.TerminateAsync(
            instanceId,
            new TerminateInstanceRequest("SDK 端到端用例：验证终止路径"),
            ct: Ct);

        Assert.Equal(InstanceStatus.Terminated, terminated.Status);
    }

    [Fact]
    public async Task Sdk_ShouldSurfacePlatformProblemAsException()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var http = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(http, "sdk-errors", Scopes);

        using var sdk = CreateClient(http, secret, subject: "lisi");

        var exception = await Assert.ThrowsAsync<NetBpmApiException>(() =>
            sdk.Processes.GetAsync("no_such_process", Ct));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.True(exception.IsNotFound);
        Assert.False(exception.IsTransient);
        Assert.Equal(ErrorCodes.ProcessNotFound, exception.Code);
        // 路径是相对形式（SDK 支持虚拟目录），异常消息里够定位
        Assert.Equal("workflow/v1/processes/no_such_process", exception.RequestPath);
    }

    [Fact]
    public async Task Sdk_AdminProcesses_ShouldRoundTripOverride()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var http = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(http, "sdk-admin", Scopes);

        using var sdk = CreateClient(http, secret, subject: "lisi");

        Assert.Empty(await sdk.AdminProcesses.ListAsync(Ct));

        var created = await sdk.AdminProcesses.UpsertAsync(
            ProcessKey,
            new ProcessCatalogOverrideRequest(
                Name: "请假（SDK 精修）",
                Category: "HR",
                SortOrder: 7),
            Ct);

        Assert.Equal("请假（SDK 精修）", created.Name);
        Assert.Equal("HR", created.Category);

        // 业务面立刻看到合并结果（覆盖行没有缓存层）
        var descriptor = await sdk.Processes.GetAsync(ProcessKey, Ct);

        Assert.Equal("请假（SDK 精修）", descriptor.Name);

        Assert.True(await sdk.AdminProcesses.DeleteAsync(ProcessKey, Ct));
        Assert.False(await sdk.AdminProcesses.DeleteAsync(ProcessKey, Ct));

        // 删掉之后回到 BPMN 派生的名字
        Assert.Equal("只读端点演示", (await sdk.Processes.GetAsync(ProcessKey, Ct)).Name);
    }

    private static NetBpmPlatformClient CreateClient(HttpClient http, string secret, string subject) =>
        new(http, new NetBpmClientOptions
        {
            ApiKey = secret,
            // 代客调用：审批结论记在这个人身上（需要 workflow:task:submit:onbehalf）
            SubjectId = subject,
        });
}

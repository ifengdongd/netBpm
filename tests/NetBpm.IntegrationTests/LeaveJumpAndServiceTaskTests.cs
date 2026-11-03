using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 示例流程 <c>web/designer/examples/leave-demo-process.bpmn20.xml</c> 的端到端守门：
/// 发起 → 两次跳转 → 审批通过 → 结束分支的 serviceTask 执行。
/// </summary>
/// <remarks>
/// <para>
/// 这条用例同时守三件容易「改着改着就坏了」的事：
/// </para>
/// <list type="number">
///   <item><description><b>示例文件本身可运行</b>：直接部署 examples 目录里的那份 XML，不复制副本。
///     设计器示例与可执行样例一旦分成两份，迟早会漂移成「图上看得到、跑起来不对」。</description></item>
///   <item><description><b>跳转语义</b>：jump 不只是改一个字段——它要删掉当前待办、在新节点上重建待办；
///     而且从跳转后的节点继续提交必须走到正确的分支。</description></item>
///   <item><description><b>serviceTask 真的执行</b>：只断言「实例结束了」不够——
///     网关选错分支、委托解析不到被静默跳过，实例照样会结束。判据是委托的执行痕迹。</description></item>
/// </list>
/// <para>
/// <b>为什么节点状态用引擎门面查、而不是 <c>GET /workflow/v1/instances/{id}</c></b>：
/// 平台面的实例视图要组装参数快照、环节聚合、时间线，这些读模型落在 <c>nb_*</c> 表上；
/// 内存宿主（<c>UseInMemoryExecutionTree=true</c>、<c>EnableHistory=false</c>）没有这些表，
/// 视图组装不出来。本用例要验的是「流程行为」，不是读模型——读模型的正确性由真库集成用例覆盖。
/// </para>
/// <para>
/// 用 API Key 而不是客户端凭据：跳转端点在 <c>/api/runtime</c>（引擎面），
/// 需要的 <c>workflow:admin:instance:jump</c> 不在客户端可申请的 scope 白名单里。
/// </para>
/// </remarks>
public sealed class LeaveJumpAndServiceTaskTests
{
    private const string ApiKey = "leave-jump-test-key";
    private const string UserId = "admin";
    private const string DefaultTenant = "default";
    private const string ProcessKey = "leave_demo";
    private const string ResourceName = "leave-demo-process.bpmn20.xml";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LeaveDemo_ShouldJumpTwice_AndRunServiceTaskOnApproval()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var counter = host.Services.GetRequiredService<ServiceCallCounter>();
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        await DeployExampleAsync(engine);

        // ① 发起：平台面（发起即申请，参数与审批选项都在 BPMN 里声明）
        var instanceId = await StartAsync(client);

        var afterStart = await FindSingleTaskAsync(engine, instanceId);

        Assert.Equal("task_lead_approve", afterStart.TaskDefinitionKey);

        // ② 跳转：审批人不在 → 转「申请人修改」
        await JumpAsync(client, instanceId, "task_apply_modify", "approver unavailable, send back to applicant");

        var afterFirstJump = await FindSingleTaskAsync(engine, instanceId);

        Assert.Equal("task_apply_modify", afterFirstJump.TaskDefinitionKey);
        Assert.NotEqual(afterStart.Id, afterFirstJump.Id);

        // ③ 跳转：修改完成 → 回审批（跳转后必须还能继续提交，否则跳转只是「看起来对」）
        await JumpAsync(client, instanceId, "task_lead_approve", "applicant finished edit, back to approval");

        var afterSecondJump = await FindSingleTaskAsync(engine, instanceId);

        Assert.Equal("task_lead_approve", afterSecondJump.TaskDefinitionKey);
        Assert.NotEqual(afterFirstJump.Id, afterSecondJump.Id);

        // ④ 审批通过：走 ${result == 1} 分支 → serviceTask → end
        var submitted = await SubmitAsync(client, afterSecondJump.Id);

        // 结论由审批决定推导（内存宿主没有历史行，提交后用本次决定合成终态视图）
        var instance = submitted.GetProperty("instance");

        Assert.Equal("COMPLETED", instance.GetProperty("status").GetString());
        Assert.Equal("APPROVE", instance.GetProperty("conclusion").GetProperty("semantic").GetString());

        // ⑤ 结束分支的 serviceTask 真的执行了
        Assert.Equal(["svc_approve_complete"], counter.Executed);

        // ⑥ 实例已结束：实例上没有残留待办
        var remaining = await engine.FindTasksAsync(
            new Runtime.TaskQuery(ProcessInstanceId: instanceId),
            Ct);

        Assert.Empty(remaining);
    }

    // ─────────────────────────── 流程操作 ───────────────────────────

    /// <summary>部署 examples 目录里的示例文件（不复制副本，防止示例与可执行版本漂移）。</summary>
    private static async Task DeployExampleAsync(IProcessEngine engine)
    {
        var path = Path.Combine(
            RepositoryLayout.Root,
            "web",
            "designer",
            "examples",
            ResourceName);

        Assert.True(File.Exists(path), $"示例流程不存在：{path}");

        await using var stream = File.OpenRead(path);

        await engine.DeployAsync(stream, ResourceName, ct: Ct);
    }

    private static async Task<string> StartAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/workflow/v1/instances", UriKind.Relative))
        {
            Content = Json("""
                {"processKey":"leave_demo","businessKey":"LEAVE-TEST-1",
                 "parameters":{"param.days":"2","param.reason":"integration: jump + service task"}}
                """),
        };

        AddCredentials(request, idempotencyKey: $"leave-start-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"发起失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("instanceId").GetString()!;
    }

    private static async Task JumpAsync(HttpClient client, string instanceId, string target, string reason)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/runtime/process-instances/{instanceId}/jump", UriKind.Relative))
        {
            Content = Json($$"""{"targetActivityId":"{{target}}","reason":"{{reason}}"}"""),
        };

        AddCredentials(request);

        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.IsSuccessStatusCode,
            $"跳转到 {target} 失败：{response.StatusCode} {body}");
    }

    private static async Task<JsonElement> SubmitAsync(HttpClient client, string taskId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/workflow/v1/tasks/{taskId}/submit", UriKind.Relative))
        {
            Content = Json("""{"optionId":"1","comment":"approve: run service task"}"""),
        };

        AddCredentials(request, idempotencyKey: $"leave-submit-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"提交审批失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }

    private static async Task<TaskEntity> FindSingleTaskAsync(IProcessEngine engine, string instanceId)
    {
        var tasks = await engine.FindTasksAsync(
            new Runtime.TaskQuery(ProcessInstanceId: instanceId),
            Ct);

        return Assert.Single(tasks);
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static void AddCredentials(HttpRequestMessage request, string? idempotencyKey = null)
    {
        request.Headers.Add("X-Api-Key", ApiKey);

        // X-User-Id 是「调用方」，X-Subject-Id 是「以谁的身份操作」：
        // 发起人 / 办理人判定都取主体，只给 User-Id 会出现「发起成功、后续动作却不是同一人」。
        request.Headers.Add("X-User-Id", UserId);
        request.Headers.Add("X-Subject-Id", UserId);
        request.Headers.Add("X-Tenant-Id", DefaultTenant);

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static WebApplicationFactory<Program> BuildHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:RequireCredential", "false");
            builder.UseSetting("NetBpm:Security:ApiKey", ApiKey);
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");

            // 内存宿主没有 ACT_HI_* 存储，开历史会被组合根直接拒绝（这是刻意的：
            // 静默忽略 EnableHistory 会把「历史没记」变成上线后才发现的问题）。
            builder.UseSetting("NetBpm:EnableHistory", "false");

            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");

            // 部署期会做「委托自检」，两个 serviceTask 的 delegateExpression 都要能解析，
            // 否则示例流程连部署都过不去。
            //
            // 这里不走 AddServiceTaskDelegates（扫描整程序集）：测试程序集里还有别的委托/监听器实现
            // （如 ExecutionSnapshotRecorder），扫描会把它们一起拉进容器，而它们的依赖（IDbSessionProvider）
            // 在内存宿主里不存在 —— 启动期 DI 校验直接失败。只注册本用例需要的两个名字。
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ServiceCallCounter>();
                services.AddSingleton<CountingLeaveCompletionDelegate>();
                services.AddKeyedSingleton<object>(
                    "leave.ApproveComplete",
                    (provider, _) => provider.GetRequiredService<CountingLeaveCompletionDelegate>());
                services.AddKeyedSingleton<object>(
                    "leave.RefuseComplete",
                    (provider, _) => provider.GetRequiredService<CountingLeaveCompletionDelegate>());
            });
        });

    /// <summary>记录 serviceTask 执行痕迹的单例。</summary>
    internal sealed class ServiceCallCounter
    {
        private readonly List<string> _executed = [];

        /// <summary>已执行的活动 id（按执行顺序）。</summary>
        public IReadOnlyList<string> Executed
        {
            get
            {
                lock (_executed)
                {
                    return [.. _executed];
                }
            }
        }

        /// <summary>记录一次执行。</summary>
        public void Record(string? activityId)
        {
            lock (_executed)
            {
                _executed.Add(activityId ?? "(unknown)");
            }
        }
    }

    /// <summary>示例流程两个结束分支的委托：只记录被调用，不做业务回写。</summary>
    internal sealed class CountingLeaveCompletionDelegate : IServiceTaskDelegate
    {
        private readonly ServiceCallCounter _counter;

        public CountingLeaveCompletionDelegate(ServiceCallCounter counter)
        {
            _counter = counter;
        }

        public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
        {
            _counter.Record(execution.CurrentActivityId);

            return ValueTask.CompletedTask;
        }
    }
}

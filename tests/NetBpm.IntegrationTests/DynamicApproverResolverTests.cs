using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 动态审批人解析：节点<b>未静态指定</b>审批人时，由 <see cref="IApproverResolver"/>
/// 依据流程变量（如 <c>applyDeptId</c>）+ 节点 id（表达「职位」）查外部系统定人，
/// 引擎把返回的人写入候选用户（进「我的待办」候选池）。
/// </summary>
/// <remarks>
/// <para>
/// 这条用例守的是「审批人从外部系统动态获取」这条链路 —— 对应真实场景：
/// 请假单按 businessKey / 部门 id 定位「该部门某职位的人」作为审批人，而 BPMN 里不写死任何人。
/// </para>
/// <para>
/// 判定用 <c>FindTaskIdentityLinksAsync</c> 看任务级 <c>candidate</c> 候选关系，
/// 而不是平台面的实例视图 —— 内存宿主没有 <c>nb_*</c> 读模型表，视图组装不出来。
/// </para>
/// </remarks>
public sealed class DynamicApproverResolverTests
{
    private const string ApiKey = "dynamic-approver-test-key";
    private const string UserId = "alice";
    private const string DefaultTenant = "default";
    private const string ProcessKey = "dynamic_approver";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NodeWithoutStaticApprover_ShouldResolveApproverFromExternalSystem()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var resolver = host.Services.GetRequiredService<RecordingApproverResolver>();

        await DeployAsync(engine);

        // 发起：传 applyDeptId（部门 id），让解析器据此定位「该部门的经理」
        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            businessKey: "LEAVE-9001",
            startUserId: UserId,
            variables: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["applyDeptId"] = "102",
            },
            tenantId: DefaultTenant,
            ct: Ct);

        // 节点无静态审批人 → 任务创建后应停在 userTask 上
        var task = Assert.Single(await engine.FindTasksAsync(
            new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        Assert.Equal("task_approve", task.TaskDefinitionKey);

        // 解析器应被调用，且拿到 businessKey + 部门变量 + 节点 id
        var invocation = Assert.Single(resolver.Invocations);
        Assert.Equal("LEAVE-9001", invocation.BusinessKey);
        Assert.Equal("102", invocation.DeptId);
        Assert.Equal("task_approve", invocation.ActivityId);

        // 引擎把解析器返回的审批人写进了候选用户（任务级 candidate 关系）
        var links = await engine.FindTaskIdentityLinksAsync(task.Id, Ct);

        var candidateUsers = links
            .Where(link => link.Type == IdentityLinkType.Candidate && link.UserId is not null)
            .Select(link => link.UserId!)
            .ToArray();

        Assert.Contains("manager1", candidateUsers);
        Assert.Equal(["manager1"], candidateUsers);
    }

    [Fact]
    public async Task WhenResolverReturnsEmpty_ShouldNotCreateCandidates()
    {
        using var host = BuildHost(noApprover: true);
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        await DeployAsync(engine);

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            startUserId: UserId,
            variables: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["applyDeptId"] = "999", // 查无此部门
            },
            tenantId: DefaultTenant,
            ct: Ct);

        var task = Assert.Single(await engine.FindTasksAsync(
            new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        // 解析器返回空 → 不写任何候选关系，任务照常停着（等业务侧后续干预）
        var links = await engine.FindTaskIdentityLinksAsync(task.Id, Ct);

        Assert.DoesNotContain(links, link => link.Type == IdentityLinkType.Candidate && link.UserId is not null);
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static string Xml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:activiti="http://activiti.org/bpmn"
                          targetNamespace="netbpm-test">
          <bpmn:process id="{{ProcessKey}}" name="动态审批人" isExecutable="true">
            <bpmn:startEvent id="start" name="Start" activiti:initiator="applyUser" />
            <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_approve" />
            <!-- 关键：不配 assignee / candidateUsers / candidateGroups，审批人全靠 IApproverResolver 动态解析 -->
            <bpmn:userTask id="task_approve" name="经理审批" />
            <bpmn:sequenceFlow id="flow2" sourceRef="task_approve" targetRef="end" />
            <bpmn:endEvent id="end" name="End" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task DeployAsync(IProcessEngine engine)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Xml()));

        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", tenantId: DefaultTenant, ct: Ct);
    }

    private static WebApplicationFactory<Program> BuildHost(bool noApprover = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:RequireCredential", "false");
            builder.UseSetting("NetBpm:Security:ApiKey", ApiKey);
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<RecordingApproverResolver>();

                if (noApprover)
                {
                    // 返回空集合的解析器：验证「查不到审批人」时不写候选关系
                    services.AddSingleton<IApproverResolver, EmptyApproverResolver>();
                }
                else
                {
                    services.AddSingleton<IApproverResolver>(
                        provider => provider.GetRequiredService<RecordingApproverResolver>());
                }
            });
        });

    /// <summary>
    /// 模拟「外部系统」：按部门 id + 节点（职位）查审批人。
    /// 真实场景里这里是一次 HTTP 调用或一条 SQL。
    /// </summary>
    internal sealed class RecordingApproverResolver : IApproverResolver
    {
        private readonly List<Invocation> _invocations = [];

        public IReadOnlyList<Invocation> Invocations
        {
            get
            {
                lock (_invocations)
                {
                    return [.. _invocations];
                }
            }
        }

        public ValueTask<IReadOnlyList<string>> PostProcessAsync(
            ApprovalChainContext context,
            IReadOnlyList<string> approvers,
            CancellationToken ct = default)
        {
            var businessKey = context.BusinessKey;
            var deptId = context.GetStringVariable("applyDeptId");
            var activityId = context.ActivityId;

            lock (_invocations)
            {
                _invocations.Add(new Invocation(businessKey, deptId, activityId));
            }

            // 模拟外部系统：部门 102（成本管理部）的「经理审批」节点 → manager1
            IReadOnlyList<string> result =
                activityId == "task_approve" && deptId == "102" ? ["manager1"] : [];

            return ValueTask.FromResult(result);
        }

        internal sealed record Invocation(string? BusinessKey, string? DeptId, string ActivityId);
    }

    /// <summary>永远返回空集合的解析器。</summary>
    internal sealed class EmptyApproverResolver : IApproverResolver
    {
        public ValueTask<IReadOnlyList<string>> PostProcessAsync(
            ApprovalChainContext context,
            IReadOnlyList<string> approvers,
            CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);
    }
}

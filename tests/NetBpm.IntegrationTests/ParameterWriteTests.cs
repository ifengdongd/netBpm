using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 参数写侧授权（<c>writableBy</c>，§4.3）：发起按 <c>APPLICANT</c>、提交按 <c>APPROVER</c> 拒绝。
/// </summary>
/// <remarks>
/// <para>
/// 这是 M7 的最后一格：v1.13 让 schema 能<b>声明</b> <c>writableBy</c>，但写校验一直没按它拒绝——
/// 「声明出来了」和「执行了」是两件事，后者才叫契约。缺了它，调用方会以为值已经生效，
/// 而流程按旧值走完，这种偏差要到对账时才会被发现。
/// </para>
/// <para>
/// 四条规则各自一个用例：声明 <c>APPROVER</c> 的不能在发起写、声明 <c>APPLICANT</c> 的不能在审批写、
/// <b>未声明</b>的发起可写而审批只读（兼容基线）、<c>ADMIN</c> 只在调用方真的持管理范围时才放行。
/// </para>
/// </remarks>
public sealed class ParameterWriteTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "writable_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_Should422_WhenParameterWritableByApprover()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "write-start-approver", [PlatformScopes.InstanceStart]);

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/workflow/v1/instances",
            secret,
            "zhangsan",
            new
            {
                processKey = ProcessKey,
                parameters = new Dictionary<string, string?> { ["param.costCenter"] = "HR-01" },
            });

        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.ParameterReadOnly, problem.GetProperty("code").GetString());
        Assert.Contains("param.costCenter", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_ShouldAccept_WhenParameterWritableByApplicant()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "write-start-applicant",
            [PlatformScopes.InstanceStart, PlatformScopes.InstanceRead]);

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/workflow/v1/instances",
            secret,
            "zhangsan",
            new
            {
                processKey = ProcessKey,
                parameters = new Dictionary<string, string?> { ["param.days"] = "3" },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// 未声明 <c>writableBy</c> 的参数：<b>发起时可写</b>（老 BPMN 不写这个属性，不能因此拒收）。
    /// </summary>
    [Fact]
    public async Task Start_ShouldAccept_WhenWritableByNotDeclared()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "write-start-undeclared", [PlatformScopes.InstanceStart]);

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/workflow/v1/instances",
            secret,
            "zhangsan",
            new
            {
                processKey = ProcessKey,
                parameters = new Dictionary<string, string?> { ["param.note"] = "无声明也可写" },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary><c>writableBy="ADMIN"</c> 对普通申请人仍然拒绝——管理角色不是「人人都有」的角色。</summary>
    [Fact]
    public async Task Start_Should422_WhenParameterWritableByAdmin_AndCallerIsNotAdmin()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "write-start-admin-denied", [PlatformScopes.InstanceStart]);

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/workflow/v1/instances",
            secret,
            "zhangsan",
            new
            {
                processKey = ProcessKey,
                parameters = new Dictionary<string, string?> { ["param.auditFlag"] = "checked" },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>管理视角（<c>workflow:history:read:all</c>）可以越权写入 <c>writableBy="ADMIN"</c> 的参数。</summary>
    [Fact]
    public async Task Start_ShouldAccept_WhenParameterWritableByAdmin_AndCallerIsAdmin()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "write-start-admin",
            [PlatformScopes.InstanceStart, PlatformScopes.HistoryReadAll]);

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/workflow/v1/instances",
            secret,
            "zhangsan",
            new
            {
                processKey = ProcessKey,
                parameters = new Dictionary<string, string?> { ["param.auditFlag"] = "checked" },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// 未声明 <c>writableBy</c> 的参数在<b>审批环节只读</b>：
    /// 「发起时可写、之后只读」是 schema 的原始约定，不是本轮新加的收紧。
    /// </summary>
    [Fact]
    public async Task Submit_Should422_WhenParameterHasNoWritableBy()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await SubmitSecretAsync(client);
        var taskId = await StartAndReachApproveAsync(host);

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/tasks/{taskId}/submit",
            secret,
            "manager",
            new
            {
                optionId = "1",
                parameters = new Dictionary<string, string?> { ["param.note"] = "审批人补写" },
            });

        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.ParameterReadOnly, problem.GetProperty("code").GetString());
    }

    /// <summary>声明 <c>approver</c> 可写的参数：审批人提交成功，且值真的进了引擎变量。</summary>
    [Fact]
    public async Task Submit_ShouldAccept_WhenParameterWritableByApprover()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await SubmitSecretAsync(client);
        var taskId = await StartAndReachApproveAsync(host);

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/tasks/{taskId}/submit",
            secret,
            "manager",
            new
            {
                optionId = "1",
                parameters = new Dictionary<string, string?> { ["param.costCenter"] = "HR-01" },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var instanceId = (await ReadJsonAsync(response))
            .GetProperty("instance").GetProperty("instanceId").GetString()!;

        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var variables = await engine.GetVariablesAsync(instanceId, Ct);

        // 对外键 param.costCenter 镜像成裸名 costCenter（与 BPMN 网关条件同一套变量名）
        Assert.Equal("HR-01", variables["costCenter"]);
    }

    /// <summary>只允许申请人写的参数，审批人提交时拒绝（方向不能反）。</summary>
    [Fact]
    public async Task Submit_Should422_WhenParameterWritableByApplicantOnly()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await SubmitSecretAsync(client);
        var taskId = await StartAndReachApproveAsync(host);

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/tasks/{taskId}/submit",
            secret,
            "manager",
            new
            {
                optionId = "1",
                parameters = new Dictionary<string, string?> { ["param.days"] = "10" },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

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
        });

    private static async Task<string> SubmitSecretAsync(HttpClient client) =>
        await RegisterAsync(
            client,
            "write-submit",
            [
                PlatformScopes.TaskRead,
                PlatformScopes.TaskSubmit,
                PlatformScopes.TaskSubmitOnBehalf,
                PlatformScopes.InstanceRead,
            ]);

    /// <summary>
    /// 从起点走到「审批」环节：申请（lisi）→ 审批（manager）。
    /// </summary>
    /// <remarks>
    /// 两步都要走完才有审批环节的待办；直接用引擎发起并完成申请环节，
    /// 是为了让用例只关注<b>参数写权限</b>，不被发起端点的参数校验干扰。
    /// </remarks>
    private static async Task<string> StartAndReachApproveAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            startUserId: "zhangsan",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["days"] = "3" },
            ct: Ct);

        var applyTasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
        var apply = Assert.Single(applyTasks);

        Assert.Equal("task_apply", apply.TaskDefinitionKey);

        await engine.CompleteTaskAsync(apply.Id, new Dictionary<string, object?> { ["result"] = "1" }, ct: Ct);

        var approveTasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
        var approve = Assert.Single(approveTasks);

        Assert.Equal("task_approve", approve.TaskDefinitionKey);

        return approve.Id;
    }

    /// <summary>
    /// 流程：申请（<c>lisi</c>）→ 审批（<c>manager</c>）→ 复核（<c>boss</c>）。
    /// 三环节是刻意的：内存宿主历史关闭时，实例一结束就查不到视图，
    /// 而提交结果里必须带回实例视图——所以用例必须在实例仍在运行时断言。
    /// </summary>
    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              xmlns:netbpm="http://netbpm.org/schema/bpmn-extensions"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ProcessKey}}" name="参数写权限演示" isExecutable="true">
                <bpmn:extensionElements>
                  <netbpm:parameters>
                    <netbpm:parameter name="days" type="long" writableBy="APPLICANT" visibleTo="APPLICANT,APPROVER" />
                    <netbpm:parameter name="costCenter" type="string" writableBy="APPROVER" visibleTo="APPROVER,ADMIN" />
                    <netbpm:parameter name="note" type="string" />
                    <netbpm:parameter name="auditFlag" type="string" writableBy="ADMIN" />
                  </netbpm:parameters>
                </bpmn:extensionElements>
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:assignee="lisi">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="days" name="天数" type="long" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="manager">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                      <activiti:value id="3" name="驳回修改" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow2</bpmn:incoming>
                  <bpmn:outgoing>flow3</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow3" sourceRef="task_approve" targetRef="task_final" />
                <bpmn:userTask id="task_final" name="复核" activiti:assignee="boss">
                  <bpmn:incoming>flow3</bpmn:incoming>
                  <bpmn:outgoing>flow4</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow4" sourceRef="task_final" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> RegisterAsync(HttpClient client, string name, string[] scopes)
    {
        // 带 X-Subject-Id 的请求在鉴权中间件就要求 onbehalf（代客调用）；这是中间件的独立规则，
        // 与端点自身的 scope 闸门不相干，所以统一补上。
        var effective = scopes
            .Append(PlatformScopes.TaskSubmitOnBehalf)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new { name, tenantId = DefaultTenant, scopes = effective },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string secret,
        string subject,
        object body)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);
        request.Headers.Add("X-Subject-Id", subject);
        request.Content = JsonContent.Create(body);

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}

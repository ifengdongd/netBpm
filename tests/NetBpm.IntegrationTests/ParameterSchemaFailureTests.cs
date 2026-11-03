using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 参数规范读不出来时，发起与提交必须<b>拒绝</b>而不是按「无 schema 放行」。
/// </summary>
/// <remarks>
/// <para>
/// 这条用例守的是一次具体的降级：<see cref="TaskSubmitService"/> 早期在读取参数规范失败时
/// 返回 <see langword="null"/>，调用方把它当成「这个流程没有参数约束」，
/// 于是 <c>writableBy</c> 校验被整段跳过，响应还是 200。
/// 表现是「存储故障期间审批人能写只读字段」，而故障恢复后没有任何线索能找出这批写入。
/// </para>
/// <para>
/// 两种「拿不到规范」在类型上都是 <see langword="null"/>，所以不能靠类型区分，只能靠**抛错**区分：
/// 没有规范 = 正常返回 null；读失败 = 503 <c>parameter.schema_unavailable</c>。
/// </para>
/// </remarks>
public sealed class ParameterSchemaFailureTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "schema_fault_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Submit_Should503_WhenParameterSchemaUnavailable()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        var secret = await RegisterAsync(host, [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);
        var (_, taskId) = await StartAndGetTaskAsync(host);

        using var client = host.CreateClient();

        // 发起之后才让目录开始失败：这条用例要证明的是「提交侧不做无 schema 放行」，
        // 不能让发起本身先失败。
        host.Services.GetRequiredService<FaultSwitch>().FailParameterSchema = true;

        using var response = await SendSubmitAsync(client, taskId, secret);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = await ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.ParameterSchemaUnavailable, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_ShouldNotWriteVariables_WhenParameterSchemaUnavailable()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        var secret = await RegisterAsync(host, [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);
        var (instanceId, taskId) = await StartAndGetTaskAsync(host);

        using var client = host.CreateClient();

        host.Services.GetRequiredService<FaultSwitch>().FailParameterSchema = true;

        using var response = await SendSubmitAsync(client, taskId, secret);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // 拒绝必须发生在「落变量 / 完成任务」之前：任务仍在，且没有 result 变量。
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);

        Assert.Single(tasks);

        var variables = await engine.GetVariablesAsync(instanceId, Ct);

        Assert.False(variables.ContainsKey("result"), "被 503 拒绝的提交不应该写进审批结论变量");
    }

    [Fact]
    public async Task Start_Should503_WhenParameterSchemaUnavailable()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        var secret = await RegisterAsync(host, [PlatformScopes.InstanceStart, PlatformScopes.ProcessRead]);
        using var client = host.CreateClient();

        host.Services.GetRequiredService<FaultSwitch>().FailParameterSchema = true;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/workflow/v1/instances", UriKind.Relative))
        {
            Content = new StringContent(
                """{"processKey":"schema_fault_demo","parameters":{"param.days":"3"}}""",
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("X-Api-Key", secret);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = await ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.ParameterSchemaUnavailable, problem.GetProperty("code").GetString());
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    /// <summary>测试里控制「参数规范是否读失败」的开关（单例，无静态全局状态）。</summary>
    private sealed class FaultSwitch
    {
        public bool FailParameterSchema { get; set; }
    }

    /// <summary>只让参数规范读取失败，其余读写照常透传给真实目录。</summary>
    private sealed class FaultyParameterCatalog : IProcessCatalog
    {
        private readonly IProcessCatalog _inner;
        private readonly FaultSwitch _switch;

        public FaultyParameterCatalog(IProcessCatalog inner, FaultSwitch faultSwitch)
        {
            _inner = inner;
            _switch = faultSwitch;
        }

        public ValueTask<CursorPage<ProcessDescriptor>> ListAsync(
            ProcessQuery query,
            RequestContext context,
            CancellationToken ct = default) => _inner.ListAsync(query, context, ct);

        public ValueTask<ProcessDescriptor?> FindAsync(
            string processKey,
            RequestContext context,
            CancellationToken ct = default) => _inner.FindAsync(processKey, context, ct);

        public ValueTask<ProcessDefinitionInfo?> GetDefinitionAsync(
            string processKey,
            bool includeXml,
            RequestContext context,
            CancellationToken ct = default) => _inner.GetDefinitionAsync(processKey, includeXml, context, ct);

        public ValueTask<IReadOnlyList<ProcessVersionInfo>?> ListVersionsAsync(
            string processKey,
            RequestContext context,
            CancellationToken ct = default) => _inner.ListVersionsAsync(processKey, context, ct);

        public ValueTask<ParameterSchema?> GetParameterSchemaAsync(
            string processKey,
            RequestContext context,
            CancellationToken ct = default)
        {
            EnsureAvailable();

            return _inner.GetParameterSchemaAsync(processKey, context, ct);
        }

        public ValueTask<ParameterSchema?> GetParameterSchemaAsync(
            string processKey,
            int? version,
            RequestContext context,
            CancellationToken ct = default)
        {
            EnsureAvailable();

            return _inner.GetParameterSchemaAsync(processKey, version, context, ct);
        }

        public ValueTask<string?> GetDiagramSvgAsync(
            string processKey,
            int? version,
            RequestContext context,
            CancellationToken ct = default) => _inner.GetDiagramSvgAsync(processKey, version, context, ct);

        public ValueTask<ProcessFormSchema?> GetFormSchemaAsync(
            string processKey,
            int? version,
            RequestContext context,
            CancellationToken ct = default) => _inner.GetFormSchemaAsync(processKey, version, context, ct);

        private void EnsureAvailable()
        {
            if (_switch.FailParameterSchema)
            {
                throw new InvalidOperationException("模拟故障：参数规范索引不可读（nb_process_parameter / 目录存储）。");
            }
        }
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

            // 后注册的 IProcessCatalog 覆盖组合根里的那一条：单服务解析取最后一次注册。
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<FaultSwitch>();
                services.AddScoped<IProcessCatalog>(provider => new FaultyParameterCatalog(
                    provider.GetRequiredService<ProcessCatalog>(),
                    provider.GetRequiredService<FaultSwitch>()));
            });
        });

    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ProcessKey}}" name="参数规范故障演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:assignee="${applicant}">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="days" name="天数" type="long" variable="days" required="true" />
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="${approver}">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow2</bpmn:incoming>
                  <bpmn:outgoing>flow3</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow3" sourceRef="task_approve" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<(string InstanceId, string TaskId)> StartAndGetTaskAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            startUserId: "zhangsan",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["applicant"] = "lisi",
                ["approver"] = "manager",
                ["days"] = "3",
            },
            ct: Ct);

        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
        var task = Assert.Single(tasks);

        Assert.Equal("task_apply", task.TaskDefinitionKey);

        return (instanceId, task.Id);
    }

    private static async Task<string> RegisterAsync(WebApplicationFactory<Program> host, string[] scopes)
    {
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new { name = "schema-fault", tenantId = DefaultTenant, scopes },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    private static Task<HttpResponseMessage> SendSubmitAsync(HttpClient client, string taskId, string secret)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/workflow/v1/tasks/{taskId}/submit", UriKind.Relative))
        {
            // 必须带参数：不带参数时提交侧根本不会去读规范（没有要校验的字段），
            // 也就不会触发降级路径。这条用例守的正是「带参数 + 规范读失败」。
            Content = new StringContent(
                """{"optionId":"1","comment":"同意","parameters":{"param.days":"5"}}""",
                Encoding.UTF8,
                "application/json"),
        };

        // 审批结论必须落到真人主体：客户端凭据只提供 scope，主体由头身份给出。
        request.Headers.Add("X-Api-Key", secret);
        request.Headers.Add("X-Subject-Id", "lisi");

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        using var document = JsonDocument.Parse(text);

        return document.RootElement.Clone();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 流程目录（<c>/workflow/v1/processes</c>）端到端：部署 → 目录 → 定义 / 版本 / 流程图，
/// 以及「未授权流程按不存在处理」与「覆盖表精修名片」。
/// </summary>
/// <remarks>
/// <para>
/// 全部跑在内存模式宿主：这一层要验证的是<b>合并口径</b>（引擎定义 + BPMN 派生 + 覆盖行）
/// 与<b>授权口径</b>（白名单 / scope），不是持久化。
/// </para>
/// <para>
/// 流程定义用仓库里的两份真实样例（请假 / 订单）而不是内联 XML：
/// 目录的派生逻辑依赖真实 BPMN 的 startEvent 与 <c>approvalResult</c> 选项，
/// 用样例才代表「现网部署上来的流程长什么样」。
/// </para>
/// </remarks>
public sealed class ProcessCatalogTests
{
    private const string DefaultTenant = "default";
    private const string LeaveKey = "leave_process";
    private const string OrderKey = "order_process";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_ShouldExposeDeployedProcess_WithDerivedCapabilities()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "hr-system",
            [PlatformScopes.ProcessRead, PlatformScopes.InstanceStart]);

        using var response = await SendAsync(client, "/workflow/v1/processes", secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = body.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());

        var process = items[0];

        // 对外稳定标识 = BPMN process/@id，不是流程定义 id（后者随版本走）
        Assert.Equal(LeaveKey, process.GetProperty("processKey").GetString());
        Assert.False(string.IsNullOrWhiteSpace(process.GetProperty("name").GetString()));

        // 部署即可见：没有登记过 nb_process_catalog 也有状态与版本
        Assert.Equal("ACTIVE", process.GetProperty("status").GetString());
        Assert.Equal(1, process.GetProperty("deployedVersion").GetInt32());

        // 名称来自 BPMN，入口来自 startEvent——调用方据此渲染「发起」按钮
        var entryPoints = process.GetProperty("entryPoints");
        Assert.Equal(1, entryPoints.GetArrayLength());
        Assert.Equal("start", entryPoints[0].GetProperty("startActivityId").GetString());

        // 能力声明：已授权发起 + 能读定义与图 + 有表单字段
        var capabilities = process.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("canStart").GetBoolean());
        Assert.True(capabilities.GetProperty("canReadDefinition").GetBoolean());
        Assert.True(capabilities.GetProperty("canReadDiagram").GetBoolean());
        Assert.True(capabilities.GetProperty("hasFormSchema").GetBoolean());

        // 请假流程声明了 days/reason 参数 → 参数规范非空，能力声明闭环
        Assert.True(capabilities.GetProperty("hasParameterSchema").GetBoolean());

        // 审批语义从 approvalResult 的 optionId 推导（1/2/3 → 三个核心语义）
        var semantics = capabilities.GetProperty("supportedSemantics")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Assert.Contains("APPROVE", semantics);
        Assert.Contains("REJECT", semantics);
        Assert.Contains("RETURN", semantics);

        // 校验和用来判断「我缓存的流程图过期了吗」，必须带算法前缀
        Assert.StartsWith("sha256:", process.GetProperty("definitionChecksum").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientWithoutGrant_ShouldNotSeeProcess_AndGet404()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");
        await DeploySampleAsync(host, "order-process.bpmn20.xml");

        using var client = host.CreateClient();

        // 白名单只给订单：请假流程对它而言**不存在**（不是「看到但没权限」）
        var secret = await RegisterAsync(
            client,
            "order-system",
            [PlatformScopes.ProcessRead],
            allowedProcessKeys: [OrderKey]);

        using var list = await SendAsync(client, "/workflow/v1/processes", secret);
        var body = await ReadJsonAsync(list);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var keys = body.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("processKey").GetString())
            .ToArray();

        Assert.Equal([OrderKey], keys);

        // 直接按 key 访问：404 而不是 403——403 会泄露「这条流程存在」
        using var forbidden = await SendAsync(client, $"/workflow/v1/processes/{LeaveKey}", secret);
        var problem = await ReadJsonAsync(forbidden);

        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(ErrorCodes.ProcessNotFound, problem.GetProperty("code").GetString());

        // 错误体是 RFC 9457：不用旧宿主的 { code, message } 信封
        Assert.StartsWith(
            "https://netbpm.dev/problems/",
            problem.GetProperty("type").GetString(),
            StringComparison.Ordinal);

        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Definition_ShouldOmitXml_UnlessExplicitlyRequested()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "hr-system", [PlatformScopes.ProcessRead]);

        using var withoutXml = await SendAsync(client, $"/workflow/v1/processes/{LeaveKey}/definition", secret);
        var definition = await ReadJsonAsync(withoutXml);

        Assert.Equal(HttpStatusCode.OK, withoutXml.StatusCode);
        Assert.Equal(LeaveKey, definition.GetProperty("processKey").GetString());
        Assert.False(definition.TryGetProperty("xml", out var xml) && xml.ValueKind == JsonValueKind.String);

        using var withXml = await SendAsync(
            client,
            $"/workflow/v1/processes/{LeaveKey}/definition?includeXml=true",
            secret);

        var full = await ReadJsonAsync(withXml);

        Assert.Equal(HttpStatusCode.OK, withXml.StatusCode);
        Assert.Contains("<bpmn:process", full.GetProperty("xml").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Versions_ShouldMarkCurrent_AndKeepHistory()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "hr-system", [PlatformScopes.ProcessRead]);

        using var response = await SendAsync(client, $"/workflow/v1/processes/{LeaveKey}/versions", secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var versions = await ReadJsonAsync(response);

        Assert.Equal(2, versions.GetArrayLength());

        // 新到旧；只有最新一版是 current
        Assert.Equal(2, versions[0].GetProperty("version").GetInt32());
        Assert.True(versions[0].GetProperty("isCurrent").GetBoolean());
        Assert.Equal(1, versions[1].GetProperty("version").GetInt32());
        Assert.False(versions[1].GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task List_ShouldPageWithCursor_WithoutDuplicates()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");
        await DeploySampleAsync(host, "order-process.bpmn20.xml");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "portal", [PlatformScopes.ProcessRead]);

        using var first = await SendAsync(client, "/workflow/v1/processes?limit=1", secret);
        var firstPage = await ReadJsonAsync(first);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, firstPage.GetProperty("items").GetArrayLength());
        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());

        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        using var second = await SendAsync(
            client,
            $"/workflow/v1/processes?limit=1&cursor={Uri.EscapeDataString(cursor!)}",
            secret);

        var secondPage = await ReadJsonAsync(second);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, secondPage.GetProperty("items").GetArrayLength());
        Assert.False(secondPage.GetProperty("hasMore").GetBoolean());

        var keys = firstPage.GetProperty("items").EnumerateArray()
            .Concat(secondPage.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("processKey").GetString())
            .ToArray();

        // 两页合起来恰好是全部流程，没有重复也没有漏行
        Assert.Equal(2, keys.Length);
        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task List_ShouldFilterByCategoryAndStatus_FromCatalogStore()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");
        await DeploySampleAsync(host, "order-process.bpmn20.xml");

        // 覆盖行只在「需要精修」时才登记：分类、图标、业务页面地址、显式状态
        var store = Assert.IsType<InMemoryProcessCatalogStore>(
            host.Services.GetRequiredService<IProcessCatalogStore>());

        var now = DateTimeOffset.UtcNow;

        store.Upsert(new ProcessCatalogEntry(
            DefaultTenant,
            LeaveKey,
            Name: "请假申请（HR）",
            Category: "HR",
            Description: "员工请假审批",
            Icon: "https://cdn.example.com/leave.svg",
            PageUrls: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["start"] = "/hr/leave/new",
                ["approve"] = "/approve.html?taskId={taskId}",
            },
            EntryPoints: null,
            Status: ProcessStatus.Active,
            SortOrder: 0,
            Labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["zh-CN"] = "请假申请" },
            ParameterSchemaVersion: 3,
            CreatedAt: now,
            UpdatedAt: now));

        store.Upsert(new ProcessCatalogEntry(
            DefaultTenant,
            OrderKey,
            Name: "订单审批（已下线）",
            Category: "SALES",
            Description: null,
            Icon: null,
            PageUrls: null,
            EntryPoints: null,
            Status: ProcessStatus.Retired,
            SortOrder: 10,
            Labels: null,
            ParameterSchemaVersion: 0,
            CreatedAt: now,
            UpdatedAt: now));

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "portal", [PlatformScopes.ProcessRead]);

        using var hr = await SendAsync(client, "/workflow/v1/processes?category=HR", secret);
        var hrBody = await ReadJsonAsync(hr);

        Assert.Equal(1, hrBody.GetProperty("items").GetArrayLength());

        var leave = hrBody.GetProperty("items")[0];

        // 覆盖行胜出：名称 / 分类 / 业务页面地址 / 参数 schema 版本都来自登记值
        Assert.Equal("请假申请（HR）", leave.GetProperty("name").GetString());
        Assert.Equal("HR", leave.GetProperty("category").GetString());
        Assert.Equal("/hr/leave/new", leave.GetProperty("pageUrls").GetProperty("start").GetString());
        Assert.Equal(3, leave.GetProperty("parameterSchemaVersion").GetInt32());
        Assert.True(leave.GetProperty("capabilities").GetProperty("hasParameterSchema").GetBoolean());

        // 已下线的流程不能再发起
        using var retired = await SendAsync(client, "/workflow/v1/processes?status=RETIRED", secret);
        var retiredBody = await ReadJsonAsync(retired);
        var order = retiredBody.GetProperty("items")[0];

        Assert.Equal(OrderKey, order.GetProperty("processKey").GetString());
        Assert.False(order.GetProperty("capabilities").GetProperty("canStart").GetBoolean());
    }

    [Fact]
    public async Task SuspendedDefinition_ShouldOverrideCatalogStatus()
    {
        using var host = BuildHost();
        var definition = await DeploySampleAsync(host, "leave-process.bpmn20.xml");

        var engine = host.Services.GetRequiredService<IProcessEngine>();
        await engine.SuspendProcessDefinitionAsync(definition, suspendProcessInstances: false, ct: Ct);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "hr-system",
            [PlatformScopes.ProcessRead, PlatformScopes.InstanceStart]);

        using var response = await SendAsync(client, $"/workflow/v1/processes/{LeaveKey}", secret);
        var body = await ReadJsonAsync(response);

        // 引擎挂起优先于覆盖行：目录不能把挂起的流程说成可发起
        Assert.Equal("SUSPENDED", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("capabilities").GetProperty("canStart").GetBoolean());
    }

    [Fact]
    public async Task Diagram_ShouldReturnSvg()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "hr-system", [PlatformScopes.ProcessRead]);

        using var response = await SendAsync(client, $"/workflow/v1/processes/{LeaveKey}/diagram.svg", secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);

        var svg = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParameterSchema_ShouldDeriveFromFormProperties()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "hr-system", [PlatformScopes.ProcessRead]);

        using var response = await SendAsync(
            client,
            $"/workflow/v1/processes/{LeaveKey}/parameters/schema",
            secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var schema = await ReadJsonAsync(response);

        Assert.Equal(LeaveKey, schema.GetProperty("processKey").GetString());
        Assert.Equal(1, schema.GetProperty("version").GetInt32());

        var parameters = schema.GetProperty("parameters");

        // 请假流程有 days / reason / comment 三个业务参数；approvalResult 是审批选项（语义字典），不进参数契约
        var byName = parameters.EnumerateArray()
            .ToDictionary(
                element => element.GetProperty("apiName").GetString()!,
                element => element,
                StringComparer.Ordinal);

        Assert.Equal(3, byName.Count);
        Assert.True(byName.ContainsKey("param.days"));
        Assert.True(byName.ContainsKey("param.reason"));
        Assert.True(byName.ContainsKey("param.comment"));

        var days = byName["param.days"];
        Assert.Equal("days", days.GetProperty("variableName").GetString());
        Assert.Equal("LONG", days.GetProperty("type").GetString());
        Assert.True(days.GetProperty("required").GetBoolean());
        Assert.Equal("请假天数", days.GetProperty("displayName").GetString());

        var reason = byName["param.reason"];
        Assert.Equal("STRING", reason.GetProperty("type").GetString());
        Assert.False(reason.GetProperty("required").GetBoolean());

        // 保留前缀：调用方自定义参数不得占用 param. / form.
        var reserved = schema.GetProperty("reservedPrefixes")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Assert.Contains("param.", reserved);
        Assert.Contains("form.", reserved);
    }

    [Fact]
    public async Task ParameterSchema_ShouldExcludeApprovalOptionField()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "order-process.bpmn20.xml");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "order-system", [PlatformScopes.ProcessRead]);

        using var response = await SendAsync(
            client,
            $"/workflow/v1/processes/{OrderKey}/parameters/schema",
            secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var schema = await ReadJsonAsync(response);
        var apiNames = schema.GetProperty("parameters")
            .EnumerateArray()
            .Select(element => element.GetProperty("apiName").GetString())
            .ToArray();

        // 订单流程有 amount / remark / comment；只有 approvalResult 被排除（它是审批选项，走语义字典）
        Assert.Contains("param.amount", apiNames);
        Assert.Contains("param.remark", apiNames);
        Assert.Contains("param.comment", apiNames);
        Assert.DoesNotContain("param.approvalResult", apiNames);
    }

    [Fact]
    public async Task ParameterSchema_ShouldBeEmpty_WhenNoBusinessParameters()
    {
        using var host = BuildHost();
        await DeployInlineAsync(host, "bare_process");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "consumer", [PlatformScopes.ProcessRead]);

        using var schemaResponse = await SendAsync(
            client,
            "/workflow/v1/processes/bare_process/parameters/schema",
            secret);

        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);

        var schema = await ReadJsonAsync(schemaResponse);
        Assert.Equal(0, schema.GetProperty("parameters").GetArrayLength());
        Assert.Equal(0, schema.GetProperty("version").GetInt32());

        // 能力声明与 schema 一致：无参数 → hasParameterSchema=false
        using var detailResponse = await SendAsync(client, "/workflow/v1/processes/bare_process", secret);
        var detail = await ReadJsonAsync(detailResponse);

        Assert.False(detail.GetProperty("capabilities").GetProperty("hasParameterSchema").GetBoolean());
    }

    [Fact]
    public async Task ParameterSchema_Should404_ForUnauthorizedProcess()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");
        await DeploySampleAsync(host, "order-process.bpmn20.xml");

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "order-system",
            [PlatformScopes.ProcessRead],
            allowedProcessKeys: [OrderKey]);

        using var response = await SendAsync(
            client,
            $"/workflow/v1/processes/{LeaveKey}/parameters/schema",
            secret);

        // 未授权流程按不存在处理：404 而非 403
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequest_ShouldBeRejected_WhenCredentialIsRequired()
    {
        using var host = BuildHost(requireCredential: true);
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");

        using var client = host.CreateClient();
        using var response = await client.GetAsync(new Uri("/workflow/v1/processes", UriKind.Relative), Ct);

        // 平台面在鉴权保护范围内：否则匿名请求就能读到全部流程目录，scope 与白名单形同虚设
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClientWithoutProcessScope_ShouldBeForbidden()
    {
        using var host = BuildHost();
        await DeploySampleAsync(host, "leave-process.bpmn20.xml");

        using var client = host.CreateClient();

        // 只有实例读权限，没有目录读权限：scope 是能力上限，不能靠「能看到流程」倒推
        var secret = await RegisterAsync(client, "reporting", [PlatformScopes.InstanceRead]);

        using var response = await SendAsync(client, "/workflow/v1/processes", secret);
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static WebApplicationFactory<Program> BuildHost(bool requireCredential = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "NetBpm:Security:RequireCredential",
                requireCredential ? "true" : "false");

            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");

            // 目录用例验证的是合并与授权，不是熔断：关掉限流避免长用例自己打到配额
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
        });

    /// <summary>部署仓库里的样例流程，返回流程定义 id。</summary>
    private static async Task<string> DeploySampleAsync(WebApplicationFactory<Program> host, string fileName)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var path = Path.Combine(RepositoryRoot(), "web", "designer", "examples", fileName);

        await using var stream = File.OpenRead(path);

        var definition = await engine.DeployAsync(stream, fileName, ct: Ct);

        return definition.Id;
    }

    /// <summary>部署一个没有任何 formProperty 的极简流程（用于「未声明参数」场景）。</summary>
    private static async Task<string> DeployInlineAsync(WebApplicationFactory<Program> host, string processKey)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{processKey}" name="Bare Process" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        var definition = await engine.DeployAsync(stream, processKey + ".bpmn20.xml", ct: Ct);

        return definition.Id;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetBpm.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("找不到仓库根目录（NetBpm.slnx）：测试必须在仓库内运行。");
    }

    private static async Task<string> RegisterAsync(
        HttpClient client,
        string name,
        string[] scopes,
        string[]? allowedProcessKeys = null)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients", UriKind.Relative),
            new
            {
                name,
                tenantId = DefaultTenant,
                scopes,
                allowedProcessKeys,
            },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}

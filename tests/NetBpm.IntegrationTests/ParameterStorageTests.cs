using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// M11 两张参数表：规范索引（<c>nb_process_parameter</c>）与修订历史（<c>nb_parameter_snapshot</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 两张表回答的是两个不同的问题，这也是它们必须分开的原因：
/// <list type="number">
///   <item><description><b>索引</b>：这一版流程<i>声明了哪些参数</i>（历史实例按当时契约渲染参数面板）；</description></item>
///   <item><description><b>修订</b>：这张单的参数<i>被谁在第几步改成了什么</i>（引擎变量只有当前值）。</description></item>
/// </list>
/// </para>
/// <para>
/// 跑 MySQL：两张表都要读库，而且「索引按定义版本累积」这条性质只有真库才能验
/// （内存实现没有主键，也就谈不上累积）。
/// </para>
/// <para>
/// ⚠️ 每个用例用<b>各自的流程 key</b>：清库工具只清引擎表（<c>ACT_*</c>），
/// 平台表的行会留在库里，共用 key 会让「索引已存在」的短路把上一个用例的旧行当成自己的。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class ParameterStorageTests : IClassFixture<RestEngineFactory>
{
    private const string DefaultTenant = "default";

    private readonly RestEngineFactory _factory;

    public ParameterStorageTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>查参数规范会把派生结果物化进索引：行的内容与 BPMN 声明一一对应。</summary>
    [Fact]
    public async Task Index_ShouldMaterializeDeclaredParameters()
    {
        const string key = "param_storage_index";

        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        var version = await DeployAsync(engine, key, includeCostCenter: true);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "parameter-index", [PlatformScopes.ProcessRead]);

        using (var response = await SendGetAsync(client, $"/workflow/v1/processes/{key}/parameters/schema", secret))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var store = _factory.Services.GetRequiredService<IProcessParameterStore>();
        var indexed = await store.FindAsync(DefaultTenant, key, version, Ct);

        Assert.NotNull(indexed);
        Assert.Equal(3, indexed!.Parameters.Count);

        var days = Assert.Single(indexed.Parameters, item => item.VariableName == "days");

        Assert.Equal("param.days", days.ApiName);
        Assert.Equal(ParameterType.Long, days.Type);
        Assert.True(days.Required);
        Assert.Equal(new[] { "APPLICANT" }, days.WritableBy);
        Assert.Equal(new[] { "APPLICANT" }, days.VisibleTo);
        Assert.Equal(1m, days.Constraints?.Minimum);
        Assert.Equal(365m, days.Constraints?.Maximum);

        // 只在 netbpm:parameters 里声明、BPMN 环节上没有对应 formProperty 的参数同样要进索引
        var costCenter = Assert.Single(indexed.Parameters, item => item.VariableName == "costCenter");

        Assert.Equal("param.costCenter", costCenter.ApiName);
        Assert.Equal(new[] { "APPROVER", "ADMIN" }, costCenter.VisibleTo);
        Assert.Equal("^[A-Z]{2,4}-\\d{2}$", costCenter.Constraints?.Pattern);
    }

    /// <summary>
    /// 重新部署（版本 +1）不在原行上覆盖：旧版本的行留着，它是还停在旧版本上的单据的唯一解释。
    /// </summary>
    [Fact]
    public async Task Index_ShouldKeepPreviousVersionRows()
    {
        const string key = "param_storage_versions";

        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        var v1 = await DeployAsync(engine, key, includeCostCenter: true);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "parameter-index-versions", [PlatformScopes.ProcessRead]);

        using (var first = await SendGetAsync(client, $"/workflow/v1/processes/{key}/parameters/schema", secret))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var v2 = await DeployAsync(engine, key, includeCostCenter: false);

        Assert.True(v2 > v1, "重新部署必须产生新版本，否则这条用例验不到累积行为。");

        using (var second = await SendGetAsync(client, $"/workflow/v1/processes/{key}/parameters/schema", secret))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        var store = _factory.Services.GetRequiredService<IProcessParameterStore>();

        var oldSchema = await store.FindAsync(DefaultTenant, key, v1, Ct);
        var newSchema = await store.FindAsync(DefaultTenant, key, v2, Ct);

        Assert.NotNull(oldSchema);
        Assert.NotNull(newSchema);

        // v1 三参，v2 只剩两参：旧行没有被 v2 的整组重建抹掉
        Assert.Equal(3, oldSchema!.Parameters.Count);
        Assert.Equal(2, newSchema!.Parameters.Count);
        Assert.DoesNotContain(newSchema.Parameters, item => item.VariableName == "costCenter");
    }

    /// <summary>发起写第 1 版、提交写第 2 版：修订号单调、来源可辨、值只记本次提交的。</summary>
    [Fact]
    public async Task History_ShouldRecordStartAndSubmit()
    {
        const string key = "param_storage_history";

        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine, key, includeCostCenter: false);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(
            client,
            "parameter-history",
            [
                PlatformScopes.ProcessRead,
                PlatformScopes.InstanceStart,
                PlatformScopes.InstanceRead,
                PlatformScopes.TaskRead,
                PlatformScopes.TaskSubmit,
                PlatformScopes.TaskSubmitOnBehalf,
            ]);

        var instanceId = await StartViaPlatformAsync(
            client,
            secret,
            key,
            new Dictionary<string, string?> { ["param.days"] = "3" });

        using (var started = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/parameters/history",
            secret))
        {
            var body = await ReadJsonAsync(started);

            Assert.Equal(HttpStatusCode.OK, started.StatusCode);

            var entries = body.EnumerateArray().ToList();

            Assert.Single(entries);
            Assert.Equal(1, entries[0].GetProperty("revision").GetInt64());
            Assert.Equal("3", entries[0].GetProperty("values").GetProperty("param.days").GetString());
            Assert.Equal("zhangsan", entries[0].GetProperty("updatedBy").GetString());
        }

        var taskId = await FindTaskAsync(engine, instanceId);

        using (var submit = await SendPostAsync(
            client,
            $"/workflow/v1/tasks/{taskId}/submit",
            secret,
            "manager",
            new
            {
                optionId = "1",
                comment = "同意",
                parameters = new Dictionary<string, string?> { ["param.note"] = "复核通过" },
            }))
        {
            Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        }

        using var history = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/parameters/history",
            secret);
        var historyBody = await ReadJsonAsync(history);

        Assert.Equal(HttpStatusCode.OK, history.StatusCode);

        var revisions = historyBody.EnumerateArray().ToList();

        Assert.Equal(2, revisions.Count);

        // 倒序：最近的一版在前
        Assert.Equal(2, revisions[0].GetProperty("revision").GetInt64());
        Assert.Equal("复核通过", revisions[0].GetProperty("values").GetProperty("param.note").GetString());
        Assert.Equal("manager", revisions[0].GetProperty("updatedBy").GetString());
        Assert.Equal(1, revisions[1].GetProperty("revision").GetInt64());
        Assert.Equal("3", revisions[1].GetProperty("values").GetProperty("param.days").GetString());
    }

    /// <summary>修订历史同样过 <c>visibleTo</c>：申请人看不到「只给审批人看」的那一栏，连历史也看不到。</summary>
    [Fact]
    public async Task History_ShouldHideParametersHiddenFromViewer()
    {
        const string key = "param_storage_visibility";

        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine, key, includeCostCenter: true);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(
            client,
            "parameter-history-visibility",
            [
                PlatformScopes.ProcessRead,
                PlatformScopes.InstanceStart,
                PlatformScopes.InstanceRead,
                PlatformScopes.TaskSubmitOnBehalf,
            ]);

        // costCenter 声明为 visibleTo="APPROVER,ADMIN"：发起人写了它，但自己看不到
        var instanceId = await StartViaPlatformAsync(
            client,
            secret,
            key,
            new Dictionary<string, string?>
            {
                ["param.days"] = "2",
                ["param.costCenter"] = "IT-01",
            });

        // 必须带主体：不带主体等于纯服务令牌，可见性一律不收窄（那是给系统调用留的口子）
        using var response = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/parameters/history",
            secret,
            "zhangsan");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var values = body.EnumerateArray().Single().GetProperty("values");

        Assert.Equal("2", values.GetProperty("param.days").GetString());
        Assert.False(values.TryGetProperty("param.costCenter", out _));
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    /// <summary>发起并返回实例 id（发起人 zhangsan）。</summary>
    private static async Task<string> StartViaPlatformAsync(
        HttpClient client,
        string secret,
        string processKey,
        IReadOnlyDictionary<string, string?> parameters)
    {
        using var response = await SendPostAsync(
            client,
            "/workflow/v1/instances",
            secret,
            "zhangsan",
            new { processKey, parameters });

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"发起失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("instanceId").GetString()!;
    }

    private static async Task<string> FindTaskAsync(IProcessEngine engine, string instanceId)
    {
        var task = Assert.Single(
            await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        return task.Id;
    }

    /// <summary>
    /// 部署三参数流程（days 必填、costCenter 只对审批人可见、note 允许审批环节改写），返回新版本号。
    /// </summary>
    /// <param name="engine">引擎门面。</param>
    /// <param name="processKey">流程 key（每个用例各自一个，避免平台表的旧行被当成自己的）。</param>
    /// <param name="includeCostCenter">是否声明 costCenter（用来验证索引按版本累积）。</param>
    private static async Task<int> DeployAsync(IProcessEngine engine, string processKey, bool includeCostCenter)
    {
        var extra = includeCostCenter
            ? """
                    <netbpm:parameter name="costCenter" type="string"
                                      visibleTo="APPROVER,ADMIN"
                                      pattern="^[A-Z]{2,4}-\d{2}$" />
              """
            : string.Empty;

        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              xmlns:netbpm="http://netbpm.org/schema/bpmn-extensions"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{processKey}}" name="参数存储演示" isExecutable="true">
                <bpmn:extensionElements>
                  <netbpm:parameters>
                    <netbpm:parameter name="days" type="long" required="true"
                                      apiName="param.days"
                                      writableBy="APPLICANT" visibleTo="APPLICANT"
                                      min="1" max="365" />
            {{extra}}
                    <netbpm:parameter name="note" type="string" writableBy="APPLICANT,APPROVER" />
                  </netbpm:parameters>
                </bpmn:extensionElements>
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_owner" />
                <bpmn:userTask id="task_owner" name="主管审批" activiti:assignee="manager">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                    <activiti:formProperty id="days" name="天数" type="long" variable="days" required="true" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_owner" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var definition = await engine.DeployAsync(stream, processKey + ".bpmn20.xml", ct: Ct);

        return definition.Version;
    }

    private static void Reset()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        MySqlTestDatabase.Reset(session);
    }

    private static void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        try
        {
            using var session = MySqlTestDatabase.CreateSessionFactory().Create();
            var count = session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.tables "
                + "where table_schema = database() "
                + "and table_name in ('nb_process_parameter', 'nb_parameter_snapshot')");

            Assert.SkipUnless(
                count >= 2,
                "本库缺参数表（nb_process_parameter / nb_parameter_snapshot）。"
                + "请把 NETBPM_MYSQL_CONNECTION 指向 netbpm_ittest，或对目标库执行 DbMigrate migrate 补齐 nb_* 表。");
        }
        catch
        {
            Assert.Skip("无法探测参数表，跳过。");
        }
    }

    private static async Task<string> RegisterAsync(HttpClient client, string name, string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new { name, tenantId = DefaultTenant, scopes },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    private static Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        string path,
        string secret,
        string? subject = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        if (subject is not null)
        {
            request.Headers.Add("X-Subject-Id", subject);
        }

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> SendPostAsync(
        HttpClient client,
        string path,
        string secret,
        string? subject,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        if (subject is not null)
        {
            request.Headers.Add("X-Subject-Id", subject);
        }

        request.Content = JsonContent.Create(body);

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        using var document = JsonDocument.Parse(text);

        return document.RootElement.Clone();
    }
}

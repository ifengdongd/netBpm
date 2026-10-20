using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 管理端流程模型治理：保存前校验、版本历史、回滚、发布。
/// <para>走真实 Admin 宿主 + MySQL，验证「模型 → 校验 → 版本 → 部署」整条链路。</para>
/// </summary>
[Collection("MySql")]
public sealed class AdminModelGovernanceTests : IClassFixture<AdminEngineFactory>
{
    private const string ModelId = "governance-test-model";

    private const string V1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="governance_process" name="治理流程 v1" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="task_approve" />
            <userTask id="task_approve" name="审批 v1" activiti:assignee="alice" />
            <sequenceFlow id="f2" sourceRef="task_approve" targetRef="end" />
            <endEvent id="end" />
          </process>
        </definitions>
        """;

    private const string V2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="governance_process" name="治理流程 v2" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="task_review" />
            <userTask id="task_review" name="复核 v2" activiti:assignee="bob" />
            <sequenceFlow id="f2" sourceRef="task_review" targetRef="end" />
            <endEvent id="end" />
          </process>
        </definitions>
        """;

    /// <summary>缺默认流的排他网关：XML 合法，但部署期校验必然报错。</summary>
    private const string Invalid = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="governance_process" name="非法流程" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="gw" />
            <exclusiveGateway id="gw" />
            <sequenceFlow id="f2" sourceRef="gw" targetRef="end">
              <conditionExpression xsi:type="bpmn:tFormalExpression">${ok}</conditionExpression>
            </sequenceFlow>
            <endEvent id="end" />
          </process>
        </definitions>
        """;

    private readonly AdminEngineFactory _factory;

    public AdminModelGovernanceTests(AdminEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_ShouldRejectInvalidModel_AndRecordHistory()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Cleanup();

        using var client = _factory.CreateClient();

        // ① 非法草稿：允许保存（草稿语义），但把阻断级问题带回；版本照常递增
        using (var invalid = await PutModelAsync(client, Invalid))
        {
            Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);

            var body = await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(1, body.GetProperty("revision").GetInt32());
            Assert.NotEmpty(body.GetProperty("errors").EnumerateArray());
        }

        // ①-1 发布非法草稿：必须被部署校验拦截（400），不会产生流程定义
        using (var publishInvalid = await client.PostAsync(
            new Uri($"/api/admin/models/{ModelId}/publish", UriKind.Relative),
            content: null,
            Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, publishInvalid.StatusCode);
        }

        // ② v1 保存：版本 2
        using (var save1 = await PutModelAsync(client, V1))
        {
            Assert.Equal(HttpStatusCode.OK, save1.StatusCode);

            var body = await save1.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(2, body.GetProperty("revision").GetInt32());
            Assert.Empty(body.GetProperty("errors").EnumerateArray());
        }

        // ③ v2 保存：版本 3
        using (var save2 = await PutModelAsync(client, V2))
        {
            Assert.Equal(HttpStatusCode.OK, save2.StatusCode);

            var body = await save2.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(3, body.GetProperty("revision").GetInt32());
        }

        var history = await HistoryAsync(client);

        Assert.Equal(3, history.Count);
        Assert.Equal(3, history[0].GetProperty("revision").GetInt32());
        Assert.Equal(2, history[1].GetProperty("revision").GetInt32());
        Assert.Equal(1, history[2].GetProperty("revision").GetInt32());
        Assert.All(history, item => Assert.Equal("save", item.GetProperty("action").GetString()));

        // ④ 回滚到 v1（revision 2）：生成第 4 版（历史只增不删），模型内容回到 v1
        var revision1Id = history[1].GetProperty("id").GetString()!;

        using (var rollback = await client.PostAsync(
            new Uri($"/api/admin/models/{ModelId}/rollback/{revision1Id}", UriKind.Relative),
            content: null,
            Ct))
        {
            var body = await rollback.Content.ReadAsStringAsync(Ct);
            Assert.True(rollback.IsSuccessStatusCode, $"回滚失败：{rollback.StatusCode} {body}");

            using var document = JsonDocument.Parse(body);
            Assert.Contains("审批 v1", document.RootElement.GetProperty("bpmnXml").GetString(), StringComparison.Ordinal);
        }

        var historyAfterRollback = await HistoryAsync(client);

        Assert.Equal(4, historyAfterRollback.Count);
        Assert.Equal(4, historyAfterRollback[0].GetProperty("revision").GetInt32());
        Assert.Equal("rollback", historyAfterRollback[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Publish_ShouldDeployCurrentModel_AndBumpVersion()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Cleanup();

        if (MySqlTestDatabase.IsAvailable)
        {
            using var session = MySqlTestDatabase.CreateSessionFactory().Create();
            NetBpm.Persistence.EngineTableCleaner.Purge(session);
        }

        using var client = _factory.CreateClient();

        using (var save = await PutModelAsync(client, V1))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        var first = await PublishAsync(client);
        var second = await PublishAsync(client);

        Assert.Equal("governance_process", first.GetProperty("processDefinitionKey").GetString());
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        Assert.Equal(2, second.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task PruneHistory_ShouldKeepLatestRevisions_AndReturn404ForMissingModel()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Cleanup();

        using var client = _factory.CreateClient();

        // 前置：模型不存在时 404
        using (var missing = await client.DeleteAsync(
            new Uri($"/api/admin/models/{ModelId}/history?keepLatest=2", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // 保存 3 个版本
        foreach (var xml in new[] { V1, V2, V1 })
        {
            using var save = await PutModelAsync(client, xml);
            Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(3, (await HistoryAsync(client)).Count);

        // 保留最近 2 版：删掉 v1，最新版本（v3）永远保留
        using (var prune = await client.DeleteAsync(
            new Uri($"/api/admin/models/{ModelId}/history?keepLatest=2", UriKind.Relative), Ct))
        {
            var body = await prune.Content.ReadAsStringAsync(Ct);
            Assert.True(prune.IsSuccessStatusCode, $"清理失败：{prune.StatusCode} {body}");

            using var document = JsonDocument.Parse(body);
            Assert.Equal(1, document.RootElement.GetProperty("removed").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("keepLatest").GetInt32());
        }

        var history = await HistoryAsync(client);

        Assert.Equal(2, history.Count);
        Assert.Equal(3, history[0].GetProperty("revision").GetInt32());
        Assert.Equal(2, history[1].GetProperty("revision").GetInt32());

        // 再次清理：没有可删的（cutoff < 1），返回 0 且不报错
        using (var pruneAgain = await client.DeleteAsync(
            new Uri($"/api/admin/models/{ModelId}/history?keepLatest=2", UriKind.Relative), Ct))
        {
            var body = await pruneAgain.Content.ReadAsStringAsync(Ct);
            Assert.True(pruneAgain.IsSuccessStatusCode, body);

            using var document = JsonDocument.Parse(body);
            Assert.Equal(0, document.RootElement.GetProperty("removed").GetInt32());
        }

        // 模型本身不受影响
        var historyAfterPrune = await HistoryAsync(client);
        Assert.Equal(2, historyAfterPrune.Count);
    }

    private async Task<HttpResponseMessage> PutModelAsync(HttpClient client, string xml) =>
        await client.PutAsJsonAsync(
            new Uri($"/api/admin/models/{ModelId}", UriKind.Relative),
            new
            {
                name = "治理测试",
                processKey = "governance_process",
                description = "模型治理测试",
                bpmnXml = xml,
            },
            Ct);

    private static async Task<IReadOnlyList<JsonElement>> HistoryAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/admin/models/{ModelId}/history", UriKind.Relative),
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"查历史失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);

        return [.. document.RootElement.EnumerateArray().Select(element => element.Clone())];
    }

    private static async Task<JsonElement> PublishAsync(HttpClient client)
    {
        using var response = await client.PostAsync(
            new Uri($"/api/admin/models/{ModelId}/publish", UriKind.Relative),
            content: null,
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"发布失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static void Cleanup()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();

        session.Connection.Execute(
            "delete from bpmn_model_history where model_id = @Id",
            new { Id = ModelId },
            session.Transaction);

        session.Connection.Execute(
            "delete from bpmn_model where id = @Id",
            new { Id = ModelId },
            session.Transaction);
    }
}

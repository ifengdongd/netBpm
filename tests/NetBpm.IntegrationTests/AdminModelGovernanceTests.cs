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

    /// <summary>
    /// 乐观并发：带 <c>If-Match</c> 且与服务端版本不一致时 409（并带回当前版本），
    /// 未带 <c>If-Match</c> 时保持兼容（不做校验）。保存 / PATCH / 发布共用同一口径。
    /// </summary>
    [Fact]
    public async Task Save_ShouldRejectStaleRevision_WithConflict()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Cleanup();

        using var client = _factory.CreateClient();

        // ① 首次保存 → 版本 1；详情里必须带出 revision（前端据此发 If-Match）
        using (var first = await PutModelAsync(client, V1))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct));
            Assert.Equal(1, body.RootElement.GetProperty("revision").GetInt32());
        }

        using (var detail = await client.GetAsync(
            new Uri($"/api/admin/models/{ModelId}", UriKind.Relative), Ct))
        {
            using var body = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct));
            Assert.Equal(1, body.RootElement.GetProperty("revision").GetInt32());
        }

        // ② 基于版本 1 保存 → 版本 2
        using (var second = await PutModelAsync(client, V2, ifMatch: "1"))
        {
            var body = await second.Content.ReadAsStringAsync(Ct);
            Assert.True(second.StatusCode == HttpStatusCode.OK, $"基于版本 1 的保存应成功：{second.StatusCode} {body}");

            using var document = JsonDocument.Parse(body);
            Assert.Equal(2, document.RootElement.GetProperty("revision").GetInt32());
        }

        // ③ 仍拿着过期的版本 1 提交 PATCH → 409 + 当前版本，且不产生新版本
        using (var stale = await PatchModelAsync(client, V1, ifMatch: "1"))
        {
            var body = await stale.Content.ReadAsStringAsync(Ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            using var document = JsonDocument.Parse(body);
            Assert.Equal(2, document.RootElement.GetProperty("currentRevision").GetInt32());
            Assert.Contains(
                "已被其他人修改",
                document.RootElement.GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }

        Assert.Equal(2, (await HistoryAsync(client)).Count);

        // ④ 不带 If-Match 的旧客户端照旧能保存（不做并发校验，版本继续递增）
        using (var legacy = await PutModelAsync(client, V1))
        {
            var body = await legacy.Content.ReadAsStringAsync(Ct);
            Assert.True(legacy.StatusCode == HttpStatusCode.OK, body);

            using var document = JsonDocument.Parse(body);
            Assert.Equal(3, document.RootElement.GetProperty("revision").GetInt32());
        }
    }

    /// <summary>
    /// 同一租户下一个 process_key 只能对应一个模型：第二次新建（或改 key 撞上）返回 409，
    /// 消息里指出被哪个模型占用。数据库唯一索引是兜底，接口层先给可读错误。
    /// </summary>
    [Fact]
    public async Task CreateModel_ShouldRejectDuplicateProcessKey()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Cleanup();

        using var client = _factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            new Uri("/api/admin/models", UriKind.Relative),
            new { name = "重名测试", processKey = "governance_process", bpmnXml = V1 },
            Ct);

        var firstBody = await first.Content.ReadAsStringAsync(Ct);
        Assert.True(first.StatusCode == HttpStatusCode.Created, $"首次新建应成功：{first.StatusCode} {firstBody}");

        using var document = JsonDocument.Parse(firstBody);
        var createdId = document.RootElement.GetProperty("id").GetString()!;

        try
        {
            using var second = await client.PostAsJsonAsync(
                new Uri("/api/admin/models", UriKind.Relative),
                new { name = "重名测试 2", processKey = "governance_process", bpmnXml = V1 },
                Ct);

            var secondBody = await second.Content.ReadAsStringAsync(Ct);

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Contains("已被模型", secondBody, StringComparison.Ordinal);
            Assert.Contains(createdId, secondBody, StringComparison.Ordinal);
        }
        finally
        {
            using var delete = await client.DeleteAsync(
                new Uri($"/api/admin/models/{createdId}", UriKind.Relative),
                Ct);
        }
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
        CleanModelAudit();

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

        // 两次清理（removed=1 与 removed=0）都写 model.history_prune 审计
        // （同秒内两条的展示顺序不定，按明细断言而不是按位置）
        var prunes = await AuditAsync(client, "model.history_prune");

        Assert.Equal(2, prunes.Count);
        Assert.All(prunes, item => Assert.True(item.GetProperty("success").GetBoolean()));

        var details = prunes.Select(item => item.GetProperty("detail").GetString()).ToList();

        Assert.Contains(details, detail => detail!.Contains("\"removed\":1", StringComparison.Ordinal));
        Assert.Contains(details, detail => detail!.Contains("\"removed\":0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelAuditTrail_ShouldRecordPublishDeleteAndPrune()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Cleanup();
        CleanModelAudit();

        using var client = _factory.CreateClient();

        // 保存 + 发布：model.publish 审计留痕（detail 含 definitionId）
        using (var save = await PutModelAsync(client, V1))
        {
            Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync(Ct));
        }

        _ = await PublishAsync(client);

        var publishes = await AuditAsync(client, "model.publish");

        Assert.Single(publishes);
        Assert.True(publishes[0].GetProperty("success").GetBoolean());
        Assert.Contains("governance_process", publishes[0].GetProperty("detail").GetString(), StringComparison.Ordinal);

        // 删除模型：model.delete 审计留痕，且审计独立于被删的模型
        using (var delete = await client.DeleteAsync(
            new Uri($"/api/admin/models/{ModelId}", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var deletes = await AuditAsync(client, "model.delete");

        Assert.Single(deletes);
        Assert.True(deletes[0].GetProperty("success").GetBoolean());

        // 404（删不存在的模型）不应再追加成功审计
        using (var deleteAgain = await client.DeleteAsync(
            new Uri($"/api/admin/models/{ModelId}", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
        }

        Assert.Single(await AuditAsync(client, "model.delete"));
    }

    /// <summary>清掉 model.* 审计（审计表跨测试累积，测试需要从确定状态断言计数）。</summary>
    private static void CleanModelAudit()
    {
        if (!MySqlTestDatabase.IsAvailable)
        {
            return;
        }

        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        session.Connection.Execute("delete from admin_audit_log where action like 'model.%'");
    }

    /// <summary>按动作查管理审计（审计查询本身要求 admin scope，用同一测试凭据）。</summary>
    private static async Task<IReadOnlyList<JsonElement>> AuditAsync(HttpClient client, string action)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/admin/audit?action={action}&maxResults=50", UriKind.Relative),
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"查审计失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);

        return [.. document.RootElement.GetProperty("items").EnumerateArray().Select(element => element.Clone())];
    }

    private static async Task<HttpResponseMessage> PutModelAsync(
        HttpClient client,
        string xml,
        string? ifMatch = null) =>
        await SendModelAsync(client, HttpMethod.Put, xml, ifMatch);

    /// <summary>PATCH 保存（只提交设计器会写的字段）；<paramref name="ifMatch"/> 为期望版本号。</summary>
    private static async Task<HttpResponseMessage> PatchModelAsync(
        HttpClient client,
        string xml,
        string? ifMatch = null) =>
        await SendModelAsync(client, HttpMethod.Patch, xml, ifMatch);

    private static async Task<HttpResponseMessage> SendModelAsync(
        HttpClient client,
        HttpMethod method,
        string xml,
        string? ifMatch)
    {
        using var request = new HttpRequestMessage(
            method,
            new Uri($"/api/admin/models/{ModelId}", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                name = "治理测试",
                processKey = "governance_process",
                description = "模型治理测试",
                bpmnXml = xml,
            }),
        };

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return await client.SendAsync(request, Ct);
    }

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

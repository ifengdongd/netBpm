extern alias AdminHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// <b>控制台 API × 双库</b>：同一个 Admin 宿主、同一套请求序列，在 MySQL 与 SqlServer 上各跑一遍。
/// <para>
/// 覆盖控制台真实会用到的链路：模型（新建 / 列表 / 详情 / 校验 / 局部保存 / 历史 / 发布 / 幂等发布 /
/// 发布历史 / 复制 / 删除）→ 实例与任务（启动 / 查询 / 改字段 / 候选人 / 委派解决 / 变量 / modify）→
/// 治理（作业 / 指标 / 审计 / 归档）。断言只盯「状态码 + 关键字段 + 关键不变量」，
/// 行级细粒度真值仍由各专项测试负责。
/// </para>
/// <para>方言不可用时该行动态跳过（与既有双库矩阵同策略）。</para>
/// </summary>
[Collection("MySql")]
public sealed class DualDatabaseConsoleApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEnumerable<TheoryDataRow<string>> Providers()
    {
        yield return new TheoryDataRow<string>("mysql");
        yield return new TheoryDataRow<string>("sqlserver");
    }

    /// <summary>测试流程 XML：process id 与模型的 processKey 保持一致（真实设计器就是这个口径）。</summary>
    private static string ModelXml(string processKey) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="{{processKey}}" name="控制台 API 冒烟" isExecutable="true">
            <startEvent id="start" />
            <userTask id="leader_approve" name="组长审批" activiti:candidateGroups="leaders" />
            <userTask id="manager_approve" name="经理审批" activiti:candidateGroups="managers" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="leader_approve" />
            <sequenceFlow id="f2" sourceRef="leader_approve" targetRef="manager_approve" />
            <sequenceFlow id="f3" sourceRef="manager_approve" targetRef="end" />
          </process>
        </definitions>
        """;

    private static TestDatabaseSpec? ResolveProvider(string provider) => provider switch
    {
        "mysql" => TestDatabases.MySql,
        "sqlserver" => TestDatabases.SqlServer,
        _ => throw new InvalidOperationException($"未知方言 '{provider}'。"),
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConsoleApi_ShouldWorkOnProvider(string provider)
    {
        var spec = ResolveProvider(provider);
        Assert.SkipUnless(
            spec is not null,
            TestDatabases.UnavailableReason(provider));

        TestDatabases.EnsureSchema(spec!);

        using var factory = new DualDatabaseAdminFactory(spec!);
        using var client = factory.CreateClient();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var processKey = $"console_{provider}_{suffix}";

        // ── 1. 模型：新建 / 列表 / 详情 / 校验 / 局部保存 ──
        using var create = await client.PostAsJsonAsync(
            "/api/admin/models",
            new { name = "控制台冒烟", processKey, bpmnXml = ModelXml(processKey) });

        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"新建模型失败：{create.StatusCode} {createBody}");

        using var created = JsonDocument.Parse(createBody);
        var modelId = created.RootElement.GetProperty("id").GetString()!;

        try
        {
            using var list = await client.GetAsync($"/api/admin/models?keyword={processKey}&maxResults=20");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);

            using var detail = await client.GetAsync($"/api/admin/models/{modelId}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

            using (var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
            {
                Assert.Equal(processKey, detailBody.RootElement.GetProperty("processKey").GetString());
            }

            using (var validate = await client.PostAsync($"/api/admin/models/{modelId}/validate", null))
            {
                var validateBody = await validate.Content.ReadAsStringAsync();
                Assert.True(validate.StatusCode == HttpStatusCode.OK, $"{validate.StatusCode} {validateBody}");

                using var document = JsonDocument.Parse(validateBody);
                Assert.Equal(0, document.RootElement.GetProperty("errors").GetArrayLength());
            }

            using (var patch = await client.PatchAsJsonAsync(
                $"/api/admin/models/{modelId}",
                new { name = "控制台冒烟（改名）" }))
            {
                Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
            }

            // ── 2. 历史（分页信封 + 版本详情 + 回滚）──
            using (var history = await client.GetAsync(
                $"/api/admin/models/{modelId}/history?envelope=true&maxResults=10"))
            {
                var historyBody = await history.Content.ReadAsStringAsync();
                Assert.True(history.StatusCode == HttpStatusCode.OK, $"{history.StatusCode} {historyBody}");

                using var document = JsonDocument.Parse(historyBody);
                var total = document.RootElement.GetProperty("total").GetInt32();
                Assert.True(total >= 2, $"PATCH 后应至少有 2 个版本，实际 {total}");

                var revisionId = document.RootElement.GetProperty("items")[0].GetProperty("id").GetString()!;

                using var revision = await client.GetAsync($"/api/admin/models/{modelId}/history/{revisionId}");
                Assert.Equal(HttpStatusCode.OK, revision.StatusCode);

                using var rollback = await client.PostAsync(
                    $"/api/admin/models/{modelId}/rollback/{revisionId}",
                    null);
                Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
            }

            // ── 3. 发布：首次生成定义版本，二次 skipIfUnchanged 跳过 ──
            string definitionId;

            using (var publish = await client.PostAsJsonAsync(
                $"/api/admin/models/{modelId}/publish",
                new { skipIfUnchanged = true }))
            {
                var publishBody = await publish.Content.ReadAsStringAsync();
                Assert.True(publish.StatusCode == HttpStatusCode.OK, $"{publish.StatusCode} {publishBody}");

                using var document = JsonDocument.Parse(publishBody);
                definitionId = document.RootElement.GetProperty("processDefinitionId").GetString()!;
                Assert.False(document.RootElement.GetProperty("skipped").GetBoolean());
            }

            using (var republish = await client.PostAsJsonAsync(
                $"/api/admin/models/{modelId}/publish",
                new { skipIfUnchanged = true }))
            {
                var body = await republish.Content.ReadAsStringAsync();
                Assert.True(republish.StatusCode == HttpStatusCode.OK, $"{republish.StatusCode} {body}");

                using var document = JsonDocument.Parse(body);
                Assert.True(document.RootElement.GetProperty("skipped").GetBoolean(), "内容未变化时应跳过部署");
            }

            using (var publications = await client.GetAsync($"/api/admin/models/{modelId}/publications"))
            {
                Assert.Equal(HttpStatusCode.OK, publications.StatusCode);

                using var document = JsonDocument.Parse(await publications.Content.ReadAsStringAsync());
                Assert.True(document.RootElement.GetArrayLength() >= 1, "发布历史至少应有一条");
            }

            // ── 4. 实例：启动 / 断言待办 ──
            using var start = await client.PostAsJsonAsync(
                "/api/runtime/process-instances",
                new { processDefinitionId = definitionId, businessKey = $"biz-{suffix}" });

            var startBody = await start.Content.ReadAsStringAsync();
            Assert.True(
                start.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
                $"{start.StatusCode} {startBody}");

            string instanceId;

            using (var document = JsonDocument.Parse(startBody))
            {
                instanceId = document.RootElement.GetProperty("processInstanceId").GetString()!;
            }

            try
            {
                using (var instance = await client.GetAsync($"/api/runtime/process-instances/{instanceId}"))
                {
                    Assert.Equal(HttpStatusCode.OK, instance.StatusCode);
                }

                var tasks = await ReadArrayAsync(client, $"/api/runtime/tasks?processInstanceId={instanceId}");
                var leaderTaskId = tasks[0].GetProperty("id").GetString()!;

                // ── 5. 任务字段 / 表单 / 候选人 / 委派解决 / 变量 ──
                using (var patchTask = await client.PatchAsJsonAsync(
                    $"/api/runtime/tasks/{leaderTaskId}",
                    new { priority = 60 }))
                {
                    Assert.True(
                        patchTask.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
                        $"PATCH 任务失败：{patchTask.StatusCode}");
                }

                using (var form = await client.GetAsync($"/api/runtime/tasks/{leaderTaskId}/form"))
                {
                    Assert.Equal(HttpStatusCode.OK, form.StatusCode);
                }

                using (var addCandidate = await client.PostAsJsonAsync(
                    $"/api/runtime/tasks/{leaderTaskId}/candidate-users",
                    new { userId = "kermit" }))
                {
                    Assert.Equal(HttpStatusCode.NoContent, addCandidate.StatusCode);
                }

                using (var removeCandidate = await client.DeleteAsync(
                    $"/api/runtime/tasks/{leaderTaskId}/candidate-users/kermit"))
                {
                    Assert.Equal(HttpStatusCode.NoContent, removeCandidate.StatusCode);
                }

                using (var delegateTask = await client.PostAsJsonAsync(
                    $"/api/runtime/tasks/{leaderTaskId}/delegate",
                    new { userId = "kermit" }))
                {
                    Assert.Equal(HttpStatusCode.NoContent, delegateTask.StatusCode);
                }

                using (var resolve = await client.PostAsync($"/api/runtime/tasks/{leaderTaskId}/resolve", null))
                {
                    Assert.Equal(HttpStatusCode.NoContent, resolve.StatusCode);
                }

                using (var setVariables = await client.PutAsJsonAsync(
                    $"/api/runtime/process-instances/{instanceId}/variables",
                    new { approved = true }))
                {
                    Assert.Equal(HttpStatusCode.NoContent, setVariables.StatusCode);
                }

                using (var variables = await client.GetAsync($"/api/runtime/process-instances/{instanceId}/variables"))
                {
                    var body = await variables.Content.ReadAsStringAsync();
                    Assert.True(variables.StatusCode == HttpStatusCode.OK, $"{variables.StatusCode} {body}");
                    Assert.Contains("approved", body, StringComparison.Ordinal);
                }

                // ── 6. modify：活动前启动 → 多一条分支；取消该分支 → 回到单分支 ──
                using (var startBefore = await client.PostAsJsonAsync(
                    $"/api/runtime/process-instances/{instanceId}/activities/manager_approve/start-before",
                    new { reason = "控制台冒烟" }))
                {
                    var body = await startBefore.Content.ReadAsStringAsync();
                    Assert.True(startBefore.StatusCode == HttpStatusCode.NoContent, $"{startBefore.StatusCode} {body}");
                }

                Assert.Equal(2, (await ReadArrayAsync(client, $"/api/runtime/tasks?processInstanceId={instanceId}")).Count);

                using (var cancel = await client.PostAsJsonAsync(
                    $"/api/runtime/process-instances/{instanceId}/activities/manager_approve/cancel",
                    new { reason = "控制台冒烟取消" }))
                {
                    var body = await cancel.Content.ReadAsStringAsync();
                    Assert.True(cancel.StatusCode == HttpStatusCode.NoContent, $"{cancel.StatusCode} {body}");
                }

                // 重复取消：409（活动已不存在），不是 500
                using (var cancelAgain = await client.PostAsJsonAsync(
                    $"/api/runtime/process-instances/{instanceId}/activities/manager_approve/cancel",
                    new { reason = "重复取消" }))
                {
                    Assert.Equal(HttpStatusCode.Conflict, cancelAgain.StatusCode);
                }

                // ── 7. 批量改期 + 完成任务（实例自然结束）──
                using (var batchDue = await client.PostAsJsonAsync(
                    "/api/runtime/tasks/batch/due-date",
                    new { taskIds = new[] { leaderTaskId }, clearDueDate = true }))
                {
                    var body = await batchDue.Content.ReadAsStringAsync();
                    Assert.True(batchDue.StatusCode == HttpStatusCode.OK, $"{batchDue.StatusCode} {body}");

                    using var document = JsonDocument.Parse(body);
                    Assert.Equal(1, document.RootElement.GetProperty("succeeded").GetInt32());
                }

                using (var complete = await client.PostAsJsonAsync(
                    $"/api/runtime/tasks/{leaderTaskId}/complete",
                    new { }))
                {
                    var body = await complete.Content.ReadAsStringAsync();
                    Assert.True(complete.StatusCode == HttpStatusCode.NoContent, $"{complete.StatusCode} {body}");
                }

                // 组长完成后流程正常流到经理节点（取消的只是 start-before 开出的那条分支）
                var afterLeader = await ReadArrayAsync(client, $"/api/runtime/tasks?processInstanceId={instanceId}");
                var managerTaskId = Assert.Single(afterLeader).GetProperty("id").GetString()!;

                using (var completeManager = await client.PostAsJsonAsync(
                    $"/api/runtime/tasks/{managerTaskId}/complete",
                    new { }))
                {
                    Assert.Equal(HttpStatusCode.NoContent, completeManager.StatusCode);
                }

                Assert.Equal(0, (await ReadArrayAsync(client, $"/api/runtime/tasks?processInstanceId={instanceId}")).Count);

                using (var historic = await client.GetAsync($"/api/runtime/history/process-instances/{instanceId}"))
                {
                    Assert.Equal(HttpStatusCode.OK, historic.StatusCode);
                }
            }
            finally
            {
                // 兜底清理：实例可能还在运行（用例中途失败时）
                using var delete = await client.DeleteAsync($"/api/runtime/process-instances/{instanceId}");
                Assert.True(
                    delete.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
                    $"清理实例失败：{delete.StatusCode}");
            }

            // ── 8. 治理：作业 / 指标 / 审计 / 归档（预演）──
            using (var jobs = await client.GetAsync("/api/admin/jobs?maxResults=5"))
            {
                Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
            }

            using (var jobStats = await client.GetAsync("/api/admin/jobs/stats"))
            {
                Assert.Equal(HttpStatusCode.OK, jobStats.StatusCode);
            }

            using (var metrics = await client.GetAsync("/api/admin/metrics"))
            {
                Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
            }

            using (var audit = await client.GetAsync("/api/admin/audit?maxResults=5"))
            {
                Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
            }

            using (var auditOptions = await client.GetAsync("/api/admin/audit/options"))
            {
                Assert.Equal(HttpStatusCode.OK, auditOptions.StatusCode);
            }

            using (var auditExport = await client.GetAsync("/api/admin/audit/export"))
            {
                Assert.Equal(HttpStatusCode.OK, auditExport.StatusCode);
            }

            using (var archive = await client.PostAsJsonAsync(
                "/api/admin/history/archive",
                new { before = DateTime.UtcNow.AddDays(1), dryRun = true }))
            {
                Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
            }

            // ── 9. 复制模型（自动换 key）→ 删除两个模型 ──
            using (var duplicate = await client.PostAsJsonAsync(
                $"/api/admin/models/{modelId}/duplicate",
                new { name = "控制台冒烟副本" }))
            {
                var body = await duplicate.Content.ReadAsStringAsync();
                Assert.True(duplicate.StatusCode == HttpStatusCode.Created, $"{duplicate.StatusCode} {body}");

                using var document = JsonDocument.Parse(body);
                var duplicateId = document.RootElement.GetProperty("id").GetString()!;

                Assert.NotEqual(modelId, duplicateId);

                // 副本必须换 key（同一租户 key 唯一），且 XML 的 process id 同步改写
                using var duplicateDetail = await client.GetAsync($"/api/admin/models/{duplicateId}");
                Assert.Equal(HttpStatusCode.OK, duplicateDetail.StatusCode);

                using var duplicateJson = JsonDocument.Parse(await duplicateDetail.Content.ReadAsStringAsync());
                var duplicateKey = duplicateJson.RootElement.GetProperty("processKey").GetString()!;

                Assert.EndsWith("_copy", duplicateKey, StringComparison.Ordinal);
                Assert.Contains($"id=\"{duplicateKey}\"", duplicateJson.RootElement.GetProperty("bpmnXml").GetString(), StringComparison.Ordinal);

                using var deleteDuplicate = await client.DeleteAsync($"/api/admin/models/{duplicateId}");
                Assert.Equal(HttpStatusCode.NoContent, deleteDuplicate.StatusCode);
            }
        }
        finally
        {
            using var delete = await client.DeleteAsync($"/api/admin/models/{modelId}");
            Assert.True(
                delete.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
                $"清理模型失败：{delete.StatusCode}");
        }
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadArrayAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} → {response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);

        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }
}

/// <summary>按方言规格启动真实 Admin 宿主（其余设置与 <see cref="AdminEngineFactory"/> 保持一致）。</summary>
internal sealed class DualDatabaseAdminFactory : WebApplicationFactory<AdminHost::Program>
{
    private readonly TestDatabaseSpec _spec;

    public DualDatabaseAdminFactory(TestDatabaseSpec spec) => _spec = spec;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("NetBpm:DatabaseType", _spec.Type.ToString());
        builder.UseSetting("NetBpm:ConnectionString", _spec.ConnectionString);
        builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "false");
        builder.UseSetting("NetBpm:EnableHistory", "true");
        builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
        builder.UseSetting("NetBpm:AutoMigrate", "false");
        builder.UseSetting("NetBpm:AdminTenantId", "1");
        builder.UseSetting("NetBpm:Timer:Enabled", "false");
        builder.UseSetting("NetBpm:Admin:RateLimit:Enabled", "false");

        // 与 AdminEngineFactory 同一姿态：清掉 Development 的 dev JWT 密钥，否则匿名请求恒 401
        builder.UseSetting("NetBpm:Security:Jwt:SigningKey", string.Empty);
        builder.UseSetting("NetBpm:Security:RequireCredential", "false");
    }
}

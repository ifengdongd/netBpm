extern alias AdminHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 管理治理闭环：细粒度 RBAC → 管理动作审计（转办 / 跳转 / 变量直写）→ 指标与历史归档。
/// <para>走真实 Admin 宿主 + MySQL；数据库或 0009 脚本缺失时动态跳过。</para>
/// </summary>
[Collection("MySql")]
public sealed class AdminGovernanceTests : IClassFixture<AdminEngineFactory>
{
    private const string SigningKey = "netbpm-jwt-signing-key-0123456789-abcdefghij";
    private const string Issuer = "https://idp.netbpm.test";
    private const string Audience = "netbpm-api";
    private const string Tenant = "tenant-governance";

    private readonly AdminEngineFactory _factory;

    public AdminGovernanceTests(AdminEngineFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdminModelEndpoints_ShouldRequireFineGrainedScopes()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var anonymous = CreateToken();
        var reader = CreateToken(scopes: "netbpm.admin.model.read");
        var writer = CreateToken(scopes: "netbpm.admin.model.write");
        var super = CreateToken(scopes: "netbpm.admin");

        // 无任何管理 scope：连读都不行
        using (var denied = await SendAsync(client, HttpMethod.Get, "/api/admin/models", anonymous))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // 读 scope：可以读
        using (var read = await SendAsync(client, HttpMethod.Get, "/api/admin/models", reader))
        {
            Assert.True(read.IsSuccessStatusCode, $"读模型失败：{read.StatusCode}");
        }

        // 只有读 scope：写被拒绝
        using (var deniedWrite = await PutModelAsync(client, reader))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        }

        // 写 scope：不再被 403 拦截（内容是否合法由引擎校验决定）
        using (var write = await PutModelAsync(client, writer))
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, write.StatusCode);
        }

        // 超级 scope 覆盖全部
        using (var read = await SendAsync(client, HttpMethod.Get, "/api/admin/models", super))
        {
            Assert.True(read.IsSuccessStatusCode, $"超级 scope 读模型失败：{read.StatusCode}");
        }
    }

    [Fact]
    public async Task InstanceDeleteAndSuspend_ShouldRequireDedicatedScopes()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var ops = CreateToken(userId: "ops", scopes: "netbpm.admin");
        var plain = CreateToken(userId: "applicant");
        var suspender = CreateToken(userId: "suspender", scopes: "netbpm.admin.instance.suspend");
        var deleter = CreateToken(userId: "deleter", scopes: "netbpm.admin.instance.delete");

        var instanceId = await DeployAndStartAsync(client, ops, "BIZ-RBAC-1");

        // 普通用户：挂起 / 删除都不行
        using (var deniedSuspend = await SendAsync(
            client, HttpMethod.Post, $"/api/runtime/process-instances/{instanceId}/suspend", plain))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedSuspend.StatusCode);
        }

        using (var deniedDelete = await SendAsync(
            client, HttpMethod.Delete, $"/api/runtime/process-instances/{instanceId}", plain))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedDelete.StatusCode);
        }

        // 挂起 scope：可挂起 / 激活，但删不掉
        using (var suspend = await SendAsync(
            client, HttpMethod.Post, $"/api/runtime/process-instances/{instanceId}/suspend", suspender))
        {
            Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);
        }

        using (var activate = await SendAsync(
            client, HttpMethod.Post, $"/api/runtime/process-instances/{instanceId}/activate", suspender))
        {
            Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        }

        using (var deniedDelete = await SendAsync(
            client, HttpMethod.Delete, $"/api/runtime/process-instances/{instanceId}", suspender))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedDelete.StatusCode);
        }

        // 删除 scope：单删与批量删都放行
        using (var delete = await SendAsync(
            client, HttpMethod.Delete, $"/api/runtime/process-instances/{instanceId}", deleter))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var second = await DeployAndStartAsync(client, ops, "BIZ-RBAC-2");

        using (var deniedBatch = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/runtime/process-instances/batch/delete",
            plain,
            new { processInstanceIds = new[] { second } }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedBatch.StatusCode);
        }

        using (var batch = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/runtime/process-instances/batch/delete",
            deleter,
            new { processInstanceIds = new[] { second } }))
        {
            Assert.True(batch.IsSuccessStatusCode, $"批量删除失败：{batch.StatusCode}");
        }

        // 实例生命周期动作全部留痕，且与被 403 拒绝的尝试区分开（后者未进入执行，不写审计）
        using (var audit = await SendAsync(client, HttpMethod.Get, "/api/admin/audit", ops))
        {
            var auditBody = await audit.Content.ReadAsStringAsync(Ct);
            Assert.True(audit.IsSuccessStatusCode, $"查审计失败：{audit.StatusCode} {auditBody}");

            using var document = JsonDocument.Parse(auditBody);
            var actions = document.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("action").GetString())
                .ToArray();

            Assert.Contains("instance.suspend", actions, StringComparer.Ordinal);
            Assert.Contains("instance.activate", actions, StringComparer.Ordinal);
            Assert.Contains("instance.delete", actions, StringComparer.Ordinal);
            Assert.Contains("instance.batch_delete", actions, StringComparer.Ordinal);

            // 越权被 403 拒绝的尝试没有进入执行，只应留下成功的那唯一一条删除记录
            Assert.Equal(1, actions.Count(action => action == "instance.delete"));
        }
    }

    [Fact]
    public async Task Reassign_ShouldBeRecordedInAuditLog()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var ops = CreateToken(userId: "ops", scopes: "netbpm.admin");

        var instanceId = await DeployAndStartAsync(client, ops, "BIZ-AUDIT-1");
        var taskId = await GetSingleTaskIdAsync(client, ops, instanceId);

        using (var assign = await SendJsonAsync(
            client, HttpMethod.Post, $"/api/runtime/tasks/{taskId}/assignee", ops, new { assignee = "dave" }))
        {
            Assert.True(assign.IsSuccessStatusCode, $"转办失败：{assign.StatusCode}");
        }

        using var response = await SendAsync(
            client, HttpMethod.Get, "/api/admin/audit?action=task.reassign", ops);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"查审计失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);
        Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());

        var item = document.RootElement.GetProperty("items")[0];

        Assert.Equal("task.reassign", item.GetProperty("action").GetString());
        Assert.Equal(taskId, item.GetProperty("targetId").GetString());
        Assert.Equal(instanceId, item.GetProperty("processInstanceId").GetString());
        Assert.Equal("ops", item.GetProperty("actor").GetString());
        Assert.True(item.GetProperty("success").GetBoolean());
        Assert.Contains("dave", item.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditPurge_ShouldDeleteOlderEntries_AndKeepItsOwnTrail()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var ops = CreateToken(userId: "ops", scopes: "netbpm.admin");
        var reader = CreateToken(userId: "reader", scopes: "netbpm.admin.audit.read");

        var instanceId = await DeployAndStartAsync(client, ops, "BIZ-PURGE-1");
        var taskId = await GetSingleTaskIdAsync(client, ops, instanceId);

        // 制造一条待清理的审计
        using (var assign = await SendJsonAsync(
            client, HttpMethod.Post, $"/api/runtime/tasks/{taskId}/assignee", ops, new { assignee = "dave" }))
        {
            Assert.True(assign.IsSuccessStatusCode, $"转办失败：{assign.StatusCode}");
        }

        // 只有 audit.read 的令牌不能清理（清理可抹除痕迹，需要单独 scope）
        using (var denied = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/admin/audit?before={Uri.EscapeDataString(DateTime.UtcNow.ToString("O"))}",
            reader))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // before 必填
        using (var missing = await SendAsync(client, HttpMethod.Delete, "/api/admin/audit", ops))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }

        // 清理此刻之前的记录（含刚写的转办审计）
        var cutoff = DateTime.UtcNow;

        using (var purge = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/admin/audit?before={Uri.EscapeDataString(cutoff.ToString("O"))}",
            ops))
        {
            var body = await purge.Content.ReadAsStringAsync(Ct);
            Assert.True(purge.IsSuccessStatusCode, $"清理失败：{purge.StatusCode} {body}");

            using var document = JsonDocument.Parse(body);
            Assert.True(document.RootElement.GetProperty("removed").GetInt32() >= 1);
        }

        // 旧记录已删
        using (var reassign = await SendAsync(
            client, HttpMethod.Get, "/api/admin/audit?action=task.reassign", ops))
        {
            var body = await reassign.Content.ReadAsStringAsync(Ct);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(0, document.RootElement.GetProperty("total").GetInt32());
        }

        // 清理动作自身留痕，且没有被同一次清理删掉
        using (var trail = await SendAsync(
            client, HttpMethod.Get, "/api/admin/audit?action=audit.purge", ops))
        {
            var body = await trail.Content.ReadAsStringAsync(Ct);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
        }
    }

    [Fact]
    public async Task FineGrainedScopes_ShouldNotLeakAcrossDomains()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var metricsOnly = CreateToken(userId: "metrics", scopes: "netbpm.admin.metrics.read");
        var auditOnly = CreateToken(userId: "audit", scopes: "netbpm.admin.audit.read");
        var archiveOnly = CreateToken(userId: "archive", scopes: "netbpm.admin.history.archive");
        var modelRead = CreateToken(userId: "reader", scopes: "netbpm.admin.model.read");

        // metrics.read：只有指标放行
        using (var ok = await SendAsync(client, HttpMethod.Get, "/api/admin/metrics", metricsOnly))
        {
            Assert.True(ok.IsSuccessStatusCode, $"指标应放行：{ok.StatusCode}");
        }

        using (var deniedAudit = await SendAsync(client, HttpMethod.Get, "/api/admin/audit", metricsOnly))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedAudit.StatusCode);
        }

        using (var deniedModels = await SendAsync(client, HttpMethod.Get, "/api/admin/models", metricsOnly))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedModels.StatusCode);
        }

        // audit.read：审计放行，归档不放行
        using (var ok = await SendAsync(client, HttpMethod.Get, "/api/admin/audit", auditOnly))
        {
            Assert.True(ok.IsSuccessStatusCode, $"审计应放行：{ok.StatusCode}");
        }

        using (var deniedArchive = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/admin/history/archive",
            auditOnly,
            new { before = DateTime.UtcNow }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedArchive.StatusCode);
        }

        // history.archive：归档放行，指标不放行
        using (var deniedMetrics = await SendAsync(client, HttpMethod.Get, "/api/admin/metrics", archiveOnly))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedMetrics.StatusCode);
        }

        using (var archive = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/admin/history/archive",
            archiveOnly,
            new { before = DateTime.UtcNow, dryRun = true }))
        {
            Assert.True(archive.IsSuccessStatusCode, $"归档预演应放行：{archive.StatusCode}");
        }

        // model.read：读放行，写不放行
        using (var deniedWrite = await PutModelAsync(client, modelRead))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        }
    }

    [Fact]
    public async Task TaskUpdateAndCandidates_ShouldBeAudited()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var ops = CreateToken(userId: "ops", scopes: "netbpm.admin");
        var instanceId = await DeployAndStartAsync(client, ops, "BIZ-AUDIT-3");
        var taskId = await GetSingleTaskIdAsync(client, ops, instanceId);

        using (var update = await SendJsonAsync(
            client, HttpMethod.Patch, $"/api/runtime/tasks/{taskId}", ops, new { priority = 80 }))
        {
            Assert.True(update.IsSuccessStatusCode, $"改任务失败：{update.StatusCode}");
        }

        using (var add = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/tasks/{taskId}/candidate-users",
            ops,
            new { userId = "erin" }))
        {
            Assert.True(add.IsSuccessStatusCode, $"加候选人失败：{add.StatusCode}");
        }

        using (var remove = await SendAsync(
            client, HttpMethod.Delete, $"/api/runtime/tasks/{taskId}/candidate-users/erin", ops))
        {
            Assert.True(remove.IsSuccessStatusCode, $"移除候选人失败：{remove.StatusCode}");
        }

        using var response = await SendAsync(client, HttpMethod.Get, "/api/admin/audit", ops);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"查审计失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);
        var actions = document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("action").GetString())
            .ToArray();

        Assert.Contains("task.update", actions, StringComparer.Ordinal);
        Assert.Contains("task.candidate_add", actions, StringComparer.Ordinal);
        Assert.Contains("task.candidate_remove", actions, StringComparer.Ordinal);
    }

    [Fact]
    public async Task DesignerWrites_ShouldRequireModelWriteScope()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var admin = CreateToken(userId: "ops", scopes: "netbpm.admin");
        var plain = CreateToken(userId: "designer");
        var writerOnly = CreateToken(userId: "writer", scopes: "netbpm.admin.model.write");
        var writer = CreateToken(
            userId: "designer", scopes: "netbpm.admin.model.write netbpm.admin.model.read");
        var writerAndDelete = CreateToken(
            userId: "ops",
            scopes: "netbpm.admin.model.write netbpm.admin.model.read netbpm.admin.instance.delete");

        // 无管理 scope：连定义列表都不能读
        using (var deniedList = await SendAsync(
            client, HttpMethod.Get, "/api/designer/definitions", plain))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
        }

        // 只有写 scope 也不能读（读写分离，避免「能发布就能看全部租户定义」的误解）
        using (var deniedListByWriter = await SendAsync(
            client, HttpMethod.Get, "/api/designer/definitions", writerOnly))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedListByWriter.StatusCode);
        }

        // 无管理 scope：不能部署
        using (var denied = await PostXmlAsync(client, "/api/designer/deploy", plain, SingleTaskProcess()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // 试校验只解析不落库：保持开放
        using (var validate = await PostXmlAsync(client, "/api/designer/validate", plain, SingleTaskProcess()))
        {
            Assert.True(validate.IsSuccessStatusCode, $"validate 应放行：{validate.StatusCode}");
        }

        // model.write：可部署
        string deploymentId;
        using (var deploy = await PostXmlAsync(client, "/api/designer/deploy", writer, SingleTaskProcess()))
        {
            var body = await deploy.Content.ReadAsStringAsync(Ct);
            Assert.True(deploy.IsSuccessStatusCode, $"部署失败：{deploy.StatusCode} {body}");

            deploymentId = JsonDocument.Parse(body).RootElement.GetProperty("deploymentId").GetString()!;
        }

        string definitionId;
        using (var definitions = await SendAsync(
            client, HttpMethod.Get, "/api/designer/definitions?key=governance-single", writer))
        {
            var body = await definitions.Content.ReadAsStringAsync(Ct);
            Assert.True(definitions.IsSuccessStatusCode, $"查定义失败：{definitions.StatusCode} {body}");

            definitionId = JsonDocument.Parse(body).RootElement[0]
                .GetProperty("processDefinitionId").GetString()!;
        }

        // 回显 XML 需要 model.read
        using (var deniedXml = await SendAsync(
            client, HttpMethod.Get, $"/api/designer/definition/{definitionId}/xml", plain))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedXml.StatusCode);
        }

        using (var xml = await SendAsync(
            client, HttpMethod.Get, $"/api/designer/definition/{definitionId}/xml", writer))
        {
            Assert.True(xml.IsSuccessStatusCode, $"回显应放行：{xml.StatusCode}");
        }

        // 定义级挂起 / 激活同样需要 model.write
        using (var deniedSuspend = await SendAsync(
            client, HttpMethod.Post, $"/api/designer/definitions/{definitionId}/suspend", plain))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedSuspend.StatusCode);
        }

        using (var suspend = await SendAsync(
            client, HttpMethod.Post, $"/api/designer/definitions/{definitionId}/suspend", writer))
        {
            Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);
        }

        using (var activate = await SendAsync(
            client, HttpMethod.Post, $"/api/designer/definitions/{definitionId}/activate", writer))
        {
            Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        }

        // cascade=true 会删运行实例：只有 model.write 时被拒
        using (var deniedCascade = await SendAsync(
            client, HttpMethod.Delete, $"/api/designer/deployments/{deploymentId}?cascade=true", writer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedCascade.StatusCode);
        }

        // model.write + instance.delete 才允许级联删除
        using (var cascade = await SendAsync(
            client, HttpMethod.Delete, $"/api/designer/deployments/{deploymentId}?cascade=true", writerAndDelete))
        {
            Assert.Equal(HttpStatusCode.NoContent, cascade.StatusCode);
        }

        // 非级联删除只需要 model.write
        using (var deployAgain = await PostXmlAsync(client, "/api/designer/deploy", writer, SingleTaskProcess()))
        {
            var body = await deployAgain.Content.ReadAsStringAsync(Ct);
            Assert.True(deployAgain.IsSuccessStatusCode, $"再次部署失败：{deployAgain.StatusCode} {body}");

            var deploymentId2 = JsonDocument.Parse(body).RootElement.GetProperty("deploymentId").GetString()!;

            using var delete = await SendAsync(
                client, HttpMethod.Delete, $"/api/designer/deployments/{deploymentId2}", writer);

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        // 设计器写操作全部留痕（部署 / 定义挂起 / 定义激活 / 删部署）
        using (var audit = await SendAsync(client, HttpMethod.Get, "/api/admin/audit", admin))
        {
            var auditBody = await audit.Content.ReadAsStringAsync(Ct);
            Assert.True(audit.IsSuccessStatusCode, $"查审计失败：{audit.StatusCode} {auditBody}");

            using var document = JsonDocument.Parse(auditBody);
            var actions = document.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("action").GetString())
                .ToArray();

            Assert.Contains("deployment.create", actions, StringComparer.Ordinal);
            Assert.Contains("definition.suspend", actions, StringComparer.Ordinal);
            Assert.Contains("definition.activate", actions, StringComparer.Ordinal);
            Assert.Contains("deployment.delete", actions, StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task Jump_And_VariableWrite_ShouldBeAudited()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var ops = CreateToken(userId: "ops", scopes: "netbpm.admin");

        var instanceId = await DeployAndStartAsync(client, ops, "BIZ-AUDIT-2");

        // 变量直写（流程实例变量）
        using (var write = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/runtime/process-instances/{instanceId}/variables/amount",
            ops,
            100))
        {
            Assert.True(write.IsSuccessStatusCode, $"写变量失败：{write.StatusCode}");
        }

        // 跳转到结束事件（强制办结）
        using (var jump = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/process-instances/{instanceId}/jump",
            ops,
            new { targetActivityId = "end", reason = "治理测试" }))
        {
            Assert.True(jump.IsSuccessStatusCode, $"跳转失败：{jump.StatusCode}");
        }

        using var response = await SendAsync(client, HttpMethod.Get, "/api/admin/audit", ops);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"查审计失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);
        var actions = document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("action").GetString())
            .ToArray();

        Assert.Contains("variable.write", actions, StringComparer.Ordinal);
        Assert.Contains("instance.jump", actions, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Metrics_ShouldReportCounts_AndArchiveShouldMoveHistory()
    {
        RequireGovernance();

        Reset();

        using var host = BuildHost();
        using var client = host.CreateClient();

        var ops = CreateToken(userId: "ops", scopes: "netbpm.admin");

        var instanceId = await DeployAndStartAsync(client, ops, "BIZ-ARCHIVE-1");
        var taskId = await GetSingleTaskIdAsync(client, ops, instanceId);

        // 以办理人身份完成任务，让实例结束并写入历史
        var applicant = CreateToken(userId: "applicant");
        using (var complete = await SendJsonAsync(
            client, HttpMethod.Post, $"/api/runtime/tasks/{taskId}/complete", applicant, new { }))
        {
            Assert.True(complete.IsSuccessStatusCode, $"完成任务失败：{complete.StatusCode}");
        }

        // 指标：待办归零、历史 +1
        using (var metrics = await SendAsync(client, HttpMethod.Get, "/api/admin/metrics", ops))
        {
            var metricsBody = await metrics.Content.ReadAsStringAsync(Ct);
            Assert.True(metrics.IsSuccessStatusCode, $"查指标失败：{metrics.StatusCode} {metricsBody}");

            using var document = JsonDocument.Parse(metricsBody);
            var root = document.RootElement;

            Assert.Equal(0, root.GetProperty("runningProcessInstances").GetInt32());
            Assert.Equal(1, root.GetProperty("historicProcessInstances").GetInt32());
            Assert.Equal(1, root.GetProperty("finishedProcessInstances").GetInt32());
            Assert.Equal(0, root.GetProperty("pendingTasks").GetInt32());
        }

        // 预演：不写库
        using (var dryRun = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/admin/history/archive",
            ops,
            new { before = DateTime.UtcNow.AddHours(1), dryRun = true }))
        {
            var dryBody = await dryRun.Content.ReadAsStringAsync(Ct);
            Assert.True(dryRun.IsSuccessStatusCode, $"归档预演失败：{dryRun.StatusCode} {dryBody}");

            using var document = JsonDocument.Parse(dryBody);
            Assert.True(document.RootElement.GetProperty("archivedCount").GetInt32() >= 1);
        }

        // 正式归档
        using (var archive = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/admin/history/archive",
            ops,
            new { before = DateTime.UtcNow.AddHours(1), dryRun = false }))
        {
            var archiveBody = await archive.Content.ReadAsStringAsync(Ct);
            Assert.True(archive.IsSuccessStatusCode, $"归档失败：{archive.StatusCode} {archiveBody}");

            using var document = JsonDocument.Parse(archiveBody);
            Assert.Equal(1, document.RootElement.GetProperty("archivedCount").GetInt32());
        }

        // 归档记录可查，且含完整快照
        using (var archives = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/admin/history/archives?processInstanceId={instanceId}",
            ops))
        {
            var archivesBody = await archives.Content.ReadAsStringAsync(Ct);
            Assert.True(archives.IsSuccessStatusCode, $"查归档失败：{archives.StatusCode} {archivesBody}");

            using var document = JsonDocument.Parse(archivesBody);
            Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
        }

        using (var detail = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/admin/history/archives/{instanceId}",
            ops))
        {
            var detailBody = await detail.Content.ReadAsStringAsync(Ct);
            Assert.True(detail.IsSuccessStatusCode, $"查归档详情失败：{detail.StatusCode} {detailBody}");

            using var document = JsonDocument.Parse(detailBody);
            var payload = document.RootElement.GetProperty("payload").GetString();

            Assert.Contains("processInstance", payload, StringComparison.Ordinal);
            Assert.Contains("variables", payload, StringComparison.Ordinal);
        }

        // 原历史行已删除：运行态 / 历史接口都应查不到
        using (var historic = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/runtime/history/process-instances/{instanceId}",
            ops))
        {
            Assert.Equal(HttpStatusCode.NotFound, historic.StatusCode);
        }

        // 归档动作本身也进了审计
        using (var audit = await SendAsync(
            client, HttpMethod.Get, "/api/admin/audit?action=history.archive", ops))
        {
            var auditBody = await audit.Content.ReadAsStringAsync(Ct);
            Assert.True(audit.IsSuccessStatusCode, $"查审计失败：{audit.StatusCode} {auditBody}");

            using var document = JsonDocument.Parse(auditBody);
            Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
        }
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static void RequireGovernance()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);
        Assert.SkipUnless(
            GovernanceTablesAvailable(),
            "测试库缺少 netbpm_init.sql（admin_audit_log / admin_history_archive）。");
    }

    private static bool GovernanceTablesAvailable()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();

        var tables = session.Connection.ExecuteScalar<long>(
            "select count(*) from information_schema.tables " +
            "where table_schema = database() " +
            "and table_name in ('admin_audit_log', 'admin_history_archive')");

        return tables == 2;
    }

    private static void Reset()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        MySqlTestDatabase.Reset(session);

        session.Connection.Execute("delete from admin_audit_log");
        session.Connection.Execute("delete from admin_history_archive");
    }

    private WebApplicationFactory<AdminHost::Program> BuildHost() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:ApiKey", string.Empty);
            builder.UseSetting("NetBpm:Security:Jwt:SigningKey", SigningKey);
            builder.UseSetting("NetBpm:Security:Jwt:Issuer", Issuer);
            builder.UseSetting("NetBpm:Security:Jwt:Audience", Audience);
        });

    private static string CreateToken(string? userId = "ops", string? scopes = null)
    {
        var claims = new List<Claim>();

        if (userId is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, userId));
        }

        claims.Add(new Claim("tenant", Tenant));

        if (scopes is not null)
        {
            claims.Add(new Claim("scope", scopes));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token) =>
        SendAsync(client, method, path, token, content: null);

    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        object body) =>
        SendAsync(client, method, path, token, JsonContent.Create(body));

    private static Task<HttpResponseMessage> PostXmlAsync(
        HttpClient client,
        string path,
        string token,
        string xml) =>
        SendAsync(
            client,
            HttpMethod.Post,
            path,
            token,
            new StringContent(xml, Encoding.UTF8, "text/xml"));

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;

        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> PutModelAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri("/api/admin/models/governance-rbac", UriKind.Relative));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            name = "RBAC 测试",
            processKey = "governance_rbac",
            description = "细粒度授权测试",
            bpmnXml = "<definitions xmlns=\"http://www.omg.org/spec/BPMN/20100524/MODEL\" />",
        });

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string> DeployAndStartAsync(
        HttpClient client,
        string token,
        string businessKey)
    {
        using (var deployRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/designer/deploy", UriKind.Relative)))
        {
            deployRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            deployRequest.Content = new StringContent(SingleTaskProcess(), Encoding.UTF8, "text/xml");

            using var deploy = await client.SendAsync(deployRequest, Ct);
            var body = await deploy.Content.ReadAsStringAsync(Ct);

            Assert.True(deploy.IsSuccessStatusCode, $"部署失败：{deploy.StatusCode} {body}");
        }

        using var startRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative));

        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        startRequest.Content = JsonContent.Create(new
        {
            processDefinitionKey = "governance-single",
            businessKey,
        });

        using var start = await client.SendAsync(startRequest, Ct);
        var startBody = await start.Content.ReadAsStringAsync(Ct);

        Assert.True(start.IsSuccessStatusCode, $"启动失败：{start.StatusCode} {startBody}");

        return JsonDocument.Parse(startBody).RootElement.GetProperty("processInstanceId").GetString()!;
    }

    private static async Task<string> GetSingleTaskIdAsync(
        HttpClient client,
        string token,
        string instanceId)
    {
        using var response = await SendAsync(
            client, HttpMethod.Get, $"/api/runtime/tasks?processInstanceId={instanceId}", token);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"任务查询失败：{response.StatusCode} {body}");

        var items = JsonDocument.Parse(body).RootElement;

        Assert.Equal(1, items.GetArrayLength());

        return items[0].GetProperty("id").GetString()!;
    }

    private static string SingleTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="governance-single" isExecutable="true">
            <startEvent id="start" />
            <userTask id="apply" activiti:assignee="applicant" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="end" />
          </process>
        </definitions>
        """;
}

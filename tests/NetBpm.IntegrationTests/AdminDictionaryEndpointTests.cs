using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 管理面字典写路径（<c>PUT/DELETE /admin/v1/dictionaries/{processKey}/options/{optionId}</c>）的守门用例。
/// </summary>
/// <remarks>
/// <para>
/// 这组端点的风险不是「写得慢」，而是<b>写进去一条永远不会被读到的配置</b>：
/// 字典按 optionId 配，解析时只按 BPMN 声明的选项逐项查——写一个定义里不存在的选项，
/// 管理端看到的是「配置完整」，实际行为一点没变。所以用例的重点在几条 422：
/// 未知流程 / 未知节点 / 未声明选项 / 未知目标节点，以及语义被平台保留（CLOSED）。
/// </para>
/// <para>
/// 另一条关键断言是<b>「写完立刻生效」</b>：解析结果按「租户 + 流程」缓存在
/// <c>IMemoryCache</c> 里，写路径如果忘了失效缓存，管理端的感受就是「改了没用，过一会儿又好了」。
/// 用例先解析一次（把空的缓存坐实），再写，再解析——不变就是 bug。
/// </para>
/// </remarks>
public sealed class AdminDictionaryEndpointTests
{
    private static readonly string ProcessKey = ReadEndpointHarness.ProcessKey;

    private static string[] AdminScopes =>
    [
        PlatformScopes.AdminProcess,
        PlatformScopes.AdminClient,
        PlatformScopes.ProcessRead,
    ];

    [Fact]
    public async Task Declared_ShouldListOptionsPerNode()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            $"/admin/v1/dictionaries/{ProcessKey}/declared",
            secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ProcessKey, body.GetProperty("processKey").GetString());
        Assert.Equal(1, body.GetProperty("definitionVersion").GetInt32());

        var nodes = body.GetProperty("nodes").EnumerateArray().ToList();

        // 只有带 approvalResult 的用户任务才出现（别的环节没有「审批选项」这个概念）
        Assert.Equal(2, nodes.Count);

        var apply = nodes.Single(node => node.GetProperty("activityId").GetString() == "task_apply");

        Assert.Equal(
            ["1", "2", "3"],
            apply.GetProperty("options").EnumerateArray().Select(item => item.GetProperty("optionId").GetString()));

        var approve = nodes.Single(node => node.GetProperty("activityId").GetString() == "task_approve");

        // 选项集是「按环节」而不是「按流程」：二审没有「驳回修改」
        Assert.Equal(
            ["1", "2"],
            approve.GetProperty("options").EnumerateArray().Select(item => item.GetProperty("optionId").GetString()));
    }

    [Fact]
    public async Task Declared_Should404_WhenProcessUnknown()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/admin/v1/dictionaries/no_such_process/declared",
            secret);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.ProcessNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_ShouldCreateRowAndTakeEffectImmediately()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        // 先解析一次：把「没有字典行」这个结果写进缓存（否则下面的断言证明不了缓存被失效）
        var before = await ResolveOptionAsync(host, "task_apply", "3");

        Assert.NotNull(before);
        Assert.False(before.RequiresComment);

        using var put = await PutAsync(client, secret, "3", new
        {
            nodeId = "task_apply",
            semantic = "RETURN",
            label = "退回修改",
            requiresComment = true,
            requiresTargetNode = true,
            allowedTargetNodes = new[] { "task_apply" },
            attributes = new Dictionary<string, string> { ["confirm"] = "true" },
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var stored = await ReadEndpointHarness.ReadJsonAsync(put);

        Assert.Equal("task_apply", stored.GetProperty("nodeId").GetString());
        Assert.Equal("RETURN", stored.GetProperty("semantic").GetString());

        // 统计口径由语义推导，不接受请求体指定
        Assert.Equal("NEGATIVE", stored.GetProperty("outcomeClass").GetString());
        Assert.True(stored.GetProperty("requiresTargetNode").GetBoolean());
        Assert.Equal("true", stored.GetProperty("attributes").GetProperty("confirm").GetString());

        // 🔑 写完立刻生效：解析路径不能还拿着旧缓存
        var after = await ResolveOptionAsync(host, "task_apply", "3");

        Assert.NotNull(after);
        Assert.Equal(ApprovalSemantic.Return, after.Semantic);
        Assert.True(after.RequiresComment);
        Assert.Equal("退回修改", after.Label);

        // 列表接口看到的是「库里存了什么」，不是合并后的结果
        using var list = await ReadEndpointHarness.GetAsync(
            client,
            $"/admin/v1/dictionaries?processKey={ProcessKey}",
            secret);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var rows = await ReadEndpointHarness.ReadJsonAsync(list);

        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("3", rows[0].GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task Put_ShouldPreserveCreatedAt_WhenOverwritten()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var first = await PutAsync(client, secret, "1", new
        {
            semantic = "APPROVE",
            label = "同意",
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var created = (await ReadEndpointHarness.ReadJsonAsync(first)).GetProperty("createdAt").GetDateTimeOffset();

        using var second = await PutAsync(client, secret, "1", new
        {
            semantic = "CUSTOM",
            label = "转财务复核",
        });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var updated = await ReadEndpointHarness.ReadJsonAsync(second);

        // 覆盖写不重置 created_at：否则「这行是什么时候配的」查不出来
        Assert.Equal(created, updated.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal("CUSTOM", updated.GetProperty("semantic").GetString());
        Assert.Equal("NEUTRAL", updated.GetProperty("outcomeClass").GetString());
    }

    [Fact]
    public async Task Put_Should422_WhenSemanticIsReserved()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await PutAsync(client, secret, "1", new { semantic = "CLOSED" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.DictionarySemanticReserved, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_Should422_WhenOptionIsNotDeclared()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        // 二审环节没有选项 3
        using var response = await PutAsync(client, secret, "3", new
        {
            nodeId = "task_approve",
            semantic = "RETURN",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.DictionaryOptionNotDeclared, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_Should422_WhenNodeIsUnknown()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await PutAsync(client, secret, "1", new
        {
            nodeId = "task_missing",
            semantic = "APPROVE",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.DictionaryNodeNotFound, problem.GetProperty("code").GetString());

        // 错误说明要给出可用的环节，否则管理端只能去翻 BPMN
        Assert.Contains("task_apply", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_Should422_WhenTargetNodeIsUnknown()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await PutAsync(client, secret, "3", new
        {
            semantic = "RETURN",
            requiresTargetNode = true,
            allowedTargetNodes = new[] { "task_apply", "task_nowhere" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.DictionaryTargetNodeUnknown, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_Should404_WhenProcessUnknown()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await PutAsync(client, secret, "1", new
        {
            semantic = "APPROVE",
        }, processKey: "no_such_process");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.ProcessNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_ShouldRemoveRow_And404OnSecondCall()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var put = await PutAsync(client, secret, "2", new
        {
            nodeId = "task_apply",
            semantic = "REJECT",
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var deleted = await DeleteAsync(client, secret, "2", "task_apply");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var again = await DeleteAsync(client, secret, "2", "task_apply");

        // 删不存在的行不能静默成功：那通常意味着 nodeId 少传了（删的是流程级）
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(again);

        Assert.Equal(ErrorCodes.DictionaryEntryNotFound, problem.GetProperty("code").GetString());

        // 删完之后解析回落到默认字典（选项 2 仍然是 REJECT，但不再是「字典声明」）
        using var list = await ReadEndpointHarness.GetAsync(
            client,
            $"/admin/v1/dictionaries?processKey={ProcessKey}",
            secret);

        Assert.Equal(0, (await ReadEndpointHarness.ReadJsonAsync(list)).GetArrayLength());
    }

    [Fact]
    public async Task Writes_ShouldBeAudited()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var put = await PutAsync(client, secret, "1", new
        {
            nodeId = "task_apply",
            semantic = "APPROVE",
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var audit = await ReadEndpointHarness.GetAsync(
            client,
            "/admin/v1/audit?action=dictionary.upsert",
            secret);

        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);

        var page = await ReadEndpointHarness.ReadJsonAsync(audit);

        Assert.Equal(1, page.GetProperty("total").GetInt32());

        var entry = page.GetProperty("items")[0];

        Assert.Equal("approval_dictionary", entry.GetProperty("targetType").GetString());
        Assert.Equal($"{ProcessKey}/task_apply/1", entry.GetProperty("targetId").GetString());
        Assert.True(entry.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Writes_ShouldRequireAdminProcessScope()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 只有业务读 scope
        var secret = await ReadEndpointHarness.RegisterAsync(
            client,
            "oa-portal",
            [PlatformScopes.ProcessRead]);

        using var put = await PutAsync(client, secret, "1", new { semantic = "APPROVE" });

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(put);

        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
        Assert.Contains(
            PlatformScopes.AdminProcess,
            problem.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        string secret,
        string optionId,
        object body,
        string? processKey = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(
                $"/admin/v1/dictionaries/{processKey ?? ProcessKey}/options/{optionId}",
                UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }

    private static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        string secret,
        string optionId,
        string? nodeId)
    {
        var path = $"/admin/v1/dictionaries/{ProcessKey}/options/{optionId}";

        if (nodeId is not null)
        {
            path += $"?nodeId={Uri.EscapeDataString(nodeId)}";
        }

        var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }

    /// <summary>走平台解析路径读一个选项（能反映缓存状态，不直连存储）。</summary>
    private static async Task<ApprovalOption?> ResolveOptionAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
        string activityId,
        string optionId)
    {
        await using var scope = host.Services.CreateAsyncScope();

        var vocabulary = scope.ServiceProvider.GetRequiredService<IApprovalVocabulary>();

        return await vocabulary.ResolveOptionAsync(
            ProcessKey,
            ReadEndpointHarness.DefaultTenant,
            activityId,
            optionId,
            fallbackLabel: null,
            ReadEndpointHarness.Ct);
    }
}

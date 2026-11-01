using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.Persistence.Deployment;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Platform;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 审批语义字典（三码模型）与部署期守卫：optionId → semantic / outcomeClass 的解析，
/// 以及「无映射的选项在部署时被识别出来」。
/// </summary>
/// <remarks>
/// 直接通过 DI 取 <see cref="IApprovalVocabulary"/> / <see cref="IApprovalDictionaryStore"/>，
/// 不起 HTTP——这一层要验证的是<b>两层合并口径</b>（默认字典 + 流程声明）与<b>守卫判定</b>，
/// 不是序列化或路由。
/// </remarks>
public sealed class ApprovalVocabularyTests
{
    private const string DefaultTenant = "default";
    private const string LeaveKey = "leave_process";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vocabulary_ShouldResolveKnownOptionIds_FromDefaultDictionary()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();
        var vocabulary = scope.ServiceProvider.GetRequiredService<IApprovalVocabulary>();

        var options = await vocabulary.ResolveOptionsAsync(
            LeaveKey,
            DefaultTenant,
            activityId: "task_leader_approve",
            optionIds:
            [
                ("1", "通过"),
                ("2", "拒绝"),
                ("3", "驳回修改"),
            ],
            ct: Ct);

        var byId = options.ToDictionary(option => option.OptionId, StringComparer.Ordinal);

        Assert.Equal(3, options.Count);

        Assert.Equal(ApprovalSemantic.Approve, byId["1"].Semantic);
        Assert.Equal(ApprovalOutcomeClass.Positive, byId["1"].OutcomeClass);
        Assert.Equal("通过", byId["1"].Label);

        Assert.Equal(ApprovalSemantic.Reject, byId["2"].Semantic);
        Assert.Equal(ApprovalOutcomeClass.Negative, byId["2"].OutcomeClass);

        Assert.Equal(ApprovalSemantic.Return, byId["3"].Semantic);
        Assert.Equal(ApprovalOutcomeClass.Negative, byId["3"].OutcomeClass);
    }

    /// <summary>
    /// 多副本下「别人改了字典」必须被本副本看到，而不是等 10 分钟 TTL 过期。
    /// </summary>
    /// <remarks>
    /// 写路径只能失效<b>写者自己进程</b>的 <see cref="IMemoryCache"/>；其他副本必须靠水位校验。
    /// 这条用例用两个独立的 <see cref="IMemoryCache"/> 模拟两台机器、共享一张字典表：
    /// 没有水位校验时，第二台会一直按旧语义把「驳回」当 CUSTOM（统计口径与审批结论都会错）。
    /// </remarks>
    [Fact]
    public async Task Vocabulary_ShouldObserveDictionaryWrittenByAnotherReplica()
    {
        var store = new InMemoryApprovalDictionaryStore();

        // 两台副本各有一份进程内缓存
        using var cacheReplicaA = new MemoryCache(new MemoryCacheOptions());
        using var cacheReplicaB = new MemoryCache(new MemoryCacheOptions());

        var replicaA = new ApprovalVocabulary(
            store,
            cacheReplicaA,
            NullLogger<ApprovalVocabulary>.Instance);

        var replicaB = new ApprovalVocabulary(
            store,
            cacheReplicaB,
            NullLogger<ApprovalVocabulary>.Instance);

        const string OptionId = "refund_to_finance";

        // ① A 先解析一次（把「没有自定义映射」这份结果放进 A 的缓存）
        var before = await replicaA.ResolveOptionAsync(
            LeaveKey,
            DefaultTenant,
            activityId: "task_leader_approve",
            OptionId,
            fallbackLabel: "转财务复核",
            ct: Ct);

        Assert.Equal(ApprovalSemantic.Custom, before!.Semantic);

        // ② B 写入映射（只失效 B 自己的缓存，A 的缓存保持不动）
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            new ApprovalDictionaryEntry(
                TenantId: DefaultTenant,
                ProcessKey: LeaveKey,
                NodeId: null,
                OptionId: OptionId,
                Semantic: ApprovalSemantic.Return,
                OutcomeClass: ApprovalOutcomeClass.Negative,
                Label: "退回财务",
                RequiresComment: true,
                RequiresTargetNode: false,
                AllowedTargetNodes: null,
                Attributes: null,
                CreatedAt: now,
                UpdatedAt: now),
            Ct);

        // ③ A 仍在 TTL 内，但必须看到新语义
        var after = await replicaA.ResolveOptionAsync(
            LeaveKey,
            DefaultTenant,
            activityId: "task_leader_approve",
            OptionId,
            fallbackLabel: "转财务复核",
            ct: Ct);

        Assert.Equal(ApprovalSemantic.Return, after!.Semantic);
        Assert.Equal(ApprovalOutcomeClass.Negative, after.OutcomeClass);
        Assert.True(after.RequiresComment);

        // 对照：B 自己也看到（说明不是只对 A 生效的特例）
        var fromB = await replicaB.ResolveOptionAsync(
            LeaveKey,
            DefaultTenant,
            activityId: "task_leader_approve",
            OptionId,
            fallbackLabel: "转财务复核",
            ct: Ct);

        Assert.Equal(ApprovalSemantic.Return, fromB!.Semantic);
    }

    [Fact]
    public async Task Vocabulary_ShouldFallBackToCustom_ForUnknownOption()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();
        var vocabulary = scope.ServiceProvider.GetRequiredService<IApprovalVocabulary>();

        var options = await vocabulary.ResolveOptionsAsync(
            LeaveKey,
            DefaultTenant,
            activityId: "task_leader_approve",
            optionIds: [("refund_to_finance", "转财务复核")],
            ct: Ct);

        var option = Assert.Single(options);

        // 未识别的选项一律 CUSTOM / NEUTRAL，不猜
        Assert.Equal(ApprovalSemantic.Custom, option.Semantic);
        Assert.Equal(ApprovalOutcomeClass.Neutral, option.OutcomeClass);
        Assert.Equal("转财务复核", option.Label);
    }

    [Fact]
    public async Task Vocabulary_ShouldPreferFlowDeclaration_OverDefaultDictionary()
    {
        using var host = BuildHost();
        var store = Assert.IsType<InMemoryApprovalDictionaryStore>(
            host.Services.GetRequiredService<IApprovalDictionaryStore>());

        var now = DateTimeOffset.UtcNow;

        // 流程声明把「1」覆写成 RETURN（虽然默认字典说它是 APPROVE）
        store.Upsert(new ApprovalDictionaryEntry(
            DefaultTenant,
            LeaveKey,
            NodeId: null,
            OptionId: "1",
            Semantic: ApprovalSemantic.Return,
            OutcomeClass: ApprovalOutcomeClass.Negative,
            Label: "打回",
            RequiresComment: true,
            RequiresTargetNode: true,
            AllowedTargetNodes: ["task_apply"],
            Attributes: null,
            CreatedAt: now,
            UpdatedAt: now));

        using var scope = host.Services.CreateScope();
        var vocabulary = scope.ServiceProvider.GetRequiredService<IApprovalVocabulary>();

        var option = await vocabulary.ResolveOptionAsync(
            LeaveKey,
            DefaultTenant,
            activityId: "task_leader_approve",
            "1",
            fallbackLabel: "通过",
            ct: Ct);

        Assert.NotNull(option);
        Assert.Equal(ApprovalSemantic.Return, option.Semantic);
        Assert.Equal("打回", option.Label);
        Assert.True(option.RequiresComment);
        Assert.True(option.RequiresTargetNode);
        Assert.Equal(["task_apply"], option.AllowedTargetNodes);
    }

    [Fact]
    public async Task Vocabulary_ShouldPreferNodeLevel_OverProcessLevel()
    {
        using var host = BuildHost();
        var store = Assert.IsType<InMemoryApprovalDictionaryStore>(
            host.Services.GetRequiredService<IApprovalDictionaryStore>());

        var now = DateTimeOffset.UtcNow;

        // 流程级：9 = CUSTOM
        store.Upsert(new ApprovalDictionaryEntry(
            DefaultTenant, LeaveKey, NodeId: null, "9",
            ApprovalSemantic.Custom, ApprovalOutcomeClass.Neutral,
            "转财务复核", false, false, null, null, now, now));

        // 节点级：manager 审批环节把 9 覆写成 REJECT
        store.Upsert(new ApprovalDictionaryEntry(
            DefaultTenant, LeaveKey, NodeId: "task_manager_approve", "9",
            ApprovalSemantic.Reject, ApprovalOutcomeClass.Negative,
            "经理否决", true, false, null, null, now, now));

        using var scope = host.Services.CreateScope();
        var vocabulary = scope.ServiceProvider.GetRequiredService<IApprovalVocabulary>();

        var managerOption = await vocabulary.ResolveOptionAsync(
            LeaveKey, DefaultTenant, "task_manager_approve", "9", null, Ct);

        var leaderOption = await vocabulary.ResolveOptionAsync(
            LeaveKey, DefaultTenant, "task_leader_approve", "9", null, Ct);

        Assert.NotNull(managerOption);
        Assert.Equal(ApprovalSemantic.Reject, managerOption.Semantic);
        Assert.Equal("经理否决", managerOption.Label);

        Assert.NotNull(leaderOption);
        Assert.Equal(ApprovalSemantic.Custom, leaderOption.Semantic);
        Assert.Equal("转财务复核", leaderOption.Label);
    }

    [Fact]
    public async Task Guard_ShouldPass_WhenAllOptionsMapped()
    {
        using var host = BuildHost();
        var definition = await DeploySampleAsync(host, "leave-process.bpmn20.xml");
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var warnings = await ApprovalVocabularyGuard.VerifyAsync(
            host.Services,
            engine,
            definition.Id,
            definition.Key,
            DefaultTenant,
            host.Services.GetRequiredService<ILogger<ApprovalVocabularyTests>>(),
            rejectUnmapped: false,
            ct: Ct);

        // 1/2/3 都在默认字典里，无告警
        Assert.Null(warnings);
    }

    [Fact]
    public async Task Guard_ShouldWarn_ForUnmappedOption_InLenientMode()
    {
        using var host = BuildHost();
        var definition = await DeployInlineAsync(host, "custom_process", "9");
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var warnings = await ApprovalVocabularyGuard.VerifyAsync(
            host.Services,
            engine,
            definition.Id,
            definition.Key,
            DefaultTenant,
            host.Services.GetRequiredService<ILogger<ApprovalVocabularyTests>>(),
            rejectUnmapped: false,
            ct: Ct);

        // 9 不在默认字典、也没有流程声明 → 告警（宽容模式不阻断）
        Assert.NotNull(warnings);
        var warning = Assert.Single(warnings);
        Assert.Equal(ApprovalVocabularyGuard.RuleName, warning.Rule);
        Assert.Contains("9", warning.Message, StringComparison.Ordinal);
        Assert.False(warning.IsError);
    }

    [Fact]
    public async Task Guard_ShouldReject_ForUnmappedOption_InStrictMode()
    {
        using var host = BuildHost();
        var definition = await DeployInlineAsync(host, "custom_process", "9");
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApprovalVocabularyGuard.VerifyAsync(
                host.Services,
                engine,
                definition.Id,
                definition.Key,
                DefaultTenant,
                host.Services.GetRequiredService<ILogger<ApprovalVocabularyTests>>(),
                rejectUnmapped: true,
                ct: Ct).AsTask());

        Assert.Contains("9", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_ShouldPass_WhenUnmappedOptionDeclaredInDictionary()
    {
        using var host = BuildHost();
        var definition = await DeployInlineAsync(host, "custom_process", "9");
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var store = Assert.IsType<InMemoryApprovalDictionaryStore>(
            host.Services.GetRequiredService<IApprovalDictionaryStore>());

        var now = DateTimeOffset.UtcNow;

        store.Upsert(new ApprovalDictionaryEntry(
            DefaultTenant, "custom_process", NodeId: null, "9",
            ApprovalSemantic.Reject, ApprovalOutcomeClass.Negative,
            "拒绝", false, false, null, null, now, now));

        var warnings = await ApprovalVocabularyGuard.VerifyAsync(
            host.Services,
            engine,
            definition.Id,
            definition.Key,
            DefaultTenant,
            host.Services.GetRequiredService<ILogger<ApprovalVocabularyTests>>(),
            rejectUnmapped: true,
            ct: Ct);

        Assert.Null(warnings);
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

    /// <summary>部署仓库里的样例流程。</summary>
    private static async Task<ProcessDefinition> DeploySampleAsync(
        WebApplicationFactory<Program> host,
        string fileName)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var path = Path.Combine(RepositoryRoot(), "web", "designer", "examples", fileName);

        await using var stream = File.OpenRead(path);
        var result = await engine.DeployWithValidationAsync(stream, fileName, ct: Ct);

        return result.ProcessDefinition;
    }

    /// <summary>部署一个只含一个审批环节、选项 id 可指定的极简流程。</summary>
    private static async Task<ProcessDefinition> DeployInlineAsync(
        WebApplicationFactory<Program> host,
        string processKey,
        string optionId)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{processKey}" name="Custom Process" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task" />
                <bpmn:userTask id="task" name="审批" activiti:assignee="user1">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="{optionId}" name="自定义选项" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        var result = await engine.DeployWithValidationAsync(stream, processKey + ".bpmn20.xml", ct: Ct);

        return result.ProcessDefinition;
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
}

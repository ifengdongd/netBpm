using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// M14 契约治理：<c>/workflow/v1</c> 的 OpenAPI 快照与「破坏性变更」守门。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有快照</b>：<c>/workflow/v1</c> 是对外承诺——一旦发布，已接入的系统就按它写死了
/// 反序列化与分支。改一个字段名、把一个可选字段变成必填，代码评审里看起来都是「小改动」，
/// 上线后却是对接方半夜打电话。快照把「承诺」变成一份可以 diff 的产物：
/// 契约变了就必须显式提交，评审时看到的是<b>那句人话</b>，而不是几百行 JSON 的行序抖动。
/// </para>
/// <para>
/// <b>为什么要分「破坏性 / 兼容」</b>：不是所有变更都需要停下来说服别人。新增一个端点、
/// 加一个可选字段是兼容的（§7.2：客户端必须容忍未知字段）；删路径、必填化、类型改变是破坏的。
/// 前者只需要更新快照并说明，后者要先过主版本 / 公告流程。
/// </para>
/// <para>
/// <b>怎么更新</b>：确认变更可接受后，设 <c>NETBPM_UPDATE_CONTRACTS=1</c> 重跑本类，
/// 快照会就地重写，把它和代码一起提交。⚠️ <b>不要</b>为了让流水线变绿直接刷新快照——
/// 那等于把「契约治理」变成「自动接受一切」。
/// </para>
/// </remarks>
public sealed class ContractSnapshotTests
{
    /// <summary>快照目录（仓库根下的 <c>contracts/openapi</c>）。</summary>
    private static readonly string SnapshotFile = Path.Combine(
        RepositoryLayout.Root,
        "contracts",
        "openapi",
        "workflow-v1.json");

    /// <summary>只治理业务面：<c>/api/**</c> 是引擎原语的直通面，随引擎演进且标了 experimental。</summary>
    private const string GovernedPathPrefix = "/workflow/v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 当前实现生成的契约必须与提交的快照一致（差异按「破坏性 / 兼容」逐条列出）。
    /// </summary>
    [Fact]
    public async Task Contract_ShouldMatchCommittedSnapshot()
    {
        var current = Normalize(await FetchDocumentAsync());

        if (IsUpdateRequested())
        {
            WriteSnapshot(current);

            return;
        }

        Assert.True(
            File.Exists(SnapshotFile),
            $"缺少契约快照：{SnapshotFile}。首次生成请设 NETBPM_UPDATE_CONTRACTS=1 重跑本类。");

        var committed = ParseSnapshot(File.ReadAllText(SnapshotFile));
        var changes = OpenApiContractDiff.Compare(committed, current);

        Assert.True(
            changes.Count == 0,
            BuildReport(changes));
    }

    /// <summary>
    /// 快照必须真的"守着东西"：一份空快照会让上一个用例永远绿。
    /// </summary>
    [Fact]
    public async Task Snapshot_ShouldCoverBusinessSurface()
    {
        var document = await FetchDocumentAsync();
        var paths = document["paths"] as JsonObject;

        Assert.NotNull(paths);

        var businessPaths = paths!
            .Where(pair => pair.Key.StartsWith(GovernedPathPrefix, StringComparison.Ordinal))
            .ToList();

        // 22 条路径 / 23 个操作是 v1.23 的现实；少于这个数说明过滤逻辑或端点注册被改坏了
        Assert.True(
            businessPaths.Count >= 20,
            $"业务面路径只有 {businessPaths.Count} 条，明显少于预期（约 22 条）。");

        var snapshot = ParseSnapshot(File.ReadAllText(SnapshotFile));
        var snapshotPaths = Assert.IsType<JsonObject>(snapshot["paths"]);

        foreach (var (path, _) in businessPaths)
        {
            Assert.True(snapshotPaths.ContainsKey(path), $"快照缺少业务面路径：{path}");
        }

        // 过滤不能太宽：治理面只收 /workflow/v1，引擎面（/api/**）不该混进对外契约
        Assert.DoesNotContain(snapshotPaths, pair => pair.Key.StartsWith("/api/", StringComparison.Ordinal));
    }

    // ─────────────────────────── 文档获取与归一化 ───────────────────────────

    /// <summary>起一个内存宿主取文档（OpenAPI 只依赖端点元数据，不需要数据库）。</summary>
    private static async Task<JsonObject> FetchDocumentAsync()
    {
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:RequireCredential", "false");
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
        });

        using var client = host.CreateClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync(Ct);

        return Assert.IsType<JsonObject>(JsonNode.Parse(text));
    }

    /// <summary>
    /// 归一化：只留治理面的路径，去掉每次构建都不同的字段（组件的版本号、servers），
    /// 其余键<b>递归按名排序</b>——快照是给人 review 的，键序抖动等于噪声。
    /// </summary>
    private static JsonObject Normalize(JsonObject document)
    {
        var result = new JsonObject
        {
            ["openapi"] = document["openapi"]?.DeepClone(),
            ["info"] = new JsonObject
            {
                ["title"] = "NetBpm Platform API",
                // 版本号来自程序集，每次构建都变；契约版本由 /v1 路径前缀表达
                ["version"] = "v1",
            },
        };

        var paths = document["paths"] as JsonObject;

        var governed = new JsonObject();

        foreach (var (path, value) in paths ?? [])
        {
            if (path.StartsWith(GovernedPathPrefix, StringComparison.Ordinal))
            {
                governed[path] = Sorted(value?.DeepClone());
            }
        }

        result["paths"] = Sorted(governed);

        if (document["components"] is { } components)
        {
            result["components"] = Sorted(components.DeepClone());
        }

        if (document["security"] is { } security)
        {
            result["security"] = Sorted(security.DeepClone());
        }

        return result;
    }

    /// <summary>递归按键名排序（对象），数组保持原序（顺序本身有语义，如 parameters）。</summary>
    /// <remarks>每一层都先 <c>DeepClone</c>：JsonNode 只能有一个父节点，直接搬运会抛「已有父节点」。</remarks>
    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject map => new JsonObject(
            map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value?.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(item => Sorted(item?.DeepClone()))]),
        _ => node?.DeepClone(),
    };

    private static JsonObject ParseSnapshot(string text) =>
        Assert.IsType<JsonObject>(JsonNode.Parse(text));

    private static bool IsUpdateRequested() =>
        string.Equals(Environment.GetEnvironmentVariable("NETBPM_UPDATE_CONTRACTS"), "1", StringComparison.Ordinal);

    private static void WriteSnapshot(JsonObject document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotFile)!);

        var json = document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        // 仓库规矩：CRLF、无 BOM（.editorconfig 的 end_of_line = crlf）
        var normalized = json.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        File.WriteAllText(SnapshotFile, normalized + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>把差异整理成一段能直接贴进评审的说明。</summary>
    private static string BuildReport(IReadOnlyList<ContractChange> changes)
    {
        var breaking = changes.Count(change => change.Severity == ContractChangeSeverity.Breaking);

        var report = new StringBuilder();

        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"契约快照与当前实现不一致：{changes.Count} 处变更（破坏性 {breaking} 处）。");

        if (breaking > 0)
        {
            report.AppendLine(
                "⚠️ 存在破坏性变更：按 §7.2 的兼容策略，这需要主版本升级 + 对接方公告，"
                + "不能只靠刷新快照放行。");
        }

        report.AppendLine();

        foreach (var change in changes.OrderBy(change => (int)change.Severity).ThenBy(change => change.Location, StringComparer.Ordinal))
        {
            report.AppendLine("  " + change);
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"快照位置：{SnapshotFile}");
        report.AppendLine(
            "确认变更可接受后更新快照：NETBPM_UPDATE_CONTRACTS=1 dotnet run --project tests/NetBpm.IntegrationTests "
            + "-class NetBpm.IntegrationTests.ContractSnapshotTests");

        return report.ToString();
    }
}

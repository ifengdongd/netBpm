using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 管理面 <c>/admin/v1</c> 的 OpenAPI 快照：守住「端点被静默删掉 / 改名」。
/// </summary>
/// <remarks>
/// <para>
/// <b>它守的不是对外兼容</b>。<c>contracts/README.md</c> 明确写「管理面不承诺向后兼容」——
/// 这条结论没有变。它守的是<b>同仓库里控制台与后端的一致性</b>：控制台 31 个操作全是手写 HTTP，
/// 后端删了一个端点，控制台不会编译失败，只会在某次点击时 404。快照把这种「静默漂移」
/// 变成一次需要显式提交的 diff。
/// </para>
/// <para>
/// <b>为什么单独一份快照</b>：<c>workflow-v1.json</c> 只收 <c>/workflow/v1</c>（对外承诺面）。
/// 把管理面塞进同一份文件会让人误以为它也有同样的兼容承诺；分开之后，两份文件各自回答一个问题。
/// </para>
/// <para>
/// 更新方式：确认变更可接受后设 <c>NETBPM_UPDATE_CONTRACTS=1</c> 重跑本类，把快照与代码一起提交。
/// </para>
/// </remarks>
public sealed class AdminContractSnapshotTests
{
    /// <summary>快照文件（<c>contracts/openapi/admin-v1.json</c>）。</summary>
    private static readonly string SnapshotFile = Path.Combine(
        RepositoryLayout.Root,
        "contracts",
        "openapi",
        "admin-v1.json");

    private const string GovernedPathPrefix = "/admin/v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdminContract_ShouldMatchCommittedSnapshot()
    {
        var current = Normalize(await FetchDocumentAsync());

        if (IsUpdateRequested())
        {
            WriteSnapshot(current);

            return;
        }

        Assert.True(
            File.Exists(SnapshotFile),
            $"缺少管理面契约快照：{SnapshotFile}。首次生成请设 NETBPM_UPDATE_CONTRACTS=1 重跑本类。");

        var committed = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(SnapshotFile)));
        var changes = OpenApiContractDiff.Compare(committed, current);

        Assert.True(
            changes.Count == 0,
            "管理面端点与快照不一致（这是内部一致性门禁，不是对外兼容承诺）："
            + Environment.NewLine
            + string.Join(Environment.NewLine, changes.Select(change => change.ToString()))
            + Environment.NewLine
            + "确认变更可接受后设 NETBPM_UPDATE_CONTRACTS=1 重跑本类，并同步控制台。");
    }

    [Fact]
    public async Task Snapshot_ShouldCoverAdminSurface()
    {
        var document = Normalize(await FetchDocumentAsync());
        var paths = Assert.IsType<JsonObject>(document["paths"]);

        // v1.41：22 条路径 / 31 个操作。少于 15 条说明过滤逻辑或端点注册被改坏了。
        Assert.True(
            paths.Count >= 15,
            $"管理面路径只有 {paths.Count} 条，明显少于预期（约 22 条）。");

        // 治理面必须是互斥的：管理面快照里不该出现业务面路径
        Assert.DoesNotContain(paths, pair => pair.Key.StartsWith("/workflow/", StringComparison.Ordinal));

        if (!File.Exists(SnapshotFile))
        {
            return;
        }

        var snapshot = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(SnapshotFile)));
        var snapshotPaths = Assert.IsType<JsonObject>(snapshot["paths"]);

        foreach (var (path, _) in paths)
        {
            Assert.True(snapshotPaths.ContainsKey(path), $"快照缺少管理面路径：{path}");
        }
    }

    // ─────────────────────────── 文档获取与归一化 ───────────────────────────

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

        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"/openapi/v1.json 取不到：{response.StatusCode}");

        return Assert.IsType<JsonObject>(JsonNode.Parse(text));
    }

    /// <summary>只留管理面路径，并递归按键名排序（快照是给人 review 的，键序抖动等于噪声）。</summary>
    private static JsonObject Normalize(JsonObject document)
    {
        var paths = document["paths"] as JsonObject;
        var governed = new JsonObject();

        foreach (var (path, value) in paths ?? [])
        {
            if (path.StartsWith(GovernedPathPrefix, StringComparison.Ordinal))
            {
                governed[path] = Sorted(value?.DeepClone());
            }
        }

        return new JsonObject
        {
            ["openapi"] = document["openapi"]?.DeepClone(),
            ["info"] = new JsonObject
            {
                ["title"] = "NetBpm Admin API (internal)",
                ["version"] = "v1",
            },
            ["paths"] = Sorted(governed),
        };
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject map => new JsonObject(
            map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value?.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(item => Sorted(item?.DeepClone()))]),
        _ => node?.DeepClone(),
    };

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

        // 仓库规矩：CRLF、无 BOM
        var normalized = json.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        File.WriteAllText(SnapshotFile, normalized + "\r\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

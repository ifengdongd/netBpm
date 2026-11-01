using NetBpm.SdkGen;

namespace NetBpm.UnitTests;

/// <summary>
/// M15：<c>sdk/</c> 下的产物必须等于「用当前快照重新生成一遍」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用「比字符串」而不是「比 AST」</b>：生成器是纯函数（无时钟、无随机、无字典序抖动），
/// 所以字节相等是可达的；而比 AST 的代价是写一个不用维护的解析器——一旦它跟不上语法，
/// 测试就变成「闭着眼也绿」。字节比较的失败信息也更好用：直接 `git diff`。
/// </para>
/// <para>
/// <b>为什么不校验「生成器写得对不对」</b>：那是 `tsc` 与 `javac` 的活（见 <c>sdk/README.md</c> §四），
/// 放进 CI 得同时装 Node 与 JDK，而它们坏了也不代表契约坏了。这里只守一件会随时间腐坏的事：
/// <b>产物和快照脱节</b>——有人改了端点却忘了重新生成，于是 SDK 里少了一个方法、少了一个字段。
/// </para>
/// </remarks>
public sealed class SdkGenerationTests
{
    private static string RepositoryRoot => FindRepositoryRoot();

    private static IReadOnlyList<GeneratedFile> Regenerate() =>
        SdkGenerator.Generate(RepositoryRoot);

    [Fact]
    public void Products_ShouldMatchSnapshot()
    {
        var files = Regenerate();
        var stale = new List<string>();

        foreach (var file in files)
        {
            var path = file.Resolve(RepositoryRoot);

            if (!File.Exists(path))
            {
                stale.Add($"缺少 {file.RelativePath}（新增产物没有提交）");

                continue;
            }

            // 行尾在 Windows(CRLF) 与 Linux(CI 上同样是 CRLF，但 .gitattributes/autocrlf 会再加工)
            // 之间不稳定，比较前统一成 LF：要比的是内容，不是 EOL。
            var committed = Normalize(File.ReadAllText(path));
            var expected = Normalize(file.Content);

            if (!string.Equals(committed, expected, StringComparison.Ordinal))
            {
                stale.Add($"内容不一致：{file.RelativePath}"
                    + $"（仓库 {committed.Length} 字符 / 重新生成 {expected.Length} 字符）");
            }
        }

        Assert.True(
            stale.Count == 0,
            "sdk/ 产物与 OpenAPI 快照不一致（手改了产物，或改了端点没重新生成）：\n  "
            + string.Join("\n  ", stale)
            + "\n\n重新生成：dotnet run --project tools/NetBpm.SdkGen");
    }

    /// <summary>产物不能悄悄「空心化」：23 个操作要一个不少地出现在客户端里。</summary>
    [Fact]
    public void Client_ShouldCoverEveryGovernedOperation()
    {
        var contract = ContractDocument.Load(SdkGenerator.ContractPath(RepositoryRoot));
        var client = Regenerate().Single(file => file.RelativePath == "sdk/typescript/src/client.ts").Content;

        Assert.Equal(23, contract.Operations.Count);

        var missing = contract.Operations
            .Where(operation => !client.Contains($"/** `{operation.Method} {operation.Path}` */", StringComparison.Ordinal))
            .Select(operation => $"{operation.Method} {operation.Path}")
            .ToArray();

        Assert.True(missing.Length == 0, "客户端缺少这些操作：\n  " + string.Join("\n  ", missing));

        // 方法名必须来自命名表：推导出来的名字会随规则微调而变化，等于偷偷破坏 SDK
        var unnamed = contract.Operations.Where(operation => !OperationNaming.IsExplicitlyNamed(operation)).ToArray();

        Assert.True(
            unnamed.Length == 0,
            "有操作未登记到 OperationNaming.Table（新增端点请补表，别让方法名靠推导）：\n  "
            + string.Join("\n  ", unnamed.Select(operation => $"{operation.Method} {operation.Path}")));
    }

    /// <summary>
    /// 只生成业务面可达的类型：把 118 个 schema 全塞进 SDK，等于让引擎面/管理面的每次演进
    /// 都变成 SDK 的破坏性变更。
    /// </summary>
    [Fact]
    public void Models_ShouldContainOnlyReachableSchemas()
    {
        var contract = ContractDocument.Load(SdkGenerator.ContractPath(RepositoryRoot));
        var models = Regenerate().Single(file => file.RelativePath == "sdk/typescript/src/models.ts").Content;

        Assert.Contains("export interface InstanceSummary", models, StringComparison.Ordinal);
        Assert.Contains("export type InstanceStatus =", models, StringComparison.Ordinal);

        // 这几个是 /api/** 的模型，业务面碰不到
        Assert.DoesNotContain("BatchTaskDueDateRequest", models, StringComparison.Ordinal);
        Assert.DoesNotContain("StartProcessInstanceRequest", models, StringComparison.Ordinal);

        // 闭包必须是从 22 条路径推出来的，而不是「少生成几个也行」
        Assert.True(
            contract.ReachableSchemaNames.Count is > 30 and < 60,
            $"可达 schema 数为 {contract.ReachableSchemaNames.Count}，明显偏离预期（约 49 个）——闭包算法可能坏了。");
    }

    /// <summary>
    /// 生成必须可重复：同一份快照跑两遍得到完全相同的字节（否则每次生成都是一个 diff）。
    /// </summary>
    [Fact]
    public void Generation_ShouldBeDeterministic()
    {
        var first = Regenerate();
        var second = Regenerate();

        Assert.Equal(first.Count, second.Count);

        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index].RelativePath, second[index].RelativePath);
            Assert.Equal(first[index].Content, second[index].Content, StringComparer.Ordinal);
        }
    }

    /// <summary>Java 侧同样要覆盖全部可达类型：一个 schema 一个文件（Java 的限制）。</summary>
    [Fact]
    public void JavaModels_ShouldCoverEveryReachableSchema()
    {
        var contract = ContractDocument.Load(SdkGenerator.ContractPath(RepositoryRoot));

        var javaFiles = Regenerate()
            .Where(file => file.RelativePath.EndsWith(".java", StringComparison.Ordinal))
            .ToDictionary(file => Path.GetFileNameWithoutExtension(file.RelativePath), file => file.Content, StringComparer.Ordinal);

        var missing = contract.ReachableSchemaNames.Where(name => !javaFiles.ContainsKey(name)).ToArray();

        Assert.True(missing.Length == 0, "Java 侧缺少这些类型：\n  " + string.Join("\n  ", missing));

        // record 里的组件名必须与快照的字段名一一对应（改名要靠 @JsonProperty 保住真实名）
        var view = javaFiles["InstanceSummary"];

        Assert.Contains("String instanceId", view, StringComparison.Ordinal);
        Assert.Contains("String processKey", view, StringComparison.Ordinal);
    }

    private static string Normalize(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetBpm.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"无法从 '{AppContext.BaseDirectory}' 向上找到 NetBpm.slnx。");
    }
}

using NetBpm.DbInit.Schema;

namespace NetBpm.UnitTests;

/// <summary>
/// <c>db/mysql|sqlserver/netbpm_init.sql</c> 必须等于「用当前 <c>SchemaCatalog</c> 渲染一遍」。
/// </summary>
/// <remarks>
/// <para>
/// 这两份脚本是生成物，却也是部署时真正执行的东西。改了 <c>SchemaCatalog</c> 忘记重新生成，
/// 结果不是编译错误，而是「CI 全绿、发版后新表 / 新列不存在」——
/// 而平台表的缺列表现是列表 500 或审批记录安静地变空，排查方向会被完全带偏。
/// </para>
/// <para>
/// 与 SDK 产物一样走字符串比较：生成器是纯函数（无时钟 / 无随机 / 无字典序抖动），
/// 字节相等可达；失败信息也能直接落到 <c>git diff</c>。
/// </para>
/// </remarks>
public sealed class DatabaseScriptDriftTests
{
    private static string RepositoryRoot => FindRepositoryRoot();

    [Fact]
    public void CommittedScripts_ShouldMatchGenerator()
    {
        var stale = new List<string>();

        foreach (var (relativePath, content) in SqlGenerator.Render())
        {
            var path = Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                stale.Add($"缺少 {relativePath}（脚本没有提交）");

                continue;
            }

            var committed = Normalize(File.ReadAllText(path));
            var expected = Normalize(content);

            if (!string.Equals(committed, expected, StringComparison.Ordinal))
            {
                stale.Add($"内容不一致：{relativePath}"
                    + $"（仓库 {committed.Length} 字符 / 重新生成 {expected.Length} 字符）");
            }
        }

        Assert.True(
            stale.Count == 0,
            "db/*.sql 与 SchemaCatalog 的渲染结果不一致（手改了脚本，或改了 catalog 没重新生成）：\n  "
            + string.Join("\n  ", stale)
            + "\n\n重新生成：dotnet run --project tools/NetBpm.DbInit -- --generate-only");
    }

    /// <summary>
    /// 结构版本必须是「可升级」的：脚本里要有一条把老库的 <c>schema.version</c> 抬到期望值的更新。
    /// </summary>
    /// <remarks>
    /// 只靠 <c>insert</c> 的脚本在老库上是空操作，版本会永远停在建库那天；
    /// 一旦发布时抬高 <c>ExpectedSchemaVersion</c>，所有老库启动校验直接失败。
    /// </remarks>
    [Fact]
    public void Scripts_ShouldUpgradeSchemaVersionOfExistingDatabases()
    {
        var version = NetBpm.Persistence.DatabaseSchemaChecker.ExpectedSchemaVersion;

        foreach (var (relativePath, content) in SqlGenerator.Render())
        {
            var normalized = Normalize(content);

            // MySQL 不带 N 前缀、SQL Server 带 N 前缀：只比方言无关的部分
            Assert.Contains(
                "where NAME_ = 'schema.version' and VALUE_ <> ",
                normalized,
                StringComparison.Ordinal);

            Assert.Contains(version, normalized, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(relativePath));
        }
    }

    private static string Normalize(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF');

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

        throw new InvalidOperationException("找不到仓库根目录（沿 AppContext.BaseDirectory 向上没有 NetBpm.slnx）。");
    }
}

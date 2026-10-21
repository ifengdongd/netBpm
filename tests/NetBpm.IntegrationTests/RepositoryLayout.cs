namespace NetBpm.IntegrationTests;

/// <summary>定位仓库根目录（测试运行目录在 <c>bin/Debug/net10.0</c> 之下）。</summary>
internal static class RepositoryLayout
{
    /// <summary>初始化脚本文件名：两方言各一份，内容由 <c>tools/NetBpm.DbInit/Schema</c> 生成。</summary>
    public const string DatabaseScriptFileName = "netbpm_init.sql";

    private static readonly Lazy<string> RootPath = new(FindRoot);

    public static string Root => RootPath.Value;

    public static string DatabaseScript(string dialect) =>
        Path.Combine(Root, "db", dialect, DatabaseScriptFileName);

    public static string CorpusDirectory(string name) => Path.Combine(Root, "tests", "corpus", name);

    private static string FindRoot()
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

        throw new InvalidOperationException(
            $"无法从 '{AppContext.BaseDirectory}' 向上找到 NetBpm.slnx（仓库根目录）。");
    }
}

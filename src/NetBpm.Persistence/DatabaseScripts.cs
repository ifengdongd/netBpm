using NetBpm.Engine;

namespace NetBpm.Persistence;

/// <summary>定位 <c>db/&lt;dialect&gt;</c> 下的建表与初始化脚本。</summary>
public static class DatabaseScripts
{
    /// <summary>方言对应的目录名。</summary>
    public static string DirectoryName(DatabaseType databaseType) => databaseType switch
    {
        DatabaseType.MySql => "mysql",
        DatabaseType.SqlServer => "sqlserver",
        _ => throw new NotSupportedException($"不支持的数据库方言：{databaseType}"),
    };

    /// <summary>
    /// 解析脚本目录。查找顺序：
    /// <list type="number">
    ///   <item><description>输出目录下的 <c>db/&lt;dialect&gt;</c>（发布/测试时由 csproj 拷贝）；</description></item>
    ///   <item><description>从当前目录向上找到仓库根（含 <c>NetBpm.slnx</c>）后的 <c>db/&lt;dialect&gt;</c>（开发时）。</description></item>
    /// </list>
    /// </summary>
    public static string ResolveDirectory(DatabaseType databaseType)
    {
        var relative = Path.Combine("db", DirectoryName(databaseType));

        var fromOutput = Path.Combine(AppContext.BaseDirectory, relative);
        if (Directory.Exists(fromOutput))
        {
            return fromOutput;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetBpm.slnx")))
            {
                var fromRoot = Path.Combine(directory.FullName, relative);

                if (Directory.Exists(fromRoot))
                {
                    return fromRoot;
                }

                break;
            }

            directory = directory.Parent;
        }

        throw new EngineException(
            $"找不到数据库脚本目录 '{relative}'。请从仓库根运行，或确认 csproj 已把 db/ 拷贝到输出目录。");
    }
}

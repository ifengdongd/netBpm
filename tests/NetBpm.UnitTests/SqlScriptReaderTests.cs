using NetBpm.Persistence;

namespace NetBpm.UnitTests;

/// <summary>
/// 脚本切分器：建库 / 选库引导段必须能被跳过，否则工具执行时会话会被切到脚本里的默认库。
/// </summary>
public sealed class SqlScriptReaderTests
{
    [Fact]
    public void ReadStatements_ShouldSkipBootstrapBlock_ByDefault()
    {
        var script = string.Join('\n',
            "-- header",
            SqlScriptReader.BootstrapBeginMarker,
            "create database if not exists `netbpm` character set utf8 collate utf8_bin;",
            "use `netbpm`;",
            SqlScriptReader.BootstrapEndMarker,
            "create table if not exists ACT_GE_PROPERTY (NAME_ varchar(64) not null);",
            string.Empty);

        var statements = SqlScriptReader.ReadStatements(script);

        var statement = Assert.Single(statements);
        Assert.StartsWith("create table", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("use ", statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("create database", statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadStatements_ShouldKeepBootstrapBlock_WhenNotSkipped()
    {
        var script = string.Join('\n',
            SqlScriptReader.BootstrapBeginMarker,
            "use `netbpm`;",
            SqlScriptReader.BootstrapEndMarker,
            "select 1;",
            string.Empty);

        var statements = SqlScriptReader.ReadStatements(script, skipBootstrapBlock: false);

        Assert.Equal(2, statements.Count);
        Assert.Contains("use `netbpm`", statements[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ReadStatements_ShouldSplitOnSemicolonAndGo()
    {
        var script = string.Join('\n',
            "-- 注释",
            "create table if not exists T (ID_ varchar(64) not null);",
            "go",
            "create index IX on T (ID_);",
            string.Empty);

        var statements = SqlScriptReader.ReadStatements(script);

        Assert.Equal(2, statements.Count);
        Assert.DoesNotContain(statements, statement => statement.StartsWith("--", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, statement => string.Equals(statement, "go", StringComparison.OrdinalIgnoreCase));
    }
}

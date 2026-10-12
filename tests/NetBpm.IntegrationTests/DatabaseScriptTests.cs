using System.Text.RegularExpressions;
using NetBpm.Persistence;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 数据库脚本自检：无需 Docker 即可运行，用于守住 §7.2 的「25 张 → 13 张」边界，
/// 并交叉校验脚本初值与引擎期望值一致（§7.3）。
/// </summary>
public sealed partial class DatabaseScriptTests
{
    /// <summary>§7.2 明确保留的 13 张表。</summary>
    private static readonly string[] KeptTables =
    [
        "ACT_GE_PROPERTY",
        "ACT_GE_BYTEARRAY",
        "ACT_RE_DEPLOYMENT",
        "ACT_RE_PROCDEF",
        "ACT_RU_EXECUTION",
        "ACT_RU_TASK",
        "ACT_RU_IDENTITYLINK",
        "ACT_RU_VARIABLE",
        "ACT_HI_PROCINST",
        "ACT_HI_ACTINST",
        "ACT_HI_TASKINST",
        "ACT_HI_VARINST",
        "ACT_HI_IDENTITYLINK",
    ];

    /// <summary>§7.2 明确裁剪的 12 张表。</summary>
    private static readonly string[] RemovedTables =
    [
        "ACT_RE_MODEL",
        "ACT_RU_JOB",
        "ACT_RU_TIMER_JOB",
        "ACT_RU_SUSPENDED_JOB",
        "ACT_RU_DEADLETTER_JOB",
        "ACT_RU_EVENT_SUBSCR",
        "ACT_EVT_LOG",
        "ACT_PROCDEF_INFO",
        "ACT_RU_INTEGRATION",
        "ACT_HI_DETAIL",
        "ACT_HI_COMMENT",
        "ACT_HI_ATTACHMENT",
    ];

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void SchemaScript_ShouldCreateExactlyTheKeptTables(string dialect)
    {
        var script = File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0001_create_schema.sql"));

        var created = CreateTableRegex()
            .Matches(script)
            .Select(match => match.Groups["table"].Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(KeptTables.Length, created.Count);
        Assert.All(KeptTables, table => Assert.Contains(table, created));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void SchemaScript_ShouldNotReferenceRemovedTables(string dialect)
    {
        // 只看 DDL 语句本身：脚本头部的注释会逐一列出被裁剪的表名（那是文档，不是建表）
        var script = StripComments(File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0001_create_schema.sql")));

        foreach (var removed in RemovedTables)
        {
            Assert.DoesNotContain(removed, script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void SchemaScript_ShouldAlignWithEngineExpectations(string dialect)
    {
        var schemaScript = File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0001_create_schema.sql"));
        var initScript = File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0002_init_data.sql"));

        // schema.version 必须与 DatabaseSchemaChecker 的期望值一致，否则引擎启动即失败
        Assert.Contains(
            $"'{DatabaseSchemaChecker.ExpectedSchemaVersion}'",
            initScript,
            StringComparison.Ordinal);

        // §7.3：ID 生成器的区块游标初值
        Assert.Contains($"'{DbIdGenerator.NextDbIdPropertyName}'", initScript, StringComparison.Ordinal);
        Assert.Contains("'schema.history'", initScript, StringComparison.Ordinal);

        // ROOT_PROC_INST_ID_ 的索引名在 MySQL 上游脚本里就是 ACT_IDC_EXEC_ROOT（保持原样）
        Assert.Contains("ROOT_PROC_INST_ID_", schemaScript, StringComparison.Ordinal);
    }

    [Fact]
    public void KeptAndRemovedTables_ShouldCoverTheUpstreamTwentyFive()
    {
        // §7.2：25 张表 = 保留 13 + 裁剪 12
        Assert.Equal(25, KeptTables.Length + RemovedTables.Length);
    }

    [GeneratedRegex(@"create\s+table\s+(?<table>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableRegex();

    private static string StripComments(string script) =>
        string.Join(
            '\n',
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
}

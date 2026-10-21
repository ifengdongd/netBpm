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

    /// <summary>§7.2 明确裁剪的 10 张表（ACT_RU_TIMER_JOB / ACT_RU_JOB 已补回，见 <see cref="AddedTables"/>）。</summary>
    private static readonly string[] RemovedTables =
    [
        "ACT_RE_MODEL",
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

    /// <summary>引擎扩展表（不属于 Activiti 13 张基础表）：定时作业 / 异步作业 / outbox。</summary>
    private static readonly string[] AddedTables =
    [
        "ACT_RU_TIMER_JOB",
        "ACT_RU_JOB",
        "ACT_EVT_OUTBOX",
    ];

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void SchemaScript_ShouldCreateExactlyTheKeptTables(string dialect)
    {
        var script = File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0001_create_engine_schema.sql"));

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
        var script = StripComments(File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0001_create_engine_schema.sql")));

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
        // 初值与建表已合并到同一脚本（原 0001 + 0002）
        var schemaScript = File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0001_create_engine_schema.sql"));

        // schema.version 必须与 DatabaseSchemaChecker 的期望值一致，否则引擎启动即失败
        Assert.Contains(
            $"'{DatabaseSchemaChecker.ExpectedSchemaVersion}'",
            schemaScript,
            StringComparison.Ordinal);

        // §7.3：ID 生成器的区块游标初值
        Assert.Contains($"'{DbIdGenerator.NextDbIdPropertyName}'", schemaScript, StringComparison.Ordinal);
        Assert.Contains("'schema.history'", schemaScript, StringComparison.Ordinal);

        // ROOT_PROC_INST_ID_ 的索引名在 MySQL 上游脚本里就是 ACT_IDC_EXEC_ROOT（保持原样）
        Assert.Contains("ROOT_PROC_INST_ID_", schemaScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void TimerJobScript_ShouldCreateExactlyTheAddedTables(string dialect)
    {
        var script = File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0006_create_timer_job.sql"));

        var created = CreateTableRegex()
            .Matches(script)
            .Select(match => match.Groups["table"].Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        // 0006 只负责定时作业表；异步作业与 outbox 在 0012
        var table = Assert.Single(created);
        Assert.Equal("ACT_RU_TIMER_JOB", table);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void UpgradeScript_ShouldBeIncrementalAndIdempotent(string dialect)
    {
        var script = StripComments(File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0011_engine_upgrade.sql")));

        // 执行日志表：增量迁移的唯一依据
        Assert.Contains("ACT_GE_SCHEMA_LOG", script, StringComparison.OrdinalIgnoreCase);

        // 定时作业可靠性列
        Assert.Contains("RETRIES_", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEAD_LETTER_", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXCEPTION_MSG_", script, StringComparison.OrdinalIgnoreCase);

        // 幂等：MySQL 用 information_schema 判重，SQL Server 用 sys.columns 判重
        var guard = dialect == "mysql" ? "information_schema.columns" : "sys.columns";
        Assert.Contains(guard, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlScript_ShouldUseMediumTextForBpmnModel()
    {
        var script = File.ReadAllText(RepositoryLayout.DatabaseScript("mysql", "0002_create_business_tables.sql"));

        // text 只有 64KB，大模型 + SVG 会被截断/写入失败；LongText 必须映射到 mediumtext
        Assert.Contains("bpmn_xml mediumtext", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("svg mediumtext", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bpmn_xml text", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void AsyncJobOutboxScript_ShouldCreateJobAndOutboxTables(string dialect)
    {
        var script = StripComments(
            File.ReadAllText(RepositoryLayout.DatabaseScript(dialect, "0012_async_jobs_outbox.sql")));

        var created = CreateTableRegex()
            .Matches(script)
            .Select(match => match.Groups["table"].Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ACT_RU_JOB", created);
        Assert.Contains("ACT_EVT_OUTBOX", created);

        // 幂等：MySQL 用 create table if not exists，SQL Server 用 object_id 判重
        var guard = dialect == "mysql" ? "if not exists" : "object_id";
        Assert.Contains(guard, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeptRemovedAndAddedTables_ShouldCoverTheDesignedSet()
    {
        // §7.2：保留 13 + 裁剪 10 + 扩展 3（ACT_RU_TIMER_JOB / ACT_RU_JOB / ACT_EVT_OUTBOX）
        Assert.Equal(13, KeptTables.Length);
        Assert.Equal(10, RemovedTables.Length);
        Assert.Equal(3, AddedTables.Length);
    }

    [GeneratedRegex(@"create\s+table\s+(?:if\s+not\s+exists\s+)?(?<table>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableRegex();

    private static string StripComments(string script) =>
        string.Join(
            '\n',
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
}

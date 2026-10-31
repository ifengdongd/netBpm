using System.Text.RegularExpressions;
using NetBpm.Persistence;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 初始化脚本自检：无需数据库即可运行。
/// <para>
/// 守住三件事：① §7.2 的「25 张 → 13 张」边界与扩展表集合；
/// ② 脚本初值与引擎期望值一致（§7.3）；③ 单文件脚本必须**全幂等**（否则生产环境无法重复执行）。
/// </para>
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

    /// <summary>业务 / Demo / 应用层扩展表（合并脚本里也必须都建出来）。</summary>
    private static readonly string[] ApplicationTables =
    [
        "instances",
        "instance_current_task",
        "deal_history",
        "workflow_type",
        "bpmn_model",
        "leave_bill",
        "order_bill",
        "workflow_parameters",
        "sys_dept",
        "sys_emp",
        "sys_emp_group",
        "sys_emp_group_member",
        "ACT_IDEMPOTENCY",
        "ACT_HI_MIGRATION",
        "bpmn_model_history",
        "admin_audit_log",
        "admin_history_archive",
        "admin_user",
        // 平台接入表（docs/platform-api-refactoring-plan.md §7.1）：nb_ 前缀与 ACT_* 严格分开
        "nb_tenant",
        "nb_api_client",
        "nb_client_process_grant",
        "nb_process_catalog",
        "nb_approval_dictionary",
        "nb_instance_timeline",
        "nb_external_task",
    ];

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void InitScript_ShouldCreateAllExpectedTables(string dialect)
    {
        var created = CreatedTables(File.ReadAllText(RepositoryLayout.DatabaseScript(dialect)));

        Assert.All(KeptTables, table => Assert.Contains(table, created));
        Assert.All(AddedTables, table => Assert.Contains(table, created));
        Assert.All(ApplicationTables, table => Assert.Contains(table, created));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void InitScript_ShouldNotReferenceRemovedTables(string dialect)
    {
        // 只看 DDL 语句本身：脚本头部的注释会逐一列出被裁剪的表名（那是文档，不是建表）
        var script = StripComments(File.ReadAllText(RepositoryLayout.DatabaseScript(dialect)));

        foreach (var removed in RemovedTables)
        {
            Assert.DoesNotContain(removed, script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void InitScript_ShouldAlignWithEngineExpectations(string dialect)
    {
        var script = File.ReadAllText(RepositoryLayout.DatabaseScript(dialect));

        // schema.version 必须与 DatabaseSchemaChecker 的期望值一致，否则引擎启动即失败
        Assert.Contains($"'{DatabaseSchemaChecker.ExpectedSchemaVersion}'", script, StringComparison.Ordinal);

        // §7.3：ID 生成器的区块游标初值
        Assert.Contains($"'{DbIdGenerator.NextDbIdPropertyName}'", script, StringComparison.Ordinal);
        Assert.Contains("'schema.history'", script, StringComparison.Ordinal);

        // ROOT_PROC_INST_ID_ 的索引名在 MySQL 上游脚本里就是 ACT_IDC_EXEC_ROOT（保持原样）
        Assert.Contains("ROOT_PROC_INST_ID_", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void InitScript_ShouldGuardEveryCreateTable(string dialect)
    {
        var script = StripComments(File.ReadAllText(RepositoryLayout.DatabaseScript(dialect)));

        var creates = CreateTableRegex().Matches(script);
        Assert.NotEmpty(creates);

        if (dialect == "mysql")
        {
            // MySQL：每条建表都必须带 if not exists
            Assert.DoesNotMatch(UnguardedCreateTableRegex(), script);
        }
        else
        {
            // SQL Server：每条 create table 前面都必须是 if object_id(...) is null 守卫
            Assert.Equal(creates.Count, GuardedCreateTableRegex().Matches(script).Count);
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void InitScript_ShouldBeRepeatable(string dialect)
    {
        var script = File.ReadAllText(RepositoryLayout.DatabaseScript(dialect));

        // 建库段必须存在，且生成器与 SqlScriptReader 用同一对标记（工具执行时跳过该段）
        var begin = script.IndexOf(SqlScriptReader.BootstrapBeginMarker, StringComparison.Ordinal);
        var end = script.IndexOf(SqlScriptReader.BootstrapEndMarker, StringComparison.Ordinal);

        Assert.True(begin >= 0, "缺少建库引导段开始标记");
        Assert.True(end > begin, "建库引导段结束标记缺失或顺序错误");
        Assert.Contains("create database", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("use ", script, StringComparison.OrdinalIgnoreCase);

        // 幂等守卫：MySQL 靠 information_schema / insert ignore；SQL Server 靠 sys.* 判重
        var guard = dialect == "mysql" ? "information_schema" : "sys.";
        Assert.Contains(guard, script, StringComparison.OrdinalIgnoreCase);

        if (dialect == "mysql")
        {
            Assert.Contains("insert ignore into ACT_GE_PROPERTY", script, StringComparison.OrdinalIgnoreCase);
        }

        // 老库补齐：模型大文本扩容与定时作业可靠性列
        Assert.Contains("ACT_RU_TIMER_JOB", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RETRIES_", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlScript_ShouldUseMediumTextForBpmnModel()
    {
        var script = File.ReadAllText(RepositoryLayout.DatabaseScript("mysql"));

        // text 只有 64KB，大模型 + SVG 会被截断/写入失败；LongText 必须映射到 mediumtext
        Assert.Contains("bpmn_xml mediumtext", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("svg mediumtext", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bpmn_xml text", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public void InitScript_ShouldNotContainLegacySchemaLogTable(string dialect)
    {
        // 脚本级增量机制（ACT_GE_SCHEMA_LOG + 校验和）已移除：单文件全幂等取代它。
        // 老库残留的 ACT_GE_SCHEMA_LOG 表不再被读写，可手工 drop。
        var script = StripComments(File.ReadAllText(RepositoryLayout.DatabaseScript(dialect)));

        Assert.DoesNotContain("ACT_GE_SCHEMA_LOG", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeptRemovedAndAddedTables_ShouldCoverTheDesignedSet()
    {
        // §7.2：保留 13 + 裁剪 10 + 扩展 3（ACT_RU_TIMER_JOB / ACT_RU_JOB / ACT_EVT_OUTBOX）
        Assert.Equal(13, KeptTables.Length);
        Assert.Equal(10, RemovedTables.Length);
        Assert.Equal(3, AddedTables.Length);
    }

    private static HashSet<string> CreatedTables(string script) =>
        CreateTableRegex()
            .Matches(StripComments(script))
            .Select(match => match.Groups["table"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"create\s+table\s+(?:if\s+not\s+exists\s+)?(?<table>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableRegex();

    /// <summary>MySQL：没有 <c>if not exists</c> 的建表语句（应为 0 条）。</summary>
    [GeneratedRegex(@"create\s+table\s+(?!if\s+not\s+exists)", RegexOptions.IgnoreCase)]
    private static partial Regex UnguardedCreateTableRegex();

    /// <summary>SQL Server：<c>if object_id(N'X', N'U') is null</c> 守卫 + 建表。</summary>
    [GeneratedRegex(@"if\s+object_id\(N?'\w+',\s*N?'U'\)\s+is\s+null\s+create\s+table", RegexOptions.IgnoreCase)]
    private static partial Regex GuardedCreateTableRegex();

    private static string StripComments(string script) =>
        string.Join(
            '\n',
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
}

using System.Collections.Generic;
using System.IO;
using System.Text;
using NetBpm.Persistence;

namespace NetBpm.DbInit.Schema;

/// <summary>
/// 把 <see cref="SchemaCatalog"/> 渲染成两方言的<b>单文件初始化脚本</b>：
/// <c>db/mysql/netbpm_init.sql</c> / <c>db/sqlserver/netbpm_init.sql</c>。
/// <para>
/// 脚本 = 建库 + 建表 + 索引 / 外键 + 初值数据，<b>全部幂等</b>：
/// 新库一次建成，老库重复执行只补缺失部分（空操作不报错）。
/// 因此不再有「编号脚本 + 脚本执行日志」的增量机制。
/// </para>
/// <para>
/// 库名由脚本顶部的建库段决定（默认 <see cref="DefaultDatabaseName"/>）；
/// 工具执行时会跳过该段，改由连接串指定库（见 <see cref="SqlScriptReader"/>）。
/// </para>
/// </summary>
internal static class SqlGenerator
{
    /// <summary>单文件初始化脚本的文件名（两方言同名，分别落 db/mysql 与 db/sqlserver）。</summary>
    internal const string InitScriptFileName = "netbpm_init.sql";

    /// <summary>手工执行时的默认库名（测试 / 生产用不同库名时改脚本顶部这一处，或用工具按连接串建库）。</summary>
    internal const string DefaultDatabaseName = "netbpm";

    private enum Dialect
    {
        MySql,
        SqlServer,
    }

    public static void Generate(string repoRoot)
    {
        GenerateFor(repoRoot, Dialect.MySql, "mysql");
        GenerateFor(repoRoot, Dialect.SqlServer, "sqlserver");
    }

    private static void GenerateFor(string repoRoot, Dialect dialect, string folder)
    {
        var directory = Path.Combine(repoRoot, "db", folder);
        Directory.CreateDirectory(directory);

        // 单脚本模型：编号脚本已全部合并。目录里残留的旧脚本会被 DatabaseMigrator 一并执行，
        // 因此这里主动清理（db/<dialect> 目录是生成物，不该手写文件）。
        foreach (var stale in Directory.EnumerateFiles(directory, "*.sql")
                     .Where(path => !string.Equals(
                         Path.GetFileName(path),
                         InitScriptFileName,
                         StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(stale);
        }

        Emit(directory, dialect, InitScriptFileName, RenderInitScript(dialect));
    }

    private static void Emit(string directory, Dialect dialect, string fileName, List<string> lines)
    {
        var content = new StringBuilder();
        foreach (var line in lines)
        {
            content.Append(line).Append("\r\n");
        }

        File.WriteAllText(Path.Combine(directory, fileName), content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  整份初始化脚本
    // ────────────────────────────────────────────────────────────────────────

    private static List<string> RenderInitScript(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(HeaderInit);
        lines.AddRange(RenderBootstrap(dialect));

        // ① 引擎基础表
        lines.AddRange(BlankSection("① 引擎基础表（13 张 ACT_*，与 Activiti 逐字一致）"));
        lines.AddRange(EngineSchemaNotes);
        foreach (var table in SchemaCatalog.EngineTables)
        {
            lines.AddRange(RenderCreateTable(table, dialect, idempotent: true));
        }

        lines.AddRange(BlankSection("①-1 引擎表索引"));
        foreach (var index in SchemaCatalog.EngineIndexes)
        {
            lines.AddRange(RenderEngineIndex(index, dialect, idempotent: true));
            lines.Add(string.Empty);
        }

        lines.AddRange(BlankSection("①-2 引擎表唯一约束与外键"));
        foreach (var table in SchemaCatalog.EngineTables)
        {
            foreach (var unique in table.UniqueKeys)
            {
                if (unique.Name.Length > 0)
                {
                    lines.AddRange(RenderGuardedUnique(table.Name, unique, dialect));
                }
            }
        }

        foreach (var fk in SchemaCatalog.EngineForeignKeys)
        {
            lines.AddRange(RenderGuardedForeignKey(fk, dialect));
        }

        lines.AddRange(BlankSection("①-3 引擎初值（ACT_GE_PROPERTY：仅在缺失时插入）"));
        lines.AddRange(RenderPropertySeeds(dialect));

        // ② 流程实例业务表 / 流程类型 / 流程模型
        lines.AddRange(BlankSection("② 流程实例业务表 / 流程类型 / 流程模型"));
        lines.AddRange(BusinessNotes);
        foreach (var table in SchemaCatalog.InstanceTables)
        {
            lines.AddRange(RenderTableSection(table, dialect));
        }

        lines.AddRange(RenderTableSection(SchemaCatalog.WorkflowTypeTable, dialect));
        lines.AddRange(RenderSeed(SchemaCatalog.WorkflowTypeSeeds, dialect, idempotent: true));
        lines.AddRange(RenderTableSection(SchemaCatalog.BpmnModelTable, dialect));

        // ③ Demo 业务表 + 组织架构
        lines.AddRange(BlankSection("③ Demo 业务表与组织架构（含演示种子数据）"));
        lines.AddRange(DemoNotes);
        foreach (var table in new[]
        {
            SchemaCatalog.LeaveBillTable,
            SchemaCatalog.OrderBillTable,
            SchemaCatalog.WorkflowParametersTable,
        })
        {
            lines.AddRange(RenderTableSection(table, dialect));
        }

        foreach (var table in SchemaCatalog.OrgTables)
        {
            lines.AddRange(RenderTableSection(table, dialect));
        }

        lines.AddRange(BlankSection("③-1 组织架构种子数据"));
        foreach (var seed in SchemaCatalog.OrgSeeds)
        {
            lines.AddRange(RenderSeed(seed, dialect, idempotent: true));
        }

        // ④ 运行期查询索引
        lines.AddRange(BlankSection("④ 运行期查询索引（性能增强，不影响行内容）"));
        lines.AddRange(PerfIndexesNotes);
        foreach (var index in SchemaCatalog.PerfIndexes)
        {
            lines.AddRange(RenderEngineIndex(index, dialect, idempotent: true));
            lines.Add(string.Empty);
        }

        // ⑤ 应用层扩展表
        lines.AddRange(BlankSection("⑤ 应用层扩展表（幂等键 / 定时作业 / 迁移审计 / 模型历史 / 治理 / 异步作业 / 平台接入）"));
        lines.AddRange(ExtensionsNotes);
        foreach (var table in ExtensionTables)
        {
            lines.AddRange(RenderTableSection(table, dialect));
        }

        // ⑥ 老库兼容补齐
        lines.AddRange(BlankSection("⑥ 老库兼容补齐（幂等；新库执行为空操作）"));
        lines.AddRange(RenderTransitionalUpgrade(dialect));

        // 注释必须排在补列之后：老库缺列时该列还不存在，先加注释会报 15135
        if (dialect == Dialect.SqlServer)
        {
            lines.AddRange(BlankSection("⑦ 表与列注释（扩展属性 MS_Description，已存在则跳过）"));
            lines.AddRange(RenderTableComments(AllTables));
        }

        lines.AddRange(Footer(dialect));
        return lines;
    }

    /// <summary>应用层扩展表（都不属于 Activiti 的 13 张引擎表）。</summary>
    private static readonly IReadOnlyList<TableDef> ExtensionTables =
    [
        SchemaCatalog.IdempotencyTable,
        SchemaCatalog.TimerJobTable,
        SchemaCatalog.MigrationTable,
        SchemaCatalog.ModelHistoryTable,
        SchemaCatalog.AdminAuditTable,
        SchemaCatalog.AdminUserTable,
        SchemaCatalog.HistoryArchiveTable,
        SchemaCatalog.AsyncJobTable,
        SchemaCatalog.EventOutboxTable,
        SchemaCatalog.CommandIdempotencyTable,
        SchemaCatalog.TenantTable,
        SchemaCatalog.ApiClientTable,
        SchemaCatalog.ClientProcessGrantTable,
        SchemaCatalog.ProcessCatalogTable,
        SchemaCatalog.ApprovalDictionaryTable,
        SchemaCatalog.InstanceTimelineTable,
        SchemaCatalog.ApprovalSnapshotTable,
    ];

    /// <summary>脚本内出现过的全部表（SQL Server 注释渲染用）。</summary>
    private static IReadOnlyList<TableDef> AllTables =>
    [
        .. SchemaCatalog.EngineTables,
        .. SchemaCatalog.InstanceTables,
        SchemaCatalog.WorkflowTypeTable,
        SchemaCatalog.BpmnModelTable,
        SchemaCatalog.LeaveBillTable,
        SchemaCatalog.OrderBillTable,
        SchemaCatalog.WorkflowParametersTable,
        .. SchemaCatalog.OrgTables,
        .. ExtensionTables,
    ];

    // ────────────────────────────────────────────────────────────────────────
    //  建库 / 老库补齐
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 建库 + 选库。手工执行（mysql 客户端 / SSMS）时生效；
    /// 工具执行时整段被 <see cref="SqlScriptReader"/> 跳过（库名取自连接串，必要时用 --create-database 建库）。
    /// </summary>
    private static List<string> RenderBootstrap(Dialect dialect)
    {
        var lines = new List<string>
        {
            string.Empty,
            "-- ─────────────────── ⓪ 建库 / 选库（仅手工执行时生效）───────────────────",
            "--",
            $"--  库名默认 {DefaultDatabaseName}：测试环境 / 生产环境用不同库名时，改下面两行即可。",
            "--  工具执行（NetBpm.DbInit / DbMigrate / 宿主 AutoMigrate）会跳过本段：",
            "--  库名取自连接串，首次建库用 DbInit / DbMigrate 的 --create-database。",
            string.Empty,
            SqlScriptReader.BootstrapBeginMarker,
        };

        if (dialect == Dialect.MySql)
        {
            // 与工具建库语句保持同一字符集/排序规则（引擎表 utf8_bin）
            lines.Add($"create database if not exists `{DefaultDatabaseName}` character set utf8 collate utf8_bin;");
            lines.Add($"use `{DefaultDatabaseName}`;");
        }
        else
        {
            lines.Add($"if db_id(N'{DefaultDatabaseName}') is null create database [{DefaultDatabaseName}];");
            lines.Add("go");
            lines.Add($"use [{DefaultDatabaseName}];");
            lines.Add("go");
        }

        lines.Add(SqlScriptReader.BootstrapEndMarker);
        return lines;
    }

    /// <summary>老库补齐：新库执行是空操作，老库才真正扩列 / 扩文本 / 回填。</summary>
    private static List<string> RenderTransitionalUpgrade(Dialect dialect)
    {
        var lines = new List<string>
        {
            "--  这一节只为「用旧版脚本建过库」的环境准备：",
            "--    · 模型大文本列仍是 text（64KB）→ 扩容到 mediumtext；",
            "--    · 定时作业表缺重试 / 死信列 → 补列；",
            "--    · 变量表缺租户列 → 补列并按流程实例回填。",
            "--  新库在上面已经把表建成最终形态，这里全部是空操作。",
            string.Empty,
        };

        if (dialect == Dialect.MySql)
        {
            lines.AddRange(RenderMySqlEnsureMediumText("bpmn_model", "bpmn_xml", notNull: true));
            lines.AddRange(RenderMySqlEnsureMediumText("bpmn_model", "svg", notNull: false));
            lines.AddRange(RenderMySqlEnsureMediumText("bpmn_model_history", "bpmn_xml", notNull: true));
            lines.AddRange(RenderMySqlEnsureMediumText("bpmn_model_history", "svg", notNull: false));
        }
        else
        {
            lines.Add("-- bpmn_xml / svg 在 SQL Server 已是 nvarchar(max)，无需扩容。");
            lines.Add(string.Empty);
        }

        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "RETRIES_", "int default 3", "int default 3"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "DEAD_LETTER_", "tinyint default 0", "tinyint default 0"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "EXCEPTION_MSG_", "varchar(1000)", "nvarchar(1000)"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "LAST_FAILURE_", "datetime(3)", "datetime"));

        // 0011：变量表补租户列（新库上面已建成最终形态，这里只服务老库）
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_VARIABLE", "TENANT_ID_", "varchar(255) default ''", "nvarchar(255) default ''"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_HI_VARINST", "TENANT_ID_", "varchar(255) default ''", "nvarchar(255) default ''"));
        lines.AddRange(RenderBackfillVariableTenant(dialect));

        // 0013：租户过滤去掉 coalesce 之后的兼容回填（老库里可能仍有 NULL 行）
        lines.AddRange(RenderNormalizeTenantNulls(dialect));

        // 0012：作业表补租约列（新库上面已建成最终形态，这里只服务老库）
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_JOB", "LOCK_OWNER_", "varchar(64)", "nvarchar(64)"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_JOB", "LOCK_EXP_TIME_", "datetime(3)", "datetime"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "LOCK_OWNER_", "varchar(64)", "nvarchar(64)"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "LOCK_EXP_TIME_", "datetime(3)", "datetime"));

        // 索引必须排在补列之后：老库缺列时先建索引会报 Key column ... doesn't exist
        lines.AddRange(RenderEngineIndex(
            new TableIndexDef("ACT_RU_JOB", "ACT_IDX_JOB_LOCK_EXP", ["LOCK_EXP_TIME_"]),
            dialect,
            idempotent: true));
        lines.Add(string.Empty);

        lines.AddRange(RenderEngineIndex(
            new TableIndexDef("ACT_RU_TIMER_JOB", "ACT_IDX_TIMER_JOB_LOCK_EXP", ["LOCK_EXP_TIME_"]),
            dialect,
            idempotent: true));
        lines.Add(string.Empty);

        return lines;
    }

    /// <summary>
    /// 0011 回填：把「流程实例的租户」刷进变量行。
    /// <para>
    /// 为什么必须回填而不是只补空列：老库里已有大量变量行，若只补列不回填，
    /// 这些历史行仍带着 <c>NULL</c>，按 <c>TENANT_ID_ = 'x'</c> 过滤时会被整体漏掉，
    /// 表现为「升级后变量凭空消失」。
    /// </para>
    /// <para>
    /// 幂等性由 <c>where TENANT_ID_ is null</c> 保证：新库建表时列带 <c>default ''</c>，
    /// 没有 NULL 行，因此整句是空操作；重复执行也只会处理尚未回填的行。
    /// </para>
    /// <para>
    /// 回填源优先取所属执行（<c>EXECUTION_ID_</c>），回退到流程实例
    /// （<c>PROC_INST_ID_</c>）：任务局部变量的 <c>EXECUTION_ID_</c> 与流程实例一致，
    /// 两条路径结果相同，但在并发分支下执行行更贴近真实作用域。
    /// </para>
    /// </summary>
    private static List<string> RenderBackfillVariableTenant(Dialect dialect)
    {
        var lines = new List<string> { "-- 0011 变量租户回填：把执行 / 流程实例上的租户刷进已有的变量行" };

        if (dialect == Dialect.MySql)
        {
            lines.AddRange(
            [
                """
                update ACT_RU_VARIABLE v
                    left join ACT_RU_EXECUTION e on e.ID_ = v.EXECUTION_ID_
                    left join ACT_RU_EXECUTION p on p.ID_ = v.PROC_INST_ID_
                set v.TENANT_ID_ = coalesce(e.TENANT_ID_, p.TENANT_ID_, '')
                where v.TENANT_ID_ is null;
                """,
                """
                update ACT_HI_VARINST v
                    left join ACT_HI_PROCINST p on p.PROC_INST_ID_ = v.PROC_INST_ID_
                set v.TENANT_ID_ = coalesce(p.TENANT_ID_, '')
                where v.TENANT_ID_ is null;
                """,
            ]);
        }
        else
        {
            lines.AddRange(
            [
                """
                update v
                set v.TENANT_ID_ = coalesce(e.TENANT_ID_, p.TENANT_ID_, N'')
                from ACT_RU_VARIABLE v
                    left join ACT_RU_EXECUTION e on e.ID_ = v.EXECUTION_ID_
                    left join ACT_RU_EXECUTION p on p.ID_ = v.PROC_INST_ID_
                where v.TENANT_ID_ is null;
                """,
                """
                update v
                set v.TENANT_ID_ = coalesce(p.TENANT_ID_, N'')
                from ACT_HI_VARINST v
                    left join ACT_HI_PROCINST p on p.PROC_INST_ID_ = v.PROC_INST_ID_
                where v.TENANT_ID_ is null;
                """,
            ]);
        }

        // 索引必须排在补列之后：老库缺列时先建索引会报 Key column ... doesn't exist
        foreach (var index in SchemaCatalog.VariableTenantIndexes)
        {
            lines.AddRange(RenderEngineIndex(index, dialect, idempotent: true));
            lines.Add(string.Empty);
        }

        lines.Add(string.Empty);
        return lines;
    }

    /// <summary>
    /// 0013 回填：把历史遗留的 <c>NULL</c> 租户归一到 <c>''</c>。
    /// <para>
    /// 查询侧已把 <c>coalesce(TENANT_ID_, '') = @t</c> 改成 <c>TENANT_ID_ = @t</c>
    /// （函数包裹会让 <c>TENANT_ID_</c> 上的索引完全失效）。但等值比较下 <c>NULL</c> 不匹配任何租户，
    /// 老库里的 NULL 行会在升级后「凭空消失」—— 所以去函数的前提就是先把 NULL 归一。
    /// </para>
    /// <para>
    /// 幂等性由 <c>where TENANT_ID_ is null</c> 保证：新库列带 <c>default ''</c>，整句是空操作。
    /// </para>
    /// </summary>
    private static List<string> RenderNormalizeTenantNulls(Dialect dialect)
    {
        var empty = dialect == Dialect.MySql ? "''" : "N''";

        var lines = new List<string>
        {
            "-- 0013 租户 NULL 归一：租户过滤改为等值比较前的兼容回填（可重复执行）",
        };

        foreach (var table in new[] { "ACT_RU_EXECUTION", "ACT_HI_PROCINST", "ACT_HI_TASKINST" })
        {
            lines.Add($"update {table} set TENANT_ID_ = {empty} where TENANT_ID_ is null;");
        }

        lines.Add(string.Empty);

        return lines;
    }

    /// <summary>MySQL：仅当列还不是 mediumtext / longtext 时才改类型（避免每次执行都重建表）。</summary>
    private static List<string> RenderMySqlEnsureMediumText(string table, string column, bool notNull)
    {
        var alter = notNull
            ? $"alter table {table} modify column {column} mediumtext not null"
            : $"alter table {table} modify column {column} mediumtext";

        return
        [
            $"set @ddl = ifnull((select if(data_type in ('mediumtext', 'longtext'), 'select 1', '{alter}')",
            "    from information_schema.columns",
            $"    where table_schema = database() and table_name = '{table}' and column_name = '{column}'), 'select 1');",
            "prepare stmt from @ddl;",
            "execute stmt;",
            "deallocate prepare stmt;",
            string.Empty,
        ];
    }

    /// <summary>幂等加列：MySQL 用 information_schema 判重后动态执行，SQL Server 用 sys.columns 判重。</summary>
    private static List<string> RenderAddColumn(
        Dialect dialect,
        string table,
        string column,
        string mySqlDefinition,
        string sqlServerDefinition)
    {
        if (dialect == Dialect.MySql)
        {
            return RenderMySqlGuardedAlter(
                $"alter table {table} add column {column} {mySqlDefinition}",
                $"from information_schema.columns where table_schema = database() and table_name = '{table}' and column_name = '{column}'");
        }

        return
        [
            $"if not exists (select 1 from sys.columns where object_id = object_id('{table}') and name = '{column}')",
            $"    alter table {table} add {column} {sqlServerDefinition};",
            "go",
            string.Empty,
        ];
    }

    // ────────────────────────────────────────────────────────────────────────
    //  表 / 索引 / 约束
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>一张表：建表 + 索引（MySQL 内联后又幂等补齐，SQL Server 独立渲染）。</summary>
    private static List<string> RenderTableSection(TableDef table, Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(RenderCreateTable(table, dialect, idempotent: true));
        lines.AddRange(RenderTableScopedIndexes(table, dialect, idempotent: true));

        if (dialect == Dialect.MySql)
        {
            // 已有库（表已存在）不会因 create table if not exists 补到内联索引，这里按索引名幂等补齐
            foreach (var unique in table.UniqueKeys)
            {
                if (unique.Name.Length > 0 && !table.EngineCharset)
                {
                    lines.AddRange(RenderMySqlIdempotentIndex(
                        new TableIndexDef(table.Name, unique.Name, unique.Columns),
                        unique: true));
                }
            }

            foreach (var index in table.Indexes)
            {
                lines.AddRange(RenderMySqlIdempotentIndex(new TableIndexDef(table.Name, index.Name, index.Columns)));
            }
        }

        return lines;
    }

    private static List<string> RenderCreateTable(TableDef table, Dialect dialect, bool idempotent)
    {
        var body = new List<string>();
        foreach (var column in table.Columns)
        {
            body.Add($"    {RenderColumn(column, table, dialect)}");
        }

        if (dialect == Dialect.MySql)
        {
            body.Add($"    primary key ({string.Join(", ", table.PrimaryKey)})");

            foreach (var unique in table.UniqueKeys)
            {
                // 引擎表具名唯一键（ACT_UNIQ_PROCDEF）走独立 alter table，不内联；
                // 业务表具名唯一键内联为 unique key。
                if (unique.Name.Length == 0)
                {
                    body.Add($"    unique ({string.Join(", ", unique.Columns)})");
                }
                else if (!table.EngineCharset)
                {
                    body.Add($"    unique key {unique.Name} ({string.Join(", ", unique.Columns)})");
                }
            }

            foreach (var index in table.Indexes)
            {
                body.Add($"    index {index.Name} ({string.Join(", ", index.Columns)})");
            }

            var charset = table.EngineCharset ? "utf8" : "utf8mb4";
            var collate = table.EngineCharset ? "utf8_bin" : "utf8mb4_bin";
            var comment = table.Comment.Length > 0 ? $" COMMENT='{table.Comment}'" : string.Empty;

            var lines = new List<string>
            {
                $"create table{(idempotent ? " if not exists" : "")} {table.Name} (",
            };
            lines.AddRange(JoinWithComma(body));
            lines.Add($") ENGINE=InnoDB DEFAULT CHARSET={charset} COLLATE {collate}{comment};");
            lines.Add(string.Empty);
            return lines;
        }

        foreach (var unique in table.UniqueKeys)
        {
            if (unique.Name.Length == 0)
            {
                body.Add($"    unique ({string.Join(", ", unique.Columns)})");
            }
        }

        body.Add(table.EngineCharset
            ? $"    primary key ({string.Join(", ", table.PrimaryKey)})"
            : $"    constraint pk_{table.Name} primary key clustered ({string.Join(", ", table.PrimaryKey)})");

        var sqlServerLines = new List<string>();
        if (idempotent)
        {
            sqlServerLines.Add($"if object_id(N'{table.Name}', N'U') is null");
            sqlServerLines.Add($"    create table {table.Name} (");
        }
        else
        {
            sqlServerLines.Add($"create table {table.Name} (");
        }

        sqlServerLines.AddRange(JoinWithComma(body));
        sqlServerLines.Add(idempotent ? "    );" : ");");

        if (idempotent)
        {
            sqlServerLines.Add("go");
        }

        sqlServerLines.Add(string.Empty);
        return sqlServerLines;
    }

    private static List<string> JoinWithComma(List<string> body)
    {
        var result = new List<string>(body.Count);
        for (var i = 0; i < body.Count; i++)
        {
            result.Add(i == body.Count - 1 ? body[i] : body[i] + ",");
        }

        return result;
    }

    private static string RenderColumn(Column column, TableDef table, Dialect dialect)
    {
        var builder = new StringBuilder($"{Quote(column.Name, dialect)} {TypeName(column, dialect)}");

        if (!column.Nullable)
        {
            builder.Append(" not null");
        }

        var defaultClause = DefaultClause(column, table, dialect);
        if (defaultClause.Length > 0)
        {
            builder.Append(' ').Append(defaultClause);
        }

        if (dialect == Dialect.MySql && column.Comment is not null)
        {
            builder.Append(" comment '").Append(column.Comment).Append('\'');
        }

        return builder.ToString();
    }

    private static string TypeName(Column column, Dialect dialect) => column.Type switch
    {
        ColType.Varchar => dialect == Dialect.MySql ? $"varchar({column.Length})" : $"nvarchar({column.Length})",
        ColType.BigInt => "bigint",
        ColType.Int => "int",
        ColType.SmallInt => "smallint",
        ColType.TinyInt => "tinyint",
        ColType.Double => dialect == Dialect.MySql ? "double" : "double precision",
        ColType.Long => dialect == Dialect.MySql ? "bigint" : "numeric(19,0)",
        ColType.Money => "decimal(18,2)",
        ColType.DateTime => "datetime",
        ColType.DateTime3 => dialect == Dialect.MySql ? "datetime(3)" : "datetime",
        ColType.Timestamp => dialect == Dialect.MySql ? "timestamp(3)" : "datetime",
        ColType.DateTime2 => dialect == Dialect.MySql ? "datetime(3)" : "datetime2(3)",
        ColType.Blob => dialect == Dialect.MySql ? "LONGBLOB" : "varbinary(max)",
        // 模型 XML / SVG 远超 text 的 64KB 上限；LongText 语义上应取 MySQL 的大文本类型
        ColType.LongText => dialect == Dialect.MySql ? "mediumtext" : "nvarchar(max)",
        ColType.MediumText => dialect == Dialect.MySql ? "mediumtext" : "nvarchar(max)",
        _ => throw new NotSupportedException($"未知列类型：{column.Type}"),
    };

    private static string DefaultClause(Column column, TableDef table, Dialect dialect)
    {
        if (column.Default is null)
        {
            return string.Empty;
        }

        if (dialect == Dialect.MySql)
        {
            return $"default {column.Default}";
        }

        // SQL Server：引擎表沿用 Activiti 的无名内联 default；业务表用命名约束
        return table.EngineCharset
            ? $"default {column.Default}"
            : $"constraint df_{table.Name}_{column.Name} default ({column.Default})";
    }

    private static string Quote(string name, Dialect dialect) => name == "describe"
        ? dialect == Dialect.MySql ? "`describe`" : "[describe]"
        : name;

    /// <summary>业务表的唯一键 / 索引：MySQL 已内联进 create table，SQL Server 渲染为独立索引。</summary>
    private static List<string> RenderTableScopedIndexes(TableDef table, Dialect dialect, bool idempotent)
    {
        if (dialect == Dialect.MySql)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var unique in table.UniqueKeys)
        {
            if (unique.Name.Length > 0)
            {
                lines.AddRange(RenderSqlServerIndex(table.Name, unique.Name, unique.Columns, unique: true, idempotent));
            }
        }

        foreach (var index in table.Indexes)
        {
            lines.AddRange(RenderSqlServerIndex(table.Name, index.Name, index.Columns, unique: false, idempotent));
        }

        return lines;
    }

    /// <summary>引擎表 / 性能索引的独立索引，索引名即显式给出所属表。</summary>
    private static List<string> RenderEngineIndex(TableIndexDef index, Dialect dialect, bool idempotent)
    {
        if (dialect == Dialect.MySql && idempotent)
        {
            return RenderMySqlIdempotentIndex(index);
        }

        if (dialect == Dialect.SqlServer && idempotent)
        {
            return RenderSqlServerIndex(index.Table, index.Name, index.Columns, unique: false, idempotent: true);
        }

        return [$"create index {index.Name} on {index.Table}({string.Join(", ", index.Columns)});"];
    }

    private static List<string> RenderMySqlIdempotentIndex(TableIndexDef index, bool unique = false)
    {
        var create = $"create {(unique ? "unique " : string.Empty)}index {index.Name} on {index.Table} ({string.Join(", ", index.Columns)})";

        return RenderMySqlGuardedAlter(
            create,
            $"from information_schema.statistics where table_schema = database() and table_name = '{index.Table}' and index_name = '{index.Name}'");
    }

    /// <summary>
    /// MySQL 的幂等 DDL 外壳：查 <paramref name="guard"/> 得 0 行才动态执行 <paramref name="statement"/>。
    /// 依赖连接串的 <c>Allow User Variables=true</c>（<c>@ddl</c> 是用户变量）。
    /// </summary>
    private static List<string> RenderMySqlGuardedAlter(string statement, string guard)
    {
        // 语句要作为字符串字面量塞进 @ddl，其中的单引号必须翻倍 ——
        // 否则带 default '' 的补列语句会被截断，MySQL 报 "near ''' at line 1"。
        var literal = statement.Replace("'", "''", StringComparison.Ordinal);

        return
        [
            "set @ddl = (select if(count(*) = 0,",
            $"        '{literal}',",
            "        'select 1')",
            $"    {guard});",
            "prepare stmt from @ddl;",
            "execute stmt;",
            "deallocate prepare stmt;",
            string.Empty,
        ];
    }

    private static List<string> RenderSqlServerIndex(string table, string name, IReadOnlyList<string> columns, bool unique, bool idempotent)
    {
        var statement = $"create {(unique ? "unique " : string.Empty)}index {name} on {table} ({string.Join(", ", columns)});";

        if (!idempotent)
        {
            return [statement];
        }

        return
        [
            $"if not exists (select 1 from sys.indexes where name = '{name}' and object_id = object_id('{table}'))",
            statement,
            "go",
        ];
    }

    /// <summary>幂等唯一约束（引擎表的 ACT_UNIQ_PROCDEF）。</summary>
    private static List<string> RenderGuardedUnique(string table, IndexDef unique, Dialect dialect)
    {
        var columns = string.Join(", ", unique.Columns);

        if (dialect == Dialect.MySql)
        {
            return RenderMySqlGuardedAlter(
                $"alter table {table} add constraint {unique.Name} unique ({columns})",
                $"from information_schema.table_constraints where constraint_schema = database() and table_name = '{table}' and constraint_name = '{unique.Name}'");
        }

        return
        [
            $"if not exists (select 1 from sys.objects where name = '{unique.Name}' and parent_object_id = object_id('{table}'))",
            $"    alter table {table}",
            $"        add constraint {unique.Name}",
            $"        unique ({columns});",
            "go",
            string.Empty,
        ];
    }

    /// <summary>幂等外键。</summary>
    private static List<string> RenderGuardedForeignKey(ForeignKeyDef fk, Dialect dialect)
    {
        var cascade = string.Empty;
        if (dialect == Dialect.MySql)
        {
            if (fk.CascadeDelete)
            {
                cascade = " on delete cascade";
            }

            if (fk.CascadeUpdate)
            {
                cascade += " on update cascade";
            }
        }

        var statement =
            $"alter table {fk.Table} add constraint {fk.Name} foreign key ({fk.Column}) references {fk.ReferencedTable} ({fk.ReferencedColumn}){cascade}";

        if (dialect == Dialect.MySql)
        {
            return RenderMySqlGuardedAlter(
                statement,
                $"from information_schema.table_constraints where constraint_schema = database() and table_name = '{fk.Table}' and constraint_name = '{fk.Name}'");
        }

        return
        [
            $"if not exists (select 1 from sys.foreign_keys where name = '{fk.Name}' and parent_object_id = object_id('{fk.Table}'))",
            $"    alter table {fk.Table}",
            $"        add constraint {fk.Name}",
            $"        foreign key ({fk.Column})",
            $"        references {fk.ReferencedTable} ({fk.ReferencedColumn});",
            "go",
            string.Empty,
        ];
    }

    // ────────────────────────────────────────────────────────────────────────
    //  初值 / 注释
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 引擎初值：<b>只在缺失时插入</b>。绝不能用 upsert —— 否则重复执行会把
    /// <c>next.dbid</c> 重置回 1，导致 id 重用。
    /// </summary>
    private static List<string> RenderPropertySeeds(Dialect dialect)
    {
        const string columns = "(NAME_, VALUE_, REV_)";
        var lines = new List<string>();

        foreach (var row in SchemaCatalog.PropertySeeds)
        {
            var name = (string)row[0]!;
            var values = string.Join(", ", RowValues(row, dialect));

            if (dialect == Dialect.MySql)
            {
                lines.Add($"insert ignore into ACT_GE_PROPERTY {columns} values ({values});");
            }
            else
            {
                lines.Add($"if not exists (select 1 from ACT_GE_PROPERTY where NAME_ = '{name}')");
                lines.Add($"    insert into ACT_GE_PROPERTY {columns} values ({values});");
                lines.Add("go");
            }
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static List<string> RenderSeed(SeedTable seed, Dialect dialect, bool idempotent)
    {
        var lines = new List<string>();
        var columns = string.Join(", ", seed.Columns.Select(name => Quote(name, dialect)));

        if (dialect == Dialect.MySql)
        {
            var values = string.Join(",\n       ", seed.Rows.Select(row => $"({string.Join(", ", RowValues(row, dialect))})"));

            if (idempotent && seed.UpsertUpdateColumns.Count == 0)
            {
                // 没有「可更新字段」的种子（如组织架构成员关系）只需保证存在
                lines.Add($"insert ignore into {seed.TableName} ({columns})");
            }
            else
            {
                lines.Add($"insert into {seed.TableName} ({columns})");
            }

            lines.Add($"values {values}");

            if (idempotent && seed.UpsertUpdateColumns.Count > 0)
            {
                // 列名必须按方言转义：workflow_type 的 `describe` 在 MySQL 里是保留字
                var updateClauses = string.Join(", ", seed.UpsertUpdateColumns.Select(column =>
                    $"{Quote(column, dialect)} = values({Quote(column, dialect)})"));
                lines[^1] += $" on duplicate key update {updateClauses}";
            }

            lines[^1] += ";";
        }
        else
        {
            var columnIndex = seed.Columns
                .Select((name, index) => (name, index))
                .ToDictionary(pair => pair.name, pair => pair.index);

            foreach (var row in seed.Rows)
            {
                if (idempotent)
                {
                    var guard = string.Join(" and ", seed.GuardColumns.Select(column =>
                        $"{column} = {Literal(row[columnIndex[column]], dialect)}"));
                    lines.Add($"if not exists (select 1 from {seed.TableName} where {guard})");
                    lines.Add($"    insert into {seed.TableName} ({columns})");
                    lines.Add($"    values ({string.Join(", ", RowValues(row, dialect))});");
                    lines.Add("go");
                    continue;
                }

                lines.Add($"insert into {seed.TableName} ({columns})");
                lines.Add($"values ({string.Join(", ", RowValues(row, dialect))});");
            }
        }

        lines.Add(string.Empty);
        return lines;
    }

    /// <summary>
    /// SQL Server 表 / 列注释。两个前置条件都必须守住：
    /// ① <c>sp_addextendedproperty</c> 对已存在的属性直接报错 → 判重；
    /// ② 对象不存在时（老库缺列，要等 ⑥ 段补列） → 判存在，否则报 15135。
    /// </summary>
    private static List<string> RenderTableComments(IReadOnlyList<TableDef> tables)
    {
        var lines = new List<string>();

        foreach (var table in tables)
        {
            if (table.Comment.Length > 0)
            {
                lines.Add($"if object_id('{table.Name}', 'U') is not null");
                lines.Add("    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'");
                lines.Add($"        and major_id = object_id('{table.Name}') and minor_id = 0)");
                lines.Add($"    exec sp_addextendedproperty @name = N'MS_Description', @value = N'{table.Comment}',");
                lines.Add("        @level0type = N'SCHEMA', @level0name = N'dbo',");
                lines.Add($"        @level1type = N'TABLE', @level1name = N'{table.Name}';");
                lines.Add("go");
                lines.Add(string.Empty);
            }

            foreach (var column in table.Columns)
            {
                if (column.Comment is null)
                {
                    continue;
                }

                // 列可能要等 ⑥ 段补列才存在（老库 / 被误删），对象不存在时加属性会报 15135
                lines.Add($"if exists (select 1 from sys.columns");
                lines.Add($"    where object_id = object_id('{table.Name}') and name = N'{column.Name}')");
                lines.Add("    and not exists (select 1 from sys.extended_properties ep");
                lines.Add("        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id");
                lines.Add($"        where ep.name = N'MS_Description' and ep.major_id = object_id('{table.Name}') and c.name = N'{column.Name}')");
                lines.Add($"    exec sp_addextendedproperty @name = N'MS_Description', @value = N'{column.Comment}',");
                lines.Add("        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'" + table.Name + "',");
                lines.Add($"        @level2type = N'COLUMN', @level2name = N'{column.Name}';");
                lines.Add("go");
            }

            if (table.Columns.Any(column => column.Comment is not null))
            {
                lines.Add(string.Empty);
            }
        }

        return lines;
    }

    private static List<string> RowValues(IReadOnlyList<object?> row, Dialect dialect) =>
        row.Select(value => Literal(value, dialect)).ToList();

    private static string Literal(object? value, Dialect dialect)
    {
        if (value is null)
        {
            return "null";
        }

        if (ReferenceEquals(value, SchemaCatalog.Now))
        {
            return dialect == Dialect.MySql ? "utc_timestamp()" : "getutcdate()";
        }

        if (value is string text)
        {
            var prefix = dialect == Dialect.SqlServer && !IsAscii(text) ? "N" : string.Empty;
            return $"{prefix}'{text}'";
        }

        return value.ToString() ?? "null";
    }

    private static bool IsAscii(string text) => text.All(ch => ch <= 0x7F);

    // ────────────────────────────────────────────────────────────────────────
    //  头部注释
    // ────────────────────────────────────────────────────────────────────────

    private static List<string> Banner(Dialect dialect)
    {
        var dialectName = dialect == Dialect.MySql ? "MySQL" : "SQL Server";
        return
        [
            "-- ============================================================================",
            $"--  NetBpm · {dialectName} 数据库初始化脚本（自动生成，请勿手改）",
            "-- ============================================================================",
            "--  本文件由单一数据源生成，两份方言（mysql / sqlserver）结构严格一一对应，",
            "--  差异仅为方言语法（类型名 / 注释写法 / 批分隔 / 幂等守卫）。",
            "--    · 单一数据源：tools/NetBpm.DbInit/Schema/SchemaCatalog.cs",
            "--    · 方言渲染器：tools/NetBpm.DbInit/Schema/SqlGenerator.cs",
            "--  重新生成：dotnet run --project tools/NetBpm.DbInit -- --generate-only",
            "--  对齐决策清单：见 db/README.md「对齐决策清单」。",
            "-- ============================================================================",
            string.Empty,
        ];
    }

    private static string[] HeaderInit =>
    [
        "--  用途：测试环境 / 生产环境通用的数据库初始化脚本 —— 建库 + 建表 + 索引外键 + 初值数据，",
        "--        **可重复执行**（每一段都带方言幂等守卫）。新库一次建成；老库重复执行只补缺失部分。",
        "--",
        "--  内容：",
        "--    ⓪ 建库 / 选库（仅手工执行时生效）",
        "--    ① 引擎基础表 13 张 ACT_*（Activiti 子集）+ 索引 / 唯一约束 / 外键 + ACT_GE_PROPERTY 初值",
        "--    ② 流程实例业务表（instances / instance_current_task / deal_history）+ 流程类型 + 流程模型",
        "--    ③ Demo 业务表（leave_bill / order_bill / workflow_parameters）+ 组织架构 + 演示种子数据",
        "--    ④ 运行期查询索引（性能增强）",
        "--    ⑤ 应用层扩展表：幂等键 / 定时作业 / 迁移审计 / 模型历史 / 管理审计与归档 / 异步作业 / outbox / 平台接入（nb_*）",
        "--    ⑥ 老库兼容补齐（大文本扩容、定时作业可靠性列）",
        "--    ⑦ 表与列注释（SQL Server 扩展属性；MySQL 注释内联在建表语句里）",
        "--",
        "--  执行方式：",
        "--    · 工具（推荐，一步建库 + 建表 + 校验结构版本）：",
        "--        dotnet run --project tools/NetBpm.DbInit -- \\",
        "--          --provider mysql --connection \"Server=127.0.0.1;Port=3306;Database=netbpm;\\",
        "--             User ID=root;Password=;Allow User Variables=true;\" --create-database",
        "--    · 手工：MySQL  mysql -u<user> -p < db/mysql/netbpm_init.sql",
        "--             SQL Server  用 sqlcmd / SSMS 连接 master 执行 db/sqlserver/netbpm_init.sql",
        "--    · 宿主启动：NetBpm:AutoMigrate=true 时执行本脚本（库需已存在，或先跑一次上面的工具命令）",
        "--",
        "--  ⚠️ 初值只在缺失时插入：重复执行不会覆盖已有值（尤其不会重置 next.dbid 造成 id 重用）。",
        "--  ⚠️ MySQL 连接串需要 Allow User Variables=true（索引 / 约束的幂等守卫用 @ddl 动态执行）。",
        "--  ⚠️ 表名、列名、索引名、外键名与 Activiti 逐字一致，请勿改名或「顺手优化」列类型。",
        "-- ============================================================================",
        string.Empty,
    ];

    private static string[] EngineSchemaNotes =>
    [
        "--  来源：Activiti 9.0.0",
        "--        activiti-core/activiti-engine/src/main/resources/org/activiti/db/create/",
        "--          activiti.<mysql|mssql>.create.engine.sql",
        "--          activiti.<mysql|mssql>.create.history.sql",
        "--",
        "--  裁剪依据：开发计划 §7.2（25 张表 → 13 张表）",
        "--    保留 · 运行时 8 张：ACT_GE_PROPERTY / ACT_GE_BYTEARRAY / ACT_RE_DEPLOYMENT /",
        "--                        ACT_RE_PROCDEF / ACT_RU_EXECUTION / ACT_RU_TASK /",
        "--                        ACT_RU_IDENTITYLINK / ACT_RU_VARIABLE",
        "--    保留 · 历史   5 张：ACT_HI_PROCINST / ACT_HI_ACTINST / ACT_HI_TASKINST /",
        "--                        ACT_HI_VARINST / ACT_HI_IDENTITYLINK",
        "--    裁剪        12 张：ACT_RE_MODEL / ACT_RU_JOB / ACT_RU_TIMER_JOB /",
        "--                        ACT_RU_SUSPENDED_JOB / ACT_RU_DEADLETTER_JOB /",
        "--                        ACT_RU_EVENT_SUBSCR / ACT_EVT_LOG / ACT_PROCDEF_INFO /",
        "--                        ACT_RU_INTEGRATION / ACT_HI_DETAIL / ACT_HI_COMMENT /",
        "--                        ACT_HI_ATTACHMENT",
        "--    （其中 ACT_RU_TIMER_JOB、ACT_RU_JOB、ACT_EVT_OUTBOX 以引擎扩展表形式在 ⑤ 补回）",
        "--",
        "--  ⚠️ ACT_IDC_EXEC_ROOT 的 IDC 为上游 Activiti 的原样拼写（非笔误），刻意保留。",
        "--  ⚠️ 裁掉 ACT_HI_DETAIL 的前提是历史级别取 AUDIT（Activiti 默认，只记活动/任务/变量实例）。",
        "--",
        "--  引擎初值必须逐字一致（开发计划 §7.3）：",
        "--     · schema.version  —— 引擎启动时校验（DatabaseSchemaChecker）",
        "--     · schema.history  —— 记录建库时的版本",
        "--     · next.dbid       —— DbIdGenerator 的区块游标；区块大小默认 2500",
        string.Empty,
        "-- ─────────────────────────── 运行时表（8 张）───────────────────────────",
        string.Empty,
    ];

    private static string[] BusinessNotes =>
    [
        "--  来源：业务系统存量库（原 wf schema，本库统一落 dbo / 无前缀，表名逐字相同）：",
        "--        instances              流程实例表",
        "--        instance_current_task  当前审批任务记录",
        "--        deal_history           审批历史记录",
        "--",
        "--  与引擎表（ACT_*）的关系：",
        "--    instances.workflow_instance_id  → ACT_RU_EXECUTION.ID_ / ACT_HI_PROCINST.PROC_INST_ID_",
        "--    instance_current_task.task_id   → ACT_RU_TASK.ID_",
        "--    deal_history.task_node          → 用户任务节点名（ACT_RU_TASK.NAME_）",
        "--",
        "--  业务表用 utf8mb4（deal_comment / describe 含长中文备注，预留 emoji / 扩展汉字空间），",
        "--  引擎表仍用 utf8（与上游 Activiti 一致，对拍口径不变）。",
        "--",
        "--  流程类型表（workflow_type）：流程信息枚举（ProcessInfoEnum）的数据源，",
        "--    系统启动时加载；种子数据在本节末尾（不新增 / 不覆盖 create_time）。",
        "--  流程模型表（bpmn_model）：管理端设计器的模型落点（BPMN XML 编辑源），",
        "--    与部署结果（ACT_RE_PROCDEF）分离 —— 模型可反复编辑，部署才产生新版本。",
        "--",
        "--  ⚠️ 主键均为业务侧生成的 BIGINT，不使用自增（与来源一致）。",
        string.Empty,
    ];

    private static string[] DemoNotes =>
    [
        "--  来源：**本项目新增** —— 订单审批 + 请假审批两条 Demo 的业务数据表。",
        "--",
        "--    leave_bill          请假单（days / reason 真列 + ext_params JSON 扩展参数）",
        "--    order_bill          订单单（status 随审批回写：1=审批中，-1=拒绝，100=通过）",
        "--    workflow_parameters 流程参数键值表（「实例 × 参数名」；param_value 用 text / nvarchar(max)）",
        "--    sys_*               组织架构表（部门 / 员工 / 审批组 / 组成员）+ 演示种子数据",
        "--",
        "--  与流程实例表的关系：",
        "--    instances.business_id          → leave_bill.business_id / order_bill.business_id",
        "--    instances.workflow_instance_id → workflow_parameters.workflow_instance_id",
        "--    instances.apply_emp_id         → sys_emp.domain_account（申请人在流程里的用户 ID）",
        "--    instances.apply_dept_id        → sys_dept.dept_id",
        "--",
        "--  ⚠️ 这一组是 Demo / 示例业务表：只接引擎、不要业务表的环境可按需忽略本节的表，",
        "--     但脚本整体执行（含本节的表）对引擎功能无影响。",
        string.Empty,
    ];

    private static string[] PerfIndexesNotes =>
    [
        "--  背景：上游 Activiti 的索引清单里，运行期（ACT_RU_*）只有少数几个；",
        "--        本引擎高频执行的查询（执行树按流程实例/父执行、任务按办理人/流程实例、",
        "--        变量按执行、关系按流程实例）在 MySQL 上会全表扫描。",
        "--",
        "--  幂等：MySQL 用 information_schema 判重后动态执行，SQL Server 用 sys.indexes 判重。",
        string.Empty,
    ];

    private static string[] ExtensionsNotes =>
    [
        "--  ACT_IDEMPOTENCY      HTTP 幂等键（POST 重试回放，不属于 Activiti 引擎表）",
        "--  ACT_RU_TIMER_JOB     边界定时器作业（任务创建时写一条，到点触发取消分支）",
        "--  ACT_HI_MIGRATION     流程实例迁移审计（源/目标定义、活动映射、原因、操作人）",
        "--  bpmn_model_history   流程模型版本历史（每次保存/回滚一版）",
        "--  admin_audit_log      管理动作审计（转办 / 跳转 / 变量直写）",
        "--  admin_user           控制台账号（账号密码登录；只存 PBKDF2 口令散列）",
        "--  admin_history_archive 历史归档快照（归档后原 ACT_HI_* 行删除）",
        "--  ACT_RU_JOB           asyncBefore / asyncAfter 异步作业（含重试与死信）",
        "--  ACT_EVT_OUTBOX       事务性事件外发队列（至少一次投递，含发布重试/死信）",
        "--  nb_tenant            平台租户（一期固定 default 一行，仍按多租户建模）",
        "--  nb_api_client        接入客户端（一系统一客户端一密钥，只存 SHA-256 摘要）",
        "--  nb_client_process_grant  客户端流程白名单（一行一条授权；无行=不限流程）",
        "--  nb_process_catalog  流程目录覆盖层（无行=由部署的 BPMN 派生，部署即可见）",
        "--  nb_approval_dictionary  审批语义字典（optionId→semantic 映射；node_id 空=流程级默认）",
        "--  nb_instance_timeline 审批记录时间线（append-only；sequence 单调，作增量拉取游标）",
        "--  nb_approval_snapshot 表单快照（键=task_id：历史办理页按提交当时的字段定义渲染）",
        "--",
        "--  全部幂等：表已存在时跳过，缺索引/缺列由后续守卫补齐。",
        string.Empty,
    ];

    private static List<string> Footer(Dialect dialect)
    {
        var check = dialect == Dialect.MySql
            ? "select NAME_, VALUE_ from ACT_GE_PROPERTY where NAME_ in ('schema.version', 'next.dbid');"
            : "select NAME_, VALUE_ from ACT_GE_PROPERTY where NAME_ in ('schema.version', 'next.dbid');";

        return
        [
            string.Empty,
            "-- ─────────────────────────── 执行结果自检 ───────────────────────────",
            "--  期望 schema.version = 8.1.4（与 DatabaseSchemaChecker.ExpectedSchemaVersion 一致）；",
            "--  next.dbid 是 ID 区块游标，重复执行不会重置。",
            check,
            string.Empty,
        ];
    }

    private static List<string> BlankSection(string title) =>
    [
        string.Empty,
        $"-- ─────────────────────────── {title} ───────────────────────────",
        string.Empty,
    ];
}

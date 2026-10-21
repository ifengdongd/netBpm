using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NetBpm.DbInit.Schema;

/// <summary>
/// 把 <see cref="SchemaCatalog"/> 渲染成 <c>db/mysql</c> 与 <c>db/sqlserver</c> 两套脚本。
/// </summary>
internal static class SqlGenerator
{
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

        Emit(directory, dialect, "0001_create_engine_schema.sql", RenderEngineSchema(dialect));
        Emit(directory, dialect, "0002_create_business_tables.sql", RenderBusinessTables(dialect));
        Emit(directory, dialect, "0003_create_demo_tables.sql", RenderDemoTables(dialect));
        Emit(directory, dialect, "0004_perf_indexes.sql", RenderPerfIndexes(dialect));
        Emit(directory, dialect, "0005_create_idempotency.sql", RenderIdempotency(dialect));
        Emit(directory, dialect, "0006_create_timer_job.sql", RenderTimerJob(dialect));
        Emit(directory, dialect, "0007_create_migration_log.sql", RenderMigrationLog(dialect));
        Emit(directory, dialect, "0008_create_model_history.sql", RenderModelHistory(dialect));
        Emit(directory, dialect, "0009_create_admin_governance.sql", RenderAdminGovernance(dialect));
        Emit(directory, dialect, "0011_engine_upgrade.sql", RenderEngineUpgrade(dialect));
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
    //  各脚本渲染
    // ────────────────────────────────────────────────────────────────────────

    private static List<string> RenderEngineSchema(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(HeaderEngineSchema);

        foreach (var table in SchemaCatalog.EngineTables)
        {
            lines.AddRange(RenderCreateTable(table, dialect, idempotent: false));
        }

        lines.AddRange(BlankSection("索引"));
        foreach (var index in SchemaCatalog.EngineIndexes)
        {
            lines.AddRange(RenderEngineIndex(index, dialect, idempotent: false));
        }

        lines.AddRange(BlankSection("唯一约束与外键"));
        foreach (var table in SchemaCatalog.EngineTables)
        {
            foreach (var unique in table.UniqueKeys)
            {
                if (unique.Name.Length > 0)
                {
                    lines.AddRange(RenderAlterUnique(table.Name, unique));
                }
            }
        }

        foreach (var fk in SchemaCatalog.EngineForeignKeys)
        {
            lines.AddRange(RenderForeignKey(fk, dialect));
        }

        lines.AddRange(BlankSection("引擎初值（ACT_GE_PROPERTY）"));
        foreach (var row in SchemaCatalog.PropertySeeds)
        {
            var values = string.Join(", ", RowValues(row, dialect));
            lines.Add($"insert into ACT_GE_PROPERTY values ({values});");
        }

        if (dialect == Dialect.SqlServer)
        {
            lines.AddRange(RenderTableComments(SchemaCatalog.EngineTables));
            lines.Add("go");
        }

        return lines;
    }

    private static List<string> RenderBusinessTables(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(HeaderBusinessTables);

        foreach (var table in SchemaCatalog.InstanceTables)
        {
            lines.AddRange(RenderCreateTable(table, dialect, idempotent: false));
            lines.AddRange(RenderTableScopedIndexes(table, dialect, idempotent: false));
        }

        lines.AddRange(BlankSection("流程类型表"));
        lines.AddRange(RenderCreateTable(SchemaCatalog.WorkflowTypeTable, dialect, idempotent: false));
        lines.AddRange(RenderTableScopedIndexes(SchemaCatalog.WorkflowTypeTable, dialect, idempotent: false));

        lines.AddRange(BlankSection("种子数据：订单审批 / 请假审批两条流程"));
        lines.AddRange(RenderSeed(SchemaCatalog.WorkflowTypeSeeds, dialect, idempotent: false));

        lines.AddRange(BlankSection("流程模型表（管理端设计器）"));
        lines.AddRange(RenderCreateTable(SchemaCatalog.BpmnModelTable, dialect, idempotent: false));
        lines.AddRange(RenderTableScopedIndexes(SchemaCatalog.BpmnModelTable, dialect, idempotent: false));

        if (dialect == Dialect.SqlServer)
        {
            var tables = new List<TableDef>(SchemaCatalog.InstanceTables)
            {
                SchemaCatalog.WorkflowTypeTable,
                SchemaCatalog.BpmnModelTable,
            };
            lines.AddRange(RenderTableComments(tables));
            lines.Add("go");
        }

        return lines;
    }

    private static List<string> RenderDemoTables(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(HeaderDemoTables);

        // 业务表单（一次性建表）
        foreach (var table in new[]
        {
            SchemaCatalog.LeaveBillTable,
            SchemaCatalog.OrderBillTable,
            SchemaCatalog.WorkflowParametersTable,
        })
        {
            lines.AddRange(RenderCreateTable(table, dialect, idempotent: false));
            lines.AddRange(RenderTableScopedIndexes(table, dialect, idempotent: false));
        }

        // 组织架构表（幂等，可重复执行）
        lines.AddRange(BlankSection("组织架构表（sys_*，幂等可重复执行）"));
        foreach (var table in SchemaCatalog.OrgTables)
        {
            lines.AddRange(RenderCreateTable(table, dialect, idempotent: true));
            lines.AddRange(RenderTableScopedIndexes(table, dialect, idempotent: true));
        }

        lines.AddRange(BlankSection("种子数据（演示用，可重复执行）"));
        foreach (var seed in SchemaCatalog.OrgSeeds)
        {
            lines.AddRange(RenderSeed(seed, dialect, idempotent: true));
        }

        if (dialect == Dialect.SqlServer)
        {
            var tables = new List<TableDef>
            {
                SchemaCatalog.LeaveBillTable,
                SchemaCatalog.OrderBillTable,
                SchemaCatalog.WorkflowParametersTable,
            };
            tables.AddRange(SchemaCatalog.OrgTables);
            lines.AddRange(RenderTableComments(tables));
            lines.Add("go");
        }

        return lines;
    }

    private static List<string> RenderPerfIndexes(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(dialect == Dialect.MySql ? HeaderPerfIndexesMySql : HeaderPerfIndexesSqlServer);

        foreach (var index in SchemaCatalog.PerfIndexes)
        {
            lines.AddRange(RenderEngineIndex(index, dialect, idempotent: true));
            lines.Add(string.Empty);
        }

        return lines;
    }

    private static List<string> RenderIdempotency(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(dialect == Dialect.MySql ? HeaderIdempotencyMySql : HeaderIdempotencySqlServer);

        lines.AddRange(RenderCreateTable(SchemaCatalog.IdempotencyTable, dialect, idempotent: true));
        lines.AddRange(RenderTableScopedIndexes(SchemaCatalog.IdempotencyTable, dialect, idempotent: true));

        return lines;
    }

    private static List<string> RenderTimerJob(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(dialect == Dialect.MySql ? HeaderTimerJobMySql : HeaderTimerJobSqlServer);

        lines.AddRange(RenderCreateTable(SchemaCatalog.TimerJobTable, dialect, idempotent: true));
        lines.AddRange(RenderTableScopedIndexes(SchemaCatalog.TimerJobTable, dialect, idempotent: true));

        if (dialect == Dialect.SqlServer)
        {
            lines.AddRange(RenderTableComments([SchemaCatalog.TimerJobTable]));
            lines.Add("go");
        }

        return lines;
    }

    private static List<string> RenderMigrationLog(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(dialect == Dialect.MySql ? HeaderMigrationMySql : HeaderMigrationSqlServer);

        lines.AddRange(RenderCreateTable(SchemaCatalog.MigrationTable, dialect, idempotent: true));
        lines.AddRange(RenderTableScopedIndexes(SchemaCatalog.MigrationTable, dialect, idempotent: true));

        if (dialect == Dialect.SqlServer)
        {
            lines.AddRange(RenderTableComments([SchemaCatalog.MigrationTable]));
            lines.Add("go");
        }

        return lines;
    }

    private static List<string> RenderModelHistory(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(dialect == Dialect.MySql ? HeaderModelHistoryMySql : HeaderModelHistorySqlServer);

        lines.AddRange(RenderCreateTable(SchemaCatalog.ModelHistoryTable, dialect, idempotent: true));
        lines.AddRange(RenderTableScopedIndexes(SchemaCatalog.ModelHistoryTable, dialect, idempotent: true));

        if (dialect == Dialect.SqlServer)
        {
            lines.AddRange(RenderTableComments([SchemaCatalog.ModelHistoryTable]));
            lines.Add("go");
        }

        return lines;
    }

    private static List<string> RenderAdminGovernance(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(dialect == Dialect.MySql ? HeaderAdminGovernanceMySql : HeaderAdminGovernanceSqlServer);

        TableDef[] tables = [SchemaCatalog.AdminAuditTable, SchemaCatalog.HistoryArchiveTable];

        foreach (var table in tables)
        {
            lines.AddRange(RenderCreateTable(table, dialect, idempotent: true));
            lines.AddRange(RenderTableScopedIndexes(table, dialect, idempotent: true));
        }

        if (dialect == Dialect.SqlServer)
        {
            lines.AddRange(RenderTableComments(tables));
            lines.Add("go");
        }

        return lines;
    }

    /// <summary>
    /// 0011 增量升级：把「老库拿不到新脚本」的缺口补上。
    /// <para>
    /// 内容全部幂等，新库（0001-0010 已包含同样结构）执行时是空操作，老库执行时才真正补列 / 扩容。
    /// 脚本执行记录见 <see cref="SchemaCatalog.SchemaLogTable"/>，由 <c>DatabaseMigrator</c> 维护。
    /// </para>
    /// </summary>
    private static List<string> RenderEngineUpgrade(Dialect dialect)
    {
        var lines = new List<string>();
        lines.AddRange(Banner(dialect));
        lines.AddRange(dialect == Dialect.MySql ? HeaderUpgradeMySql : HeaderUpgradeSqlServer);

        lines.AddRange(BlankSection("脚本执行日志（增量迁移依据）"));
        lines.AddRange(RenderCreateTable(SchemaCatalog.SchemaLogTable, dialect, idempotent: true));
        lines.AddRange(RenderTableScopedIndexes(SchemaCatalog.SchemaLogTable, dialect, idempotent: true));

        lines.AddRange(BlankSection("模型大文本列扩容（MySQL text 64KB → mediumtext 16MB）"));
        if (dialect == Dialect.MySql)
        {
            lines.Add("alter table bpmn_model modify column bpmn_xml mediumtext not null;");
            lines.Add("alter table bpmn_model modify column svg mediumtext;");
            lines.Add("alter table bpmn_model_history modify column bpmn_xml mediumtext not null;");
            lines.Add("alter table bpmn_model_history modify column svg mediumtext;");
        }
        else
        {
            lines.Add("-- SQL Server 的 bpmn_xml / svg 已是 nvarchar(max)，无需扩容。");
        }

        lines.AddRange(BlankSection("定时作业可靠性列（重试 / 死信 / 失败原因）"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "RETRIES_", "int default 3", "int default 3"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "DEAD_LETTER_", "tinyint default 0", "tinyint default 0"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "EXCEPTION_MSG_", "varchar(1000)", "nvarchar(1000)"));
        lines.AddRange(RenderAddColumn(dialect, "ACT_RU_TIMER_JOB", "LAST_FAILURE_", "datetime(3)", "datetime"));

        lines.AddRange(BlankSection("定时作业死信查询索引"));
        lines.AddRange(RenderEngineIndex(
            new TableIndexDef("ACT_RU_TIMER_JOB", "ACT_IDX_TIMER_JOB_DEADLINE", ["DEAD_LETTER_", "DUEDATE_"]),
            dialect,
            idempotent: true));

        // 迁移审计表的复合索引已归入 0007 建表脚本（新库直接带索引）；
        // 老库的 ACT_HI_MIGRATION 早已建好，靠这里幂等补齐。
        lines.AddRange(BlankSection("流程实例迁移审计查询索引"));
        lines.AddRange(RenderEngineIndex(
            new TableIndexDef("ACT_HI_MIGRATION", "ACT_IDX_MIGRATION_PROCINST_TIME", ["PROC_INST_ID_", "MIGRATED_AT_"]),
            dialect,
            idempotent: true));

        return lines;
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
            var add = $"alter table {table} add column {column} {mySqlDefinition}";
            return
            [
                "set @ddl = (select if(count(*) = 0,",
                $"        '{add}',",
                "        'select 1')",
                "    from information_schema.columns",
                $"    where table_schema = database() and table_name = '{table}'",
                $"      and column_name = '{column}');",
                "prepare stmt from @ddl;",
                "execute stmt;",
                "deallocate prepare stmt;",
                string.Empty,
            ];
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
    //  DDL 片段
    // ────────────────────────────────────────────────────────────────────────

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
                // 引擎表具名唯一键（ACT_UNIQ_PROCDEF）走 alter table，不内联；
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
            sqlServerLines.Add($"if object_id('{table.Name}', 'U') is null");
            sqlServerLines.Add($"    create table {table.Name} (");
        }
        else
        {
            sqlServerLines.Add($"create table {table.Name} (");
        }

        sqlServerLines.AddRange(JoinWithComma(body));
        sqlServerLines.Add(idempotent ? "    )" : ");");

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

    /// <summary>引擎表（0001）/ 性能索引（0009）的独立索引，索引名即显式给出所属表。</summary>
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

    private static List<string> RenderMySqlIdempotentIndex(TableIndexDef index)
    {
        var columns = string.Join(", ", index.Columns);
        var create = $"create index {index.Name} on {index.Table} ({columns})";
        return
        [
            "set @ddl = (select if(count(*) = 0,",
            $"        '{create}',",
            "        'select 1')",
            "    from information_schema.statistics",
            $"    where table_schema = database() and table_name = '{index.Table}'",
            $"      and index_name = '{index.Name}');",
            "prepare stmt from @ddl;",
            "execute stmt;",
            "deallocate prepare stmt;",
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

    private static List<string> RenderAlterUnique(string table, IndexDef unique)
    {
        var columns = string.Join(", ", unique.Columns);
        return
        [
            $"alter table {table}",
            $"    add constraint {unique.Name}",
            $"    unique ({columns});",
        ];
    }

    private static List<string> RenderForeignKey(ForeignKeyDef fk, Dialect dialect)
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

        return
        [
            $"alter table {fk.Table}",
            $"    add constraint {fk.Name}",
            $"    foreign key ({fk.Column})",
            $"    references {fk.ReferencedTable} ({fk.ReferencedColumn}){cascade};",
        ];
    }

    private static List<string> RenderSeed(SeedTable seed, Dialect dialect, bool idempotent)
    {
        var lines = new List<string>();
        var columns = string.Join(", ", seed.Columns.Select(name => Quote(name, dialect)));

        if (dialect == Dialect.MySql)
        {
            var values = string.Join(",\n       ", seed.Rows.Select(row => $"({string.Join(", ", RowValues(row, dialect))})"));
            lines.Add($"insert into {seed.TableName} ({columns})");
            lines.Add($"values {values}");

            if (idempotent)
            {
                var updateColumns = seed.UpsertUpdateColumns.Count > 0 ? seed.UpsertUpdateColumns : ["create_time"];
                var updateClauses = string.Join(", ", updateColumns.Select(column => $"{column} = values({column})"));
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
                var guard = string.Join(" and ", seed.GuardColumns.Select(column =>
                    $"{column} = {Literal(row[columnIndex[column]], dialect)}"));
                lines.Add($"if not exists (select 1 from {seed.TableName} where {guard})");
                lines.Add($"    insert into {seed.TableName} ({columns})");
                lines.Add($"    values ({string.Join(", ", RowValues(row, dialect))});");
                lines.Add("go");
            }
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static List<string> RenderTableComments(IReadOnlyList<TableDef> tables)
    {
        var lines = new List<string>();
        lines.AddRange(BlankSection("表与列注释（扩展属性 MS_Description）"));

        foreach (var table in tables)
        {
            if (table.Comment.Length > 0)
            {
                lines.Add($"exec sp_addextendedproperty @name = N'MS_Description', @value = N'{table.Comment}',");
                lines.Add("    @level0type = N'SCHEMA', @level0name = N'dbo',");
                lines.Add($"    @level1type = N'TABLE', @level1name = N'{table.Name}';");
                lines.Add(string.Empty);
            }

            foreach (var column in table.Columns)
            {
                if (column.Comment is null)
                {
                    continue;
                }

                lines.Add($"exec sp_addextendedproperty @name = N'MS_Description', @value = N'{column.Comment}',");
                lines.Add($"    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'{table.Name}',");
                lines.Add($"    @level2type = N'COLUMN', @level2name = N'{column.Name}';");
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

    private static List<string> Banner(Dialect dialect)
    {
        var dialectName = dialect == Dialect.MySql ? "MySQL" : "SQL Server";
        return
        [
            "-- ============================================================================",
            $"--  NetBpm · {dialectName} 数据库脚本（自动生成，请勿手改）",
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

    private static List<string> BlankSection(string title) =>
    [
        string.Empty,
        $"-- ─────────────────────────── {title} ───────────────────────────",
        string.Empty,
    ];

    private static string[] HeaderEngineSchema =>
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
        "--",
        "--  ⚠️ 表名、列名、索引名、外键名与 Activiti 逐字一致 —— §7.1 决定两端可读写同一套",
        "--     表结构，从而支持 §9.2 的「库级 dump 行快照对拍」。请勿改名或「顺手优化」列类型。",
        "--  ⚠️ ACT_IDC_EXEC_ROOT 的 IDC 为上游 Activiti 的原样拼写（非笔误），刻意保留。",
        "--  ⚠️ 裁掉 ACT_HI_DETAIL 的前提是历史级别取 AUDIT（Activiti 默认，只记活动/任务/变量实例）。",
        "--",
        "--  本脚本合并原 0001（建表）与 0002（初值）：13 张引擎表 + 索引 + 唯一约束 / 外键 +",
        "--  ACT_GE_PROPERTY 的三条初值。",
        "--  ⚠️ 初值必须逐字一致（开发计划 §7.3）：",
        "--     · schema.version  —— 引擎启动时校验（DatabaseSchemaChecker）",
        "--     · schema.history  —— 记录建库时的版本，供后续迁移判断",
        "--     · next.dbid       —— DbIdGenerator 的区块游标；区块大小默认 2500",
        "-- ============================================================================",
        string.Empty,
        "-- ─────────────────────────── 运行时表（8 张）───────────────────────────",
        string.Empty,
    ];

    private static string[] HeaderBusinessTables =>
    [
        "--  来源：业务系统存量库（原 wf schema，本库统一落 dbo / 无前缀，表名逐字相同）：",
        "--        instances              流程实例表",
        "--        instance_current_task  当前审批任务记录",
        "--        deal_history           审批历史记录",
        "--",
        "--  与引擎表（ACT_*，见 0001_create_engine_schema.sql）的关系：",
        "--    instances.workflow_instance_id  → ACT_RU_EXECUTION.ID_ / ACT_HI_PROCINST.PROC_INST_ID_",
        "--    instance_current_task.task_id   → ACT_RU_TASK.ID_",
        "--    deal_history.task_node          → 用户任务节点名（ACT_RU_TASK.NAME_）",
        "--",
        "--  类型映射：nvarchar → varchar；datetime / bigint / smallint / tinyint 原样保留。",
        "--  ⚠️ 主键均为业务侧生成的 BIGINT，不使用自增（与来源一致）。",
        "--  ⚠️ 业务表用 utf8mb4（deal_comment / describe 含长中文备注，预留 emoji / 扩展汉字空间），",
        "--     引擎表仍沿用 0001 的 utf8（对拍口径不变）。",
        "--  ➕ 补充了查询索引（apply_id / task_id / workflow_instance_id / business_id / status_id /",
        "--     deal_emp_id），不改变列结构与默认值。",
        "--",
        "--  流程类型表（workflow_type）：流程信息枚举（ProcessInfoEnum）的数据源，",
        "--    流程的 Id / Name / Describe / ViewUrl / ApproveUrl 配置在表里，系统启动时加载。",
        "--    workflow_type.id = instances.workflow_type_id（实例登记的类型 id）。",
        "--    name 必须与 BPMN process/@id（流程 key）一致；id 建议用设计器 modelId。",
        "--",
        "--  流程模型表（bpmn_model）：管理端流程设计器的模型落点（BPMN XML 编辑源），",
        "--    与部署结果（ACT_RE_PROCDEF）分离 —— 模型可反复编辑，部署才产生新版本。",
        "--",
        "--  执行顺序：0001_create_engine_schema.sql → 0002_create_business_tables.sql（本文件）",
        "-- ============================================================================",
        string.Empty,
        "-- ─────────────────────────── 流程实例表（3 张）───────────────────────────",
        string.Empty,
    ];

    private static string[] HeaderDemoTables =>
    [
        "--  来源：**本项目新增** —— 订单审批 + 请假审批两条 Demo 的业务数据表。",
        "--",
        "--  包含：",
        "--    leave_bill          请假单（days / reason 真列 + ext_params JSON 扩展参数）",
        "--    order_bill          订单单（status 随审批回写：1=审批中，-1=拒绝，100=通过）",
        "--    workflow_parameters 流程参数键值表（「实例 × 参数名」；param_value 用 text / nvarchar(max)）",
        "--    sys_*               组织架构表（部门 / 员工 / 审批组 / 组成员，幂等，含演示种子数据）",
        "--",
        "--  与流程实例表（0002_create_business_tables.sql）的关系：",
        "--    instances.business_id          → leave_bill.business_id / order_bill.business_id",
        "--    instances.workflow_instance_id → workflow_parameters.workflow_instance_id",
        "--    instances.apply_emp_id         → sys_emp.domain_account（申请人在流程里的用户 ID）",
        "--    instances.apply_dept_id        → sys_dept.dept_id",
        "--",
        "--  幂等性：sys_* 建表与种子可重复执行；其余三张（leave_bill / order_bill /",
        "--          workflow_parameters）一次性建表。",
        "--",
        "--  执行顺序：0001 → 0002 → 0003_create_demo_tables.sql（本文件）",
        "-- ============================================================================",
        string.Empty,
        "-- ─────────────────────────── 业务表单（3 张）───────────────────────────",
        string.Empty,
    ];

    private static string[] HeaderPerfIndexesMySql =>
    [
        "--  运行期查询索引（性能增强，不影响行内容，可与 Activiti 共库）。",
        "--",
        "--  背景：0001 的索引沿用上游 Activiti 清单，其中运行期（ACT_RU_*）只有少数几个。",
        "--  本引擎实际高频执行的查询（执行树按流程实例/父执行、任务按办理人/流程实例、",
        "--  变量按执行、关系按流程实例）在 MySQL 上会全表扫描。",
        "--",
        "--  幂等：MySQL 不支持 create index if not exists，这里用 information_schema 判重后动态执行。",
        "--  对已初始化的库请单独执行本脚本：",
        "--    NetBpm.DbMigrate apply --provider mysql --connection \"...\" --script db/mysql/0004_perf_indexes.sql",
        "--  新库执行 migrate 时会连同本脚本一起执行。",
        string.Empty,
    ];

    private static string[] HeaderPerfIndexesSqlServer =>
    [
        "--  运行期查询索引（性能增强，不影响行内容，可与 Activiti 共库）。",
        "--",
        "--  幂等：sys.indexes 判重，可重复执行。",
        "--  对已初始化的库请单独执行本脚本：",
        "--    NetBpm.DbMigrate apply --provider sqlserver --connection \"...\" --script db/sqlserver/0004_perf_indexes.sql",
        string.Empty,
    ];

    private static string[] HeaderUpgradeMySql =>
    [
        "--  增量升级脚本（0011）：补齐老库缺失的结构。",
        "--",
        "--  背景：此前 migrate 判定「库已初始化」后一条脚本都不执行，导致用 0001 建过的库",
        "--        永远拿不到 0004 索引、0006 定时作业表与后续扩展表。现在由 ACT_GE_SCHEMA_LOG",
        "--        记录已执行脚本，migrate 会按文件名补跑未执行的脚本。",
        "--",
        "--  本脚本幂等：MySQL 用 information_schema 判重后动态执行；",
        "--  内容：执行日志表、模型大文本列扩容、定时作业重试/死信列、死信查询索引。",
        string.Empty,
    ];

    private static string[] HeaderUpgradeSqlServer =>
    [
        "--  增量升级脚本（0011）：补齐老库缺失的结构。",
        "--",
        "--  幂等：object_id / sys.columns / sys.indexes 判重，可重复执行。",
        "--  内容：执行日志表、定时作业重试/死信列、死信查询索引。",
        "--        （bpmn_xml / svg 在 SQL Server 已是 nvarchar(max)，无需扩容。）",
        string.Empty,
    ];

    private static string[] HeaderIdempotencyMySql =>
    [
        "--  HTTP 幂等键（Idempotency-Key）。",
        "--",
        "--  背景：POST 启动实例 / 批量操作在客户端超时重试时会被重复执行。",
        "--  本表缓存「键 → 首次响应」，同键重试直接回放，不进入引擎命令。",
        "--",
        "--  注意：",
        "--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider mysql --connection \"...\" --script db/mysql/0005_create_idempotency.sql",
        "--   3. STATUS_CODE_ 为空表示「已占位、仍在处理」，并发同键请求会得到 409。",
        string.Empty,
    ];

    private static string[] HeaderTimerJobMySql =>
    [
        "--  定时作业表（边界定时器）。",
        "--",
        "--  背景：引擎支持「用户任务 + 定时边界事件」，任务创建时写一条作业，到点由宿主轮询触发：",
        "--        取消任务 → 沿边界事件出流继续。",
        "--",
        "--  注意：",
        "--   1. 本表是 Activiti ACT_RU_TIMER_JOB 的裁剪版（只保留一次性定时所需列）；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider mysql --connection \"...\" --script db/mysql/0006_create_timer_job.sql",
        "--   3. 本脚本幂等，可重复执行。",
        string.Empty,
    ];

    private static string[] HeaderTimerJobSqlServer =>
    [
        "--  定时作业表（边界定时器）。",
        "--",
        "--  背景：引擎支持「用户任务 + 定时边界事件」，任务创建时写一条作业，到点由宿主轮询触发：",
        "--        取消任务 → 沿边界事件出流继续。",
        "--",
        "--  注意：",
        "--   1. 本表是 Activiti ACT_RU_TIMER_JOB 的裁剪版（只保留一次性定时所需列）；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider sqlserver --connection \"...\" --script db/sqlserver/0006_create_timer_job.sql",
        "--   3. 本脚本幂等（object_id / sys.indexes 判重），可重复执行。",
        string.Empty,
    ];

    private static string[] HeaderMigrationMySql =>
    [
        "--  流程实例迁移审计表。",
        "--",
        "--  背景：流程定义升级后，运行中的实例可以迁到同 key 的新版本。",
        "--        历史表（ACT_HI_PROCINST）保持实例启动时的定义不变，",
        "--        迁移动作用本表记录（源/目标定义、活动映射、原因、操作人）。",
        "--",
        "--  注意：",
        "--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider mysql --connection \"...\" --script db/mysql/0007_create_migration_log.sql",
        "--   3. 本脚本幂等，可重复执行。",
        string.Empty,
    ];

    private static string[] HeaderMigrationSqlServer =>
    [
        "--  流程实例迁移审计表。",
        "--",
        "--  背景：流程定义升级后，运行中的实例可以迁到同 key 的新版本。",
        "--        历史表（ACT_HI_PROCINST）保持实例启动时的定义不变，",
        "--        迁移动作用本表记录（源/目标定义、活动映射、原因、操作人）。",
        "--",
        "--  注意：",
        "--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider sqlserver --connection \"...\" --script db/sqlserver/0007_create_migration_log.sql",
        "--   3. 本脚本幂等（object_id / sys.indexes 判重），可重复执行。",
        string.Empty,
    ];

    private static string[] HeaderModelHistoryMySql =>
    [
        "--  流程模型版本历史（管理端发布治理）。",
        "--",
        "--  背景：设计器保存模型时会覆盖 bpmn_model.bpmn_xml；本表在每次保存/回滚时追加一版快照，",
        "--        支持查看历史、回滚到任意版本，并记录操作人与动作。",
        "--",
        "--  注意：",
        "--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider mysql --connection \"...\" --script db/mysql/0008_create_model_history.sql",
        "--   3. 本脚本幂等，可重复执行。",
        string.Empty,
    ];

    private static string[] HeaderModelHistorySqlServer =>
    [
        "--  流程模型版本历史（管理端发布治理）。",
        "--",
        "--  背景：设计器保存模型时会覆盖 bpmn_model.bpmn_xml；本表在每次保存/回滚时追加一版快照，",
        "--        支持查看历史、回滚到任意版本，并记录操作人与动作。",
        "--",
        "--  注意：",
        "--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider sqlserver --connection \"...\" --script db/sqlserver/0008_create_model_history.sql",
        "--   3. 本脚本幂等（object_id / sys.indexes 判重），可重复执行。",
        string.Empty,
    ];

    private static string[] HeaderAdminGovernanceMySql =>
    [
        "--  管理治理（审计 + 历史归档）。",
        "--",
        "--  admin_audit_log：转办 / 跳转 / 变量直写等高风险管理动作的审计留痕",
        "--    （操作人、租户、动作、目标、结果、时间；不记录变量值）。",
        "--  admin_history_archive：已结束实例的 ACT_HI_* 行 JSON 快照；",
        "--    归档后原历史行在同一事务内删除，使 ACT_HI_* 保持轻量。",
        "--",
        "--  注意：",
        "--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider mysql --connection \"...\" --script db/mysql/0009_create_admin_governance.sql",
        "--   3. 本脚本幂等，可重复执行。",
        string.Empty,
    ];

    private static string[] HeaderAdminGovernanceSqlServer =>
    [
        "--  管理治理（审计 + 历史归档）。",
        "--",
        "--  admin_audit_log：转办 / 跳转 / 变量直写等高风险管理动作的审计留痕",
        "--    （操作人、租户、动作、目标、结果、时间；不记录变量值）。",
        "--  admin_history_archive：已结束实例的 ACT_HI_* 行 JSON 快照；",
        "--    归档后原历史行在同一事务内删除，使 ACT_HI_* 保持轻量。",
        "--",
        "--  注意：",
        "--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider sqlserver --connection \"...\" --script db/sqlserver/0009_create_admin_governance.sql",
        "--   3. 本脚本幂等（object_id / sys.indexes 判重），可重复执行。",
        string.Empty,
    ];

    private static string[] HeaderIdempotencySqlServer =>
    [
        "--  HTTP 幂等键（Idempotency-Key）。",
        "--",
        "--  背景：POST 启动实例 / 批量操作在客户端超时重试时会被重复执行。",
        "--  本表缓存「键 → 首次响应」，同键重试直接回放，不进入引擎命令。",
        "--",
        "--  注意：",
        "--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；",
        "--   2. 已初始化的库需单独执行：",
        "--      NetBpm.DbMigrate apply --provider sqlserver --connection \"...\" --script db/sqlserver/0005_create_idempotency.sql",
        "--   3. STATUS_CODE_ 为空表示「已占位、仍在处理」，并发同键请求会得到 409。",
        string.Empty,
    ];
}

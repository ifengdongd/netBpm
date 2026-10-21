using System.Security.Cryptography;
using System.Text;
using Dapper;
using NetBpm.Engine;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence;

/// <summary>迁移结果。</summary>
/// <param name="AlreadyInitialized">库中已存在 <c>ACT_GE_PROPERTY</c>（本次是增量升级而不是首次建库）。</param>
/// <param name="AppliedScripts">本次执行的脚本文件名（按顺序）。</param>
public sealed record MigrationResult(bool AlreadyInitialized, IReadOnlyList<string> AppliedScripts);

/// <summary>
/// 数据库脚本执行器：按文件名顺序补跑<b>尚未执行</b>的脚本，并把执行记录写入
/// <c>ACT_GE_SCHEMA_LOG</c>。
/// <para>
/// 🔑 早期实现只判断「<c>ACT_GE_PROPERTY</c> 是否存在」：已初始化的库一条脚本都不执行，
/// 于是用 0001 建过的老库永远拿不到 0004 索引与 0006-0011 的扩展表。
/// 现在改为「脚本级增量」：
/// <list type="number">
///   <item><description>已执行 + 校验和一致 → 跳过；</description></item>
///   <item><description>已执行 + 校验和变化 → 幂等脚本重跑并更新校验和；一次性建表脚本（0001-0003）直接报错；</description></item>
///   <item><description>未执行 → 执行并记录。</description></item>
/// </list>
/// </para>
/// <para>
/// 老库首次接入（没有 <c>ACT_GE_SCHEMA_LOG</c>）时，会按<b>实际存在的表 / 索引</b>回填基线，
/// 避免把 0001-0003 这些非幂等建表脚本重跑一遍。0004（索引）与 0011（结构补齐）始终是幂等的，
/// 因此会被补跑 —— 这正是老库缺失的部分。
/// </para>
/// </summary>
public sealed class DatabaseMigrator
{
    private readonly IDbSessionFactory _sessionFactory;
    private readonly string _scriptDirectory;

    /// <param name="sessionFactory">会话工厂。</param>
    /// <param name="scriptDirectory">脚本目录（如 <c>db/mysql</c>）。</param>
    public DatabaseMigrator(IDbSessionFactory sessionFactory, string scriptDirectory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptDirectory);

        _sessionFactory = sessionFactory;
        _scriptDirectory = scriptDirectory;
    }

    /// <summary>执行迁移。</summary>
    /// <exception cref="EngineException">脚本缺失、结构版本不匹配、一次性脚本被修改。</exception>
    public MigrationResult Migrate()
    {
        var scripts = Directory
            .EnumerateFiles(_scriptDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (scripts.Count == 0)
        {
            throw new EngineException($"脚本目录 '{_scriptDirectory}' 下没有 .sql 文件。");
        }

        using var session = _sessionFactory.Create();

        var alreadyInitialized = SchemaExists(session);

        if (alreadyInitialized)
        {
            var version = ReadSchemaVersion(session);

            if (!string.Equals(version, DatabaseSchemaChecker.ExpectedSchemaVersion, StringComparison.Ordinal))
            {
                throw new EngineException(
                    $"数据库结构版本不匹配：库中为 '{version}'，本引擎期望 " +
                    $"'{DatabaseSchemaChecker.ExpectedSchemaVersion}'。请使用对应版本的升级脚本，不要直接重跑建表脚本。");
            }
        }

        var logTableExisted = TableExists(session, "ACT_GE_SCHEMA_LOG");
        EnsureSchemaLogTable(session);

        var applied = LoadAppliedScripts(session);

        if (alreadyInitialized && !logTableExisted)
        {
            BaselineExistingSchema(session, scripts, applied);
        }

        var executed = new List<string>();

        foreach (var scriptPath in scripts)
        {
            var name = Path.GetFileName(scriptPath);
            var content = File.ReadAllText(scriptPath);
            var checksum = ComputeChecksum(content);

            if (applied.TryGetValue(name, out var existingChecksum))
            {
                if (string.Equals(existingChecksum, checksum, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsOneTimeScript(name))
                {
                    throw new EngineException(
                        $"脚本 '{name}' 已在库中执行，但文件内容已变更（校验和不一致）。" +
                        "一次性建表脚本不允许重跑；请新增一个增量脚本（00xx_*.sql）承载变更。");
                }
            }

            ExecuteScript(session, content);
            RecordScript(session, name, checksum);
            executed.Add(name);
        }

        return new MigrationResult(alreadyInitialized, executed);
    }

    /// <summary>一次性建表脚本：重跑会因对象已存在而失败，校验和变化必须走新脚本。</summary>
    private static bool IsOneTimeScript(string name) =>
        name.StartsWith("0001", StringComparison.Ordinal)
        || name.StartsWith("0002", StringComparison.Ordinal)
        || name.StartsWith("0003", StringComparison.Ordinal);

    private static void ExecuteScript(IDbSession session, string content)
    {
        session.Begin();

        try
        {
            foreach (var statement in SqlScriptReader.ReadStatements(content))
            {
                session.Connection.Execute(statement, transaction: session.Transaction);
            }

            // 迁移工具是同步 CLI：把 ValueTask 转成 Task 再阻塞等待，避免 CA2012
            session.CommitAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            session.Rollback();
            throw;
        }
    }

    private static void RecordScript(IDbSession session, string name, string checksum)
    {
        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql =>
                "insert into ACT_GE_SCHEMA_LOG (SCRIPT_, CHECKSUM_, APPLIED_AT_) values (@name, @checksum, @appliedAt) " +
                "on duplicate key update CHECKSUM_ = values(CHECKSUM_), APPLIED_AT_ = values(APPLIED_AT_)",

            DatabaseType.SqlServer =>
                "merge ACT_GE_SCHEMA_LOG as target " +
                "using (select @name as SCRIPT_, @checksum as CHECKSUM_, @appliedAt as APPLIED_AT_) as source " +
                "on target.SCRIPT_ = source.SCRIPT_ " +
                "when matched then update set CHECKSUM_ = source.CHECKSUM_, APPLIED_AT_ = source.APPLIED_AT_ " +
                "when not matched then insert (SCRIPT_, CHECKSUM_, APPLIED_AT_) " +
                "values (source.SCRIPT_, source.CHECKSUM_, source.APPLIED_AT_);",

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        session.Connection.Execute(sql, new { name, checksum, appliedAt = DateTime.UtcNow });
    }

    private static Dictionary<string, string?> LoadAppliedScripts(IDbSession session)
    {
        var rows = session.Connection.Query<(string Name, string? Checksum)>(
            "select SCRIPT_, CHECKSUM_ from ACT_GE_SCHEMA_LOG");

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            result[row.Name] = row.Checksum;
        }

        return result;
    }

    /// <summary>
    /// 老库首次接入：按实际存在的表 / 索引回填「已执行」基线。
    /// 只回填能确认已生效的脚本；确认不了的一律当作未执行（由幂等脚本补跑）。
    /// </summary>
    private static void BaselineExistingSchema(
        IDbSession session,
        IReadOnlyList<string> scripts,
        Dictionary<string, string?> applied)
    {
        var baseline = new List<string>();

        if (TableExists(session, "ACT_GE_PROPERTY"))
        {
            baseline.Add("0001");
        }

        if (TableExists(session, "instances"))
        {
            baseline.Add("0002");
        }

        if (TableExists(session, "sys_emp"))
        {
            baseline.Add("0003");
        }

        if (TableExists(session, "ACT_IDEMPOTENCY"))
        {
            baseline.Add("0005");
        }

        if (TableExists(session, "ACT_RU_TIMER_JOB"))
        {
            baseline.Add("0006");
        }

        if (TableExists(session, "ACT_HI_MIGRATION"))
        {
            baseline.Add("0007");
        }

        if (TableExists(session, "bpmn_model_history"))
        {
            baseline.Add("0008");
        }

        if (TableExists(session, "admin_audit_log"))
        {
            baseline.Add("0009");
        }

        if (IndexExists(session, "bpmn_model", "ux_bpmn_model_key"))
        {
            baseline.Add("0010");
        }

        foreach (var prefix in baseline)
        {
            var path = scripts.FirstOrDefault(candidate =>
                Path.GetFileName(candidate).StartsWith(prefix, StringComparison.Ordinal));

            if (path is null)
            {
                continue;
            }

            var name = Path.GetFileName(path);
            var checksum = ComputeChecksum(File.ReadAllText(path));

            RecordScript(session, name, checksum);
            applied[name] = checksum;
        }
    }

    private static void EnsureSchemaLogTable(IDbSession session)
    {
        if (TableExists(session, "ACT_GE_SCHEMA_LOG"))
        {
            return;
        }

        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql =>
                "create table if not exists ACT_GE_SCHEMA_LOG (" +
                "SCRIPT_ varchar(255) not null, " +
                "CHECKSUM_ varchar(64), " +
                "APPLIED_AT_ datetime(3) not null, " +
                "primary key (SCRIPT_)) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin",

            DatabaseType.SqlServer =>
                "if object_id('ACT_GE_SCHEMA_LOG', 'U') is null " +
                "create table ACT_GE_SCHEMA_LOG (" +
                "SCRIPT_ nvarchar(255) not null primary key, " +
                "CHECKSUM_ nvarchar(64), " +
                "APPLIED_AT_ datetime not null)",

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        session.Connection.Execute(sql);
    }

    private static string ComputeChecksum(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static bool SchemaExists(IDbSession session) => TableExists(session, "ACT_GE_PROPERTY");

    private static bool TableExists(IDbSession session, string table)
    {
        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql =>
                "select count(*) from information_schema.tables " +
                "where table_schema = database() and table_name = @table",

            DatabaseType.SqlServer =>
                "select count(*) from information_schema.tables where table_name = @table",

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        return session.Connection.ExecuteScalar<long>(sql, new { table }) > 0;
    }

    private static bool IndexExists(IDbSession session, string table, string index)
    {
        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql =>
                "select count(*) from information_schema.statistics " +
                "where table_schema = database() and table_name = @table and index_name = @index",

            DatabaseType.SqlServer =>
                "select count(*) from sys.indexes where name = @index and object_id = object_id(@table)",

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        return session.Connection.ExecuteScalar<long>(sql, new { table, index }) > 0;
    }

    private static string ReadSchemaVersion(IDbSession session)
    {
        const string sql = "select VALUE_ from ACT_GE_PROPERTY where NAME_ = 'schema.version'";

        return session.Connection.ExecuteScalar<string>(sql)
            ?? throw new EngineException("ACT_GE_PROPERTY 中缺少 'schema.version'。");
    }
}

/// <summary>
/// 极简 DDL/DML 脚本切分器。
/// <para>
/// 只处理本仓库自带的脚本形态：<c>--</c> 行注释、以 <c>;</c> 结尾的语句、
/// SQL Server 的 <c>go</c> 批分隔符。刻意<b>不</b>做通用 SQL 解析
/// （不需要存储过程 / 触发器 / <c>DELIMITER</c>）——
/// 一旦脚本复杂度上升，应换成 DB 迁移库而不是继续加正则。
/// </para>
/// </summary>
public static class SqlScriptReader
{
    /// <summary>把脚本正文切分为可逐条执行的语句。</summary>
    public static IReadOnlyList<string> ReadStatements(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var statements = new List<string>();
        var buffer = new StringBuilder();

        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.Length == 0
                || trimmed.StartsWith("--", StringComparison.Ordinal)
                || string.Equals(trimmed, "go", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            buffer.AppendLine(line);

            if (trimmed.EndsWith(';'))
            {
                var statement = buffer.ToString().Trim();
                if (statement.Length > 0)
                {
                    statements.Add(statement);
                }

                buffer.Clear();
            }
        }

        // 容错：末尾没有分号的语句也要执行
        var trailing = buffer.ToString().Trim();
        if (trailing.Length > 0)
        {
            statements.Add(trailing);
        }

        return statements;
    }
}

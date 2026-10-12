using Dapper;
using NetBpm.Engine;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence;

/// <summary>迁移结果。</summary>
/// <param name="AlreadyInitialized">库中已存在 <c>ACT_GE_PROPERTY</c>（跳过建表）。</param>
/// <param name="AppliedScripts">本次执行的脚本文件名（按顺序）。</param>
public sealed record MigrationResult(bool AlreadyInitialized, IReadOnlyList<string> AppliedScripts);

/// <summary>
/// 数据库脚本执行器。对应 Activiti 的「DDL 脚本 + <c>schema.version</c> 记录」做法。
/// <para>
/// 🔑 刻意<b>不引入额外的迁移记录表</b>：开发计划 §7.1 要求两端可读写同一套表结构
/// （对拍的前提），多一张表就破坏了「13 张表」的约定。
/// 因此幂等性通过「<c>ACT_GE_PROPERTY</c> 是否存在 + <c>schema.version</c> 是否匹配」判断。
/// </para>
/// <para>
/// 后续若需要真正的版本化演进，应把脚本改为 <c>0003_xxx.sql / 0004_xxx.sql</c> 形式，
/// 并把已执行版本追加到 <c>schema.history</c>（同样是 Activiti 的字段）。
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
    /// <exception cref="EngineException">脚本缺失、结构版本不匹配。</exception>
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

        if (SchemaExists(session))
        {
            var version = ReadSchemaVersion(session);

            if (!string.Equals(version, DatabaseSchemaChecker.ExpectedSchemaVersion, StringComparison.Ordinal))
            {
                throw new EngineException(
                    $"数据库结构版本不匹配：库中为 '{version}'，本引擎期望 " +
                    $"'{DatabaseSchemaChecker.ExpectedSchemaVersion}'。请勿对已初始化的库重复执行建表脚本。");
            }

            return new MigrationResult(AlreadyInitialized: true, AppliedScripts: []);
        }

        var applied = new List<string>();

        foreach (var scriptPath in scripts)
        {
            var statements = SqlScriptReader.ReadStatements(File.ReadAllText(scriptPath));

            foreach (var statement in statements)
            {
                session.Connection.Execute(statement);
            }

            applied.Add(Path.GetFileName(scriptPath));
        }

        return new MigrationResult(AlreadyInitialized: false, AppliedScripts: applied);
    }

    private static bool SchemaExists(IDbSession session)
    {
        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql =>
                "select count(*) from information_schema.tables " +
                "where table_schema = database() and table_name = 'ACT_GE_PROPERTY'",

            DatabaseType.SqlServer =>
                "select count(*) from information_schema.tables where table_name = 'ACT_GE_PROPERTY'",

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        return session.Connection.ExecuteScalar<long>(sql) > 0;
    }

    private static string ReadSchemaVersion(IDbSession session)
    {
        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql => "select VALUE_ from ACT_GE_PROPERTY where NAME_ = 'schema.version'",
            DatabaseType.SqlServer => "select VALUE_ from ACT_GE_PROPERTY where NAME_ = 'schema.version'",
            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

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
        var buffer = new System.Text.StringBuilder();

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

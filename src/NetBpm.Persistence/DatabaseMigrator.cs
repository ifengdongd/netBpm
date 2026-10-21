using System.Text;
using Dapper;
using NetBpm.Engine;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence;

/// <summary>迁移结果。</summary>
/// <param name="AlreadyInitialized">执行前库中已存在 <c>ACT_GE_PROPERTY</c>（即不是首次建库）。</param>
/// <param name="AppliedScripts">本次执行的脚本文件名（按文件名顺序）。</param>
public sealed record MigrationResult(bool AlreadyInitialized, IReadOnlyList<string> AppliedScripts);

/// <summary>
/// 数据库初始化脚本执行器。
/// <para>
/// 脚本形态：<c>db/&lt;dialect&gt;/netbpm_init.sql</c> —— <b>单文件、全幂等</b>：
/// 建库 / 建表 / 索引 / 外键 / 初值数据都带方言守卫，重复执行等于空操作。
/// </para>
/// <para>
/// 因此这里<b>不再</b>维护「脚本执行日志 + 校验和」的脚本级增量机制：
/// 每次 <see cref="Migrate"/> 都完整执行脚本，老库缺什么补什么（幂等守卫决定），
/// 新库一次建成。库名由连接串决定（脚本内的建库段由
/// <see cref="SqlScriptReader"/> 跳过），手工执行脚本时才走建库段。
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

    /// <summary>执行初始化脚本（幂等，可重复调用）。</summary>
    /// <exception cref="EngineException">脚本目录为空、脚本缺失。</exception>
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

        // 是否首次建库只用于日志呈现；结构的收敛由脚本自身的幂等守卫保证
        var alreadyInitialized = SchemaExists(session);

        var executed = new List<string>();

        foreach (var scriptPath in scripts)
        {
            ExecuteScript(session, File.ReadAllText(scriptPath));
            executed.Add(Path.GetFileName(scriptPath));
        }

        return new MigrationResult(alreadyInitialized, executed);
    }

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

    private static bool SchemaExists(IDbSession session)
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

        return session.Connection.ExecuteScalar<long>(sql, new { table = "ACT_GE_PROPERTY" }) > 0;
    }
}

/// <summary>
/// 极简 DDL/DML 脚本切分器。
/// <para>
/// 只处理本仓库自带的脚本形态：<c>--</c> 行注释、以 <c>;</c> 结尾的语句、
/// SQL Server 的 <c>go</c> 批分隔符，以及脚本顶部的「建库 / 选库」引导段。
/// 刻意<b>不</b>做通用 SQL 解析（不需要存储过程 / 触发器 / <c>DELIMITER</c>）——
/// 一旦脚本复杂度上升，应换成 DB 迁移库而不是继续加正则。
/// </para>
/// <para>
/// 引导段（<see cref="BootstrapBeginMarker"/> 与 <see cref="BootstrapEndMarker"/> 之间）默认跳过：
/// 工具执行时库由连接串决定，脚本里的 <c>create database</c> / <c>use</c> 会把会话切到另一个库。
/// 只有手工用 mysql 客户端 / SSMS 整份执行时，那一段才需要生效。
/// </para>
/// </summary>
public static class SqlScriptReader
{
    /// <summary>引导段开始标记（生成器与执行器共用）。</summary>
    public const string BootstrapBeginMarker = "-- @@bootstrap-begin";

    /// <summary>引导段结束标记（生成器与执行器共用）。</summary>
    public const string BootstrapEndMarker = "-- @@bootstrap-end";

    /// <summary>把脚本正文切分为可逐条执行的语句。</summary>
    /// <param name="script">脚本正文。</param>
    /// <param name="skipBootstrapBlock">是否跳过建库 / 选库引导段（默认跳过）。</param>
    public static IReadOnlyList<string> ReadStatements(string script, bool skipBootstrapBlock = true)
    {
        ArgumentNullException.ThrowIfNull(script);

        var statements = new List<string>();
        var buffer = new StringBuilder();
        var inBootstrap = false;

        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (string.Equals(trimmed, BootstrapBeginMarker, StringComparison.Ordinal))
            {
                inBootstrap = true;
                continue;
            }

            if (string.Equals(trimmed, BootstrapEndMarker, StringComparison.Ordinal))
            {
                inBootstrap = false;
                continue;
            }

            if (inBootstrap && skipBootstrapBlock)
            {
                continue;
            }

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

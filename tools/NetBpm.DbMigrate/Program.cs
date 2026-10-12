using System.Data.Common;
using System.Text.RegularExpressions;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

// NetBpm 数据库运维工具（开发计划 §7.3）。
//
// 用法：
//   dotnet run --project tools/NetBpm.DbMigrate -- probe
//        --provider mysql --connection "Server=127.0.0.1;User ID=root;Password=;"
//
//   dotnet run --project tools/NetBpm.DbMigrate -- migrate --create-database
//        --provider mysql --connection "Server=127.0.0.1;Database=netbpm;User ID=root;Password=;"
//
//   dotnet run --project tools/NetBpm.DbMigrate -- check
//        --provider mysql --connection "Server=127.0.0.1;Database=netbpm;User ID=root;Password=;"
//
// 说明：不引入 DbUp 之类的迁移库 —— 脚本形态固定（建表 + 初值数据），
// 幂等性由「ACT_GE_PROPERTY 是否存在 + schema.version」判定（见 DatabaseMigrator）。

var options = CommandLineOptions.Parse(args);

if (options is null)
{
    CommandLineOptions.PrintUsage();
    return 2;
}

return options.Command switch
{
    "probe" => Probe(options),
    "check" => Check(options),
    "purge" => Purge(options),
    "migrate" => Migrate(options),
    _ => Unknown(options.Command),
};

static int Purge(CommandLineOptions options)
{
    Console.WriteLine("⚠️ 即将清空引擎表的数据（保留表结构，不动 ACT_GE_PROPERTY）。");

    var sessionFactory = BuildSessionFactory(options);

    using var session = sessionFactory.Create();
    EngineTableCleaner.Purge(session);

    Console.WriteLine($"已清空：{string.Join(", ", EngineTableCleaner.OrderedTables)}");
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"未知命令：{command}");
    CommandLineOptions.PrintUsage();
    return 2;
}

static int Probe(CommandLineOptions options)
{
    var serverConnectionString = StripDatabase(options.ConnectionString, out _);

    using var connection = new DbConnectionFactory(options.DatabaseType, serverConnectionString).CreateConnection();
    using var command = connection.CreateCommand();
    command.CommandText = options.DatabaseType == DatabaseType.MySql
        ? "select schema_name from information_schema.schemata order by schema_name"
        : "select name from sys.databases order by name";

    using var reader = command.ExecuteReader();

    var names = new List<string>();
    while (reader.Read())
    {
        names.Add(reader.GetString(0));
    }

    Console.WriteLine($"连接成功（{options.DatabaseType}）。");
    Console.WriteLine($"可访问的库：{string.Join(", ", names)}");

    return 0;
}

static int Check(CommandLineOptions options)
{
    var sessionFactory = BuildSessionFactory(options);
    var checker = new DatabaseSchemaChecker(
        new DapperPropertyStore(new CommandScopedDbSessionProvider(sessionFactory)));

    checker.Check();

    Console.WriteLine($"结构版本校验通过：schema.version = {DatabaseSchemaChecker.ExpectedSchemaVersion}");
    return 0;
}

static int Migrate(CommandLineOptions options)
{
    if (options.CreateDatabase)
    {
        if (options.DatabaseName is null)
        {
            Console.Error.WriteLine("--create-database 要求连接字符串里指定 Database。");
            return 2;
        }

        CreateDatabase(options);
    }

    var sessionFactory = BuildSessionFactory(options);
    var scriptDirectory = options.ScriptDirectory ?? DatabaseScripts.ResolveDirectory(options.DatabaseType);

    Console.WriteLine($"脚本目录：{scriptDirectory}");

    var result = new DatabaseMigrator(sessionFactory, scriptDirectory).Migrate();

    Console.WriteLine(result.AlreadyInitialized
        ? "数据库已初始化，跳过建表脚本。"
        : $"已执行脚本：{string.Join(", ", result.AppliedScripts)}");

    var checker = new DatabaseSchemaChecker(
        new DapperPropertyStore(new CommandScopedDbSessionProvider(sessionFactory)));
    checker.Check();
    Console.WriteLine($"结构版本校验通过：schema.version = {DatabaseSchemaChecker.ExpectedSchemaVersion}");

    return 0;
}

static void CreateDatabase(CommandLineOptions options)
{
    var databaseName = options.DatabaseName!;

    // 库名来自运维自己的连接串，仍做白名单校验以免拼接出可执行 SQL
    if (!Regex.IsMatch(databaseName, "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant))
    {
        throw new InvalidOperationException($"库名 '{databaseName}' 含非法字符（只允许字母、数字、下划线）。");
    }

    var serverConnectionString = StripDatabase(options.ConnectionString, out _);

    using var connection = new DbConnectionFactory(options.DatabaseType, serverConnectionString).CreateConnection();
    using var command = connection.CreateCommand();

    command.CommandText = options.DatabaseType switch
    {
        // 与 db/mysql 脚本保持同一字符集/排序规则
        DatabaseType.MySql =>
            $"create database if not exists `{databaseName}` character set utf8 collate utf8_bin",

        DatabaseType.SqlServer =>
            $"if db_id('{databaseName}') is null create database [{databaseName}]",

        _ => throw new NotSupportedException($"不支持的数据库方言：{options.DatabaseType}"),
    };

    command.ExecuteNonQuery();
    Console.WriteLine($"数据库 '{databaseName}' 已就绪。");
}

static IDbSessionFactory BuildSessionFactory(CommandLineOptions options) =>
    new DbSessionFactory(new DbConnectionFactory(options.DatabaseType, options.ConnectionString));

static string StripDatabase(string connectionString, out string? databaseName)
{
    var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
    databaseName = null;

    foreach (var key in new[] { "Database", "Initial Catalog" })
    {
        if (builder.TryGetValue(key, out var value) && value is not null)
        {
            databaseName = value.ToString();
            builder.Remove(key);
        }
    }

    return builder.ConnectionString;
}

internal sealed class CommandLineOptions
{
    public required string Command { get; init; }

    public required DatabaseType DatabaseType { get; init; }

    public required string ConnectionString { get; init; }

    public bool CreateDatabase { get; init; }

    public string? ScriptDirectory { get; init; }

    public string? DatabaseName { get; init; }

    public static CommandLineOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            var key = argument[2..];
            var separator = key.IndexOf('=', StringComparison.Ordinal);

            if (separator >= 0)
            {
                values[key[..separator]] = key[(separator + 1)..];
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[key] = args[++i];
                continue;
            }

            flags.Add(key);
        }

        if (!values.TryGetValue("provider", out var provider)
            || !Enum.TryParse<DatabaseType>(provider, ignoreCase: true, out var databaseType)
            || !values.TryGetValue("connection", out var connectionString)
            || string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        return new CommandLineOptions
        {
            Command = args[0],
            DatabaseType = databaseType,
            ConnectionString = connectionString,
            CreateDatabase = flags.Contains("create-database"),
            ScriptDirectory = values.GetValueOrDefault("script-dir"),
            DatabaseName = ExtractDatabaseName(connectionString),
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("用法：NetBpm.DbMigrate <probe|migrate|check|purge> --provider <mysql|sqlserver> --connection \"<连接字符串>\"");
        Console.WriteLine("命令：");
        Console.WriteLine("  probe    列出可访问的库");
        Console.WriteLine("  migrate  执行建表脚本并校验 schema.version");
        Console.WriteLine("  check    仅校验 schema.version");
        Console.WriteLine("  purge    清空引擎表数据（保留结构；勿在生产库执行）");
        Console.WriteLine("选项：");
        Console.WriteLine("  --create-database    migrate 前按需创建数据库（库名取自连接字符串的 Database）");
        Console.WriteLine("  --script-dir <目录>  指定脚本目录（默认自动定位 db/<dialect>）");
    }

    private static string? ExtractDatabaseName(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        foreach (var key in new[] { "Database", "Initial Catalog" })
        {
            if (builder.TryGetValue(key, out var value) && value is not null)
            {
                return value.ToString();
            }
        }

        return null;
    }
}

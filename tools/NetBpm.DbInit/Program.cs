using System.Data.Common;
using System.Text.RegularExpressions;
using NetBpm.DbInit.Schema;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

// NetBpm 数据库初始化器。
//
// 把「生成脚本」与「初始化结构 + 数据」合并为一步：
//   1. 从单一数据源（SchemaCatalog）生成 db/mysql 与 db/sqlserver 两套脚本；
//   2. 顺序执行对应方言脚本，初始化表结构 / 索引 / 外键与种子数据；
//   3. 校验 schema.version。
//
// 用法：
//   dotnet run --project tools/NetBpm.DbInit -- --provider mysql \
//       --connection "Server=127.0.0.1;Database=netbpm;User ID=root;Password=;" --create-database
//
//   # 只重新生成脚本（不连库）
//   dotnet run --project tools/NetBpm.DbInit -- --generate-only
//
// 说明：不引入 DbUp 之类的迁移库 —— 脚本形态固定（单个 netbpm_init.sql：建库 + 建表 + 初值），
// 幂等性由脚本内的方言守卫保证（create table if not exists / information_schema 判重），
// 因此可以重复执行：新库一次建成，老库只补缺失部分（见 DatabaseMigrator）。

var options = DbInitOptions.Parse(args);

if (options is null)
{
    DbInitOptions.PrintUsage();
    return 2;
}

var root = ResolveRepoRoot();
if (root is null)
{
    Console.Error.WriteLine("找不到仓库根目录（NetBpm.slnx）。请从仓库内运行。");
    return 2;
}

// 1. 从单一数据源生成脚本（两方言一并生成，保证脚本与代码一致）
SqlGenerator.Generate(root);
Console.WriteLine($"已生成脚本：{Path.Combine(root, "db", "mysql")}、{Path.Combine(root, "db", "sqlserver")}");

// 2. 只生成、不初始化
if (options.GenerateOnly)
{
    return 0;
}

// 3. 按需建库
if (options.CreateDatabase)
{
    if (options.DatabaseName is null)
    {
        Console.Error.WriteLine("--create-database 要求连接字符串里指定 Database。");
        return 2;
    }

    CreateDatabase(options);
}

// 4. 初始化结构 + 数据
var scriptDirectory = Path.Combine(root, "db", DatabaseScripts.DirectoryName(options.DatabaseType));
var sessionFactory = new DbSessionFactory(new DbConnectionFactory(options.DatabaseType, options.ConnectionString));

Console.WriteLine($"脚本目录：{scriptDirectory}");

var result = new DatabaseMigrator(sessionFactory, scriptDirectory).Migrate();

Console.WriteLine(result.AlreadyInitialized
    ? $"数据库已存在，已按幂等脚本补齐结构：{string.Join(", ", result.AppliedScripts)}"
    : $"已执行数据库初始化脚本：{string.Join(", ", result.AppliedScripts)}");

// 5. 校验结构版本
var checker = new DatabaseSchemaChecker(
    new DapperPropertyStore(new CommandScopedDbSessionProvider(sessionFactory)));
checker.Check();
Console.WriteLine($"结构版本校验通过：schema.version = {DatabaseSchemaChecker.ExpectedSchemaVersion}");

return 0;

static void CreateDatabase(DbInitOptions options)
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

static string? ResolveRepoRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "NetBpm.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return null;
}

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

/// <summary>
/// 命令行参数。
/// </summary>
/// <remarks>
/// 名字带前缀是刻意的：全局命名空间里的 <c>Options</c> 会和 <c>Microsoft.Extensions.Options.Options</c>
/// 撞车（本项目通过 <c>InternalsVisibleTo</c> 对单测可见，于是整个测试项目里的 <c>Options.Create(...)</c>
/// 都会解析到这个类型）。
/// </remarks>
internal sealed class DbInitOptions
{
    public required DatabaseType DatabaseType { get; init; }

    public required string ConnectionString { get; init; }

    public bool CreateDatabase { get; init; }

    public bool GenerateOnly { get; init; }

    public string? DatabaseName { get; init; }

    public static DbInitOptions? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
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

        // 默认：生成 + 初始化（需要 provider / connection）；
        // --generate-only：只生成（不连库，provider / connection 可选）。
        var generateOnly = flags.Contains("generate-only");

        var databaseType = DatabaseType.MySql;
        var connectionString = string.Empty;

        if (!generateOnly)
        {
            if (!values.TryGetValue("provider", out var provider)
                || !Enum.TryParse<DatabaseType>(provider, ignoreCase: true, out databaseType)
                || !values.TryGetValue("connection", out connectionString)
                || string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }
        }

        return new DbInitOptions
        {
            DatabaseType = databaseType,
            ConnectionString = connectionString,
            CreateDatabase = flags.Contains("create-database"),
            GenerateOnly = generateOnly,
            DatabaseName = connectionString.Length > 0 ? ExtractDatabaseName(connectionString) : null,
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("用法：NetBpm.DbInit --provider <mysql|sqlserver> --connection \"<连接字符串>\" [--create-database] [--generate-only]");
        Console.WriteLine("说明：从单一数据源生成脚本，并初始化数据库结构 + 种子数据（幂等）。");
        Console.WriteLine("选项：");
        Console.WriteLine("  --provider <mysql|sqlserver>  数据库方言");
        Console.WriteLine("  --connection \"<连接串>\"       数据库连接");
        Console.WriteLine("  --create-database             初始化前按需创建数据库（库名取自连接串 Database）");
        Console.WriteLine("  --generate-only               只生成 db/mysql 与 db/sqlserver 脚本，不连库");
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

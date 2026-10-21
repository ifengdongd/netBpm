using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using NetBpm.Persistence;
using NetBpm.Persistence.Admin;
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
//   dotnet run --project tools/NetBpm.DbMigrate -- users add --username admin --scopes netbpm.admin
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
    "apply" => Apply(options),
    "users" => Users(options).GetAwaiter().GetResult(),
    _ => Unknown(options.Command),
};

/// <summary>
/// 执行<b>单个</b> SQL 脚本（幂等性由脚本自己保证）。
/// </summary>
/// <remarks>
/// <c>migrate</c> 只管引擎表（库已初始化就整体跳过），业务侧的增量脚本
/// （如 0003/0004/0005 业务表）走这条命令按文件执行。
/// </remarks>
static int Apply(CommandLineOptions options)
{
    if (string.IsNullOrWhiteSpace(options.ScriptFile))
    {
        Console.Error.WriteLine("apply 命令需要 --script <sql文件路径>。");
        return 2;
    }

    if (!File.Exists(options.ScriptFile))
    {
        Console.Error.WriteLine($"脚本不存在：{options.ScriptFile}");
        return 2;
    }

    var statements = SqlScriptReader.ReadStatements(File.ReadAllText(options.ScriptFile));

    using var connection = new DbConnectionFactory(options.DatabaseType, options.ConnectionString).CreateConnection();

    foreach (var statement in statements)
    {
        using var command = connection.CreateCommand();
        command.CommandText = statement;
        command.ExecuteNonQuery();
    }

    Console.WriteLine($"已执行 {options.ScriptFile}（{statements.Count} 条语句）。");
    return 0;
}

/// <summary>
/// 控制台账号（<c>admin_user</c>）运维：建号、改口令、启停、列表。
/// 口令只以 PBKDF2 散列落库，命令行传的明文不会留在库里。
/// </summary>
static async Task<int> Users(CommandLineOptions options)
{
    var action = options.SubCommand;

    if (string.IsNullOrWhiteSpace(action))
    {
        Console.Error.WriteLine("users 需要子命令：add | list | reset-password | enable | disable");
        return 2;
    }

    var store = new DapperAdminUserStore(
        new DbConnectionFactory(options.DatabaseType, options.ConnectionString));

    return action.ToLowerInvariant() switch
    {
        "list" => await UsersList(store),
        "add" => await UsersAdd(options, store),
        "reset-password" => await UsersResetPassword(options, store),
        "enable" => await UsersSetEnabled(options, store, true),
        "disable" => await UsersSetEnabled(options, store, false),
        _ => UsersUnknown(action),
    };
}

static async Task<int> UsersList(IAdminUserStore store)
{
    var users = await store.ListAsync();

    if (users.Count == 0)
    {
        Console.WriteLine("（没有账号）");
        return 0;
    }

    Console.WriteLine($"{"登录名",-20}{"显示名",-16}{"范围",-28}{"租户",-12}{"状态",-8}上次登录");
    Console.WriteLine(new string('-', 100));

    foreach (var user in users)
    {
        Console.WriteLine(
            $"{Trim(user.Username, 20),-20}" +
            $"{Trim(user.DisplayName ?? "-", 16),-16}" +
            $"{Trim(user.Scopes.Count == 0 ? "-" : string.Join(' ', user.Scopes), 28),-28}" +
            $"{Trim(user.TenantId ?? "-", 12),-12}" +
            $"{Status(user),-8}" +
            $"{user.LastLoginAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-"}");
    }

    Console.WriteLine();
    Console.WriteLine($"共 {users.Count} 个账号。");
    return 0;

    static string Status(AdminUser user)
    {
        if (!user.Enabled)
        {
            return "停用";
        }

        return user.LockedUntil is { } until && until > DateTime.UtcNow
            ? $"锁定至{until.ToLocalTime():HH:mm}"
            : user.FailedAttempts > 0 ? $"启用({user.FailedAttempts}败)" : "启用";
    }

    static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}

static async Task<int> UsersAdd(CommandLineOptions options, IAdminUserStore store)
{
    var username = options.Username;

    if (string.IsNullOrWhiteSpace(username))
    {
        Console.Error.WriteLine("users add 需要 --username <登录名>。");
        return 2;
    }

    username = username.Trim();

    if (await store.FindByUsernameAsync(username) is not null)
    {
        Console.Error.WriteLine($"账号 '{username}' 已存在（改口令请用 reset-password）。");
        return 1;
    }

    var password = options.Password ?? ReadPassword("口令：");

    if (!PasswordHasher.IsStrongEnough(password))
    {
        Console.Error.WriteLine("口令至少 8 位。");
        return 2;
    }

    var scopes = ParseScopes(options.Scopes);
    var now = DateTime.UtcNow;

    var created = await store.CreateAsync(new AdminUser(
        Guid.NewGuid().ToString("N"),
        username,
        PasswordHasher.Hash(password),
        scopes,
        options.DisplayName,
        options.Tenant,
        true,
        0,
        null,
        null,
        now,
        now));

    if (!created)
    {
        Console.Error.WriteLine($"账号 '{username}' 创建失败（可能已存在）。");
        return 1;
    }

    Console.WriteLine($"已创建账号 '{username}'，范围：{(scopes.Count == 0 ? "（无）" : string.Join(' ', scopes))}");
    return 0;
}

static async Task<int> UsersResetPassword(CommandLineOptions options, IAdminUserStore store)
{
    var username = options.Username;

    if (string.IsNullOrWhiteSpace(username))
    {
        Console.Error.WriteLine("users reset-password 需要 --username <登录名>。");
        return 2;
    }

    var user = await store.FindByUsernameAsync(username.Trim());

    if (user is null)
    {
        Console.Error.WriteLine($"账号 '{username}' 不存在。");
        return 1;
    }

    var password = options.Password ?? ReadPassword("新口令：");

    if (!PasswordHasher.IsStrongEnough(password))
    {
        Console.Error.WriteLine("口令至少 8 位。");
        return 2;
    }

    await store.SetPasswordAsync(user.Id, PasswordHasher.Hash(password));

    Console.WriteLine($"已重置 '{user.Username}' 的口令（失败计数与锁定同时清零）。");
    return 0;
}

static async Task<int> UsersSetEnabled(CommandLineOptions options, IAdminUserStore store, bool enabled)
{
    var username = options.Username;

    if (string.IsNullOrWhiteSpace(username))
    {
        Console.Error.WriteLine($"users {(enabled ? "enable" : "disable")} 需要 --username <登录名>。");
        return 2;
    }

    var user = await store.FindByUsernameAsync(username.Trim());

    if (user is null)
    {
        Console.Error.WriteLine($"账号 '{username}' 不存在。");
        return 1;
    }

    await store.SetEnabledAsync(user.Id, enabled);

    Console.WriteLine($"账号 '{user.Username}' 已{(enabled ? "启用" : "停用")}。");
    return 0;
}

static int UsersUnknown(string action)
{
    Console.Error.WriteLine($"未知子命令：users {action}");
    Console.Error.WriteLine("可用：add | list | reset-password | enable | disable");
    return 2;
}

/// <summary>口令不在命令历史里最好；不传 --password 时交互式读取（不回显）。</summary>
static string ReadPassword(string prompt)
{
    Console.Write(prompt);

    var buffer = new System.Text.StringBuilder();

    while (true)
    {
        var key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Enter)
        {
            break;
        }

        if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
        {
            buffer.Length--;
            continue;
        }

        if (key.KeyChar is '\0')
        {
            continue;
        }

        buffer.Append(key.KeyChar);
    }

    Console.WriteLine();

    return buffer.ToString();
}

static IReadOnlyList<string> ParseScopes(string? scopes) =>
    string.IsNullOrWhiteSpace(scopes)
        ? []
        : scopes.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
        ? $"数据库已存在，已按幂等脚本补齐结构：{string.Join(", ", result.AppliedScripts)}"
        : $"已执行数据库初始化脚本：{string.Join(", ", result.AppliedScripts)}");

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
    public string? ScriptFile { get; init; }

    /// <summary>子命令（目前只有 <c>users</c> 用：add/list/reset-password/enable/disable）。</summary>
    public string? SubCommand { get; init; }

    /// <summary><c>users</c> 子命令的账号字段。</summary>
    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? DisplayName { get; init; }

    public string? Scopes { get; init; }

    public string? Tenant { get; init; }

    public static CommandLineOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // "users add ..."：第二个词是子命令（不以 -- 开头），之后才是选项。
        var startIndex = 1;
        string? subCommand = null;

        if (args.Length > 1
            && !args[1].StartsWith("--", StringComparison.Ordinal)
            && string.Equals(args[0], "users", StringComparison.OrdinalIgnoreCase))
        {
            subCommand = args[1];
            startIndex = 2;
        }

        for (var i = startIndex; i < args.Length; i++)
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
            ScriptFile = values.GetValueOrDefault("script"),
            SubCommand = subCommand,
            Username = values.GetValueOrDefault("username"),
            Password = values.GetValueOrDefault("password"),
            DisplayName = values.GetValueOrDefault("display-name"),
            Scopes = values.GetValueOrDefault("scopes"),
            Tenant = values.GetValueOrDefault("tenant"),
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("用法：NetBpm.DbMigrate <probe|migrate|check|purge|apply|users> --provider <mysql|sqlserver> --connection \"<连接字符串>\"");
        Console.WriteLine("命令：");
        Console.WriteLine("  probe    列出可访问的库");
        Console.WriteLine("  migrate  执行建表脚本并校验 schema.version");
        Console.WriteLine("  check    仅校验 schema.version");
        Console.WriteLine("  purge    清空引擎表数据（保留结构；勿在生产库执行）");
        Console.WriteLine("  apply    执行单个 SQL 脚本（--script <文件>；幂等性由脚本保证）");
        Console.WriteLine("  users    控制台账号（admin_user）：add|list|reset-password|enable|disable");
        Console.WriteLine("选项：");
        Console.WriteLine("  --create-database    migrate 前按需创建数据库（库名取自连接字符串的 Database）");
        Console.WriteLine("  --script-dir <目录>  指定脚本目录（默认自动定位 db/<dialect>）");
        Console.WriteLine("  --script <文件>      apply 命令的脚本文件路径");
        Console.WriteLine("  --username <登录名>  users 子命令的账号");
        Console.WriteLine("  --password <明文>    users add/reset-password 的口令（省略则交互式输入，不回显）");
        Console.WriteLine("  --display-name <名>  显示名");
        Console.WriteLine("  --scopes <范围>      空格分隔，如 \"netbpm.admin\"");
        Console.WriteLine("  --tenant <租户>      所属租户（为空表示不限租户）");
        Console.WriteLine();
        Console.WriteLine("示例：");
        Console.WriteLine("  ... users add --username admin --display-name 管理员 --scopes netbpm.admin");
        Console.WriteLine("  ... users list");

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

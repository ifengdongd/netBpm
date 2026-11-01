namespace NetBpm.SdkGen;

/// <summary>
/// 把 OpenAPI 快照变成 <c>sdk/</c> 目录下的产物。
/// </summary>
/// <remarks>
/// <para>
/// 生成是<b>纯函数</b>：同样的快照必然得到同样的字节（操作按路径排序、schema 按名称排序、
/// 行尾统一 CRLF）。因此 <c>SdkGenerationTests</c> 可以靠「重新生成一遍，和仓库里的文件比一次」
/// 守住产物——没有隐藏状态，也没有时钟。
/// </para>
/// <para>
/// 反过来说：<b>手改 <c>sdk/</c> 下的文件一定会在下一个用例里红</b>。要改 SDK 就改快照或改生成器。
/// </para>
/// </remarks>
public sealed class SdkGenerator
{
    private readonly ContractDocument _contract;

    public SdkGenerator(ContractDocument contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        _contract = contract;
    }

    /// <summary>快照里的业务面操作数（用于日志与自检）。</summary>
    public int OperationCount => _contract.Operations.Count;

    /// <summary>可达 schema 数（118 里的那 49 个）。</summary>
    public int SchemaCount => _contract.ReachableSchemaNames.Count;

    /// <summary>命名表未覆盖的操作（生成器会告警，提醒补表）。</summary>
    public IEnumerable<ContractOperation> UnnamedOperations =>
        _contract.Operations.Where(operation => !OperationNaming.IsExplicitlyNamed(operation));

    /// <summary>从仓库根读快照并生成全部产物。</summary>
    public static IReadOnlyList<GeneratedFile> Generate(string repositoryRoot) =>
        new SdkGenerator(ContractDocument.Load(ContractPath(repositoryRoot))).Generate();

    /// <summary>快照路径（仓库根下的固定位置）。</summary>
    public static string ContractPath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "contracts", "openapi", "workflow-v1.json");

    /// <summary>生成全部文件（顺序稳定，便于比对日志）。</summary>
    public IReadOnlyList<GeneratedFile> Generate()
    {
        var files = new List<GeneratedFile>
        {
            new("sdk/README.md", Readme()),
        };

        files.AddRange(new TypeScriptEmitter(_contract).Emit());
        files.AddRange(new JavaEmitter(_contract).Emit());

        return files;
    }

    /// <summary>落盘；返回实际发生变化的文件（内容相同的跳过，避免无意义的 mtime 抖动）。</summary>
    public static IReadOnlyList<string> Write(string repositoryRoot, IReadOnlyList<GeneratedFile> files)
    {
        var changed = new List<string>();

        foreach (var file in files)
        {
            if (GeneratedText.WriteIfChanged(file.Resolve(repositoryRoot), file.Content))
            {
                changed.Add(file.RelativePath);
            }
        }

        return changed;
    }

    /// <summary>从运行目录向上找到仓库根（含 <c>NetBpm.slnx</c> 的那一层）。</summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetBpm.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"无法从 '{AppContext.BaseDirectory}' 向上找到 NetBpm.slnx（仓库根目录）。");
    }

    private string Readme() => $$"""
    # sdk/ —— 官方 SDK 与跨语言模型

    这里的文件**全部由生成器产出**，唯一的真源是 `contracts/openapi/workflow-v1.json`。
    手改任何一个文件，`SdkGenerationTests` 都会红。

    ```bash
    dotnet run --project tools/NetBpm.SdkGen
    ```

    ```
    sdk/
    ├── README.md                     # 本文件（同样由生成器写，改它要改 tools/NetBpm.SdkGen）
    ├── typescript/                   # 模型 + 客户端
    │   ├── package.json / tsconfig.json
    │   └── src/{models.ts,client.ts,index.ts}
    └── java/                         # 只有模型（record / enum）
        ├── pom.xml
        └── src/main/java/cn/netbpm/platform/model/*.java
    ```

    当前产物来自 **{{_contract.Operations.Count}} 个业务面操作**，
    覆盖 **{{_contract.ReachableSchemaNames.Count}} 个可达 schema**（快照里另有大量 `/api/**`、`/admin/v1` 的类型，不在其中）。

    ## 一、为什么 .NET 是手写、TS/Java 是生成

    | | 做法 | 原因 |
    |---|---|---|
    | .NET | `src/NetBpm.Platform.Client`，手写一层 transport + 资源客户端 | 它直接引用 `NetBpm.Platform.Contracts`（生成 OpenAPI 快照的同一份类型）。再生成一遍 .NET DTO 就等于把真源分成两份，早晚分叉 |
    | TypeScript / Java | 从快照生成 | 这两个栈没有可复用的类型定义，手抄字段名是纯损耗：抄错一个字段就是静默丢字段 |

    三个 SDK 的资源分组与方法名刻意保持一致（`instances` / `processes` / `tasks`，
    `list` / `get` / `start` / `submit` / `terminate` / `claim` / `unclaim` / `transfer`），
    这样跨栈对照文档时不会多一层翻译。

    ## 二、生成规则（评审时看这一节）

    | 快照里的写法 | TS | Java | 说明 |
    |---|---|---|---|
    | `{"type":["null","string"]}` | `string \| null` | `String` | 可空靠联合类型 / 包装类型表达 |
    | `{"type":"integer","format":"int64"}` | `number` | `Long` | 见下一条 |
    | `{"type":["integer","string"]}` | `number` | `Long`/`Integer` | 这是服务端 `AllowReadingFromString` 的产物：**线上恒为数字**，字符串只是入参被额外放宽。生成 `number \| string` 会让每个调用点都写 `typeof` |
    | `{"type":"string","format":"date-time"}` | `string` | `String` | 保留下发的 ISO-8601 原文；Java 不映射 `OffsetDateTime`，避免强制使用方注册 `jackson-datatype-jsr310` |
    | `{"type":"object","additionalProperties":...}` | `{ [key: string]: T }` | `Map<String, T>` | 字典 |
    | `{"enum":[...]}` | 联合类型 + `values` 常量 + `parseX()` | `enum` + `UNKNOWN` | 见第三条 |
    | 不在 `required` 里 | `field?: T` | 包装类型（天然可空） | |
    | 未知 / 空 schema | `unknown` | `Object` | 逃逸口，宁可松也不猜 |

    ## 三、两条「必须容忍」的约定

    契约治理（`contracts/README.md` §四）允许服务端**新增字段**与**新增枚举取值**，
    因此生成的客户端必须容忍两者，否则一次兼容升级就会让老客户端崩：

    - **未知字段**：Java 每个 record 都带 `@JsonIgnoreProperties(ignoreUnknown = true)`；
      TS 天然容忍；.NET 的 `System.Text.Json` 默认忽略。
    - **未知枚举取值**：Java 枚举末尾有 `@JsonEnumDefaultValue UNKNOWN`，
      使用方需开启 `DeserializationFeature.READ_UNKNOWN_ENUM_VALUES_USING_DEFAULT_VALUE`；
      TS 用 `parseX()` 兜底后再 `switch`，别直接 `as`。

    ## 四、怎么校验

    ```bash
    # 1. 产物与快照一致（不需要数据库，CI 的 contract-governance job 会跑）
    dotnet run --project tests/NetBpm.UnitTests -- -class NetBpm.UnitTests.SdkGenerationTests

    # 2. TypeScript 能否通过类型检查
    cd sdk/typescript && npm install && npm run typecheck

    # 3. Java 能否编译
    cd sdk/java && mvn -q -o compile
    ```

    第 2、3 步不进 CI：它们验证的是「生成的语法有没有写错」，属于生成器自身的问题，
    本地改生成器时跑一次即可；CI 守的是「产物有没有跟快照脱节」，那才是会随时间腐坏的东西。

    ## 五、兼容性怎么保证

    `tests/NetBpm.IntegrationTests/CompatibilityTests` 拿**上一版基线**（`contracts/baseline/`）
    打**当前服务**：基线里的每条路径必须还在，返回体的结构必须仍符合基线 schema。
    快照管「声明变了没」，它管「声明没变但行为变了没」——两边合起来才是完整的兼容性判据。
    """;
}

/// <summary>命令行入口：把生成结果写到仓库里，并打印一份摘要。</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
        var root = ReadOption(args, "--root") ?? SdkGenerator.FindRepositoryRoot();

        var contractPath = SdkGenerator.ContractPath(root);
        var generator = new SdkGenerator(ContractDocument.Load(contractPath));
        var files = generator.Generate();

        foreach (var operation in generator.UnnamedOperations)
        {
            Console.WriteLine(
                $"warn: 操作未在命名表中登记（已按路径推导方法名，建议补 OperationNaming.Table）："
                + $"{operation.Method} {operation.Path}");
        }

        if (dryRun)
        {
            Console.WriteLine($"[dry-run] 将生成 {files.Count} 个文件（{generator.OperationCount} 个操作 / "
                + $"{generator.SchemaCount} 个可达 schema）：");

            foreach (var file in files)
            {
                Console.WriteLine("  " + file.RelativePath);
            }

            return 0;
        }

        var changed = SdkGenerator.Write(root, files);

        Console.WriteLine($"快照：{contractPath}");
        Console.WriteLine($"操作：{generator.OperationCount} 个；可达 schema：{generator.SchemaCount} 个；产物：{files.Count} 个文件");

        if (changed.Count == 0)
        {
            Console.WriteLine("产物已是最新（无文件改动）。");

            return 0;
        }

        Console.WriteLine($"已更新 {changed.Count} 个文件：");

        foreach (var path in changed)
        {
            Console.WriteLine("  " + path);
        }

        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

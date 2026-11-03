namespace NetBpm.ServerTaskWorker;

/// <summary>
/// 启动期直接打到控制台的提示。
/// </summary>
/// <remarks>
/// 不用 <c>ILogger</c>：这些消息要在宿主起来之前 / 起不来的时候也看得见，
/// 而且从 IDE 按「运行」时，控制台是最先被看到的那个窗口。
/// </remarks>
internal static class StartupMessages
{
    /// <summary>没配客户端密钥时的降级说明。</summary>
    /// <param name="options">用于打印实际监听地址。</param>
    public static void PrintMissingClientKey(WorkerOptions options)
    {
        const string Rule = "============================================================";

        Console.WriteLine(Rule);
        Console.WriteLine("[worker 未启动] 没有配置客户端密钥 —— 进程照常运行，但不去引擎领任务");
        Console.WriteLine($"  业务接口仍然可用：{options.ListenUrl}/api/serverTask/{{name}}");
        Console.WriteLine($"  不会访问引擎：{options.NormalizedBaseUrl}（配好密钥后重启即开始领取）");
        Console.WriteLine();
        Console.WriteLine("  配置方式（任选其一，改完重启进程）：");
        Console.WriteLine("    1) 环境变量    NETBPM_CLIENT_KEY=nbc.{clientId}.{secret}");
        Console.WriteLine("    2) 用户机密    dotnet user-secrets set \"Worker:ClientKey\" \"nbc.{clientId}.{secret}\" --project samples/NetBpm.ServerTaskWorker");
        Console.WriteLine("    3) appsettings.Development.json 里 \"Worker\": { \"ClientKey\": \"nbc.{clientId}.{secret}\" }");
        Console.WriteLine();
        Console.WriteLine("  密钥在控制台「接入管理 → 客户端」签发，整串只显示一次，别自己拼 nbc. 前缀。");
        Console.WriteLine("  详见 samples/NetBpm.ServerTaskWorker/README.md");
        Console.WriteLine(Rule);
    }
}

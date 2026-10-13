using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 启动即部署：把设计器示例里的请假流程灌进引擎，Demo 打开就能跑。
/// </summary>
internal static class LeaveProcessBootstrapper
{
    /// <summary>流程 key（= BPMN 里 <c>process/@id</c>）。</summary>
    internal const string ProcessKey = "leave_process";

    /// <summary>资源名，同时也是链接进输出目录的文件名。</summary>
    internal const string ResourceName = "leave-process.bpmn20.xml";

    /// <summary>
    /// 部署流程定义，返回流程定义 id。
    /// <para>
    /// ⚠️ 每次启动都会产生一个新部署（内存宿主下版本恒为 1；接数据库后版本会递增）。
    /// 这是有意为之：改了 <c>leave-process.bpmn20.xml</c> 重启就生效，不用先清库。
    /// </para>
    /// </summary>
    internal static async Task<string> DeployAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        // 流程定义的单一来源：web/designer/examples/leave-process.bpmn20.xml
        // csproj 用 Link 把它拷到输出目录的 processes/ 下（见 NetBpm.Demo.LeaveApproval.csproj）
        var path = Path.Combine(AppContext.BaseDirectory, "processes", ResourceName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到流程定义文件 '{path}'。请重新构建（csproj 里把它以 Link 方式拷到 processes/ 下）。",
                path);
        }

        var engine = services.GetRequiredService<IProcessEngine>();

        await using var stream = File.OpenRead(path);

        var definition = await engine
            .DeployAsync(stream, ResourceName, "请假审批 Demo", ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "已部署流程定义 {ProcessDefinitionId}（key={Key}，版本 {Version}，来源 {Path}）",
            definition.Id, definition.Key, definition.Version, path);

        // 部署成功后立刻自检：BPMN 里的 approvalResult 选项与 C# 枚举必须一致（见 ApprovalResultFormGuard）
        await ApprovalResultFormGuard.VerifyAsync(engine, definition.Id, logger, ct).ConfigureAwait(false);

        // 再自检：每个服务任务的 class / delegateExpression 都能解析成已注册的委托
        //（见 ServiceTaskDelegateGuard）—— 名字写错、忘了注册都要在启动期暴露，别等流程跑到那儿
        await ServiceTaskDelegateGuard.VerifyAsync(services, engine, definition.Id, logger, ct).ConfigureAwait(false);

        // 部署完才有「流程定义 id」和「环节名 → 环节 id」映射，这里填进运行时上下文（见 LeaveRuntime）。
        // 业务表（deal_history / instance_current_task）只记环节名，而引擎侧对的是环节 id，
        // 落库与读库两头都要用这张对照表，所以必须在接受请求之前就装好。
        var runtime = services.GetRequiredService<LeaveRuntime>();

        await runtime.InitializeAsync(engine, definition.Id, logger, ct).ConfigureAwait(false);

        return definition.Id;
    }
}

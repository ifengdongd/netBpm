using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// <see cref="WorkflowProcessBootstrapper"/> 的部署参数。
/// </summary>
public sealed class WorkflowDeployOptions
{
    /// <summary>
    /// BPMN 资源文件相对 <c>AppContext.BaseDirectory</c> 的路径（如 <c>processes/leave.bpmn20.xml</c>）。
    /// </summary>
    public required string ResourcePath { get; init; }

    /// <summary>部署名（展示用，如「请假审批」）。</summary>
    public string DeploymentName { get; init; } = "NetBpm 流程定义";

    /// <summary>
    /// 是否在部署成功后填充 <see cref="WorkflowRuntimeContext"/>（默认 <see langword="true"/>；
    /// 宿主必须先注册它，<see cref="WorkflowServiceCollectionExtensions.AddNetBpmWorkflow{TDataStore}"/> 已包含）。
    /// </summary>
    public bool InitializeRuntimeContext { get; init; } = true;

    /// <summary>
    /// 部署成功后的自检钩子（可选）：流程定义级守卫在这里跑，任何一条不过都让宿主<b>起不来</b>。
    /// </summary>
    /// <remarks>
    /// 典型用法：审批选项与业务枚举一致性自检（<c>ApprovalResultFormGuard</c>）。
    /// <para>
    /// 服务任务委托预解析（<c>ServiceTaskDelegateGuard</c>）<b>不需要</b>在这里再写一遍 ——
    /// 引导器默认就会跑（见 <see cref="SkipServiceTaskDelegateCheck"/>）。
    /// </para>
    /// </remarks>
    public Func<IServiceProvider, IProcessEngine, string, ILogger, CancellationToken, ValueTask>? VerifyAsync { get; init; }

    /// <summary>
    /// 跳过默认的服务任务委托自检（默认 <see langword="false"/>，即默认开启）。
    /// </summary>
    /// <remarks>
    /// 自检会借用引擎自己的解析器，把 BPMN 里每个 <c>class</c> / <c>delegateExpression</c>
    /// 以及各类监听器逐个预解析一遍：解析不到就让宿主起不来，而不是等流程跑到该节点才炸。
    /// 只有「委托在运行时才动态注册」这种非常规宿主才需要关掉它。
    /// </remarks>
    public bool SkipServiceTaskDelegateCheck { get; init; }
}

/// <summary>
/// 启动即部署的公共引导器：把 BPMN 资源文件灌进引擎，供宿主在 <c>app.Run()</c> 之前调用。
/// </summary>
/// <remarks>
/// <para>
/// 职责：找到资源文件 → 部署 → 跑自检（宿主钩子 + 默认的委托 / 候选组守卫）→ 填充
/// <see cref="WorkflowRuntimeContext"/>。流程相关的业务语义（守卫、运行期数据）全部通过
/// <see cref="WorkflowDeployOptions"/> 注入，本类不认识任何具体流程。
/// </para>
/// <para>
/// ⚠️ 每次启动都会产生一个新部署（内存宿主下版本恒为 1；接数据库后版本会递增）。
/// 这是有意为之：改了 BPMN 重启就生效，不用先清库。
/// </para>
/// </remarks>
public static class WorkflowProcessBootstrapper
{
    /// <summary>
    /// 部署 <paramref name="options"/> 指定的 BPMN 资源，返回流程定义 id。
    /// </summary>
    /// <param name="services">服务提供者（引擎门面、运行期上下文与自检钩子的依赖都从这里取）。</param>
    /// <param name="options">部署参数。</param>
    /// <param name="logger">日志。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>部署出来的流程定义 id。</returns>
    public static async Task<string> DeployAsync(
        IServiceProvider services,
        WorkflowDeployOptions options,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var path = Path.IsPathRooted(options.ResourcePath)
            ? options.ResourcePath
            : Path.Combine(AppContext.BaseDirectory, options.ResourcePath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到流程定义文件 '{path}'。请确认资源随宿主一起发布。", path);
        }

        var engine = services.GetRequiredService<IProcessEngine>();

        await using var stream = File.OpenRead(path);

        var definition = await engine
            .DeployAsync(stream, Path.GetFileName(path), options.DeploymentName, ct: ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "已部署流程定义 {ProcessDefinitionId}（key={Key}，版本 {Version}，来源 {Path}）",
            definition.Id, definition.Key, definition.Version, path);

        if (options.VerifyAsync is not null)
        {
            await options.VerifyAsync(services, engine, definition.Id, logger, ct).ConfigureAwait(false);
        }

        // 默认守卫 ①：委托预解析。放在宿主钩子之后是为了「业务一致性先报错」的优先级更直观，
        // 但无论宿主是否提供了 VerifyAsync 都会跑 —— 忘了调用是集成期最常见的坑，
        // 代价是流程跑到该节点才炸（那时实例已经推进了一半）。
        if (!options.SkipServiceTaskDelegateCheck)
        {
            await ServiceTaskDelegateGuard.VerifyAsync(services, engine, definition.Id, logger, ct).ConfigureAwait(false);
        }

        // 默认守卫 ②：候选组自检（只告警）。IUserGroupManager 仍是空实现时，
        // 候选组会展开成 0 个人，流程卡在第一个 userTask 且日志无异常。
        await CandidateGroupGuard.WarnIfUnresolvableAsync(services, engine, definition.Id, logger, ct).ConfigureAwait(false);

        if (options.InitializeRuntimeContext)
        {
            // 部署完才有「流程定义 id」和「环节名 → 环节 id」映射，这里填进运行期上下文。
            // 业务表通常只记环节名，而引擎侧对的是环节 id，落库与读库两头都要用这张对照表，
            // 所以必须在接受请求之前就装好。
            var runtime = services.GetRequiredService<WorkflowRuntimeContext>();

            await runtime.InitializeAsync(engine, definition.Id, logger, ct).ConfigureAwait(false);
        }

        return definition.Id;
    }
}

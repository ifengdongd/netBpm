using Microsoft.Extensions.Logging;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Leave;

/// <summary>
/// 启动即部署：把设计器示例里的请假流程灌进引擎，Demo 打开就能跑。
/// </summary>
/// <remarks>
/// 部署动作本身是通用能力（<see cref="WorkflowProcessBootstrapper"/>），这里只剩
/// 请假业务的两个常量与两条部署期自检（守卫都是通用类，往钩子里塞即可）。
/// </remarks>
internal static class LeaveProcessBootstrapper
{
    /// <summary>流程 key（= BPMN 里 <c>process/@id</c>）。</summary>
    internal const string ProcessKey = "leave_process";

    /// <summary>资源名，同时也是链接进输出目录的文件名。</summary>
    internal const string ResourceName = "leave-process.bpmn20.xml";

    /// <summary>部署流程定义，返回流程定义 id（自检不过会让宿主起不来）。</summary>
    internal static Task<string> DeployAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct = default)
    {
        return WorkflowProcessBootstrapper.DeployAsync(
            services,
            new WorkflowDeployOptions
            {
                ResourcePath = Path.Combine("processes", ResourceName),
                DeploymentName = "请假审批 Demo",

                // 部署成功后立刻自检：
                // ① BPMN 里的 approvalResult 选项与 C# 枚举必须一致（见 ApprovalFormGuard）；
                // ② 每个服务任务的 class / delegateExpression 都能解析成已注册的委托。
                VerifyAsync = async (provider, engine, definitionId, deployLogger, token) =>
                {
                    await ApprovalFormGuard
                        .VerifyAsync(engine, definitionId, deployLogger, token)
                        .ConfigureAwait(false);

                    await ServiceTaskDelegateGuard
                        .VerifyAsync(provider, engine, definitionId, deployLogger, token)
                        .ConfigureAwait(false);
                },
            },
            logger,
            ct);
    }
}

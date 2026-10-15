using Microsoft.Extensions.Logging;
using NetBpm.Demo.Common;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Leave;

/// <summary>
/// 启动即部署：把设计器示例里的请假流程灌进引擎，Demo 打开就能跑。
/// </summary>
/// <remarks>
/// 部署动作本身是通用能力（<see cref="WorkflowProcessBootstrapper"/>），这里只剩
/// 两条部署期自检（守卫都是通用类，往钩子里塞即可）；常量统一定义在 <see cref="ConstField"/>。
/// </remarks>
internal static class LeaveProcessBootstrapper
{
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
                ResourcePath = Path.Combine(ConstField.ProcessResourceDirectory, ConstField.LeaveResourceName),
                DeploymentName = ConstField.LeaveDeploymentName,

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

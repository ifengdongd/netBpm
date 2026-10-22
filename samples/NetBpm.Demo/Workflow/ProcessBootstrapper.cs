using Microsoft.Extensions.Logging;
using NetBpm.Demo.Common;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Workflow;

/// <summary>
/// 启动即部署（两条流程共用）：把设计器导出的 BPMN 灌进引擎，Demo 打开就能跑。
/// </summary>
/// <remarks>
/// <para>
/// 部署动作本身是通用能力（<see cref="WorkflowProcessBootstrapper"/>），本类只剩
/// 「流程 key + 资源名 + 部署名」三个参数（见 <see cref="ConstField"/>）与一条业务流程自检：
/// 审批选项自检 —— BPMN 里的 approvalResult 选项必须与 C# 枚举一致（<see cref="ApprovalFormGuard"/>）。
/// </para>
/// <para>
/// 委托自检（<see cref="ServiceTaskDelegateGuard"/>）由引导器默认执行，不需要在这里重复挂；
/// 候选组空实现告警同理。
/// </para>
/// </remarks>
internal static class ProcessBootstrapper
{
    /// <summary>部署流程定义，返回流程定义 id（自检不过会让宿主起不来）。</summary>
    internal static Task<string> DeployAsync(
        IServiceProvider services,
        ILogger logger,
        string processKey,
        string resourceName,
        string deploymentName,
        CancellationToken ct = default)
    {
        return WorkflowProcessBootstrapper.DeployAsync(
            services,
            new WorkflowDeployOptions
            {
                ResourcePath = Path.Combine(ConstField.ProcessResourceDirectory, resourceName),
                DeploymentName = deploymentName,

                VerifyAsync = async (_, engine, definitionId, deployLogger, token) =>
                {
                    await ApprovalFormGuard
                        .VerifyAsync(engine, definitionId, deployLogger, token)
                        .ConfigureAwait(false);
                },
            },
            logger,
            ct);
    }
}

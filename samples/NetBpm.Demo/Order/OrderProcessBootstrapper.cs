using Microsoft.Extensions.Logging;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Order;

/// <summary>
/// 启动即部署：把订单审批流程灌进引擎，Demo 打开就能跑。
/// </summary>
/// <remarks>
/// <para>
/// 流程定义来自 <c>web/designer/examples/order-process.bpmn20.xml</c>（经 csproj 链接进输出目录）。
/// 设计器里画的订单流程（<c>?modelId=…</c>）导出 XML 后替换该文件即可，委托契约
/// <c>order.ApproveComplete</c> / <c>order.RefuseComplete</c> 保持不变。
/// </para>
/// <para>
/// 部署动作本身是通用能力（<see cref="WorkflowProcessBootstrapper"/>），这里只剩流程 key
/// 与部署期自检（守卫都是通用类，往钩子里塞即可）。
/// </para>
/// </remarks>
internal static class OrderProcessBootstrapper
{
    /// <summary>流程 key（= BPMN 里 <c>process/@id</c>）。</summary>
    internal const string ProcessKey = "order_process";

    /// <summary>资源名，同时也是链接进输出目录的文件名。</summary>
    internal const string ResourceName = "order-process.bpmn20.xml";

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
                DeploymentName = "订单审批 Demo",

                // 部署成功后立刻自检：BPMN 里的 approvalResult 选项与 C# 枚举必须一致；
                // 每个服务任务的 delegateExpression 都能解析成已注册的委托
                //（order.ApproveComplete / order.RefuseComplete 写错、忘注册都在启动期暴露）。
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

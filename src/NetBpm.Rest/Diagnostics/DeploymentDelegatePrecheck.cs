using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Validation;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Rest.Diagnostics;

/// <summary>
/// 部署后的委托自检：把「部署成功、但流程跑到某个节点才炸」变成部署响应里的明确提示。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServiceTaskDelegateGuard"/> 会借用引擎自己的解析器，把流程里的服务任务与
/// 各类监听器（执行 / 任务 / 事件）逐个预解析一遍；解析不到就抛
/// <see cref="InvalidOperationException"/> —— 那是「宿主起不来」级别的硬失败，
/// 而部署端点已经把定义落库了，硬失败会让调用方以为部署没成功。
/// </para>
/// <para>
/// 所以这里把硬失败转成<b>提示级校验项</b>随部署响应返回：调用方（控制台 / 设计器）
/// 能立刻看到「哪个委托没有实现」，而不是等实例跑到该节点才报错。
/// </para>
/// </remarks>
public static class DeploymentDelegatePrecheck
{
    /// <summary>自检规则名（与部署期其它校验规则同一命名风格）。</summary>
    public const string RuleName = "delegate-resolution";

    /// <summary>自检刚部署的流程定义；返回提示项（通过时返回 null）。</summary>
    public static async Task<IReadOnlyList<ValidationError>?> VerifyAsync(
        IServiceProvider services,
        IProcessEngine engine,
        string processDefinitionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await ServiceTaskDelegateGuard.VerifyAsync(services, engine, processDefinitionId, logger, ct)
                .ConfigureAwait(false);

            return null;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "委托自检未通过：流程定义 {ProcessDefinitionId} 里有委托无法解析（实例运行到该节点会失败）。",
                processDefinitionId);

            return new[]
            {
                new ValidationError(
                    RuleName,
                    processDefinitionId,
                    "delegateExpression",
                    exception.Message,
                    ValidationSeverity.Warning),
            };
        }
    }
}

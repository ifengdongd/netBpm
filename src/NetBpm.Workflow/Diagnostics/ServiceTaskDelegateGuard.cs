using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Behavior;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 部署后自检：流程定义里每个服务任务的 <c>class</c> / <c>delegateExpression</c>
/// 都必须能解析成已注册的 <see cref="IServiceTaskDelegate"/>。
/// </summary>
/// <remarks>
/// <para>
/// 部署期校验只保证「三个属性必填其一」（<c>required-attributes</c> 规则），
/// <b>不保证那个名字真的能解析出实现</b> —— 名字写错、忘了注册，都要等流程跑到该节点才炸，
/// 而那时流程已经推进了一半。
/// </para>
/// <para>
/// 所以部署完立刻借用引擎自己的解析器（<see cref="IActivityDelegateResolver"/>）逐个预解析一遍：
/// 解析不到就<b>让宿主起不来</b>，并把节点 id 与写错的名字一起报出来。
/// 顺带把「实际注册了哪些名字」打进日志，换个角度暴露「注册了但没人用」的委托。
/// </para>
/// </remarks>
public static class ServiceTaskDelegateGuard
{
    /// <summary>校验流程定义里服务任务的委托都能解析；解析不到抛 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="services">服务提供者（用来取引擎自己的解析器）。</param>
    /// <param name="engine">引擎门面。</param>
    /// <param name="processDefinitionId">刚部署的流程定义 id。</param>
    /// <param name="logger">日志。</param>
    /// <param name="ct">取消标记。</param>
    /// <exception cref="InvalidOperationException">存在解析不到（或解析成了非委托对象）的服务任务。</exception>
    public static async ValueTask VerifyAsync(
        IServiceProvider services,
        IProcessEngine engine,
        string processDefinitionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        // 借用引擎自己的解析器 —— 它怎么在运行时解析，这里就怎么预解析，语义不会漂。
        var resolver = services.GetRequiredService<IActivityDelegateResolver>();

        var model = await engine.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false);

        var serviceTasks = model?.GetMainProcess()?.FindFlowElementsOfType<ServiceTask>().ToArray() ?? [];

        var resolvable = new List<string>();
        var unresolved = new List<string>();

        foreach (var task in serviceTasks)
        {
            switch (task.ImplementationType)
            {
                case ServiceTaskImplementationType.Class:
                    Check(task, "activiti:class", task.ClassName, resolver.ResolveByClassName);
                    break;

                case ServiceTaskImplementationType.DelegateExpression:
                    Check(task, "activiti:delegateExpression", task.DelegateExpression, resolver.ResolveByExpressionName);
                    break;

                default:
                    // activiti:expression 是 EL 表达式，部署期无法求值（要等执行上下文）。
                    // 未指定实现已被部署期校验规则拒掉，这里不会出现。
                    break;
            }
        }

        if (unresolved.Count > 0)
        {
            throw new InvalidOperationException(
                $"流程定义 {processDefinitionId} 里有服务任务的委托无法解析：{string.Join("；", unresolved)}。"
                + "请检查 BPMN 里写的名字，以及宿主是否已注册该委托"
                + "（可用 [ServiceTaskName] 特性 + AddServiceTaskDelegates 扫描注册）。");
        }

        logger.LogInformation(
            "服务任务委托自检通过（{Count} 个）：{Resolved}",
            resolvable.Count,
            string.Join(" / ", resolvable));

        void Check(ServiceTask task, string attribute, string? name, Func<string, object?> resolve)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                unresolved.Add($"{task.Id}（{attribute} 为空）");
                return;
            }

            var target = resolve(name);

            if (target is IServiceTaskDelegate)
            {
                resolvable.Add($"{task.Id} → {name}");
            }
            else if (target is null)
            {
                unresolved.Add($"{task.Id}（{attribute}=\"{name}\" 解析不到）");
            }
            else
            {
                unresolved.Add(
                    $"{task.Id}（{attribute}=\"{name}\" 解析成了 {target.GetType().FullName}，不是 IServiceTaskDelegate）");
            }
        }
    }
}

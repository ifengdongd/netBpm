using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 一致性自检：流程定义里 <c>approvalResult</c> 的枚举选项，必须与 <see cref="ApprovalResult"/> 枚举一一对应。
/// </summary>
/// <remarks>
/// <para>
/// 这里存在<b>两处定义</b>且无法合并：选项的"真身"在 BPMN 里（前端照 <c>activiti:value</c> 渲染、
/// 网关条件按选项 id 判定），而宿主需要强类型视图（<see cref="ApprovalResult"/>）。
/// </para>
/// <para>
/// 漂移的表现非常隐蔽 —— 设计器里加了个选项，页面上能选，流程却走不到那个分支。
/// 与其等出事，不如部署完立刻对一遍：不一致就<b>让宿主起不来</b>，并指明该改哪个文件。
/// </para>
/// </remarks>
public static class ApprovalFormGuard
{
    /// <summary>校验流程定义里的选项与枚举一致；不一致抛 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="engine">引擎门面。</param>
    /// <param name="processDefinitionId">刚部署的流程定义 id。</param>
    /// <param name="logger">日志。</param>
    /// <param name="ct">取消标记。</param>
    /// <exception cref="InvalidOperationException">两处定义的选项不一致。</exception>
    public static async ValueTask VerifyAsync(
        IProcessEngine engine,
        string processDefinitionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        var model = await engine.GetBpmnModelAsync(processDefinitionId, ct).ConfigureAwait(false);

        var declared = (model?.GetMainProcess()?
                .FindFlowElementsOfType<UserTask>()
                .SelectMany(task => task.FormProperties)
                .Where(property => string.Equals(
                    property.Id, ApprovalResults.FormPropertyId, StringComparison.Ordinal))
                .SelectMany(property => property.Values)
                .Select(value => value.Id ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray())
            ?? [];

        var expected = ApprovalResults.All
            .Select(result => result.ToOptionId())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (!declared.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"流程定义里 {ApprovalResults.FormPropertyId} 的选项 [{string.Join(", ", declared)}] "
                + $"与 ApprovalResult 枚举 [{string.Join(", ", expected)}] 不一致。"
                + "请同步修改流程定义里的 activiti:value，或 ApprovalResult 枚举。");
        }

        logger.LogInformation(
            "{FormPropertyId} 选项自检通过：{Options}",
            ApprovalResults.FormPropertyId,
            string.Join(" / ", expected.Select(id =>
                $"{id} {ApprovalResults.Parse(id).ToDisplayName()}")));
    }
}

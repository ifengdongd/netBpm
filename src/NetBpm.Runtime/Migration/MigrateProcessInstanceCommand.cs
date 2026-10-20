using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Migration;
using NetBpm.Persistence.Deployment;
using NetBpm.Runtime.Migration;

namespace NetBpm.Runtime;

/// <summary>
/// 把运行中的流程实例迁移到同 key 的另一个流程定义（通常是新版本）。
/// <para>
/// 一个命令 = 一个事务：
/// </para>
/// <list type="number">
///   <item><description>重新计算迁移计划（校验与预演共用同一套规则，避免预览通过、执行失败）；</description></item>
///   <item><description>整棵执行树的 <c>PROC_DEF_ID_</c> 切到目标定义；</description></item>
///   <item><description>有待办的执行：取消旧待办（历史留删除原因）、作废旧定时器、收口旧活动行；</description></item>
///   <item><description>把执行挪到目标节点，按新定义重新进入（重建待办/办理人/表单/边界定时器）；</description></item>
///   <item><description>写一条迁移审计（源/目标定义、映射、原因、操作人）。</description></item>
/// </list>
/// <para>
/// ⚠️ 历史口径：<c>ACT_HI_PROCINST.PROC_DEF_ID_</c> 保持实例<b>启动时</b>的定义不变，
/// 迁移事实由 <c>ACT_HI_MIGRATION</c> 记录；迁移后新产生的历史行（活动/任务）落目标定义 id。
/// </para>
/// </summary>
public sealed class MigrateProcessInstanceCommand : ICommand<MigrationPlan>
{
    private readonly IProcessDefinitionStore _definitions;
    private readonly ProcessInstanceMigrationRequest _request;
    private readonly IMigrationLog? _migrationLog;

    public MigrateProcessInstanceCommand(
        IProcessDefinitionStore definitions,
        ProcessInstanceMigrationRequest request,
        IMigrationLog? migrationLog = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(request);

        _definitions = definitions;
        _request = request;
        _migrationLog = migrationLog;
    }

    public async ValueTask<MigrationPlan> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var taskManager = services.TaskManager
            ?? throw new EngineException("当前引擎没有配置 ITaskManager，无法迁移流程实例。");

        var planner = new ProcessInstanceMigrationPlanner(
            services.ExecutionManager,
            taskManager,
            _definitions);

        var details = await planner.BuildAsync(_request, ct).ConfigureAwait(false);
        var plan = details.Plan;
        var reason = string.IsNullOrWhiteSpace(_request.Reason)
            ? $"流程迁移 v{plan.SourceProcessDefinitionId} → v{plan.TargetProcessDefinitionId}"
            : _request.Reason.Trim();

        // 1. 整棵树切定义（含没有当前节点的作用域执行）
        foreach (var execution in details.AllExecutions)
        {
            execution.ProcessDefinitionId = plan.TargetProcessDefinitionId;
        }

        // 2. 旧待办 / 旧定时器 / 旧活动收口，并把有活动的执行挪到目标节点
        foreach (var move in details.Moves)
        {
            var execution = move.Execution;

            if (move.Task is { } task)
            {
                await taskManager.DeleteTaskAsync(task, reason, ct).ConfigureAwait(false);

                if (services.TimerJobManager is { } timerJobs)
                {
                    await timerJobs.DeleteByExecutionAsync(execution.Id, ct).ConfigureAwait(false);
                }

                if (execution.CurrentActivityId is not null)
                {
                    await services.HistoryManager
                        .RecordActivityEndQuietlyAsync(execution, reason, ct)
                        .ConfigureAwait(false);
                }

                execution.IsActive = true;
            }

            // CurrentFlowElement 的 setter 会同步 ACT_ID_/ACT_TYPE_
            execution.CurrentFlowElement = move.Target;
        }

        // 3. 全部落库后再统一重入目标节点（此时新待办才会引用目标定义 id）
        foreach (var execution in details.AllExecutions)
        {
            await services.ExecutionManager.UpdateAsync(execution, ct).ConfigureAwait(false);
        }

        // 4. 重入目标节点：重建待办 / 办理人 / 表单 / 边界定时器
        foreach (var move in details.Moves.Where(move => move.RecreateTask))
        {
            services.Agenda.PlanContinueProcessOperation(move.Execution);
        }

        // 5. 审计（与执行同一事务；命令失败一起回滚）
        if (_migrationLog is not null)
        {
            await _migrationLog.RecordAsync(
                new MigrationRecord(
                    services.IdGenerator.NextId(),
                    plan.ProcessInstanceId,
                    plan.SourceProcessDefinitionId,
                    plan.TargetProcessDefinitionId,
                    _request.NormalizedMappings(),
                    _request.Reason?.Trim(),
                    _request.UserId?.Trim(),
                    DateTime.UtcNow),
                ct).ConfigureAwait(false);
        }

        return plan;
    }
}

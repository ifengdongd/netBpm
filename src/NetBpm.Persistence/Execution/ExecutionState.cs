using System.Globalization;
using NetBpm.Engine.Execution;

namespace NetBpm.Persistence.Execution;

/// <summary>
/// <c>ACT_RU_EXECUTION</c> 一行的持久化状态。
/// <para>
/// 既是「行 → 实体」的映射结果，也是脏检查（flush 时判断是否需要 UPDATE）的基线 ——
/// 对应 Activiti <c>ExecutionEntityImpl</c> 的 <c>persistentState</c> 机制。
/// 用 <c>record</c> 是为了直接拿到值相等语义。
/// </para>
/// </summary>
internal sealed record ExecutionState(
    string Id,
    int Revision,
    string? ProcessInstanceId,
    string? BusinessKey,
    string? ProcessDefinitionId,
    string? ActivityId,
    bool IsActive,
    bool IsConcurrent,
    bool IsScope,
    bool IsEventScope,
    int SuspensionState,
    string? ParentId,
    string? SuperExecutionId,
    string? RootProcessInstanceId,
    string? Name,
    DateTime? StartTime,
    string? StartUserId)
{
    /// <summary>从实体快照出持久化状态（用于 INSERT / UPDATE 参数与脏检查）。</summary>
    public static ExecutionState From(IExecutionEntity execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return new ExecutionState(
            execution.Id,
            execution.Revision,
            execution.ProcessInstanceId,
            execution.BusinessKey,
            execution.ProcessDefinitionId,
            execution.CurrentActivityId,
            execution.IsActive,
            execution.IsConcurrent,
            execution.IsScope,
            execution.IsEventScope,
            execution.SuspensionState,
            execution.ParentId,
            execution.SuperExecutionId,
            execution.RootProcessInstanceId,
            execution is ExecutionEntity entity ? entity.Name : null,
            execution.StartTime,
            execution.StartUserId);
    }

    /// <summary>映射为执行实体（不含 <see cref="IExecutionEntity.Children"/>，由上层按需装配）。</summary>
    public ExecutionEntity ToEntity()
    {
        var execution = new ExecutionEntity
        {
            Id = Id,
            Revision = Revision,
            ProcessInstanceId = ProcessInstanceId,
            BusinessKey = BusinessKey,
            ProcessDefinitionId = ProcessDefinitionId,
            CurrentActivityId = ActivityId,
            IsActive = IsActive,
            IsConcurrent = IsConcurrent,
            IsScope = IsScope,
            IsEventScope = IsEventScope,
            SuspensionState = SuspensionState,
            ParentId = ParentId,
            SuperExecutionId = SuperExecutionId,
            RootProcessInstanceId = RootProcessInstanceId,
            Name = Name,
            StartTime = StartTime,
            StartUserId = StartUserId,
        };

        return execution;
    }
}

/// <summary><c>ACT_RU_EXECUTION</c> 的列名与取值。</summary>
internal static class ExecutionColumns
{
    /// <summary>与 Activiti <c>Execution.xml</c> 的 resultMap 一致的列清单。</summary>
    public const string All =
        "ID_, REV_, PROC_INST_ID_, BUSINESS_KEY_, PROC_DEF_ID_, ACT_ID_, IS_ACTIVE_, IS_CONCURRENT_, IS_SCOPE_, " +
        "IS_EVENT_SCOPE_, IS_MI_ROOT_, PARENT_ID_, SUPER_EXEC_, ROOT_PROC_INST_ID_, SUSPENSION_STATE_, TENANT_ID_, " +
        "NAME_, START_TIME_, START_USER_ID_, IS_COUNT_ENABLED_, EVT_SUBSCR_COUNT_, TASK_COUNT_, JOB_COUNT_, " +
        "TIMER_JOB_COUNT_, SUSP_JOB_COUNT_, DEADLETTER_JOB_COUNT_, VAR_COUNT_, ID_LINK_COUNT_, APP_VERSION_";

    /// <summary>把一行结果集映射为持久化状态。</summary>
    public static ExecutionState Read(IDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ExecutionState(
            Id: Text(row, "ID_") ?? throw new Engine.EngineException("ACT_RU_EXECUTION 行缺少 ID_。"),
            Revision: Number(row, "REV_"),
            ProcessInstanceId: Text(row, "PROC_INST_ID_"),
            BusinessKey: Text(row, "BUSINESS_KEY_"),
            ProcessDefinitionId: Text(row, "PROC_DEF_ID_"),
            ActivityId: Text(row, "ACT_ID_"),
            IsActive: Flag(row, "IS_ACTIVE_"),
            IsConcurrent: Flag(row, "IS_CONCURRENT_"),
            IsScope: Flag(row, "IS_SCOPE_"),
            IsEventScope: Flag(row, "IS_EVENT_SCOPE_"),
            SuspensionState: Number(row, "SUSPENSION_STATE_"),
            ParentId: Text(row, "PARENT_ID_"),
            SuperExecutionId: Text(row, "SUPER_EXEC_"),
            RootProcessInstanceId: Text(row, "ROOT_PROC_INST_ID_"),
            Name: Text(row, "NAME_"),
            StartTime: Time(row, "START_TIME_"),
            StartUserId: Text(row, "START_USER_ID_"));
    }

    private static object? Raw(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value : null;

    private static string? Text(IDictionary<string, object?> row, string column) =>
        Raw(row, column) as string;

    private static int Number(IDictionary<string, object?> row, string column)
    {
        var value = Raw(row, column);
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 读取 TINYINT 标志位。
    /// <para>
    /// 显式走数值转换而不是让 Dapper 直接映射 <c>bool</c>：SQL Server 的 <c>bit</c>、
    /// MySQL 的 <c>TINYINT</c> 被驱动返回的类型在两种方言下不同（<c>bool</c> / <c>sbyte</c> / <c>byte</c>），
    /// 统一转成数值再判非零最稳。
    /// </para>
    /// </summary>
    private static bool Flag(IDictionary<string, object?> row, string column) => Number(row, column) != 0;

    private static DateTime? Time(IDictionary<string, object?> row, string column) =>
        Raw(row, column) as DateTime?;
}

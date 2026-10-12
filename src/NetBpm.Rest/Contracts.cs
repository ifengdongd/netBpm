using System.Text.Json;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine.History;

namespace NetBpm.Rest;

/// <summary>部署流程定义的响应。</summary>
/// <param name="ProcessDefinitionId">流程定义 id（<c>{key}:{version}:{idGeneratorId}</c>）。</param>
/// <param name="ProcessDefinitionKey">流程 key。</param>
/// <param name="Version">版本号。</param>
/// <param name="DeploymentId">部署 id。</param>
/// <param name="ResourceName">资源名。</param>
public sealed record DeployResponse(
    string ProcessDefinitionId,
    string ProcessDefinitionKey,
    int Version,
    string DeploymentId,
    string ResourceName);

/// <summary>启动流程实例的请求。</summary>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="StartUserId">发起人。</param>
/// <param name="Variables">初始变量。</param>
public sealed record StartProcessInstanceRequest(
    string ProcessDefinitionId,
    string? BusinessKey = null,
    string? StartUserId = null,
    IDictionary<string, JsonElement>? Variables = null);

/// <summary>启动流程实例的响应。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
public sealed record StartProcessInstanceResponse(string ProcessInstanceId);

/// <summary>完成任务请求。</summary>
/// <param name="Variables">随完成写入流程的变量（JSON 标量，见 <see cref="JsonVariableReader"/>）。</param>
public sealed record CompleteTaskRequest(IDictionary<string, JsonElement>? Variables = null);

/// <summary>认领任务请求。</summary>
/// <param name="UserId">认领人；为 <see langword="null"/> 表示取消认领。</param>
public sealed record ClaimTaskRequest(string? UserId = null);

/// <summary>
/// 历史活动 DTO（流程图着色用）。对应 <c>ACT_HI_ACTINST</c> 一行的前端视图。
/// </summary>
/// <param name="ActivityId">节点 id（与 BPMN 元素 id 对齐）。</param>
/// <param name="ActivityName">节点名。</param>
/// <param name="ActivityType">节点类型（如 <c>userTask</c>）。</param>
/// <param name="TaskId">用户任务 id（若为任务节点）。</param>
/// <param name="Assignee">办理人（若为任务节点）。</param>
/// <param name="StartTime">进入时间。</param>
/// <param name="EndTime">离开时间；为空表示正在该节点上等待。</param>
/// <param name="IsActive">是否正在执行（<c>EndTime is null</c>）。</param>
public sealed record HistoricActivityResponse(
    string ActivityId,
    string? ActivityName,
    string? ActivityType,
    string? TaskId,
    string? Assignee,
    DateTime? StartTime,
    DateTime? EndTime,
    bool IsActive)
{
    /// <summary>从历史活动映射。</summary>
    public static HistoricActivityResponse From(HistoricActivityInstance activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return new HistoricActivityResponse(
            activity.ActivityId,
            activity.ActivityName,
            activity.ActivityType,
            activity.TaskId,
            activity.Assignee,
            activity.StartTime,
            activity.EndTime,
            activity.IsActive);
    }
}

/// <summary>
/// 流程图数据 DTO（P9 交付项）。<see cref="Activities"/> 的 <c>EndTime</c> 为空即「正在该节点等待」。
/// </summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="BpmnXml">BPMN XML 原文（含 BPMNDI）。</param>
/// <param name="IsRunning">实例是否仍在运行。</param>
/// <param name="Activities">活动高亮序列。</param>
public sealed record DiagramResponse(
    string ProcessInstanceId,
    string ProcessDefinitionId,
    string? BpmnXml,
    bool IsRunning,
    IReadOnlyList<HistoricActivityResponse> Activities);

/// <summary>历史流程实例 DTO（P9「查历史」）。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="Name">流程实例名。</param>
/// <param name="StartUserId">发起人。</param>
/// <param name="StartActivityId">开始事件 id。</param>
/// <param name="EndActivityId">结束时所在节点 id。</param>
/// <param name="StartTime">启动时间。</param>
/// <param name="EndTime">结束时间；为空表示仍在运行。</param>
/// <param name="DurationInMillis">耗时（毫秒）。</param>
/// <param name="DeleteReason">删除原因；正常结束为 <see langword="null"/>。</param>
/// <param name="IsRunning">是否仍在运行。</param>
public sealed record HistoricProcessInstanceResponse(
    string ProcessInstanceId,
    string? BusinessKey,
    string ProcessDefinitionId,
    string? Name,
    string? StartUserId,
    string? StartActivityId,
    string? EndActivityId,
    DateTime? StartTime,
    DateTime? EndTime,
    long? DurationInMillis,
    string? DeleteReason,
    bool IsRunning)
{
    /// <summary>从历史流程实例映射。</summary>
    public static HistoricProcessInstanceResponse From(HistoricProcessInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new HistoricProcessInstanceResponse(
            instance.ProcessInstanceId,
            instance.BusinessKey,
            instance.ProcessDefinitionId,
            instance.Name,
            instance.StartUserId,
            instance.StartActivityId,
            instance.EndActivityId,
            instance.StartTime,
            instance.EndTime,
            instance.DurationInMillis,
            instance.DeleteReason,
            instance.IsRunning);
    }
}

/// <summary>历史任务 DTO（已办查询 / 审计）。</summary>
/// <param name="Id">任务 id。</param>
/// <param name="TaskDefinitionKey">BPMN 中 userTask 的 id。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="Name">任务名。</param>
/// <param name="Assignee">办理人。</param>
/// <param name="Owner">所有者。</param>
/// <param name="Priority">优先级。</param>
/// <param name="FormKey">表单 Key。</param>
/// <param name="StartTime">创建时间。</param>
/// <param name="EndTime">结束时间；为空表示任务仍未完成。</param>
/// <param name="DurationInMillis">耗时（毫秒）。</param>
/// <param name="DeleteReason">删除原因。</param>
public sealed record HistoricTaskResponse(
    string Id,
    string? TaskDefinitionKey,
    string? ProcessInstanceId,
    string? Name,
    string? Assignee,
    string? Owner,
    int? Priority,
    string? FormKey,
    DateTime? StartTime,
    DateTime? EndTime,
    long? DurationInMillis,
    string? DeleteReason)
{
    /// <summary>从历史任务映射。</summary>
    public static HistoricTaskResponse From(HistoricTaskInstance task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new HistoricTaskResponse(
            task.Id,
            task.TaskDefinitionKey,
            task.ProcessInstanceId,
            task.Name,
            task.Assignee,
            task.Owner,
            task.Priority,
            task.FormKey,
            task.StartTime,
            task.EndTime,
            task.DurationInMillis,
            task.DeleteReason);
    }
}

/// <summary>统一错误响应。</summary>
/// <param name="Message">错误说明。</param>
/// <param name="Errors">校验错误明细（仅校验失败时返回）。</param>
public sealed record ErrorResponse(string Message, IReadOnlyList<ValidationError>? Errors = null);

/// <summary>尚未实现的能力说明（对应开发计划阶段代号）。</summary>
/// <param name="Message">说明。</param>
/// <param name="PlanPhase">计划阶段。</param>
public sealed record NotImplementedResponse(string Message, string PlanPhase);

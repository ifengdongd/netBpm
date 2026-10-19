using System.Text.Json;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;

namespace NetBpm.Rest;

/// <summary>部署流程定义的响应。</summary>
/// <param name="ProcessDefinitionId">流程定义 id（<c>{key}:{version}:{idGeneratorId}</c>）。</param>
/// <param name="ProcessDefinitionKey">流程 key。</param>
/// <param name="Version">版本号。</param>
/// <param name="DeploymentId">部署 id。</param>
/// <param name="ResourceName">资源名。</param>
/// <param name="DeploymentName">部署名（<c>?deploymentName=</c> 未传时为空）。</param>
/// <param name="Warnings">
/// 不阻断部署的提示级校验结果（对应 Activiti <c>ValidationError.isWarning()</c>）。
/// 为空表示没有任何提示；有值时说明"部署成功，但建议检查"。
/// </param>
public sealed record DeployResponse(
    string ProcessDefinitionId,
    string ProcessDefinitionKey,
    int Version,
    string DeploymentId,
    string ResourceName,
    string? DeploymentName = null,
    IReadOnlyList<ValidationError>? Warnings = null);

/// <summary>
/// 已部署流程定义 DTO（流程部署列表）。
/// </summary>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="Key">流程 key。</param>
/// <param name="Name">流程名。</param>
/// <param name="Version">版本号。</param>
/// <param name="DeploymentId">部署 id。</param>
/// <param name="TenantId">租户 id（无租户为空串，与 Activiti 行级一致）。</param>
/// <param name="DeployTime">部署时间（UTC）。</param>
/// <param name="IsSuspended">是否已挂起（挂起后不能再启动新实例）。</param>
public sealed record ProcessDefinitionResponse(
    string ProcessDefinitionId,
    string Key,
    string? Name,
    int Version,
    string DeploymentId,
    string TenantId,
    DateTime? DeployTime,
    bool IsSuspended = false);

/// <summary>启动流程实例的请求。</summary>
/// <param name="ProcessDefinitionId">流程定义 id（与 <paramref name="ProcessDefinitionKey"/> 二选一）。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="StartUserId">发起人；JWT 模式下必须与令牌 <c>sub</c> 一致，否则 403。</param>
/// <param name="Variables">初始变量。</param>
/// <param name="ProcessDefinitionKey">
/// 流程 key（对应 <c>startProcessInstanceByKey</c>）：按 key 启动<b>最新版本</b>。
/// 两个 id/key 都为空 → 400；都给出时以 id 为准（精确指定版本）。
/// </param>
/// <param name="TenantId">
/// 租户（仅在按 key 启动时参与「最新版本」的选取；按 id 启动时随定义继承）。
/// JWT 模式下必须与令牌租户一致，否则 403。
/// </param>
public sealed record StartProcessInstanceRequest(
    string? ProcessDefinitionId = null,
    string? BusinessKey = null,
    string? StartUserId = null,
    IDictionary<string, JsonElement>? Variables = null,
    string? ProcessDefinitionKey = null,
    string? TenantId = null);

/// <summary>启动流程实例的响应。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
public sealed record StartProcessInstanceResponse(string ProcessInstanceId);

/// <summary>完成任务请求。</summary>
/// <param name="Variables">随完成写入流程的变量（JSON 标量，见 <see cref="JsonVariableReader"/>）。</param>
/// <param name="ExpectedRevision">
/// 可选的乐观并发校验：与任务当前 <c>revision</c> 不一致时返回 409，防止重复提交。
/// </param>
public sealed record CompleteTaskRequest(
    IDictionary<string, JsonElement>? Variables = null,
    int? ExpectedRevision = null);

/// <summary>认领 / 取消认领任务请求。</summary>
/// <param name="UserId">
/// 认领人。省略时取 JWT 的 <c>sub</c> 或 <c>X-User-Id</c>；解析不出认领人时返回 400
/// （<b>不再</b>把空 userId 当作取消认领）。取消认领请用 <c>POST /tasks/{id}/unclaim</c>。
/// </param>
/// <param name="ExpectedRevision">可选的乐观并发校验（不一致返回 409）。</param>
/// <param name="Unclaim">
/// 为 <see langword="true"/> 时按取消认领处理（兼容旧客户端，等价于 <c>/unclaim</c>）。
/// </param>
public sealed record ClaimTaskRequest(
    string? UserId = null,
    int? ExpectedRevision = null,
    bool? Unclaim = null);

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
    bool IsRunning,
    string? TenantId = null)
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
            instance.IsRunning,
            instance.TenantId);
    }
}

/// <summary>历史任务 DTO（已办查询 / 审计）。</summary>
/// <param name="Id">任务 id。</param>
/// <param name="TaskDefinitionKey">BPMN 中 userTask 的 id。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ProcessDefinitionId">流程定义 id（已办列表按定义过滤后回显用）。</param>
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
    string? ProcessDefinitionId,
    string? Name,
    string? Assignee,
    string? Owner,
    int? Priority,
    string? FormKey,
    DateTime? StartTime,
    DateTime? EndTime,
    long? DurationInMillis,
    string? DeleteReason,
    string? TenantId = null)
{
    /// <summary>从历史任务映射。</summary>
    public static HistoricTaskResponse From(HistoricTaskInstance task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new HistoricTaskResponse(
            task.Id,
            task.TaskDefinitionKey,
            task.ProcessInstanceId,
            task.ProcessDefinitionId,
            task.Name,
            task.Assignee,
            task.Owner,
            task.Priority,
            task.FormKey,
            task.StartTime,
            task.EndTime,
            task.DurationInMillis,
            task.DeleteReason,
            task.TenantId);
    }
}

/// <summary>统一错误响应。</summary>
/// <param name="Message">错误说明。</param>
/// <param name="Errors">校验错误明细（仅校验失败时返回）。</param>
/// <param name="TraceId">服务端请求标识（排查 5xx 时提供给运维，不包含异常细节）。</param>
/// <param name="Code">
/// 机器可读错误码（<c>validation_error</c> / <c>forbidden</c> / <c>not_found</c> /
/// <c>conflict</c> / <c>engine_rule_violation</c> / <c>internal_error</c> …），
/// 供调用方程序化分支，避免匹配中文文案。
/// </param>
public sealed record ErrorResponse(
    string Message,
    IReadOnlyList<ValidationError>? Errors = null,
    string? TraceId = null,
    string? Code = null);

/// <summary>尚未实现的能力说明（对应开发计划阶段代号）。</summary>
/// <param name="Message">说明。</param>
/// <param name="PlanPhase">计划阶段。</param>
public sealed record NotImplementedResponse(string Message, string PlanPhase);

/// <summary>
/// 运行中流程实例 DTO（在办监控 / 实例详情）。
/// <para>比执行树多一层产品语义：拆出 <see cref="ProcessDefinitionKey"/>，不暴露父/子执行内部 id。</para>
/// </summary>
/// <param name="Id">实例 id（= 根执行 id）。</param>
/// <param name="ProcessInstanceId">流程实例 id（无调用活动时与 <paramref name="Id"/> 相同）。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="ProcessDefinitionKey">从定义 id 解析出的流程 key。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="Name">实例名。</param>
/// <param name="StartUserId">发起人。</param>
/// <param name="StartTime">开始时间（UTC）。</param>
/// <param name="IsActive">根执行是否活动。</param>
/// <param name="IsSuspended">是否挂起。</param>
/// <param name="TenantId">租户（无租户为 <see langword="null"/>）。</param>
/// <param name="Children">子执行 id（调试 / 兼容用；列表接口不预加载）。</param>
public sealed record ProcessInstanceResponse(
    string Id,
    string ProcessInstanceId,
    string ProcessDefinitionId,
    string? ProcessDefinitionKey,
    string? BusinessKey,
    string? Name,
    string? StartUserId,
    DateTime? StartTime,
    bool IsActive,
    bool IsSuspended,
    string? TenantId,
    IReadOnlyList<string> Children)
{
    /// <summary>从执行实体映射。</summary>
    public static ProcessInstanceResponse From(IExecutionEntity execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return new ProcessInstanceResponse(
            execution.Id,
            execution.ProcessInstanceId ?? execution.Id,
            execution.ProcessDefinitionId ?? string.Empty,
            ExtractDefinitionKey(execution.ProcessDefinitionId),
            execution.BusinessKey,
            execution.Name,
            execution.StartUserId,
            execution.StartTime,
            execution.IsActive,
            execution.SuspensionState == 2,
            execution.TenantId,
            [.. execution.Children.Select(child => child.Id)]);
    }

    /// <summary>定义 id 形如 <c>{key}:{version}:{idGeneratorId}</c>，取第一段作为 key。</summary>
    private static string? ExtractDefinitionKey(string? processDefinitionId)
    {
        if (string.IsNullOrEmpty(processDefinitionId))
        {
            return null;
        }

        var separator = processDefinitionId.IndexOf(':', StringComparison.Ordinal);

        return separator > 0 ? processDefinitionId[..separator] : processDefinitionId;
    }
}

/// <summary>任务身份关系 DTO（候选人 / 候选组 / 办理人 / 所有者）。</summary>
/// <param name="Type">关系类型（<c>candidate</c> / <c>participant</c> …）。</param>
/// <param name="UserId">用户 id（候选组时为空）。</param>
/// <param name="GroupId">组 id（候选人时为空）。</param>
/// <param name="TaskId">任务 id。</param>
/// <param name="ProcessInstanceId">流程实例 id（流程实例级关系才有值）。</param>
public sealed record IdentityLinkResponse(
    string Type,
    string? UserId,
    string? GroupId,
    string? TaskId,
    string? ProcessInstanceId)
{
    /// <summary>从身份关系记录映射。</summary>
    public static IdentityLinkResponse From(IdentityLinkRecord link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new IdentityLinkResponse(
            link.Type,
            link.UserId,
            link.GroupId,
            link.TaskId,
            link.ProcessInstanceId);
    }
}

/// <summary>
/// 任务详情 DTO（列表 DTO 的超集）。
/// <para>列表只带高频字段；详情补 <c>description</c> / <c>category</c> / <c>businessKey</c> / 挂起与委派状态。</para>
/// </summary>
/// <param name="Id">任务 id。</param>
/// <param name="Name">任务名。</param>
/// <param name="Description">任务描述。</param>
/// <param name="TaskDefinitionKey">BPMN 中 userTask 的 id。</param>
/// <param name="Assignee">办理人。</param>
/// <param name="Owner">所有者。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="ProcessDefinitionKey">从定义 id 解析出的流程 key。</param>
/// <param name="ExecutionId">执行 id。</param>
/// <param name="BusinessKey">业务主键（从流程实例继承）。</param>
/// <param name="FormKey">表单 Key。</param>
/// <param name="Category">任务分类。</param>
/// <param name="Priority">优先级。</param>
/// <param name="Revision">乐观锁版本（完成 / 认领时可回传 <c>expectedRevision</c>）。</param>
/// <param name="CreateTime">创建时间。</param>
/// <param name="DueDate">到期时间。</param>
/// <param name="ClaimTime">认领时间。</param>
/// <param name="TenantId">租户。</param>
/// <param name="IsSuspended">是否挂起。</param>
/// <param name="DelegationState">委派状态（<c>PENDING</c> / <c>RESOLVED</c> / 空）。</param>
/// <param name="IdentityLinks">身份关系。</param>
public sealed record TaskDetailResponse(
    string Id,
    string? Name,
    string? Description,
    string? TaskDefinitionKey,
    string? Assignee,
    string? Owner,
    string? ProcessInstanceId,
    string? ProcessDefinitionId,
    string? ProcessDefinitionKey,
    string? ExecutionId,
    string? BusinessKey,
    string? FormKey,
    string? Category,
    int Priority,
    int Revision,
    DateTime? CreateTime,
    DateTime? DueDate,
    DateTime? ClaimTime,
    string? TenantId,
    bool IsSuspended,
    string? DelegationState,
    IReadOnlyList<IdentityLinkResponse> IdentityLinks)
{
    /// <summary>从任务实体与身份关系映射。</summary>
    public static TaskDetailResponse From(TaskEntity task, IReadOnlyList<IdentityLinkRecord> identityLinks)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(identityLinks);

        return new TaskDetailResponse(
            task.Id,
            task.Name,
            task.Description,
            task.TaskDefinitionKey,
            task.Assignee,
            task.Owner,
            task.ProcessInstanceId,
            task.ProcessDefinitionId,
            DefinitionIdParser.Key(task.ProcessDefinitionId),
            task.ExecutionId,
            task.BusinessKey,
            task.FormKey,
            task.Category,
            task.Priority,
            task.Revision,
            task.CreateTime,
            task.DueDate,
            task.ClaimTime,
            task.TenantId,
            task.SuspensionState == TaskEntity.SuspendedSuspensionState,
            task.DelegationStateText,
            [.. identityLinks.Select(IdentityLinkResponse.From)]);
    }
}

/// <summary>历史变量 DTO（审计：这个实例上出现过哪些变量、最后是什么值）。</summary>
/// <param name="Name">变量名。</param>
/// <param name="Type">Activiti 变量类型（<c>string</c> / <c>long</c> / <c>date</c> …）。</param>
/// <param name="Value">解码后的值。</param>
/// <param name="ExecutionId">所属执行 id。</param>
/// <param name="TaskId">任务局部变量才有值。</param>
/// <param name="CreateTime">首次写入时间。</param>
/// <param name="LastUpdatedTime">最后更新时间。</param>
public sealed record HistoricVariableResponse(
    string Name,
    string? Type,
    object? Value,
    string? ExecutionId,
    string? TaskId,
    DateTime? CreateTime,
    DateTime? LastUpdatedTime)
{
    /// <summary>从历史变量记录映射。</summary>
    public static HistoricVariableResponse From(HistoricVariableInstance variable)
    {
        ArgumentNullException.ThrowIfNull(variable);

        return new HistoricVariableResponse(
            variable.Name,
            variable.TypeName,
            variable.Value,
            variable.ExecutionId,
            variable.TaskId,
            variable.CreateTime,
            variable.LastUpdatedTime);
    }
}

/// <summary>
/// 转办（改派）任务请求。
/// <para>
/// 只有 <paramref name="Assignee"/>：引擎的 <c>setAssignee</c> 不落备注列，原先的
/// <c>reason</c> 字段接受后被丢弃（契约与实现不一致），已删除。需要审计说明请写业务侧记录。
/// </para>
/// </summary>
/// <param name="Assignee">新办理人；<see langword="null"/> / 空白表示退回未分配。</param>
public sealed record SetAssigneeRequest(string? Assignee = null);

/// <summary>流程实例跳转请求。</summary>
/// <param name="TargetActivityId">目标节点 id（节点 / 网关 / 结束事件）。</param>
/// <param name="Reason">跳转原因（写进被删任务与活动历史）。</param>
public sealed record JumpProcessInstanceRequest(string? TargetActivityId = null, string? Reason = null);

/// <summary>
/// 任务增量更新请求。每个字段为 <see langword="null"/> 表示不修改；
/// <c>owner</c> 传空串表示清空所有者。
/// </summary>
/// <param name="Name">任务名。</param>
/// <param name="Description">任务描述。</param>
/// <param name="Priority">优先级。</param>
/// <param name="DueDate">到期时间（UTC）。</param>
/// <param name="Category">任务分类。</param>
/// <param name="Owner">所有者（空串 = 清空）。</param>
/// <summary>
/// 修改任务请求（PATCH <c>/tasks/{taskId}</c>）。
/// <para>
/// 字段独立可空：<see langword="null"/> 表示不修改。到期时间 / 描述本身可空，
/// 用 <see cref="ClearDueDate"/> / <see cref="ClearDescription"/> 显式清空。
/// </para>
/// </summary>
public sealed record UpdateTaskRequest(
    string? Name = null,
    string? Description = null,
    int? Priority = null,
    DateTime? DueDate = null,
    string? Category = null,
    string? Owner = null,
    bool? ClearDueDate = null,
    bool? ClearDescription = null);

/// <summary>候选人 / 候选组请求（两个端点各用其中一个字段）。</summary>
/// <param name="UserId">候选用户 id。</param>
/// <param name="GroupId">候选组 id。</param>
public sealed record TaskCandidateRequest(string? UserId = null, string? GroupId = null);

/// <summary>委派任务请求。</summary>
/// <param name="UserId">被委派人。</param>
public sealed record DelegateTaskRequest(string? UserId = null);

/// <summary>表单属性枚举选项。</summary>
/// <param name="Id">选项值。</param>
/// <param name="Name">选项显示名。</param>
public sealed record FormPropertyValueResponse(string? Id, string? Name);

/// <summary>
/// 表单属性（<c>activiti:formProperty</c>）的只读元数据。
/// <para>引擎不渲染表单、也不执行表单校验；这里只回传模型里声明的字段，供宿主自行渲染。</para>
/// </summary>
/// <param name="Id">属性 id（提交时的键）。</param>
/// <param name="Name">显示名。</param>
/// <param name="Type">类型（string / long / date / boolean / enum）。</param>
/// <param name="Variable">绑定流程变量名。</param>
/// <param name="Expression">值表达式。</param>
/// <param name="Default">默认值。</param>
/// <param name="DatePattern">日期格式。</param>
/// <param name="Required">是否必填。</param>
/// <param name="Readable">是否可读。</param>
/// <param name="Writable">是否可写。</param>
/// <param name="Values">枚举选项。</param>
public sealed record FormPropertyResponse(
    string? Id,
    string? Name,
    string? Type,
    string? Variable,
    string? Expression,
    string? Default,
    string? DatePattern,
    bool? Required,
    bool? Readable,
    bool? Writable,
    IReadOnlyList<FormPropertyValueResponse> Values)
{
    /// <summary>从模型映射。</summary>
    public static FormPropertyResponse From(NetBpm.Bpmn.Model.FormProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return new FormPropertyResponse(
            property.Id,
            property.Name,
            property.Type,
            property.Variable,
            property.Expression,
            property.Default,
            property.DatePattern,
            property.Required,
            property.Readable,
            property.Writable,
            [.. property.Values.Select(value => new FormPropertyValueResponse(value.Id, value.Name))]);
    }
}

/// <summary>
/// 用户任务的表单元数据。
/// <para>本子集的表单声明只落在 <c>userTask</c>（<c>activiti:formKey</c> + <c>formProperty</c>）。</para>
/// </summary>
/// <param name="FormKey">表单 Key（宿主可据此切换自定义渲染器）。</param>
/// <param name="ActivityId">节点 id。</param>
/// <param name="ActivityName">节点名。</param>
/// <param name="FormProperties">表单属性。</param>
public sealed record FormMetadataResponse(
    string? FormKey,
    string? ActivityId,
    string? ActivityName,
    IReadOnlyList<FormPropertyResponse> FormProperties)
{
    /// <summary>从用户任务映射。</summary>
    public static FormMetadataResponse From(NetBpm.Bpmn.Model.UserTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new FormMetadataResponse(
            task.FormKey,
            task.Id,
            task.Name,
            [.. task.FormProperties.Select(FormPropertyResponse.From)]);
    }
}

/// <summary>部署中的一个流程定义（部署列表内嵌）。</summary>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="Key">流程 key。</param>
/// <param name="Name">流程名。</param>
/// <param name="Version">版本号。</param>
/// <param name="ResourceName">资源名。</param>
/// <param name="IsSuspended">是否挂起。</param>
public sealed record DeploymentDefinitionResponse(
    string ProcessDefinitionId,
    string Key,
    string? Name,
    int Version,
    string? ResourceName,
    bool IsSuspended);

/// <summary>部署列表项（一次部署一行，内嵌该部署下的定义）。</summary>
/// <param name="DeploymentId">部署 id。</param>
/// <param name="Name">部署名（部署时 <c>?deploymentName=</c> 未传则为空）。</param>
/// <param name="DeployTime">部署时间（UTC）。</param>
/// <param name="TenantId">租户。</param>
/// <param name="Definitions">该部署下的流程定义。</param>
public sealed record DeploymentSummaryResponse(
    string DeploymentId,
    string? Name,
    DateTime? DeployTime,
    string? TenantId,
    IReadOnlyList<DeploymentDefinitionResponse> Definitions);

/// <summary>部署资源项（资源名 → 流程定义）。</summary>
/// <param name="Name">资源名（如 <c>designer.bpmn20.xml</c>）。</param>
/// <param name="ProcessDefinitionId">该资源对应的流程定义 id。</param>
public sealed record DeploymentResourceResponse(string? Name, string ProcessDefinitionId);

/// <summary>批量转办请求。</summary>
/// <param name="TaskIds">任务 id 列表。</param>
/// <param name="Assignee">新办理人（空表示退回未分配）。</param>
public sealed record BatchTaskAssigneeRequest(
    IReadOnlyList<string>? TaskIds = null,
    string? Assignee = null);

/// <summary>批量删除实例请求。</summary>
/// <param name="ProcessInstanceIds">流程实例 id 列表。</param>
/// <param name="DeleteReason">删除原因（写进历史）。</param>
public sealed record BatchDeleteInstancesRequest(
    IReadOnlyList<string>? ProcessInstanceIds = null,
    string? DeleteReason = null);

/// <summary>批量操作的单项结果。</summary>
/// <param name="Id">任务 / 实例 id。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Error">失败原因（成功时为 <see langword="null"/>）。</param>
public sealed record BatchItemResult(string Id, bool Success, string? Error);

/// <summary>批量操作结果（逐项成功 / 失败，不因单项失败回滚已成功项）。</summary>
/// <param name="Total">总条数。</param>
/// <param name="Succeeded">成功条数。</param>
/// <param name="Failed">失败条数。</param>
/// <param name="Items">逐项结果。</param>
public sealed record BatchResultResponse(
    int Total,
    int Succeeded,
    int Failed,
    IReadOnlyList<BatchItemResult> Items);

/// <summary>待办统计（首页角标 / 逾期提醒）。</summary>
/// <param name="Total">总待办数。</param>
/// <param name="Overdue">已逾期（<c>dueDate</c> 早于当前时间）。</param>
/// <param name="DueToday">今天到期（按 <c>timeZoneOffsetMinutes</c> 时区计算的自然日）。</param>
/// <param name="ByDefinition">按<b>流程 key</b> 分组的数量（同 key 多版本合并，业务上更直观）。</param>
public sealed record TaskStatsResponse(
    int Total,
    int Overdue,
    int DueToday,
    IReadOnlyDictionary<string, int> ByDefinition);

/// <summary>组信息（身份模块只读视图）。</summary>
/// <param name="Id">组 id。</param>
/// <param name="Name">组名。</param>
/// <param name="Type">组类型。</param>
public sealed record UserGroupResponse(string Id, string? Name, string? Type)
{
    /// <summary>从身份模块的组记录映射。</summary>
    public static UserGroupResponse From(NetBpm.Identity.UserGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return new UserGroupResponse(group.Id, group.Name, group.Type);
    }
}

/// <summary>BPMN 试校验结果（不落库）。</summary>
/// <param name="Errors">阻断级问题（有值时部署会被拒绝）。</param>
/// <param name="Warnings">提示级问题。</param>
public sealed record ValidationResponse(
    IReadOnlyList<ValidationError> Errors,
    IReadOnlyList<ValidationError> Warnings)
{
    /// <summary>是否没有阻断级问题（可以部署）。</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>按严重级别拆分校验结果。</summary>
    public static ValidationResponse From(IReadOnlyList<ValidationError> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return new ValidationResponse(
            [.. results.Where(result => result.IsError)],
            [.. results.Where(result => !result.IsError)]);
    }
}

/// <summary>
/// 流程定义 id 的解析工具。
/// <para>定义 id 形如 <c>{key}:{version}:{idGeneratorId}</c>（见 README），key 不含冒号，
/// 因此取第一段即可得到流程 key。</para>
/// </summary>
internal static class DefinitionIdParser
{
    public static string? Key(string? processDefinitionId)
    {
        if (string.IsNullOrEmpty(processDefinitionId))
        {
            return null;
        }

        var separator = processDefinitionId.IndexOf(':', StringComparison.Ordinal);

        return separator > 0 ? processDefinitionId[..separator] : processDefinitionId;
    }
}

namespace NetBpm.Bpmn.Model;

/// <summary>
/// 流程级事件监听器（<c>activiti:eventListener</c>，挂在流程的 <c>extensionElements</c> 下）。
/// 对应 Activiti 的 <c>ActivitiEventListener</c> 注册方式 —— 引擎在发生对应事件时逐个通知。
/// </summary>
/// <remarks>
/// <para>
/// 事件类型（<see cref="Events"/>，逗号分隔多个）：引擎当前分发
/// <c>PROCESS_STARTED</c> / <c>PROCESS_COMPLETED</c> / <c>ACTIVITY_STARTED</c> /
/// <c>ACTIVITY_COMPLETED</c> / <c>TASK_CREATED</c> / <c>TASK_COMPLETED</c>；
/// <see cref="EntityType"/> 仅为描述信息，实体类型由事件本身决定。
/// </para>
/// <para>
/// <see cref="ThrowOnFailure"/> 对应 Activiti 的 <c>isFailOnException()</c>：
/// <c>true</c> 时监听器抛错使整个命令失败；缺省（false）只记录并继续。
/// </para>
/// </remarks>
public class EventListener
{
    /// <summary>订阅的事件类型，逗号分隔（如 <c>TASK_COMPLETED</c>，对齐 Activiti 的事件枚举名）。</summary>
    public string? Events { get; set; }

    /// <summary>实体类型描述（如 TASK / EXECUTION）；仅描述用途，不影响分发。</summary>
    public string? EntityType { get; set; }

    /// <summary>实现（.NET 类型名，须实现引擎的监听器契约）。</summary>
    public string? ClassName { get; set; }

    /// <summary>值表达式（求值结果须实现监听器契约）。</summary>
    public string? Expression { get; set; }

    /// <summary>委托表达式（容器中的服务名，支持 <c>${name}</c> 形式）。</summary>
    public string? DelegateExpression { get; set; }

    /// <summary>监听器抛错时是否使整个命令失败；缺省只记录并继续。</summary>
    public bool? ThrowOnFailure { get; set; }
}

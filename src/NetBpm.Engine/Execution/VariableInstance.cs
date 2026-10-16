namespace NetBpm.Engine.Execution;

/// <summary>
/// 变量实例。对应 Activiti 的 <c>VariableInstanceEntity</c> / <c>ACT_RU_VARIABLE</c> 一行。
/// <para>
/// 🔑 <b>已核实的列语义</b>（决定行内容，必须照搬）：
/// <list type="bullet">
///   <item><description><c>EXECUTION_ID_</c> 指向<b>实际持有该变量的作用域执行</b>
///     （不是「触发设置的执行」）—— 由 <c>VariableScopeImpl.createVariableInstance</c> 回填；</description></item>
///   <item><description><c>PROC_INST_ID_</c> = 所属流程实例 id；</description></item>
///   <item><description><c>TASK_ID_</c> 仅任务局部变量非空；</description></item>
///   <item><description><c>REV_</c>：数据管理器 <c>create()</c> 显式置 <b>0</b>，而 INSERT 语句硬编码写 <b>1</b>，
///     之后每次更新 +1（乐观锁）；</description></item>
///   <item><description><c>TYPE_</c> 取变量类型名（<c>string</c> / <c>integer</c> / <c>long</c> / <c>double</c> /
///     <c>boolean</c> / <c>date</c> / <c>null</c> …），值列由类型决定（见 <c>DapperVariableInstanceManager</c>）。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class VariableInstance
{
    /// <summary><c>ID_</c>（未落库时为空，由 INSERT 时的 ID 生成器补齐）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary><c>REV_</c>（0 表示尚未 INSERT）。</summary>
    public int Revision { get; set; }

    /// <summary><c>NAME_</c>。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>TYPE_</c>。</summary>
    public string? TypeName { get; set; }

    /// <summary>解码后的值（写库时按类型落到不同列）。</summary>
    public object? Value { get; set; }

    /// <summary><c>EXECUTION_ID_</c>：持有该变量的作用域执行。</summary>
    public string? ExecutionId { get; set; }

    /// <summary><c>PROC_INST_ID_</c>。</summary>
    public string? ProcessInstanceId { get; set; }

    /// <summary><c>TASK_ID_</c>（任务局部变量）。</summary>
    public string? TaskId { get; set; }

    /// <summary>
    /// <c>BYTEARRAY_ID_</c>：超长值（<c>longString</c>）指向 <c>ACT_GE_BYTEARRAY</c> 的行；
    /// 普通类型为 null。
    /// </summary>
    public string? ByteArrayId { get; set; }

    public override string ToString() =>
        $"{Name}={Value ?? "null"} ({TypeName}, execution={ExecutionId}, rev={Revision})";
}

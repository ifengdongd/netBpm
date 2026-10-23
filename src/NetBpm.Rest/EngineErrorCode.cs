namespace NetBpm.Rest;

/// <summary>
/// <see cref="ErrorResponse.Code"/> 的稳定取值（REST 契约的一部分）。
/// <para>
/// 收敛前同一语义有 <c>not_found</c> / <c>task_not_found</c> / <c>process_instance_not_found</c> 三种写法：
/// 调用方无法写出一条「对象不存在」的通用判断。这里是唯一来源 —— 端点与
/// <see cref="GlobalExceptionHandler"/> 必须引用常量，不再散落字面量。
/// </para>
/// <para>
/// 命名口径：<c>not_found</c> 表达「目标不存在或跨租户不可见」（不区分对象类型，避免存在性泄露）；
/// 只有语义确实不同的情况才单独取值（如 <see cref="ProcessInstanceNotRunning"/>：
/// 实例存在历史记录但已不在运行，调用方应回落到历史视图）。
/// </para>
/// </summary>
public static class EngineErrorCode
{
    /// <summary>请求本身不合法（参数缺失 / 语义错误 / 批量条数超限）。</summary>
    public const string ValidationError = "validation_error";

    /// <summary>目标对象不存在（含跨租户访问：按「不存在」处理，不泄露存在性）。</summary>
    public const string NotFound = "not_found";

    /// <summary>实例已结束 / 已删除，运行态查询不可用（调用方应改查历史）。</summary>
    public const string ProcessInstanceNotRunning = "process_instance_not_running";

    /// <summary>已认证但身份 / 租户 / scope 不足。</summary>
    public const string Forbidden = "forbidden";

    /// <summary>状态冲突（已认领 / 挂起 / 仍有运行实例等）。</summary>
    public const string Conflict = "conflict";

    /// <summary>乐观锁失败（版本号不匹配，重读后重试）。</summary>
    public const string OptimisticLockConflict = "optimistic_lock_conflict";

    /// <summary>唯一约束冲突（并发写入撞车，重读后重试）。</summary>
    public const string UniqueViolation = "unique_violation";

    /// <summary>数据库暂时不可用（可重试；不回传 SQL / 连接串）。</summary>
    public const string DatabaseUnavailable = "database_unavailable";

    /// <summary>引擎业务规则拒绝（请求不被接受，但不是参数格式错误）。</summary>
    public const string EngineRuleViolation = "engine_rule_violation";

    /// <summary>BPMN XML 解析失败。</summary>
    public const string BpmnXmlParseError = "bpmn_xml_parse_error";

    /// <summary>明确未实现的能力。</summary>
    public const string NotImplemented = "not_implemented";

    /// <summary>调用方取消。</summary>
    public const string RequestCancelled = "request_cancelled";

    /// <summary>未预期异常（只回传 traceId）。</summary>
    public const string InternalError = "internal_error";
}

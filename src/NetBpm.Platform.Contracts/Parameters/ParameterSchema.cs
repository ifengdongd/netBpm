using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 参数类型。全部以字符串传输（<c>application/x-www-form-urlencoded</c> 的语义），
/// 由服务端按本枚举做类型与约束校验。
/// </summary>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<ParameterType>))]
public enum ParameterType
{
    /// <summary>字符串。</summary>
    String = 0,

    /// <summary>整数（64 位）。</summary>
    Long = 1,

    /// <summary>定点小数（避免 double 精度问题）。</summary>
    Decimal = 2,

    /// <summary>布尔（<c>true</c> / <c>false</c>）。</summary>
    Boolean = 3,

    /// <summary>日期（<c>yyyy-MM-dd</c>）。</summary>
    Date = 4,

    /// <summary>日期时间（RFC 3339）。</summary>
    DateTime = 5,

    /// <summary>枚举（取值受 <see cref="ParameterConstraints.EnumValues"/> 约束）。</summary>
    Enum = 6,

    /// <summary>结构化扩展（JSON 字符串，仅透传，不参与流程判断）。</summary>
    Json = 7,
}

/// <summary>
/// 参数的取值约束。
/// </summary>
/// <param name="MinLength">字符串最小长度。</param>
/// <param name="MaxLength">字符串最大长度。</param>
/// <param name="Minimum">数值下限。</param>
/// <param name="Maximum">数值上限。</param>
/// <param name="Pattern">正则（服务端编译缓存；调用方可据此做前置校验）。</param>
/// <param name="EnumValues">枚举候选值。</param>
public sealed record ParameterConstraints(
    int? MinLength,
    int? MaxLength,
    decimal? Minimum,
    decimal? Maximum,
    string? Pattern,
    IReadOnlyList<string>? EnumValues);

/// <summary>
/// 一个流程参数的定义——参数规范的原子单元。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有 schema</b>：现状 <c>ParaDict.ToVariables()</c> 把参数原样展开成流程变量，
/// 唯一防线是「与保留名撞名就拒绝」。于是 <c>day</c> 拼成 <c>days</c> 不会报错，
/// 只是让 <c>${days &gt; 3}</c> 取到空值、网关静默走默认流——这类 bug 在集成期几乎无法定位。
/// </para>
/// <para>
/// <b>对外分层、对内扁平</b>：API 上参数带命名空间（<see cref="ApiName"/> 形如 <c>param.days</c>），
/// 写入时镜像成裸变量（<see cref="VariableName"/> = <c>days</c>）。这样 BPMN 与 EL 一个字都不用改，
/// 与 Activiti 的对拍能力也不受影响。
/// </para>
/// </remarks>
/// <param name="ApiName">对外参数名（带命名空间前缀，如 <c>param.days</c>、<c>form.reason</c>）。</param>
/// <param name="VariableName">镜像写入的流程变量名；为空表示不写入变量（仅存档）。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Type">类型。</param>
/// <param name="Required">发起时是否必填。</param>
/// <param name="DefaultValue">默认值（字符串形式，按 <see cref="Type"/> 解析）。</param>
/// <param name="Description">说明。</param>
/// <param name="Constraints">取值约束。</param>
/// <param name="WritableBy">允许写入的角色（<c>APPLICANT</c> / <c>APPROVER</c> / <c>ADMIN</c> / <c>NONE</c>）；为 <see langword="null"/> 表示发起时可写、之后只读。</param>
/// <param name="VisibleTo">可见角色（<c>APPLICANT</c> / <c>APPROVER</c> / <c>AUDITOR</c>）；为 <see langword="null"/> 表示全部可见。</param>
/// <param name="SortOrder">表单渲染顺序。</param>
public sealed record ParameterDefinition(
    string ApiName,
    string? VariableName,
    string? DisplayName,
    ParameterType Type,
    bool Required,
    string? DefaultValue,
    string? Description,
    ParameterConstraints? Constraints,
    IReadOnlyList<string>? WritableBy,
    IReadOnlyList<string>? VisibleTo,
    int SortOrder);

/// <summary>
/// 参数规范（一个流程一个版本）。
/// </summary>
/// <remarks>
/// <see cref="Version"/> 变化即意味着<b>调用方需要重新拉取</b>：它不随流程定义版本走，
/// 只随参数本身的结构变化走（加参数、改类型、改必填）。这样「改个流程图的连线」不会惊动所有集成方。
/// </remarks>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="Version">参数规范版本（单调递增）。</param>
/// <param name="Parameters">参数定义列表（按 <see cref="ParameterDefinition.SortOrder"/> 升序）。</param>
/// <param name="ReservedPrefixes">保留的命名空间前缀，调用方自定义参数不得使用。</param>
public sealed record ParameterSchema(
    string ProcessKey,
    int Version,
    IReadOnlyList<ParameterDefinition> Parameters,
    IReadOnlyList<string> ReservedPrefixes);

/// <summary>
/// 流程参数快照（实例维度）。
/// </summary>
/// <remarks>
/// 参数与流程变量是两份数据：变量是引擎的<b>当前状态</b>，参数是业务的<b>提交记录</b>。
/// 参数只增不改（每次提交产生新的 revision），这样「谁在第几步把金额改成了多少」可回溯。
/// </remarks>
/// <param name="InstanceId">实例 id（与 <see cref="InstanceView.InstanceId"/> 同值）。</param>
/// <param name="Revision">修订号（单调递增，可用于乐观并发）。</param>
/// <param name="Values">参数值（键 = <see cref="ParameterDefinition.ApiName"/>）。</param>
/// <param name="UpdatedAt">最后更新时间。</param>
/// <param name="UpdatedBy">最后修改人（<c>subject</c>）。</param>
public sealed record ParameterSnapshot(
    string InstanceId,
    long Revision,
    IReadOnlyDictionary<string, string?> Values,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// 实例参数视图——一个实例的<b>参数声明 + 当前值</b>（<c>GET /instances/{id}/parameters</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="InstanceView.Parameters"/> 的分工：那里只给值（详情页顺带带着），
/// 这里把值<b>接到声明上</b>——标签、类型、约束、谁能写，调用方据此直接渲染参数面板，
/// 不必再拉一次 <c>/processes/{key}/parameters/schema</c> 然后自己按 apiName 对齐。
/// </para>
/// <para>
/// <b>值已经过 <c>visibleTo</c> 过滤</b>（与实例详情同一条路径）：不满足可见角色的参数
/// 整条消失，而不是给个空值。声明里不能看的那些也不出现在 <see cref="Parameters"/> 里，
/// 否则「有这个字段但值为空」本身就是一次泄露。
/// </para>
/// <para>
/// 带 <see cref="Revision"/> 是为了让调用方写回参数时能用同一套乐观并发
/// （与 <c>ExpectedRevision</c> 同源），避免「看着旧值改、覆盖别人刚写的新值」。
/// </para>
/// </remarks>
/// <param name="InstanceId">实例 id。</param>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="ProcessDefinitionId">实例所停的定义 id（可能不是当前生效版本）。</param>
/// <param name="SchemaVersion">参数规范版本；该流程未声明参数时为 0。</param>
/// <param name="Revision">实例修订号（写回参数时的乐观并发依据）。</param>
/// <param name="Parameters">参数行（先按声明顺序，再按名称补上未声明的）。</param>
public sealed record InstanceParameterView(
    string InstanceId,
    string ProcessKey,
    string? ProcessDefinitionId,
    int SchemaVersion,
    long Revision,
    IReadOnlyList<InstanceParameter> Parameters);

/// <summary>
/// 参数面板的一行：声明（可能没有）+ 当前值。
/// </summary>
/// <remarks>
/// <see cref="Declared"/> 为 <see langword="false"/> 的行是<b>引擎里存在但规范里没声明</b>的变量
/// （历史遗留、管理面直写、老 BPMN 的裸变量）。它们必须出现：审计要看到「这张单上有哪些参数」，
/// 只列声明就等于对未声明的那部分失明。
/// </remarks>
/// <param name="ApiName">对外参数名（<c>param.{name}</c>）。</param>
/// <param name="VariableName">镜像的流程变量名（裸名）。</param>
/// <param name="DisplayName">显示名（未声明时为 <see langword="null"/>，调用方退化用 <see cref="ApiName"/>）。</param>
/// <param name="Type">类型（未声明时按字符串）。</param>
/// <param name="Required">发起时是否必填。</param>
/// <param name="Value">当前值（字符串形式，与传输格式一致）。</param>
/// <param name="Description">说明。</param>
/// <param name="Constraints">取值约束。</param>
/// <param name="WritableBy">允许写入的角色；<see langword="null"/> 表示发起时可写、之后只读。</param>
/// <param name="SortOrder">渲染顺序。</param>
/// <param name="Declared">是否在参数规范里声明过。</param>
public sealed record InstanceParameter(
    string ApiName,
    string? VariableName,
    string? DisplayName,
    ParameterType Type,
    bool Required,
    string? Value,
    string? Description,
    ParameterConstraints? Constraints,
    IReadOnlyList<string>? WritableBy,
    int SortOrder,
    bool Declared);

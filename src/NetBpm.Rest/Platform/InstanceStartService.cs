using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetBpm.Engine;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 业务主键发号器（<c>POST /workflow/v1/instances</c> 调用方未传 <c>BusinessKey</c> 时兜底）。
/// </summary>
/// <remarks>
/// 默认实现是「毫秒时间戳 + 进程内序号」的正整数串（与 Demo 的 <c>BusinessKeys</c> 同口径，
/// 保证单号能落进业务表的 <c>bigint</c> 列）。多实例部署要换成统一发号服务；按流程配置的
/// 前缀 / 模板（§4 附录 L 的后续候选）通过替换本接口实现接入，不惊动发起编排。
/// </remarks>
public interface IBusinessKeyGenerator
{
    /// <summary>为指定流程发一个业务单号。</summary>
    /// <param name="processKey">流程 key（将来按流程配置前缀/模板用）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<string> NextAsync(string processKey, CancellationToken ct = default);
}

/// <summary>默认业务主键发号器：毫秒时间戳 + 进程内序号。</summary>
public sealed class BusinessKeyGenerator : IBusinessKeyGenerator
{
    private long _sequence;

    /// <inheritdoc />
    public ValueTask<string> NextAsync(string processKey, CancellationToken ct = default)
    {
        var ticks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var value = (ticks << 20) | (Interlocked.Increment(ref _sequence) & 0xFFFFF);

        return ValueTask.FromResult(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// 发起流程（<c>POST /workflow/v1/instances</c>）的编排服务。
/// </summary>
/// <remarks>
/// <para>
/// 职责边界：闸门（流程存在 + 可发起）→ 参数镜像与校验 → 业务主键发号 → 引擎启动 → 组装结果。
/// 发起人只来自令牌的 <c>subject</c>（<see cref="RequestContext.SubjectId"/>），请求体<b>没有</b>
/// <c>startedBy</c> 字段——允许请求体声明发起人等于允许替任意人发起单据。
/// </para>
/// <para>
/// 参数镜像（§4.3）：对外键 <c>param.{name}</c> 剥前缀后以裸名写进引擎变量，
/// 与 BPMN 网关条件 / EL 表达式同一套变量名；撞 <see cref="BaseModelVariables"/> 保留名即 400。
/// 类型校验只做「校验 + 原样写字符串」，不做强转——引擎 EL 的 <c>CoerceToLong</c> 已能按需收窄。
/// </para>
/// </remarks>
public interface IInstanceStartService
{
    /// <summary>
    /// 发起流程实例。校验失败抛 <see cref="StartInstanceException"/>（携带稳定错误码与状态码）。
    /// </summary>
    /// <param name="request">发起请求。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<StartInstanceResult> StartAsync(
        StartInstanceRequest request,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class InstanceStartService : IInstanceStartService
{
    /// <summary>对外参数命名空间前缀（与目录 / 参数 schema 派生同口径）。</summary>
    private const string ParameterApiPrefix = "param.";

    private readonly IProcessEngine _engine;
    private readonly IProcessCatalog _catalog;
    private readonly IInstanceCatalog _instanceCatalog;
    private readonly IBusinessKeyGenerator _businessKeys;
    private readonly IApprovalTimeline _timeline;
    private readonly ILogger<InstanceStartService> _logger;

    /// <summary>构造。</summary>
    /// <param name="engine">引擎门面（启动实例）。</param>
    /// <param name="catalog">流程目录（存在性 / 状态闸门 + 参数 schema + 当前定义 id）。</param>
    /// <param name="instanceCatalog">实例读模型（结果的 Instance 单一出口）。</param>
    /// <param name="businessKeys">业务主键发号器。</param>
    /// <param name="timeline">审批记录时间线（写 <c>INSTANCE_STARTED</c>）。</param>
    /// <param name="logger">日志。</param>
    public InstanceStartService(
        IProcessEngine engine,
        IProcessCatalog catalog,
        IInstanceCatalog instanceCatalog,
        IBusinessKeyGenerator businessKeys,
        IApprovalTimeline timeline,
        ILogger<InstanceStartService> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(instanceCatalog);
        ArgumentNullException.ThrowIfNull(businessKeys);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _catalog = catalog;
        _instanceCatalog = instanceCatalog;
        _businessKeys = businessKeys;
        _timeline = timeline;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<StartInstanceResult> StartAsync(
        StartInstanceRequest request,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProcessKey);

        // 闸门一：存在 + 授权 + 可发起状态。未授权与不存在同口径 404（不泄露流程清单）。
        var descriptor = await _catalog.FindAsync(request.ProcessKey, context, ct).ConfigureAwait(false);

        if (descriptor is null)
        {
            throw StartException(
                ErrorCodes.ProcessNotFound,
                "流程不存在",
                StatusCodes.Status404NotFound,
                $"流程 '{request.ProcessKey}' 不存在，或当前调用方未被授权使用它。");
        }

        EnsureStartable(descriptor, request.EntryPointKey);

        // 参数：镜像 + 校验（保留名冲突 400，必填缺失 / 类型不符 422）
        var schema = await _catalog.GetParameterSchemaAsync(request.ProcessKey, context, ct).ConfigureAwait(false);
        var variables = BuildVariables(request, schema);

        // 业务主键：调用方传入优先，否则发号
        var businessKey = string.IsNullOrWhiteSpace(request.BusinessKey)
            ? await _businessKeys.NextAsync(request.ProcessKey, ct).ConfigureAwait(false)
            : request.BusinessKey.Trim();

        // 当前定义 id（与目录同一条租户桥接逻辑，避免「全局部署 + 租户客户端」查不到）
        var definition = await _catalog
            .GetDefinitionAsync(request.ProcessKey, includeXml: false, context, ct)
            .ConfigureAwait(false);

        if (definition is null)
        {
            throw StartException(
                ErrorCodes.ProcessNotFound,
                "流程不存在",
                StatusCodes.Status404NotFound,
                $"流程 '{request.ProcessKey}' 不存在，或当前调用方未被授权使用它。");
        }

        string instanceId;

        try
        {
            instanceId = await _engine.StartProcessInstanceAsync(
                definition.ProcessDefinitionId,
                businessKey,
                startUserId: context.SubjectId,
                variables: variables.Count == 0 ? null : variables,
                ct).ConfigureAwait(false);
        }
        catch (EngineException exception)
        {
            // 闸门已通过后的竞态（挂起 / 删除定义）——按 409 让调用方刷新后重试
            throw StartException(
                ErrorCodes.ProcessSuspended,
                "流程状态已变更",
                StatusCodes.Status409Conflict,
                exception.Message);
        }

        // 时间线第一条：实例发起。写失败不阻断（AppendAsync 内部已吞并按警告记录）
        await _timeline.AppendAsync(
            instanceId,
            new TimelineEntry(
                Sequence: 0,
                InstanceId: instanceId,
                Kind: TimelineEntryKind.InstanceStarted,
                ActivityId: null,
                ActivityName: null,
                TaskId: null,
                ActorId: context.SubjectId,
                ActorCallerId: null,
                OptionId: null,
                Semantic: null,
                Comment: null,
                TargetNodeId: null,
                FormSnapshotTaskId: null,
                OccurredAt: DateTimeOffset.UtcNow),
            context,
            ct).ConfigureAwait(false);

        var instance = await _instanceCatalog.FindAsync(instanceId, context, ct).ConfigureAwait(false);

        return new StartInstanceResult(instanceId, request.ProcessKey, businessKey, instance);
    }

    /// <summary>按目录状态判定能否发起；Draft/Retired/Suspended 都拒绝，入口标识必须存在。</summary>
    private static void EnsureStartable(ProcessDescriptor descriptor, string? entryPointKey)
    {
        if (descriptor.Status != ProcessStatus.Active)
        {
            var (code, detail) = descriptor.Status switch
            {
                ProcessStatus.Suspended => (ErrorCodes.ProcessSuspended, "流程已挂起，不能发起新实例。"),
                ProcessStatus.Retired => (ErrorCodes.ProcessSuspended, "流程已下线，不能发起新实例。"),
                _ => (ErrorCodes.ProcessNotDeployed, "流程尚未部署生效版本，不能发起。"),
            };

            throw StartException(code, "流程不可发起", StatusCodes.Status409Conflict, detail);
        }

        if (!string.IsNullOrWhiteSpace(entryPointKey)
            && descriptor.EntryPoints.All(entry => !string.Equals(entry.Key, entryPointKey, StringComparison.Ordinal)))
        {
            throw StartException(
                ErrorCodes.RequestMalformed,
                "入口标识不存在",
                StatusCodes.Status400BadRequest,
                $"入口 '{entryPointKey}' 不在流程 '{descriptor.ProcessKey}' 的发起入口里（"
                + string.Join(" / ", descriptor.EntryPoints.Select(entry => entry.Key)) + "）。");
        }
    }

    /// <summary>
    /// 组装发起要写进引擎的变量：<c>param.{name}</c> 剥前缀镜像为裸名，校验保留名 / 必填 / 类型。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 未声明参数的流程（schema 为空）按「无 schema 放行」：全部参数原样镜像，不校验（保持旧流程可用）。
    /// schema 非空时也只对<b>已声明</b>的参数做类型 / 约束校验，未声明的参数仍镜像透传——
    /// 因为 <c>formProperty</c> 派生覆盖不了全部变量（如 <c>${approver}</c> 这种候选组变量）。
    /// </para>
    /// </remarks>
    private static Dictionary<string, object?> BuildVariables(
        StartInstanceRequest request,
        ParameterSchema? schema)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        var definitions = schema?.Parameters ?? [];

        if (request.Parameters is { Count: > 0 })
        {
            foreach (var (name, value) in request.Parameters)
            {
                // 对外键 param.{name} 镜像回引擎裸名（与 BPMN 网关条件同变量名）
                var bareName = name.StartsWith(ParameterApiPrefix, StringComparison.Ordinal)
                    ? name[ParameterApiPrefix.Length..]
                    : name;

                if (BaseModelVariables.All.Contains(bareName, StringComparer.Ordinal))
                {
                    throw StartException(
                        ErrorCodes.ParameterConflictWithReserved,
                        "参数名与保留字段冲突",
                        StatusCodes.Status400BadRequest,
                        $"参数 '{name}' 与审批元数据字段 '{bareName}' 冲突。");
                }

                var definition = definitions.FirstOrDefault(item =>
                    string.Equals(item.VariableName, bareName, StringComparison.Ordinal)
                    || string.Equals(item.ApiName, name, StringComparison.Ordinal));

                if (definition is not null && value is { Length: > 0 })
                {
                    ValidateValue(definition, name, value);
                }

                if (value is not null)
                {
                    variables[bareName] = value;
                }
            }
        }

        // 必填缺失 → 422（在推进流程之前，保证「校验失败不产生实例」）
        foreach (var definition in definitions.Where(item => item.Required))
        {
            var bareName = definition.VariableName ?? StripPrefix(definition.ApiName);

            if (variables.TryGetValue(bareName, out var value) && value is not null)
            {
                continue;
            }

            throw StartException(
                ErrorCodes.ParameterRequiredMissing,
                "缺少必填参数",
                StatusCodes.Status422UnprocessableEntity,
                $"参数 '{definition.ApiName}'（{definition.DisplayName ?? bareName}）为必填。");
        }

        return variables;
    }

    /// <summary>按参数定义做类型与约束校验（值一律按字符串收，不做强转）。</summary>
    private static void ValidateValue(ParameterDefinition definition, string name, string value)
    {
        var valid = definition.Type switch
        {
            ParameterType.Long => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            ParameterType.Decimal => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            ParameterType.Boolean => bool.TryParse(value, out _),
            ParameterType.Date => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            ParameterType.DateTime => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            ParameterType.Enum => definition.Constraints?.EnumValues is not { Count: > 0 } enumValues
                || enumValues.Contains(value, StringComparer.Ordinal),
            ParameterType.Json => IsValidJson(value),
            _ => true,
        };

        if (!valid)
        {
            throw StartException(
                ErrorCodes.ParameterTypeInvalid,
                "参数类型不符",
                StatusCodes.Status422UnprocessableEntity,
                $"参数 '{name}' 的值 '{value}' 不符合类型 {definition.Type}。");
        }

        var constraints = definition.Constraints;

        if (constraints is null)
        {
            return;
        }

        if (constraints.MinLength is { } minLength && value.Length < minLength
            || constraints.MaxLength is { } maxLength && value.Length > maxLength)
        {
            throw StartException(
                ErrorCodes.ParameterTypeInvalid,
                "参数长度不符",
                StatusCodes.Status422UnprocessableEntity,
                $"参数 '{name}' 的长度 {value.Length} 不在允许范围（{LengthRangeText(constraints)}）。");
        }

        if (constraints.Minimum is { } || constraints.Maximum is { })
        {
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric)
                && (constraints.Minimum is { } min && numeric < min
                    || constraints.Maximum is { } max && numeric > max))
            {
                throw StartException(
                    ErrorCodes.ParameterTypeInvalid,
                    "参数取值越界",
                    StatusCodes.Status422UnprocessableEntity,
                    $"参数 '{name}' 的值 '{value}' 不在允许范围（{NumericRangeText(constraints)}）。");
            }
        }

        if (constraints.Pattern is { Length: > 0 } pattern
            && !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
        {
            throw StartException(
                ErrorCodes.ParameterTypeInvalid,
                "参数格式不符",
                StatusCodes.Status422UnprocessableEntity,
                $"参数 '{name}' 的值 '{value}' 不匹配格式 {pattern}。");
        }
    }

    private static string LengthRangeText(ParameterConstraints constraints) =>
        constraints.MinLength is { } min && constraints.MaxLength is { } max
            ? $"[{min}, {max}]"
            : constraints.MinLength is { } minOnly
                ? $"[{minOnly}, ∞)"
                : $"(-∞, {constraints.MaxLength}]";

    private static string NumericRangeText(ParameterConstraints constraints) =>
        constraints.Minimum is { } min && constraints.Maximum is { } max
            ? $"[{min}, {max}]"
            : constraints.Minimum is { } minOnly
                ? $"[{minOnly}, ∞)"
                : $"(-∞, {constraints.Maximum}]";

    /// <summary>剥 <c>param.</c> 前缀（<see cref="ParameterDefinition.ApiName"/> → 裸名）。</summary>
    private static string StripPrefix(string apiName) =>
        apiName.StartsWith(ParameterApiPrefix, StringComparison.Ordinal)
            ? apiName[ParameterApiPrefix.Length..]
            : apiName;

    /// <summary>粗略 JSON 校验（只保证能解析，不校验结构）。</summary>
    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(value);

            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static StartInstanceException StartException(
        string code,
        string title,
        int status,
        string? detail = null) => new(code, title, status, detail);
}

/// <summary>
/// 发起的可预期拒绝（流程不存在 / 不可发起 / 参数校验失败），携带稳定错误码与状态码。
/// </summary>
/// <remarks>由端点捕获后转 <see cref="PlatformProblems.Create"/>，不经过全局异常处理器的旧错误体。</remarks>
internal sealed class StartInstanceException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="code">稳定错误码（见 <see cref="ErrorCodes"/>）。</param>
    /// <param name="title">短标题。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <param name="detail">具体说明。</param>
    public StartInstanceException(string code, string title, int status, string? detail = null)
        : base(title)
    {
        Code = code;
        Title = title;
        Status = status;
        Detail = detail;
    }

    /// <summary>稳定错误码。</summary>
    public string Code { get; }

    /// <summary>短标题。</summary>
    public string Title { get; }

    /// <summary>HTTP 状态码。</summary>
    public int Status { get; }

    /// <summary>具体说明。</summary>
    public string? Detail { get; }
}

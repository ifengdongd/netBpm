using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 管理面：<b>审批语义字典的写路径</b>（<c>/admin/v1/dictionaries</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 读在同一组路径上的 <see cref="AdminReadEndpoints"/>（<c>GET /dictionaries</c>），
/// 写在这里：读端点的口径是「库里存了什么」，写端点额外承担「按定义校验」的职责。
/// 分成两个文件是为了让「只读观测」那组保持只读——一个文件里既有 GET 又有 PUT，
/// 后来者很难一眼看出哪些动过数据。
/// </para>
/// <para>
/// 三条纪律，与目录覆盖行一致：
/// ① scope 是 <c>workflow:admin:process</c>（字典属于流程能力域，不是客户端接入）；
/// ② 错误体 RFC 9457，错误码集中在 <see cref="ErrorCodes"/>；
/// ③ 每次写入都进管理审计（谁把哪个选项的语义改了，必须可回查）。
/// </para>
/// <para>
/// 🔑 <b>不接受请求体里的 tenantId</b>：字典跟着当前管理身份所属租户走。
/// 允许指定就等于允许一个租户的管理员改另一个租户的审批口径。
/// </para>
/// </remarks>
public static class AdminDictionaryEndpoints
{
    private const string DefaultTenantId = "default";

    /// <summary>注册端点。</summary>
    /// <param name="endpoints">端点构建器。</param>
    public static IEndpointRouteBuilder MapAdminDictionaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/admin/v1").WithTags("platform-admin-dictionary");

        // 定义里有哪些选项可以配（字典维护的「可选范围」）
        group.MapGet("/dictionaries/{processKey}/declared", GetDeclaredOptionsAsync)
            .Produces<DeclaredApprovalOptionsView>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapPut("/dictionaries/{processKey}/options/{optionId}", UpsertAsync)
            .Accepts<ApprovalDictionaryUpsertRequest>("application/json")
            .Produces<ApprovalDictionaryEntryView>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status422UnprocessableEntity);

        // 删除：nodeId 走查询参数（同一个 optionId 可以同时有流程级与节点级两行，
        // 路径里再塞一段会让「删哪一条」变得不直观）
        group.MapDelete("/dictionaries/{processKey}/options/{optionId}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetDeclaredOptionsAsync(
        string processKey,
        HttpContext http,
        RequestContext context,
        IApprovalDictionaryAdmin admin,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminProcess))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminProcess);
        }

        var declared = await admin.GetDeclaredOptionsAsync(processKey, ct).ConfigureAwait(false);

        return declared is null
            ? PlatformProblems.ProcessNotFound(http, context, processKey)
            : Results.Ok(declared);
    }

    private static async Task<IResult> UpsertAsync(
        string processKey,
        string optionId,
        ApprovalDictionaryUpsertRequest? request,
        HttpContext http,
        RequestContext context,
        IApprovalDictionaryAdmin admin,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminProcess))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminProcess);
        }

        if (request is null)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestBodyRequired,
                "缺少请求体",
                StatusCodes.Status400BadRequest,
                "PUT 需要完整的映射（semantic 必填）；要删除映射请用 DELETE。");
        }

        var tenantId = ResolveTenant(context);

        try
        {
            var stored = await admin
                .UpsertAsync(tenantId, processKey, optionId, request, ct)
                .ConfigureAwait(false);

            await auditor.RecordAsync(
                context,
                AdminAuditActions.DictionaryUpsert,
                AdminAuditTargets.ApprovalDictionary,
                $"{processKey}/{stored.NodeId ?? "*"}/{optionId}",
                detail: new
                {
                    stored.ProcessKey,
                    stored.NodeId,
                    stored.OptionId,
                    Semantic = stored.Semantic.ToString(),
                    OutcomeClass = stored.OutcomeClass.ToString(),
                    stored.Label,
                    stored.RequiresComment,
                    stored.RequiresTargetNode,
                    TargetNodes = stored.AllowedTargetNodes,
                    // 只记属性名不记值：附加属性里可能有业务侧的敏感配置
                    AttributeKeys = stored.Attributes?.Keys.ToArray(),
                    Caller = context.CallerId,
                },
                ct: ct).ConfigureAwait(false);

            return Results.Ok(ToView(stored));
        }
        catch (PlatformActionRejectedException exception)
        {
            await auditor.RecordAsync(
                context,
                AdminAuditActions.DictionaryUpsert,
                AdminAuditTargets.ApprovalDictionary,
                $"{processKey}/{request.NodeId ?? "*"}/{optionId}",
                detail: new { Caller = context.CallerId },
                success: false,
                error: exception.Code,
                ct: ct).ConfigureAwait(false);

            return PlatformProblems.Create(
                http,
                context,
                exception.Code,
                exception.Title,
                exception.Status,
                exception.Detail);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string processKey,
        string optionId,
        string? nodeId,
        HttpContext http,
        RequestContext context,
        IApprovalDictionaryAdmin admin,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminProcess))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminProcess);
        }

        var removed = await admin
            .DeleteAsync(ResolveTenant(context), processKey, nodeId, optionId, ct)
            .ConfigureAwait(false);

        if (!removed)
        {
            // 404 而不是 204：删一个不存在的映射，说明「nodeId 少写了一段，删的是流程级」
            // 或「流程 key 拼错了」。静默成功会让调用方以为已经清干净。
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.DictionaryEntryNotFound,
                "字典行不存在",
                StatusCodes.Status404NotFound,
                nodeId is null
                    ? $"流程 '{processKey}' 没有选项 '{optionId}' 的流程级映射。"
                    : $"流程 '{processKey}' 的环节 '{nodeId}' 没有选项 '{optionId}' 的映射。");
        }

        await auditor.RecordAsync(
            context,
            AdminAuditActions.DictionaryDelete,
            AdminAuditTargets.ApprovalDictionary,
            $"{processKey}/{nodeId ?? "*"}/{optionId}",
            detail: new { ProcessKey = processKey, NodeId = nodeId, OptionId = optionId, Caller = context.CallerId },
            ct: ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>字典的租户跟着管理身份走；没有租户上下文的部署落到 <c>default</c>。</summary>
    private static string ResolveTenant(RequestContext context) =>
        string.IsNullOrWhiteSpace(context.EffectiveTenantId) ? DefaultTenantId : context.EffectiveTenantId;

    private static ApprovalDictionaryEntryView ToView(ApprovalDictionaryEntry entry) => new(
        entry.TenantId,
        entry.ProcessKey,
        entry.NodeId,
        entry.OptionId,
        entry.Semantic,
        entry.OutcomeClass,
        entry.Label,
        entry.RequiresComment,
        entry.RequiresTargetNode,
        entry.AllowedTargetNodes ?? [],
        entry.Attributes ?? new Dictionary<string, string>(StringComparer.Ordinal),
        entry.CreatedAt,
        entry.UpdatedAt);
}

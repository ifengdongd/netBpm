using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Persistence.Admin;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 管理面：<b>流程目录覆盖行</b>（<c>/admin/v1/processes</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这组端点的存在理由：目录覆盖行里放的是「给人看的名片」——分类、图标、业务页面地址、
/// 显式上下线状态。它们不在 BPMN 里（BPMN 是流程逻辑，不是运营配置），
/// 没有写入口就只能改库，而改库的后果是「谁在什么时候把这条流程下线了」永远查不到。
/// </para>
/// <para>
/// 与 <c>/workflow/v1/processes</c> 的分工：
/// <list type="bullet">
///   <item><description>业务面只读，返回<b>合并三层之后</b>的描述符；</description></item>
///   <item><description>管理面读写，返回<b>覆盖行本身</b>——管理端要能区分「精修过」与「BPMN 派生」，
///     拿到描述符永远分不清这两个来源。</description></item>
/// </list>
/// </para>
/// <para>
/// 三条纪律：
/// ① scope 是 <c>workflow:admin:process</c>（部署 / 挂起同属一个能力域），且 <c>workflow:*</c> 不覆盖它；
/// ② 错误体用 RFC 9457（<see cref="PlatformProblem"/>），与业务面同一套错误码；
/// ③ 每次写入都进管理审计，明细里带 <c>caller</c> 与覆盖了哪些字段。
/// </para>
/// <para>
/// 路径放在 <c>/admin/v1</c> 而不是 <c>/api/admin</c>（后者是过渡期的旧管理面）：
/// 这一组从第一天就用平台面的鉴权与错误体，不需要跟着旧端点一起迁。
/// </para>
/// </remarks>
public static class AdminProcessCatalogEndpoints
{
    /// <summary>注册端点。</summary>
    /// <param name="endpoints">端点构建器。</param>
    public static IEndpointRouteBuilder MapAdminProcessCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/admin/v1").WithTags("platform-admin-catalog");

        // 列出「被精修过的流程」：没有覆盖行的流程不出现（它们的名片全部由 BPMN 派生，
        // 管理端不需要看一份几十条的清单去确认「这条没改过」）
        group.MapGet("/processes", ListOverridesAsync)
            .Produces<IReadOnlyList<ProcessCatalogOverride>>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        group.MapGet("/processes/{processKey}", FindOverrideAsync)
            .Produces<ProcessCatalogOverride>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapPut("/processes/{processKey}", UpsertOverrideAsync)
            .Accepts<ProcessCatalogOverrideRequest>("application/json")
            .Produces<ProcessCatalogOverride>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/processes/{processKey}", DeleteOverrideAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListOverridesAsync(
        HttpContext http,
        RequestContext context,
        IProcessCatalogAdmin catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminProcess))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminProcess);
        }

        var overrides = await catalog.ListOverridesAsync(context, ct).ConfigureAwait(false);

        return Results.Ok(overrides);
    }

    private static async Task<IResult> FindOverrideAsync(
        string processKey,
        HttpContext http,
        RequestContext context,
        IProcessCatalogAdmin catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminProcess))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminProcess);
        }

        var entry = await catalog.FindOverrideAsync(processKey, context, ct).ConfigureAwait(false);

        return entry is null
            ? OverrideNotFound(http, context, processKey)
            : Results.Ok(entry);
    }

    private static async Task<IResult> UpsertOverrideAsync(
        string processKey,
        ProcessCatalogOverrideRequest? request,
        HttpContext http,
        RequestContext context,
        IProcessCatalogAdmin catalog,
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
                "PUT /admin/v1/processes/{processKey} 需要完整的覆盖行；不要覆盖任何字段就删掉这条覆盖。");
        }

        try
        {
            var entry = await catalog
                .UpsertOverrideAsync(processKey, request, context, ct)
                .ConfigureAwait(false);

            if (entry is null)
            {
                await RecordAsync(
                    auditor,
                    context,
                    AdminAuditActions.ProcessCatalogUpsert,
                    processKey,
                    request,
                    success: false,
                    error: "流程不存在",
                    ct).ConfigureAwait(false);

                return PlatformProblems.ProcessNotFound(http, context, processKey);
            }

            await RecordAsync(
                auditor,
                context,
                AdminAuditActions.ProcessCatalogUpsert,
                processKey,
                request,
                success: true,
                error: null,
                ct).ConfigureAwait(false);

            return Results.Ok(entry);
        }
        catch (PlatformActionRejectedException exception)
        {
            await RecordAsync(
                auditor,
                context,
                AdminAuditActions.ProcessCatalogUpsert,
                processKey,
                request,
                success: false,
                error: exception.Code,
                ct).ConfigureAwait(false);

            return PlatformProblems.Create(
                http,
                context,
                exception.Code,
                exception.Title,
                exception.Status,
                exception.Detail);
        }
    }

    private static async Task<IResult> DeleteOverrideAsync(
        string processKey,
        HttpContext http,
        RequestContext context,
        IProcessCatalogAdmin catalog,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminProcess))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminProcess);
        }

        if (!await catalog.DeleteOverrideAsync(processKey, context, ct).ConfigureAwait(false))
        {
            return OverrideNotFound(http, context, processKey);
        }

        await auditor.RecordAsync(
            context,
            AdminAuditActions.ProcessCatalogDelete,
            AdminAuditTargets.ProcessCatalog,
            processKey,
            detail: new { Caller = context.CallerId },
            ct: ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// 404：这条流程没有覆盖行。
    /// </summary>
    /// <remarks>
    /// 用 <c>catalog.override_not_found</c> 而不是 <c>process.not_found</c>：
    /// 「流程不存在」和「流程存在但没有精修过」对管理端是两件事——
    /// 前者说明 key 打错了，后者只需要 PUT 一次就能建出来。
    /// </remarks>
    private static IResult OverrideNotFound(HttpContext http, RequestContext context, string processKey) =>
        PlatformProblems.Create(
            http,
            context,
            ErrorCodes.CatalogOverrideNotFound,
            "覆盖行不存在",
            StatusCodes.Status404NotFound,
            $"流程 '{processKey}' 没有目录覆盖行（名片全部由 BPMN 派生）。用 PUT 建立覆盖。");

    /// <summary>写审计：只记「覆盖了哪些字段」，不记字段值（业务页面地址可能带内部域名）。</summary>
    private static Task RecordAsync(
        AdminActionAuditor auditor,
        RequestContext context,
        string action,
        string processKey,
        ProcessCatalogOverrideRequest request,
        bool success,
        string? error,
        CancellationToken ct) =>
        auditor.RecordAsync(
            context,
            action,
            AdminAuditTargets.ProcessCatalog,
            processKey,
            detail: new
            {
                Fields = DescribeOverriddenFields(request),
                Status = request.Status?.ToString(),
                SortOrder = request.SortOrder,
                Caller = context.CallerId,
            },
            success: success,
            error: error,
            ct: ct);

    /// <summary>列出本次请求覆盖了哪些字段（审计里一眼能看出「这次改的是分类还是业务页面」）。</summary>
    private static string[] DescribeOverriddenFields(ProcessCatalogOverrideRequest request)
    {
        var fields = new List<string>(10);

        if (request.Name is not null)
        {
            fields.Add(nameof(request.Name));
        }

        if (request.Category is not null)
        {
            fields.Add(nameof(request.Category));
        }

        if (request.Description is not null)
        {
            fields.Add(nameof(request.Description));
        }

        if (request.Icon is not null)
        {
            fields.Add(nameof(request.Icon));
        }

        if (request.PageUrls is { Count: > 0 })
        {
            fields.Add(nameof(request.PageUrls));
        }

        if (request.EntryPoints is { Count: > 0 })
        {
            fields.Add(nameof(request.EntryPoints));
        }

        if (request.Status is not null)
        {
            fields.Add(nameof(request.Status));
        }

        if (request.SortOrder is not null)
        {
            fields.Add(nameof(request.SortOrder));
        }

        if (request.Labels is { Count: > 0 })
        {
            fields.Add(nameof(request.Labels));
        }

        if (request.ParameterSchemaVersion is not null)
        {
            fields.Add(nameof(request.ParameterSchemaVersion));
        }

        return [.. fields];
    }
}

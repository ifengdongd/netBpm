using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 平台业务面：<b>流程目录</b>（<c>/workflow/v1/processes</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么单独一组而不是塞进 <c>/api/**</c>：<c>/workflow/v1</c> 是<b>对外契约</b>，
/// 一旦发布就要保持向后兼容；<c>/api/**</c> 是引擎原语的直通面，随引擎演进（文档里标
/// <c>stability: experimental</c>）。两者的变更纪律不同，路径必须先分开。
/// </para>
/// <para>
/// 五个端点全部读同一个 <see cref="IProcessCatalog"/>，因此「目录里看到的」与
/// 「按 key 取到的」永远一致——不会出现列表能查到、详情 404 的分裂。
/// </para>
/// <para>错误体一律 RFC 9457（见 <see cref="PlatformProblems"/>），与旧端点并存但互不影响。</para>
/// </remarks>
public static class ProcessCatalogEndpoints
{
    /// <summary>注册流程目录端点。</summary>
    /// <param name="endpoints">端点构建器。</param>
    public static IEndpointRouteBuilder MapProcessCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/workflow/v1").WithTags("workflow-catalog");

        group.MapGet("/processes", ListAsync)
            .Produces<CursorPage<ProcessDescriptor>>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        group.MapGet("/processes/{processKey}", FindAsync)
            .Produces<ProcessDescriptor>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/processes/{processKey}/definition", GetDefinitionAsync)
            .Produces<ProcessDefinitionInfo>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/processes/{processKey}/versions", ListVersionsAsync)
            .Produces<IReadOnlyList<ProcessVersionInfo>>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/processes/{processKey}/parameters/schema", GetParameterSchemaAsync)
            .Produces<ParameterSchema>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        // 流程图直接给 SVG：浏览器 / <img> 就能显示，调用方不必为了画一张图引入 BPMN 渲染库
        group.MapGet("/processes/{processKey}/diagram.svg", GetDiagramAsync)
            .Produces<string>(StatusCodes.Status200OK, "image/svg+xml")
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? keyword,
        string? category,
        string? status,
        string? cursor,
        int? limit,
        HttpContext http,
        RequestContext context,
        IProcessCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.ProcessRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.ProcessRead);
        }

        var query = new ProcessQuery(keyword, category, null, cursor, limit);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ProcessStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
            {
                return PlatformProblems.Create(
                    http,
                    context,
                    ErrorCodes.RequestMalformed,
                    "查询参数不合法",
                    StatusCodes.Status400BadRequest,
                    $"status 的取值必须是 DRAFT / ACTIVE / SUSPENDED / RETIRED，实际为 '{status}'。");
            }

            query = query with { Status = parsedStatus };
        }

        try
        {
            var page = await catalog.ListAsync(query, context, ct).ConfigureAwait(false);

            return Results.Ok(page);
        }
        catch (FormatException exception)
        {
            // 游标是服务端生成的不透明串；解不开只能是调用方改过它
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "查询参数不合法",
                StatusCodes.Status400BadRequest,
                exception.Message);
        }
    }

    private static async Task<IResult> FindAsync(
        string processKey,
        HttpContext http,
        RequestContext context,
        IProcessCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.ProcessRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.ProcessRead);
        }

        var descriptor = await catalog.FindAsync(processKey, context, ct).ConfigureAwait(false);

        return descriptor is null
            ? PlatformProblems.ProcessNotFound(http, context, processKey)
            : Results.Ok(descriptor);
    }

    private static async Task<IResult> GetDefinitionAsync(
        string processKey,
        bool? includeXml,
        HttpContext http,
        RequestContext context,
        IProcessCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.ProcessRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.ProcessRead);
        }

        // 默认不返回 XML：BPMN 原文有几十 KB，绝大多数调用方只需要结构化信息
        var definition = await catalog
            .GetDefinitionAsync(processKey, includeXml ?? false, context, ct)
            .ConfigureAwait(false);

        return definition is null
            ? PlatformProblems.ProcessNotFound(http, context, processKey)
            : Results.Ok(definition);
    }

    private static async Task<IResult> ListVersionsAsync(
        string processKey,
        HttpContext http,
        RequestContext context,
        IProcessCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.ProcessRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.ProcessRead);
        }

        var versions = await catalog.ListVersionsAsync(processKey, context, ct).ConfigureAwait(false);

        return versions is null
            ? PlatformProblems.ProcessNotFound(http, context, processKey)
            : Results.Ok(versions);
    }

    private static async Task<IResult> GetDiagramAsync(
        string processKey,
        int? version,
        HttpContext http,
        RequestContext context,
        IProcessCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.ProcessRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.ProcessRead);
        }

        var svg = await catalog.GetDiagramSvgAsync(processKey, version, context, ct).ConfigureAwait(false);

        return svg is null
            ? PlatformProblems.ProcessNotFound(http, context, processKey)
            : Results.Content(svg, "image/svg+xml");
    }

    private static async Task<IResult> GetParameterSchemaAsync(
        string processKey,
        HttpContext http,
        RequestContext context,
        IProcessCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.ProcessRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.ProcessRead);
        }

        var schema = await catalog.GetParameterSchemaAsync(processKey, context, ct).ConfigureAwait(false);

        return schema is null
            ? PlatformProblems.ProcessNotFound(http, context, processKey)
            : Results.Ok(schema);
    }
}

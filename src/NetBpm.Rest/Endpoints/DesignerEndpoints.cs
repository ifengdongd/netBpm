using System.Text;
using NetBpm.Engine;
using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Deployment;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 设计器契约端点（开发计划 §6.3：设计器与后端只有两个契约）。
/// <list type="bullet">
///   <item><description><c>POST /api/designer/deploy</c> —— 提交 BPMN XML；</description></item>
///   <item><description><c>GET  /api/designer/definition/{id}/xml</c> —— 回显 BPMN XML。</description></item>
/// </list>
/// <para>设计器<b>不直接操作数据库</b>，一切经后端解析 + 校验 + 落库。</para>
/// </summary>
public static class DesignerEndpoints
{
    /// <summary>定义列表默认页大小。</summary>
    private const int DefaultPageSize = 20;

    /// <summary>定义列表页大小上限。</summary>
    private const int MaxPageSize = 200;

    public static RouteGroupBuilder MapDesignerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/designer").WithTags("designer");

        // 🔐 设计器是管理工具：发布定义 / 定义级挂起 / 删部署都改变"线上行为"，需要管理 scope
        // （validate 只解析校验、不落库，保持开放；读接口只受租户约束）
        group.MapPost("/deploy", DeployAsync).Accepts<string>("text/xml").Produces<DeployResponse>()
            .RequireAdminScope(TaskScopes.AdminModelWrite, "部署流程定义");
        group.MapPost("/validate", ValidateAsync).Accepts<string>("text/xml").Produces<ValidationResponse>();
        group.MapGet("/definitions", FindDefinitionsAsync).Produces<IReadOnlyList<ProcessDefinitionResponse>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看流程定义");
        group.MapGet("/definitions/{processDefinitionKey}/versions", FindDefinitionVersionsAsync)
            .Produces<IReadOnlyList<ProcessDefinitionResponse>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看流程定义");
        group.MapGet("/definition/{processDefinitionId}/xml", GetDefinitionXmlAsync).Produces<string>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "回显流程定义");

        // 流程定义挂起 / 激活（仓库服务语义，不属于设计器契约；放在同一组只是为了按定义聚合端点）
        // ?suspendProcessInstances=true / ?activateProcessInstances=true 一并挂起（激活）运行中的实例
        group.MapPost("/definitions/{processDefinitionId}/suspend", SuspendDefinitionAsync)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "挂起流程定义");
        group.MapPost("/definitions/{processDefinitionId}/activate", ActivateDefinitionAsync)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "激活流程定义");

        // 部署维度：列表 + 资源清单（删除见下方 DELETE）
        group.MapGet("/deployments", FindDeploymentsAsync).Produces<IReadOnlyList<DeploymentSummaryResponse>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看部署列表");
        group.MapGet("/deployments/{deploymentId}/resources", FindDeploymentResourcesAsync)
            .Produces<IReadOnlyList<DeploymentResourceResponse>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看部署资源");

        // 删除部署（repositoryService.deleteDeployment）：cascade=true 时连同运行中的实例一起删
        group.MapDelete("/deployments/{deploymentId}", DeleteDeploymentAsync)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "删除部署");

        return group;
    }

    private static async Task<IResult> DeployAsync(
        HttpRequest request,
        string? tenantId,
        string? resourceName,
        string? deploymentName,
        RequestContext requestContext,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        // JWT 下租户只能来自令牌；API Key / 开发模式下显式传参优先
        var tenant = RequestIdentity.Tenant(tenantId, requestContext);

        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var xml = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(xml))
        {
            return Results.BadRequest(new ErrorResponse("请求体必须为 BPMN 2.0 XML 文本。"));
        }

        var resolvedResourceName = string.IsNullOrWhiteSpace(resourceName)
            ? "designer.bpmn20.xml"
            : resourceName;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await engine.DeployWithValidationAsync(
                stream,
                resolvedResourceName,
                deploymentName: string.IsNullOrWhiteSpace(deploymentName) ? null : deploymentName,
                tenantId: tenant,
                ct: ct)
            .ConfigureAwait(false);

        var definition = result.ProcessDefinition;

        // 只记「部署成功」：设计器会在编辑过程中反复提交、校验失败很常见，
        // 若失败也记会把审计表刷满（与 validate 的噪声同源）。
        await auditor.RecordAsync(
            requestContext,
            AdminAuditActions.DeploymentCreate,
            AdminAuditTargets.Deployment,
            definition.DeploymentId,
            processInstanceId: null,
            detail: new
            {
                processDefinitionId = definition.Id,
                processDefinitionKey = definition.Key,
                version = definition.Version,
                resourceName = definition.ResourceName,
                deploymentName = string.IsNullOrWhiteSpace(deploymentName) ? null : deploymentName,
                warningCount = result.Warnings.Count,
            },
            success: true,
            ct: ct).ConfigureAwait(false);

        return Results.Created(
            $"/api/designer/definitions/{definition.Id}",
            new DeployResponse(
                definition.Id,
                definition.Key,
                definition.Version,
                definition.DeploymentId,
                definition.ResourceName,
                DeploymentName: string.IsNullOrWhiteSpace(deploymentName) ? null : deploymentName,
                Warnings: result.Warnings.Count == 0 ? null : result.Warnings));
    }

    /// <summary>
    /// 试校验 BPMN XML（<b>不落库</b>）：返回全部阻断级 / 提示级问题。
    /// <para>与部署共用同一个校验器，设计器可以「保存前先校验」而不产生部署记录。</para>
    /// </summary>
    private static async Task<IResult> ValidateAsync(
        HttpRequest request,
        IProcessEngine engine,
        CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var xml = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(xml))
        {
            return Results.BadRequest(new ErrorResponse("请求体必须为 BPMN 2.0 XML 文本。"));
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var results = await engine.ValidateAsync(stream, ct).ConfigureAwait(false);

        return Results.Ok(ValidationResponse.From(results));
    }

    /// <summary>
    /// 已部署流程定义列表（按部署时间倒序）。
    /// <para>
    /// 过滤：<c>tenantId</c> / <c>key</c>；分页 <c>firstResult</c> / <c>maxResults</c>
    /// （默认 20，上限 200），总数放 <c>X-Total-Count</c>。
    /// </para>
    /// </summary>
    private static async Task<IResult> FindDefinitionsAsync(
        string? tenantId,
        string? key,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        HttpResponse response,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var tenant = RequestIdentity.Tenant(tenantId, requestContext);

        var first = firstResult ?? 0;
        var size = Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize);

        var query = new ProcessDefinitionQuery(key, tenant, first, size);

        var definitions = await engine.FindProcessDefinitionsAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountProcessDefinitionsAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            definitions.Select(ToResponse).ToArray(),
            total,
            first,
            size,
            response,
            envelope ?? false);
    }

    /// <summary>按流程 key 查全部版本（版本管理 / 回滚参照），按版本号倒序；分页同其它列表端点。</summary>
    private static async Task<IResult> FindDefinitionVersionsAsync(
        string processDefinitionKey,
        string? tenantId,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        HttpResponse response,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var tenant = RequestIdentity.Tenant(tenantId, requestContext);

        var first = firstResult ?? 0;
        var size = Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize);

        var query = new ProcessDefinitionQuery(processDefinitionKey, tenant, first, size);

        var definitions = await engine.FindProcessDefinitionsAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountProcessDefinitionsAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            definitions.OrderByDescending(definition => definition.Version).Select(ToResponse).ToArray(),
            total,
            first,
            size,
            response,
            envelope ?? false);
    }

    private static ProcessDefinitionResponse ToResponse(DeployedProcessDefinition definition) =>
        new(
            definition.Id,
            definition.Key,
            definition.Name,
            definition.Version,
            definition.DeploymentId,
            definition.TenantId ?? string.Empty,
            definition.DeployTime,
            definition.IsSuspended);

    /// <summary>
    /// 挂起流程定义：之后不能再启动新实例。
    /// <para><c>suspendProcessInstances=true</c> 时连同运行中的实例一起挂起（待办不可完成 / 认领）。</para>
    /// </summary>
    private static async Task<IResult> SuspendDefinitionAsync(
        string processDefinitionId,
        bool? suspendProcessInstances,
        RequestContext requestContext,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        await EnsureDefinitionAccessAsync(engine, requestContext, processDefinitionId, ct).ConfigureAwait(false);

        return await AuditedAction.RunAsync(
            auditor,
            requestContext,
            AdminAuditActions.DefinitionSuspend,
            AdminAuditTargets.ProcessDefinition,
            processDefinitionId,
            processInstanceId: null,
            new { suspendProcessInstances = suspendProcessInstances ?? false },
            async () =>
            {
                await engine
                    .SuspendProcessDefinitionAsync(processDefinitionId, suspendProcessInstances ?? false, ct)
                    .ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>激活流程定义：恢复「可启动新实例」；<c>activateProcessInstances=true</c> 时一并激活实例。</summary>
    private static async Task<IResult> ActivateDefinitionAsync(
        string processDefinitionId,
        bool? activateProcessInstances,
        RequestContext requestContext,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        await EnsureDefinitionAccessAsync(engine, requestContext, processDefinitionId, ct).ConfigureAwait(false);

        return await AuditedAction.RunAsync(
            auditor,
            requestContext,
            AdminAuditActions.DefinitionActivate,
            AdminAuditTargets.ProcessDefinition,
            processDefinitionId,
            processInstanceId: null,
            new { activateProcessInstances = activateProcessInstances ?? false },
            async () =>
            {
                await engine
                    .ActivateProcessDefinitionAsync(processDefinitionId, activateProcessInstances ?? false, ct)
                    .ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 按定义 id 校验租户归属（仅 JWT 生效）：不属于令牌租户的一律按 404 处理。
    /// <para>本子集定义数量有限，这里用租户过滤后的列表做归属判断；API Key 模式保持跨租户管理能力。</para>
    /// </summary>
    private static async Task EnsureDefinitionAccessAsync(
        IProcessEngine engine,
        RequestContext requestContext,
        string processDefinitionId,
        CancellationToken ct)
    {
        if (!requestContext.IsTenantRestricted)
        {
            return;
        }

        var definitions = await engine
            .FindProcessDefinitionsAsync(
                new ProcessDefinitionQuery(TenantId: RequestIdentity.Tenant(null, requestContext)), ct)
            .ConfigureAwait(false);

        if (!definitions.Any(definition => string.Equals(
                definition.Id,
                processDefinitionId,
                StringComparison.Ordinal)))
        {
            throw new EngineObjectNotFoundException($"流程定义 '{processDefinitionId}' 不存在。");
        }
    }

    /// <summary>
    /// 部署列表：按 <c>deploymentId</c> 聚合定义（一次部署一行），按部署时间倒序。
    /// <para>
    /// 本子集的部署与定义一一对应（一次 <c>deploy</c> 落一条部署行 + 一条定义行），
    /// 因此这里从定义列表聚合，不额外查 <c>ACT_RE_DEPLOYMENT</c>，避免两套分页口径。
    /// 分页在聚合后按部署切片，总数放 <c>X-Total-Count</c>。
    /// </para>
    /// </summary>
    private static async Task<IResult> FindDeploymentsAsync(
        string? tenantId,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        HttpResponse response,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var tenant = RequestIdentity.Tenant(tenantId, requestContext);

        var first = firstResult ?? 0;
        var size = Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize);

        var definitions = await engine
            .FindProcessDefinitionsAsync(new ProcessDefinitionQuery(TenantId: tenant), ct)
            .ConfigureAwait(false);

        var deployments = definitions
            .GroupBy(definition => definition.DeploymentId, StringComparer.Ordinal)
            .Select(group =>
            {
                var latest = group.MaxBy(definition => definition.Version)!;

                return new DeploymentSummaryResponse(
                    group.Key,
                    latest.DeploymentName,
                    latest.DeployTime,
                    latest.TenantId,
                    [.. group
                        .OrderByDescending(definition => definition.Version)
                        .Select(definition => new DeploymentDefinitionResponse(
                            definition.Id,
                            definition.Key,
                            definition.Name,
                            definition.Version,
                            definition.ResourceName,
                            definition.IsSuspended))]);
            })
            .OrderByDescending(deployment => deployment.DeployTime ?? DateTime.MinValue)
            .ToArray();

        var total = deployments.Length;
        var page = deployments.Skip(first).Take(size).ToArray();

        return PagedResults.Create(page, total, first, size, response, envelope ?? false);
    }

    /// <summary>某次部署的资源清单（资源名 → 流程定义）；部署不存在返回 404。</summary>
    private static async Task<IResult> FindDeploymentResourcesAsync(
        string deploymentId,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        // 受限身份只在本租户内查找；查不到按 404（不泄露其它租户的部署是否存在）
        var definitions = await engine
            .FindProcessDefinitionsAsync(
                new ProcessDefinitionQuery(
                    TenantId: requestContext.IsTenantRestricted
                        ? RequestIdentity.Tenant(null, requestContext)
                        : null),
                ct)
            .ConfigureAwait(false);

        var resources = definitions
            .Where(definition => string.Equals(
                definition.DeploymentId,
                deploymentId,
                StringComparison.Ordinal))
            .Select(definition => new DeploymentResourceResponse(
                definition.ResourceName,
                definition.Id))
            .ToArray();

        return resources.Length == 0
            ? Results.NotFound(new ErrorResponse($"部署 '{deploymentId}' 不存在。"))
            : Results.Ok(resources);
    }

    /// <summary>
    /// 删除部署。<c>cascade=false</c>（默认）时若仍有运行实例返回 409；
    /// <c>cascade=true</c> 时连同运行实例一起删（历史保留）。
    /// </summary>
    private static async Task<IResult> DeleteDeploymentAsync(
        string deploymentId,
        bool? cascade,
        RequestContext requestContext,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        // cascade=true 会连带删除运行中的实例：在 model.write 之外再要求实例删除权限，
        // 避免持有「改定义」权限的角色顺带清空运行数据
        if (cascade == true)
        {
            AdminAuthorization.Ensure(
                requestContext, TaskScopes.AdminInstanceDelete, "级联删除部署（连同运行实例）");
        }

        // 跨租户删除按 404 处理；仍有运行实例时由引擎抛 409（cascade=false）
        if (requestContext.IsTenantRestricted)
        {
            var definitions = await engine
                .FindProcessDefinitionsAsync(
                    new ProcessDefinitionQuery(TenantId: RequestIdentity.Tenant(null, requestContext)), ct)
                .ConfigureAwait(false);

            if (!definitions.Any(definition => string.Equals(
                    definition.DeploymentId,
                    deploymentId,
                    StringComparison.Ordinal)))
            {
                throw new EngineObjectNotFoundException($"部署 '{deploymentId}' 不存在。");
            }
        }

        return await AuditedAction.RunAsync(
            auditor,
            requestContext,
            AdminAuditActions.DeploymentDelete,
            AdminAuditTargets.Deployment,
            deploymentId,
            processInstanceId: null,
            new { cascade = cascade ?? false },
            async () =>
            {
                await engine.DeleteDeploymentAsync(deploymentId, cascade ?? false, ct).ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GetDefinitionXmlAsync(
        string processDefinitionId,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        await EnsureDefinitionAccessAsync(engine, requestContext, processDefinitionId, ct).ConfigureAwait(false);

        var xml = await engine.GetProcessDefinitionXmlAsync(processDefinitionId, ct).ConfigureAwait(false);

        return xml is null
            ? Results.NotFound(new ErrorResponse($"流程定义 '{processDefinitionId}' 不存在。"))
            : Results.Text(xml, "text/xml", Encoding.UTF8);
    }
}

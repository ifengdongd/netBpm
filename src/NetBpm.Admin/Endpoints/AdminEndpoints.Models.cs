using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetBpm.Admin.Storage;
using NetBpm.Bpmn.Converter;
using NetBpm.Persistence.Deployment;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Persistence.Admin;
using NetBpm.Rest;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点（partial）：流程模型（bpmn_model）与流程类型（workflow_type 注册表）的处理器。
/// 路由表见 <see cref="AdminEndpoints.MapAdminEndpoints"/>。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 分页查模型列表（按最近修改倒序；<c>keyword</c> 模糊匹配名称 / 流程 Key / 描述）。
    /// <para>
    /// 分页协议与引擎列表端点一致：默认裸数组 + <c>X-Total-Count</c>，
    /// <c>envelope=true</c> 返回 <see cref="PagedResponse{T}"/> 信封。
    /// </para>
    /// </summary>
    private static async Task<IResult> ListModelsAsync(
        string? keyword,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        BpmnModelStore store,
        HttpResponse response,
        CancellationToken ct)
    {
        var page = await store
            .ListAsync(
                Math.Max(0, firstResult ?? 0),
                Math.Clamp(maxResults ?? 100, 1, 200),
                keyword,
                ct)
            .ConfigureAwait(false);

        return PagedResults.Create(
            page.Items,
            page.Total,
            Math.Max(0, firstResult ?? 0),
            Math.Clamp(maxResults ?? 100, 1, 200),
            response,
            envelope == true);
    }

    private static async Task<IResult> GetModelAsync(
        string id,
        BpmnModelStore store,
        CancellationToken ct) =>
        await store.GetAsync(id, ct).ConfigureAwait(false) is { } model
            ? Results.Ok(model)
            : Results.NotFound();

    private static async Task<IResult> SaveModelAsync(
        string id,
        SaveBpmnModelRequest request,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BpmnXml))
        {
            return Results.BadRequest(new ErrorResponse("bpmnXml 不能为空。"));
        }

        IReadOnlyList<ValidationError> issues;

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.BpmnXml));
            issues = await engine.ValidateAsync(stream, ct).ConfigureAwait(false);
        }
        catch (BpmnXmlParseException exception)
        {
            return Results.BadRequest(new ErrorResponse($"BPMN XML 解析失败：{exception.Message}"));
        }

        var errors = issues.Where(issue => issue.IsError).ToArray();
        var warnings = issues.Where(issue => !issue.IsError).ToArray();

        // 草稿语义：校验结果如实返回，但不阻止保存半成品；发布/部署时再强制拦截。
        var revision = await store
            .SaveAsync(id, request, requestContext.UserId, "save", ct)
            .ConfigureAwait(false);

        return Results.Ok(new SaveBpmnModelResponse(id, revision, errors, warnings));
    }

    /// <summary>
    /// 删除模型（连同版本历史；已部署的定义不受影响）。删除写审计 model.delete。
    /// 模型不存在时 404 且不留审计（与发布一致：「删不存在的模型」不算一次管理动作）。
    /// </summary>
    private static async Task<IResult> DeleteModelAsync(
        string id,
        RequestContext requestContext,
        BpmnModelStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (await store.GetAsync(id, ct).ConfigureAwait(false) is null)
        {
            return Results.NotFound();
        }

        var removed = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ModelDelete,
            AdminAuditTargets.Model,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { modelId = id },
            successDetail: _ => new { modelId = id, removed = true },
            operation: async token => await store.DeleteAsync(id, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IReadOnlyList<BpmnModelRevisionSummary>> ListModelHistoryAsync(
        string id,
        BpmnModelStore store,
        CancellationToken ct) => await store.ListHistoryAsync(id, ct).ConfigureAwait(false);

    private static async Task<IResult> GetModelRevisionAsync(
        string id,
        string revisionId,
        BpmnModelStore store,
        CancellationToken ct) =>
        await store.GetRevisionAsync(id, revisionId, ct).ConfigureAwait(false) is { } revision
            ? Results.Ok(revision)
            : Results.NotFound();

    private static async Task<IResult> RollbackModelAsync(
        string id,
        string revisionId,
        BpmnModelStore store,
        RequestContext requestContext,
        CancellationToken ct) =>
        await store.RollbackAsync(id, revisionId, requestContext.UserId, ct).ConfigureAwait(false) is { } model
            ? Results.Ok(model)
            : Results.NotFound();

    /// <summary>
    /// 清理模型历史：只保留最近 <c>keepLatest</c> 个版本快照（默认 10，最新版本永远保留）。
    /// <para>清理本身也是管理动作（<c>model.history_prune</c>），成功 / 失败都写审计。</para>
    /// </summary>
    private static async Task<IResult> PruneModelHistoryAsync(
        string id,
        int? keepLatest,
        RequestContext requestContext,
        BpmnModelStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var keep = Math.Clamp(keepLatest ?? 10, 0, 1000);

        if (await store.GetAsync(id, ct).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        var removed = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ModelHistoryPrune,
            AdminAuditTargets.Model,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { modelId = id, keepLatest = keep },
            successDetail: removed => new { modelId = id, keepLatest = keep, removed },
            operation: async token => await store.PruneHistoryAsync(id, keep, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return Results.Ok(new ModelHistoryPruneResponse(removed, id, keep));
    }

    /// <summary>
    /// 发布模型：用当前 XML 走引擎部署（解析 + 校验 + 版本递增，与设计器部署同一套口径）。
    /// <para>
    /// 阻断级校验问题返回 400（<c>ProcessValidationException</c> → 400 + errors 明细），
    /// 提示级问题随响应 warnings 返回。请求体可省略；带 <c>svg</c> 时（管理端部署前用
    /// bpmn-js 渲染的流程图图片），把当前 XML 连同图片写入一条 <c>publish</c> 版本快照 ——
    /// 版本历史里即可查看「部署当时」的流程图。
    /// </para>
    /// <para>发布属高危管理动作，写审计 model.publish；模型不存在时 404 且不留审计。</para>
    /// </summary>
    private static async Task<IResult> PublishModelAsync(
        string id,
        HttpRequest httpRequest,
        RequestContext requestContext,
        BpmnModelStore store,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // 请求体可选：有 JSON body 才解析（无 body 的旧调用方 / 测试照常工作）。
        // 不用「可空 body 参数」绑定 —— 无 Content-Type 的 POST 会被推断为缺失 body 而拿不到正确响应。
        PublishModelRequest? request = null;

        if (httpRequest.ContentLength is > 0)
        {
            request = await httpRequest.ReadFromJsonAsync<PublishModelRequest>(ct).ConfigureAwait(false);
        }

        var model = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound();
        }

        ProcessDefinition definition = null!;
        IReadOnlyList<NetBpm.Bpmn.Validation.ValidationError> deployWarnings =
            Array.Empty<NetBpm.Bpmn.Validation.ValidationError>();

        definition = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ModelPublish,
            AdminAuditTargets.Model,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { modelId = id, processKey = model.ProcessKey },
            successDetail: deployed => new
            {
                modelId = id,
                processKey = model.ProcessKey,
                definitionId = deployed.Id,
                version = deployed.Version,
                deploymentId = deployed.DeploymentId,
            },
            operation: async token =>
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(model.BpmnXml));

                var result = await engine
                    .DeployWithValidationAsync(
                        stream,
                        $"{model.Id}.bpmn20.xml",
                        $"模型发布：{model.Name}",
                        ct: token)
                    .ConfigureAwait(false);

                deployWarnings = result.Warnings;

                // 部署成功后落一条 publish 版本快照（含流程图图片）：
                // 版本历史里即可查看「部署当时」的图；快照失败不影响部署结果本身。
                if (!string.IsNullOrWhiteSpace(request?.Svg))
                {
                    await store
                        .SaveAsync(
                            id,
                            new SaveBpmnModelRequest
                            {
                                Name = model.Name,
                                ProcessKey = model.ProcessKey,
                                Description = model.Description,
                                BpmnXml = model.BpmnXml,
                                Svg = request.Svg,
                            },
                            requestContext.UserId,
                            "publish",
                            token)
                        .ConfigureAwait(false);
                }

                return result.ProcessDefinition;
            },
            ct).ConfigureAwait(false);

        // 发布后自检：委托（服务任务 / 各类监听器）能否解析 —— 解析不到时随响应回给调用方，
        // 避免「发布成功、实例跑到该节点才发现没实现」。
        var precheck = await DeploymentDelegatePrecheck.VerifyAsync(
                httpContext.RequestServices,
                engine,
                definition!.Id,
                loggerFactory.CreateLogger(nameof(AdminEndpoints)),
                ct)
            .ConfigureAwait(false);

        // 提示级校验 + 委托自检合并返回；全空时给 null（契约里「无提示」的语义）
        var warnings = (deployWarnings.Concat(precheck ?? Array.Empty<NetBpm.Bpmn.Validation.ValidationError>()))
            .ToArray();

        return Results.Ok(new DeployResponse(
            definition.Id,
            definition.Key,
            definition.Version,
            definition.DeploymentId,
            definition.ResourceName,
            $"模型发布：{model.Name}",
            Warnings: warnings.Length == 0 ? null : warnings));
    }

    private static async Task<IReadOnlyList<ProcessType>> ListProcessTypesAsync(
        ProcessTypeStore store,
        CancellationToken ct) => await store.ListAsync(ct).ConfigureAwait(false);

    private static async Task<IResult> GetProcessTypeAsync(
        string id,
        ProcessTypeStore store,
        CancellationToken ct) =>
        await store.GetAsync(id, ct).ConfigureAwait(false) is { } item
            ? Results.Ok(item)
            : Results.NotFound();

    private static async Task<IResult> CreateProcessTypeAsync(
        SaveProcessTypeRequest request,
        ProcessTypeStore store,
        CancellationToken ct)
    {
        var created = await store.CreateAsync(request, ct).ConfigureAwait(false);
        return Results.Created($"/api/admin/process-types/{created.Id}", created);
    }

    private static async Task<IResult> UpdateProcessTypeAsync(
        string id,
        SaveProcessTypeRequest request,
        ProcessTypeStore store,
        CancellationToken ct) =>
        await store.UpdateAsync(id, request, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> DeleteProcessTypeAsync(
        string id,
        ProcessTypeStore store,
        CancellationToken ct) =>
        await store.DeleteAsync(id, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();
}

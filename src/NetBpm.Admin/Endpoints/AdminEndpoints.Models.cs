using System.Text;
using NetBpm.Admin.Storage;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Rest;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点（partial）：流程模型（bpmn_model）与流程类型（workflow_type 注册表）的处理器。
/// 路由表见 <see cref="AdminEndpoints.MapAdminEndpoints"/>。
/// </summary>
public static partial class AdminEndpoints
{
    private static async Task<IReadOnlyList<BpmnModelSummary>> ListModelsAsync(
        BpmnModelStore store,
        CancellationToken ct) => await store.ListAsync(ct).ConfigureAwait(false);

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

    private static async Task<IResult> DeleteModelAsync(
        string id,
        BpmnModelStore store,
        CancellationToken ct) =>
        await store.DeleteAsync(id, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

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

    /// <summary>发布模型：用当前 XML 走引擎部署（校验/版本递增与设计器部署完全一致）。</summary>
    private static async Task<IResult> PublishModelAsync(
        string id,
        BpmnModelStore store,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var model = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound();
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(model.BpmnXml));

        var definition = await engine
            .DeployAsync(
                stream,
                $"{model.Id}.bpmn20.xml",
                $"模型发布：{model.Name}",
                ct: ct)
            .ConfigureAwait(false);

        return Results.Ok(new DeployResponse(
            definition.Id,
            definition.Key,
            definition.Version,
            definition.DeploymentId,
            definition.ResourceName,
            $"模型发布：{model.Name}"));
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

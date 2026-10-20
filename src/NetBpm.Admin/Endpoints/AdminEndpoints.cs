using NetBpm.Admin.Storage;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点：流程模型（设计器）与流程类型（workflow_type 注册表）的增删改查。
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin").WithTags("admin");

        // ── 流程模型（bpmn_model，设计器编辑源）──
        group.MapGet("/models", ListModelsAsync).Produces<IReadOnlyList<BpmnModelSummary>>();
        group.MapGet("/models/{id}", GetModelAsync).Produces<BpmnModelDetail>();
        group.MapPut("/models/{id}", SaveModelAsync).Accepts<SaveBpmnModelRequest>("application/json");
        group.MapDelete("/models/{id}", DeleteModelAsync);

        // ── 流程类型（workflow_type 注册表）──
        group.MapGet("/process-types", ListProcessTypesAsync).Produces<IReadOnlyList<ProcessType>>();
        group.MapGet("/process-types/{id}", GetProcessTypeAsync).Produces<ProcessType>();
        group.MapPost("/process-types", CreateProcessTypeAsync).Accepts<SaveProcessTypeRequest>("application/json");
        group.MapPut("/process-types/{id}", UpdateProcessTypeAsync).Accepts<SaveProcessTypeRequest>("application/json");
        group.MapDelete("/process-types/{id}", DeleteProcessTypeAsync);

        return group;
    }

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
        CancellationToken ct)
    {
        await store.SaveAsync(id, request, ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteModelAsync(
        string id,
        BpmnModelStore store,
        CancellationToken ct) =>
        await store.DeleteAsync(id, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

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

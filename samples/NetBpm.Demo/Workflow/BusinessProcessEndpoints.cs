using Microsoft.AspNetCore.Routing;
using NetBpm.Workflow;

namespace NetBpm.Demo.Workflow;

/// <summary>
/// 业务流程的统一业务入口：为一条 <see cref="IBusinessProcess"/> 挂上
/// <c>POST {ApiPrefix}/business/start</c>。
/// </summary>
/// <remarks>
/// 端点内部：业务处理（<see cref="IBusinessProcess.PrepareStart"/>：单号 / 业务数据 / 参数）
/// → 发起流程 → 收尾（<see cref="IBusinessProcess.OnStartedAsync"/>）→ 返回首个待办视图。
/// 业务校验不过（<see cref="WorkflowValidationException"/>）统一翻译成 400。
/// </remarks>
public static class BusinessProcessEndpoints
{
    /// <summary>为一条业务流程挂上统一业务入口。</summary>
    public static IEndpointConventionBuilder MapBusinessProcess(this IEndpointRouteBuilder endpoints, IBusinessProcess process)
    {
        var group = endpoints.MapGroup(process.ApiPrefix).WithTags($"business[{process.ProcessKey}]");

        group.MapPost("/business/start", async (
                StartWorkflowRequest? request,
                IWorkflowService workflow,
                CancellationToken ct) =>
            {
                if (request is null)
                {
                    return Results.BadRequest(new ErrorResponse("请求体不能为空。"));
                }

                try
                {
                    var prepared = await process.PrepareStart(request, ct).ConfigureAwait(false);
                    var view = await workflow
                        .StartAsync(process.ProcessKey, prepared, ct)
                        .ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(prepared.BusinessKey))
                    {
                        await process
                            .OnStartedAsync(prepared.BusinessKey, view.ProcessInstanceId, ct)
                            .ConfigureAwait(false);
                    }

                    return Results.Ok(view);
                }
                catch (WorkflowValidationException exception)
                {
                    return Results.BadRequest(new ErrorResponse(exception.Message, exception.Errors));
                }
            })
            .Produces<WorkflowInstanceView>();

        return group;
    }
}

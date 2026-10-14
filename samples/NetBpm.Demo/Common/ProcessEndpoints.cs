using Microsoft.AspNetCore.Routing;
using NetBpm.Demo.Common;

namespace NetBpm.Demo;

/// <summary>统一错误响应（合并 Demo 的公共端点用）。</summary>
/// <param name="Message">错误说明。</param>
/// <param name="Errors">明细。</param>
public sealed record ErrorResponse(string Message, IReadOnlyList<string>? Errors = null);

/// <summary>
/// 流程信息端点：把 <see cref="ProcessInfoEnum"/> 暴露给前端。
/// </summary>
/// <remarks>
/// 页面不写死任何流程 —— 首页的流程卡片、待办列表的所属流程列、审批/查看页的跳转地址
/// 都从这两个端点拿。新增流程只需在 <see cref="ProcessInfoEnum"/> 加一行，这里零改动。
/// </remarks>
public static class ProcessEndpoints
{
    /// <summary>映射 <c>/api/processes</c> 端点组。</summary>
    public static IEndpointConventionBuilder MapProcessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/processes").WithTags("processes");

        group.MapGet(string.Empty, ListAsync);              // 全部流程
        group.MapGet("/{name}", GetByNameAsync);            // 按流程名称（key）查一条

        return group;
    }

    /// <summary>全部流程信息（清单顺序即页面展示顺序）。</summary>
    private static IResult ListAsync() => Results.Ok(ProcessInfoEnum.All);

    /// <summary>按流程名称（key，如 order_process）查一条。</summary>
    private static IResult GetByNameAsync(string name) =>
        ProcessInfoEnum.Of(name) is { } process
            ? Results.Ok(process)
            : Results.NotFound(new ErrorResponse($"流程 '{name}' 不存在（可用：{string.Join(" / ", ProcessInfoEnum.All.Select(p => p.Name))}）。"));
}

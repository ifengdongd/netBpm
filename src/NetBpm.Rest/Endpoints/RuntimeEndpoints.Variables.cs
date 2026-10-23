using System.Text.Json;
using NetBpm.Engine.Execution;
using NetBpm.Rest.Security;
using NetBpm.Rest.Services;

namespace NetBpm.Rest.Endpoints;

/// <summary>变量端点：流程实例作用域与任务局部作用域。</summary>
public static partial class RuntimeEndpoints
{
    private static async Task<IResult> GetVariablesAsync(
        string processInstanceId,
        HttpContext http,
        IExecutionManager executionManager,
        IVariableService variables,
        CancellationToken ct)
    {
        // 运行态变量只对「还在跑」的实例成立：实例已结束 / 已删除时返回 404，
        // 调用方（管理端实例详情抽屉）据此回落到历史变量，而不是把它当成一次失败。
        // 注意：受租户限制时过滤器已校验过存在性 / 归属，这里只补「未受限身份」的一次查库。
        var running = http.InstanceOf()
            ?? await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

        if (running is null)
        {
            return Results.NotFound(new ErrorResponse(
                $"流程实例 '{processInstanceId}' 不在运行中（可能已结束或已删除）。",
                Code: EngineErrorCode.ProcessInstanceNotRunning));
        }

        var values = await variables.GetInstanceVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(values);
    }

    /// <summary>
    /// 写流程实例变量（运行期补偿 / 纠错）。请求体即 JSON 值，与任务局部变量同一套类型映射。
    /// <para>变量已有则更新，没有则在流程实例作用域新建；历史行同步更新。</para>
    /// </summary>
    private static async Task<IResult> SetVariableAsync(
        string processInstanceId,
        string name,
        JsonElement? value,
        RequestContext requestContext,
        IVariableService variables,
        CancellationToken ct)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return InvalidRequest("请求体不能为空（不支持写入 null 变量）。");
        }

        var parsed = JsonVariableReader.Read(new Dictionary<string, JsonElement> { [name] = value.Value });

        if (parsed is null || !parsed.TryGetValue(name, out var parsedValue))
        {
            return InvalidRequest($"变量 '{name}' 的值为 null，未写入；删除变量请调用 DELETE。");
        }

        await variables
            .SetVariableAsync(
                processInstanceId,
                name,
                parsedValue,
                value.Value.ValueKind.ToString(),
                requestContext,
                ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>批量写流程实例变量（同一事务）。请求体为 <c>{ "name": value, ... }</c>。</summary>
    private static async Task<IResult> SetVariablesAsync(
        string processInstanceId,
        Dictionary<string, JsonElement>? variables,
        RequestContext requestContext,
        IVariableService variableService,
        CancellationToken ct)
    {
        if (variables is null || variables.Count == 0)
        {
            return InvalidRequest("请求体必须是非空的变量字典。");
        }

        var values = JsonVariableReader.Read(variables);

        if (values is null)
        {
            return InvalidRequest(
                "变量值不能全部为 null：本次请求没有可写入的变量（删除变量请用 DELETE）。");
        }

        await variableService
            .SetVariablesAsync(
                processInstanceId,
                new Dictionary<string, object?>(values, StringComparer.Ordinal),
                requestContext,
                ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>删除流程实例作用域上的变量（B2.4）：沿作用域链查找，历史行保留。</summary>
    private static async Task<IResult> RemoveVariableAsync(
        string processInstanceId,
        string name,
        RequestContext requestContext,
        IVariableService variables,
        CancellationToken ct)
    {
        await variables.RemoveVariableAsync(processInstanceId, name, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> GetTaskVariablesAsync(
        HttpContext http,
        IVariableService variables,
        CancellationToken ct)
    {
        var values = await variables.GetTaskVariablesAsync(http.TaskOf()!.Id, ct).ConfigureAwait(false);

        return Results.Ok(values);
    }

    /// <summary>写任务局部变量。请求体即变量值（JSON 原文，走 <see cref="JsonVariableReader"/> 同一套类型映射）。</summary>
    private static async Task<IResult> SetTaskVariableAsync(
        string name,
        JsonElement? value,
        HttpContext http,
        RequestContext requestContext,
        IVariableService variables,
        CancellationToken ct)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return InvalidRequest("请求体不能为空（不支持写入 null 变量）。");
        }

        var parsed = JsonVariableReader.Read(new Dictionary<string, JsonElement> { [name] = value.Value });

        if (parsed is null || !parsed.TryGetValue(name, out var parsedValue))
        {
            return InvalidRequest($"任务局部变量 '{name}' 的值为 null，未写入；删除变量请调用 DELETE。");
        }

        await variables
            .SetTaskVariableAsync(
                http.TaskOf()!,
                name,
                parsedValue,
                value.Value.ValueKind.ToString(),
                requestContext,
                ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> RemoveTaskVariableAsync(
        string name,
        HttpContext http,
        RequestContext requestContext,
        IVariableService variables,
        CancellationToken ct)
    {
        await variables
            .RemoveTaskVariableAsync(http.TaskOf()!, name, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }
}

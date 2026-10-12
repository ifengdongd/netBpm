using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
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
    public static RouteGroupBuilder MapDesignerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/designer").WithTags("designer");

        group.MapPost("/deploy", DeployAsync).Accepts<string>("text/xml").Produces<DeployResponse>();
        group.MapGet("/definition/{processDefinitionId}/xml", GetDefinitionXmlAsync).Produces<string>();

        return group;
    }

    private static async Task<IResult> DeployAsync(
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

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var definition = await engine.DeployAsync(stream, "designer.bpmn20.xml", deploymentName: null, ct: ct)
                .ConfigureAwait(false);

            return Results.Ok(new DeployResponse(
                definition.Id,
                definition.Key,
                definition.Version,
                definition.DeploymentId,
                definition.ResourceName));
        }
        catch (ProcessValidationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message, ex.Errors));
        }
        catch (BpmnXmlParseException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (NetBpm.Engine.EngineException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> GetDefinitionXmlAsync(
        string processDefinitionId,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var xml = await engine.GetProcessDefinitionXmlAsync(processDefinitionId, ct).ConfigureAwait(false);

        return xml is null
            ? Results.NotFound(new ErrorResponse($"流程定义 '{processDefinitionId}' 不存在。"))
            : Results.Text(xml, "text/xml", Encoding.UTF8);
    }
}

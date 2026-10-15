using NetBpm.Demo.Common;
using NetBpm.Demo.Order;
using NetBpm.Persistence;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Workflow;

/// <summary>
/// Demo 的启动序列：部署两条流程 → 加载流程信息（workflow_type 表）→ 生成流程图图片。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 必须在 <c>app.Run()</c> 之前执行：运行期上下文（流程定义 id + 环节名对照表）
/// 与流程信息枚举都要在接受请求前装好。
/// </para>
/// <para>
/// 各步骤失败的行为：部署自检不过会让宿主起不来；workflow_type 表加载失败回退内置默认清单；
/// 流程图生成失败只打警告（图片可由端点实时渲染兜底）。
/// </para>
/// </remarks>
internal static class StartupInitializer
{
    /// <summary>执行完整启动序列。</summary>
    public static async Task InitializeAsync(WebApplication app)
    {
        await DeployProcessesAsync(app).ConfigureAwait(false);
        await LoadProcessInfosAsync(app).ConfigureAwait(false);
        await PublishDiagramsAsync(app).ConfigureAwait(false);

        var billStore = app.Services.GetRequiredService<IOrderBillStore>();
        app.Logger.LogInformation("订单数据落点：{Location}", billStore.Location);
    }

    /// <summary>部署两条流程（定义来自 web/designer/examples/*.bpmn20.xml，ConstField 提供常量）。</summary>
    private static async Task DeployProcessesAsync(WebApplication app)
    {
        await ProcessBootstrapper.DeployAsync(
            app.Services, app.Logger,
            ConstField.OrderProcessKey, ConstField.OrderResourceName, ConstField.OrderDeploymentName)
            .ConfigureAwait(false);

        await ProcessBootstrapper.DeployAsync(
            app.Services, app.Logger,
            ConstField.LeaveProcessKey, ConstField.LeaveResourceName, ConstField.LeaveDeploymentName)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 从 workflow_type 表（0008）加载流程信息至 ProcessInfoEnum：
    /// 新增流程 = 表里插一行，不用改代码。表不存在 / 加载失败回退内置默认清单。
    /// </summary>
    private static async Task LoadProcessInfosAsync(WebApplication app)
    {
        var storage = app.Configuration[ConstField.StorageConfigKey] ?? ConstField.DefaultStorage;

        if (!string.Equals(storage, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var loader = new WorkflowTypeLoader(
            app.Services.GetRequiredService<IDbConnectionFactory>(),
            app.Configuration,
            app.Logger);

        var workflowTypes = await loader.LoadAsync().ConfigureAwait(false);

        if (workflowTypes is not null)
        {
            ProcessInfoEnum.Load(workflowTypes);
        }
        else
        {
            app.Logger.LogInformation(
                "流程信息枚举使用内置默认清单（{Count} 条）。",
                ProcessInfoEnum.Defaults.Count);
        }
    }

    /// <summary>
    /// 发布即生成流程图图片（SVG）：落到 wwwroot/diagrams/{key}.svg 静态可访问；
    /// 实例的流转状态图（红线/红框标注走过的节点与线）走
    /// /api/{prefix}/instances/{id}/diagram.svg，由审批历史实时渲染。
    /// </summary>
    private static async Task PublishDiagramsAsync(WebApplication app)
    {
        foreach (var processKey in new[] { ConstField.OrderProcessKey, ConstField.LeaveProcessKey })
        {
            try
            {
                await PublishDiagramAsync(app, processKey).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "流程 {ProcessKey} 的流程图生成失败（可由端点实时渲染兜底）。", processKey);
            }
        }
    }

    private static async Task PublishDiagramAsync(WebApplication app, string processKey)
    {
        var workflow = app.Services.GetRequiredService<IWorkflowService>();
        var engine = app.Services.GetRequiredService<IProcessEngine>();

        var definitionId = await workflow.ResolveDefinitionIdAsync(processKey).ConfigureAwait(false);
        if (definitionId is null)
        {
            return;
        }

        var xml = await engine.GetProcessDefinitionXmlAsync(definitionId).ConfigureAwait(false);

        if (string.IsNullOrEmpty(xml))
        {
            app.Logger.LogWarning("流程定义 {DefinitionId} 没有 XML 资源，跳过流程图生成。", definitionId);
            return;
        }

        var svg = BpmnDiagramSvgRenderer.RenderSvg(xml, DiagramHighlight.None);

        var directory = Path.Combine(app.Environment.WebRootPath, ConstField.DiagramDirectory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{processKey}.svg"), svg).ConfigureAwait(false);
        app.Logger.LogInformation("流程图图片已生成：/diagrams/{ProcessKey}.svg", processKey);
    }
}

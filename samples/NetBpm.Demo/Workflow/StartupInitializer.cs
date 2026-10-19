using NetBpm.Demo.Common;
using NetBpm.Demo.Order;
using NetBpm.Demo.Storage;
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
    /// <summary>执行完整启动序列（部署清单来自注册的 IBusinessProcess —— 新流程自动纳入）。</summary>
    public static async Task InitializeAsync(WebApplication app)
    {
        var processes = app.Services.GetServices<IBusinessProcess>().ToArray();

        EnsureDatabaseSchema(app);
        LogStorageMode(app);

        await DeployProcessesAsync(app, processes).ConfigureAwait(false);
        await LoadProcessInfosAsync(app).ConfigureAwait(false);
        await PublishDiagramsAsync(app, processes).ConfigureAwait(false);

        var billStore = app.Services.GetRequiredService<IOrderBillStore>();
        app.Logger.LogInformation("订单数据落点：{Location}", billStore.Location);
    }

    /// <summary>
    /// 建库（若缺）+ 幂等执行 db/&lt;dialect&gt; 脚本（引擎 ACT_* 表 + 业务表，双方言同库同 schema）。
    /// 已初始化的库直接跳过，重复启动不重复建表。
    /// </summary>
    private static void EnsureDatabaseSchema(WebApplication app)
    {
        var databaseType = app.Configuration.GetValue("NetBpm:DatabaseType", DatabaseType.MySql);
        var connectionString = app.Configuration["NetBpm:ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("NetBpm:ConnectionString 未配置。");
        }

        var migration = DemoSchemaInitializer.EnsureSchema(databaseType, connectionString);

        if (migration.AlreadyInitialized)
        {
            app.Logger.LogInformation("数据库已初始化（{Dialect}），跳过建表脚本。", databaseType);
        }
        else
        {
            app.Logger.LogInformation(
                "已执行建表脚本（{Dialect}）：{Scripts}",
                databaseType,
                string.Join(", ", migration.AppliedScripts));
        }
    }

    /// <summary>
    /// 打印生效的存储模式。⚠️ 业务数据固定落真库，引擎也必须落同一个真库
    /// （UseInMemoryExecutionTree=false）：内存宿主的实例 id 每次启动从 1 重新开始，
    /// 会撞真库里累积的 workflow_instance_id，启动即警告。
    /// </summary>
    private static void LogStorageMode(WebApplication app)
    {
        var databaseType = app.Configuration.GetValue("NetBpm:DatabaseType", DatabaseType.MySql);
        var inMemoryEngine = app.Configuration.GetValue("NetBpm:UseInMemoryExecutionTree", false);

        app.Logger.LogInformation(
            "存储模式：{Dialect}（引擎：{EngineMode}）—— 引擎与业务数据都落同一个库",
            databaseType,
            inMemoryEngine ? "内存执行树（危险：会与真库 instances 撞号）" : $"{databaseType}（ACT_* 表）");

        if (inMemoryEngine)
        {
            app.Logger.LogWarning(
                "危险组合：引擎是内存执行树而业务数据落真库 —— "
                + "重启后流程实例 id 从 1 重新开始，会覆盖真库里 workflow_instance_id 相同的旧登记行。"
                + "请设置 NetBpm:UseInMemoryExecutionTree=false。");
        }
    }

    /// <summary>部署全部已注册的业务流程（定义来自 web/designer/examples/*.bpmn20.xml）。</summary>
    private static async Task DeployProcessesAsync(WebApplication app, IReadOnlyList<IBusinessProcess> processes)
    {
        foreach (var process in processes)
        {
            await ProcessBootstrapper.DeployAsync(
                app.Services, app.Logger,
                process.ProcessKey, process.ResourceName, process.DeploymentName)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 从 workflow_type 表（0008）加载流程信息至 ProcessInfoEnum：
    /// 新增流程 = 表里插一行，不用改代码。表不存在 / 加载失败回退内置默认清单。
    /// </summary>
    private static async Task LoadProcessInfosAsync(WebApplication app)
    {
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
    private static async Task PublishDiagramsAsync(WebApplication app, IReadOnlyList<IBusinessProcess> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                await PublishDiagramAsync(app, process.ProcessKey).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "流程 {ProcessKey} 的流程图生成失败（可由端点实时渲染兜底）。", process.ProcessKey);
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

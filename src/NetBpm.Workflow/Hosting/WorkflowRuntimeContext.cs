using Microsoft.Extensions.Logging;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 部署之后才有值的运行期上下文：流程定义 id + 环节名 → 环节 id 对照表。
/// </summary>
/// <remarks>
/// <para>
/// 流程定义通常在宿主启动阶段（<c>app.Build()</c> 之后）才由
/// <see cref="WorkflowProcessBootstrapper"/> 部署，所以「当前流程定义 id」这类值
/// 拿不到构造注入 —— 用一个单例装起来，部署完填充，之后只读。
/// </para>
/// <para>
/// 注册方式：<see cref="WorkflowServiceCollectionExtensions.AddNetBpmWorkflow{TDataStore}"/>
/// 已经把它注册成单例；业务数据存储等组件可构造注入，用
/// <see cref="EnsureInitialized"/> 保证没被绕过启动流程。
/// </para>
/// </remarks>
public sealed class WorkflowRuntimeContext
{
    private bool _initialized;

    /// <summary>最近一次部署出来的流程定义 id。</summary>
    public string ProcessDefinitionId { get; private set; } = string.Empty;

    /// <summary>环节名 → 环节 id 映射（未装载时为空表）。</summary>
    public WorkflowNodeRegistry Nodes { get; private set; } = WorkflowNodeRegistry.Empty;

    /// <summary>部署完调用一次，把上面两项填上。</summary>
    public async ValueTask InitializeAsync(
        IProcessEngine engine,
        string processDefinitionId,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        ProcessDefinitionId = processDefinitionId;
        Nodes = await WorkflowNodeRegistry.BuildAsync(engine, processDefinitionId, logger, ct).ConfigureAwait(false);
        _initialized = true;
    }

    /// <summary>确认已装载；没装载说明启动流程被绕过了，早点报错比默默写错数据好。</summary>
    public void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "WorkflowRuntimeContext 尚未初始化：流程定义部署后必须调用 InitializeAsync"
                + "（见 WorkflowProcessBootstrapper.DeployAsync）。");
        }
    }
}

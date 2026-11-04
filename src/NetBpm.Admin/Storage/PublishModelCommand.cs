using NetBpm.Bpmn.Validation;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Deployment;
using NetBpm.Runtime;

namespace NetBpm.Admin.Storage;

/// <summary>
/// 发布模型：把「引擎部署」与「<c>publish</c> 版本快照」放进<b>同一个命令（= 同一个事务）</b>。
/// </summary>
/// <remarks>
/// <para>
/// 过去这两步各自提交：部署成功、写快照失败时，版本历史里就没有这次的发布记录，
/// 「发布历史 / 是否已发布」全部失真；反过来重试又会多出一个流程定义版本。
/// </para>
/// <para>
/// 🔑 同事务的关键是<b>嵌套命令</b>：<see cref="IProcessEngine.DeployWithValidationAsync"/>
/// 内部会走 <see cref="ICommandExecutor"/>，而 <c>CommandExecutor</c> 检测到
/// <see cref="CommandContext.Current"/> 非空时复用外层上下文（同一事务、同一会话）；
/// 快照写入用的 <c>IDbSessionProvider.Acquire()</c> 同样拿到这个命令的会话。
/// </para>
/// <para>
/// 因此本命令<b>不能</b>在命令外自己开事务 —— 那样部署仍会另起一个连接，两个事务互不相干。
/// </para>
/// </remarks>
internal sealed class PublishModelCommand : ICommand<ProcessDefinition>
{
    private readonly IProcessEngine _engine;
    private readonly BpmnModelStore _store;
    private readonly string _modelId;
    private readonly byte[] _bpmnXml;
    private readonly SaveBpmnModelRequest _snapshot;
    private readonly string? _operatorId;
    private readonly string? _tenantId;

    public PublishModelCommand(
        IProcessEngine engine,
        BpmnModelStore store,
        string modelId,
        byte[] bpmnXml,
        SaveBpmnModelRequest snapshot,
        string? operatorId,
        string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(bpmnXml);
        ArgumentNullException.ThrowIfNull(snapshot);

        _engine = engine;
        _store = store;
        _modelId = modelId;
        _bpmnXml = bpmnXml;
        _snapshot = snapshot;
        _operatorId = operatorId;
        _tenantId = tenantId;
    }

    /// <summary>部署时产生的提示级校验结果（提交后由调用方取用）。</summary>
    public IReadOnlyList<ValidationError> Warnings { get; private set; } = [];

    /// <inheritdoc />
    public async ValueTask<ProcessDefinition> ExecuteAsync(
        ICommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var stream = new MemoryStream(_bpmnXml);

        // 嵌套命令：复用本命令的事务与连接
        var result = await _engine
            .DeployWithValidationAsync(
                stream,
                $"{_modelId}.bpmn20.xml",
                $"模型发布：{_snapshot.Name}",
                // 🔑 必须带租户：引擎按 (KEY_, TENANT_ID_) 取最新定义，
                // 不带的话定义落在空租户，租户化的运行侧（tenant=default）永远看不到这次发布。
                tenantId: _tenantId,
                ct: ct)
            .ConfigureAwait(false);

        Warnings = result.Warnings;

        // 同一事务内落 publish 快照：任一步失败，整体回滚（不会出现「部署了但没记录」）
        await _store
            .SaveAsync(
                _modelId,
                _snapshot,
                _operatorId,
                "publish",
                expectedRevision: null,
                ct: ct)
            .ConfigureAwait(false);

        return result.ProcessDefinition;
    }
}

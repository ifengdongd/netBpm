using NetBpm.Engine.Jobs;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Command;

/// <summary>
/// 领取外部任务（fetch &amp; lock）。对应 Camunda 的 <c>FetchExternalTasksCmd</c>。
/// <para>
/// 之所以也走命令：领取是<b>写操作</b>（加锁 + 改状态），必须落在命令事务里。
/// 直接调 broker 会让它跑在事务之外，命令回滚时锁却留下了 —— 流程没推进、任务却被锁住，
/// 只能等锁过期才能恢复。
/// </para>
/// </summary>
public sealed class FetchExternalTasksCommand : ICommand<IReadOnlyList<ExternalTask>>
{
    private readonly ExternalTaskFetchRequest _request;

    public FetchExternalTasksCommand(ExternalTaskFetchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);

        _request = request;
    }

    /// <inheritdoc />
    public string? CommandId => null;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ExternalTask>> ExecuteAsync(
        ICommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;

        var broker = services.ExternalTaskBroker
            ?? throw new EngineException(
                "未注册 IExternalTaskBroker，无法领取外部任务（独立部署需要外部任务存储）。");

        return await broker
            .FetchAndLockAsync(_request, services.Clock.UtcNow, ct)
            .ConfigureAwait(false);
    }
}

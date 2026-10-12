namespace NetBpm.Engine.Agenda;

/// <summary>
/// 议程中的一步操作。对应 Activiti 的 <c>AbstractOperation</c> 层次。
/// </summary>
public interface IOperation
{
    /// <summary>该操作的名称（用于日志与对拍时的事件序列）。</summary>
    string Name { get; }

    ValueTask RunAsync(CancellationToken ct = default);
}

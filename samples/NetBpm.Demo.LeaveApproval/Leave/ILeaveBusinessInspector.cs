namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 「业务数据落在哪儿、现在长什么样」—— 演示页与 <c>GET /api/leave/business</c> 只认这个接口。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 它存在的意义是让<b>两个业务存储实现可以互换</b>：<see cref="LeaveBusinessStore"/>（JSON）
/// 与 <see cref="MySqlWorkflowDataStore"/>（四张业务表）都实现它，于是端点、演示页一行都不用改 ——
/// 换的只是 <c>Leave:Store</c> 这一个配置。
/// </para>
/// <para>
/// ⚠️ 它<b>不属于</b>应用层的 <see cref="NetBpm.Workflow.IWorkflowDataStore"/>：
/// 那份端口是「怎么存业务数据」，这份是「怎么把存的东西给人看」，只有 Demo 的核对页面需要。
/// </para>
/// </remarks>
public interface ILeaveBusinessInspector
{
    /// <summary>存储类型标识，取值与配置项 <c>Leave:Store</c> 一致（<c>Json</c> / <c>MySql</c>）。</summary>
    /// <remarks>
    /// 给调用方一个<b>可靠的判据</b>：快照的键名与「能给就给」的字段（如表单值快照）
    /// 是随落点变的，靠字符串前缀猜落点太脆。
    /// </remarks>
    string Store { get; }

    /// <summary>落点自述（一句话说清数据落在哪儿，会显示在演示页上）。</summary>
    string Location { get; }

    /// <summary>
    /// 把业务数据 dump 成可序列化的快照；<b>键名由实现方决定</b>
    /// （JSON 实现给区块名，MySQL 实现给表名 —— 「表名」对看库的人才有意义）。
    /// </summary>
    /// <param name="ct">取消标记。</param>
    ValueTask<IReadOnlyDictionary<string, object?>> SnapshotAsync(CancellationToken ct = default);
}

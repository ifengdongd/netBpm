namespace NetBpm.Engine;

/// <summary>
/// 主键生成器。对应 Activiti 的 <c>IdGenerator</c>，默认实现为 <c>DbIdGenerator</c>。
/// <para>
/// ⚠️ 兼容性硬要求（开发计划 §7.3 / 风险 R8）：必须实现「区块分配 + <c>ACT_GE_PROPERTY.REV_</c>
/// 乐观锁」语义（<c>idBlockSize</c> 默认 2500），不能改成 UUID 或数据库自增 ——
/// 否则两端 ID 空间不同，§9.2 的库级对拍会全线失败。
/// </para>
/// </summary>
public interface IIdGenerator
{
    /// <summary>取下一个 id（同步：区块内自增，跨区块时才访问数据库）。</summary>
    string NextId();
}

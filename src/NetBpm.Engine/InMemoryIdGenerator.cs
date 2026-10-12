using System.Globalization;

namespace NetBpm.Engine;

/// <summary>
/// 内存自增 id 生成器。
/// <para>
/// ⚠️ <b>仅用于单元测试与 POC 脚手架</b>。生产必须使用
/// <c>NetBpm.Persistence.DbIdGenerator</c>（区块分配 + <c>next.dbid</c> 乐观锁），
/// 否则两端 ID 空间不同，§9.2 的库级对拍会全线失败（风险 R8）。
/// </para>
/// </summary>
public sealed class InMemoryIdGenerator : IIdGenerator
{
    private long _nextId;

    public InMemoryIdGenerator(long startAt = 0)
    {
        _nextId = startAt;
    }

    /// <inheritdoc />
    public string NextId() => Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
}

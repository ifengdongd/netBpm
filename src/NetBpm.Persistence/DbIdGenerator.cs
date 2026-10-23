using System.Globalization;
using NetBpm.Engine;

namespace NetBpm.Persistence;

/// <summary>
/// Activiti <c>DbIdGenerator</c> 的等价实现。
/// <para>
/// 机制（开发计划 §7.3，必须逐行对齐）：
/// </para>
/// <list type="number">
///   <item><description>向 <see cref="IIdBlockAllocator"/> 占走一个区块（<c>idBlockSize</c>，默认 <b>2500</b>）；</description></item>
///   <item><description>本次进程内从内存区块里自增发放（热路径纯内存，无锁）；</description></item>
///   <item><description>区块用尽再去占下一块。</description></item>
/// </list>
/// <para>
/// ⚠️ <b>不要改成 UUID 或数据库自增</b>：两端 ID 空间不同会让 §9.2 的库级对拍全线失败（风险 R8）。
/// </para>
/// <para>
/// 🔑 <b>并发设计</b>：发放走 <see cref="Interlocked.CompareExchange(ref long, long, long)"/> 无锁热路径；
/// 占块是网络 I/O，<b>在锁外</b>执行。并发耗尽时最多各自多占一个区块（id 允许跳跃，只要求唯一），
/// 这是刻意用「偶尔浪费一个区块」换「绝不在锁内做 I/O」。
/// </para>
/// </summary>
public sealed class DbIdGenerator : IIdGenerator
{
    /// <summary>Activiti 默认的区块大小。</summary>
    public const int DefaultIdBlockSize = 2500;

    /// <summary><c>ACT_GE_PROPERTY</c> 中存放区块游标的属性名。</summary>
    public const string NextDbIdPropertyName = "next.dbid";

    private readonly IIdBlockAllocator _allocator;
    private readonly int _idBlockSize;
    private readonly Lock _blockSwapLock = new();

    // ⚠️ 初值必须与 Activiti 一致：lastId = -1、nextId = 0。
    // 若 lastId 也取 0，首次调用会判定"区块未用尽"而直接发出 id 0（随后 id 1），
    // 既污染 ID 空间又会在第二次部署时撞主键（已实测：Duplicate entry '0' for key 'PRIMARY'）。
    private long _lastId = -1;
    private long _nextId;
    private long _issuedCount;

    /// <summary>
    /// 用区块分配器装配（<b>生产路径</b>）：分配器决定区块是否独立事务，见 <see cref="DapperIdBlockAllocator"/>。
    /// </summary>
    public DbIdGenerator(IIdBlockAllocator allocator, int idBlockSize = DefaultIdBlockSize)
    {
        ArgumentNullException.ThrowIfNull(allocator);

        if (idBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(idBlockSize), idBlockSize, "区块大小必须为正整数。");
        }

        _allocator = allocator;
        _idBlockSize = idBlockSize;
    }

    /// <summary>
    /// 兼容装配：直接用 <see cref="IPropertyStore"/>（走当前会话，参与命令事务）。
    /// <para>仅建议测试夹具 / 自装配宿主使用；生产请走 <see cref="DapperIdBlockAllocator"/>。</para>
    /// </summary>
    public DbIdGenerator(IPropertyStore propertyStore, int idBlockSize = DefaultIdBlockSize)
        : this(new PropertyStoreIdBlockAllocator(propertyStore), idBlockSize)
    {
    }

    /// <summary>本进程已发放的 id 数量（诊断用）。</summary>
    public long IssuedCount => Interlocked.Read(ref _issuedCount);

    /// <inheritdoc />
    public string NextId()
    {
        // 快路径：区块还有余量 —— 纯内存 CAS，不碰锁、不碰数据库。
        if (TryTakeFromBlock(out var id))
        {
            Interlocked.Increment(ref _issuedCount);
            return id.ToString(CultureInfo.InvariantCulture);
        }

        // 慢路径：区块用尽。占块是数据库往返，必须在锁外做（否则所有线程排在这里等 I/O）。
        var block = _allocator.Allocate(NextDbIdPropertyName, _idBlockSize);

        lock (_blockSwapLock)
        {
            // 并发下别的线程可能已经换好区块：直接复用它的，本次占的块作废（最多浪费一个区块）。
            // 反过来若在这里无条件覆盖，会把别人正在发的区块顶掉 —— 那才会产生重复 id。
            if (_lastId < _nextId)
            {
                _nextId = block.FirstId;
                _lastId = block.LastId;
            }

            Interlocked.Increment(ref _issuedCount);
            return (_nextId++).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 从当前区块取一个 id（无锁）。返回 <see langword="false"/> 表示区块已用尽。
    /// </summary>
    private bool TryTakeFromBlock(out long id)
    {
        while (true)
        {
            var next = Volatile.Read(ref _nextId);
            var last = Volatile.Read(ref _lastId);

            // 区块未初始化（-1 / 0）或已用尽
            if (next > last)
            {
                id = 0;
                return false;
            }

            if (Interlocked.CompareExchange(ref _nextId, next + 1, next) == next)
            {
                id = next;
                return true;
            }
        }
    }
}

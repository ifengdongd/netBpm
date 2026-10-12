using System.Globalization;
using NetBpm.Engine;

namespace NetBpm.Persistence;

/// <summary>
/// Activiti <c>DbIdGenerator</c> 的等价实现。
/// <para>
/// 机制（开发计划 §7.3，必须逐行对齐）：
/// </para>
/// <list type="number">
///   <item><description>从 <c>ACT_GE_PROPERTY</c> 读 <c>next.dbid</c>；</description></item>
///   <item><description><b>一次性占走一个区块</b>（<c>idBlockSize</c>，默认 <b>2500</b>），
///     把 <c>next.dbid</c> 更新为「值 + 2500」，更新用 <c>REV_</c> 乐观锁；</description></item>
///   <item><description>本次进程内从内存区块里自增发放；</description></item>
///   <item><description>区块用尽再去数据库占下一块。</description></item>
/// </list>
/// <para>
/// ⚠️ <b>不要改成 UUID 或数据库自增</b>：两端 ID 空间不同会让 §9.2 的库级对拍全线失败（风险 R8）。
/// </para>
/// </summary>
public sealed class DbIdGenerator : IIdGenerator
{
    /// <summary>Activiti 默认的区块大小。</summary>
    public const int DefaultIdBlockSize = 2500;

    /// <summary><c>ACT_GE_PROPERTY</c> 中存放区块游标的属性名。</summary>
    public const string NextDbIdPropertyName = "next.dbid";

    private readonly IPropertyStore _propertyStore;
    private readonly int _idBlockSize;
    private readonly Lock _syncRoot = new();

    // ⚠️ 初值必须与 Activiti 一致：lastId = -1、nextId = 0。
    // 若 lastId 也取 0，首次调用会判定"区块未用尽"而直接发出 id 0（随后 id 1），
    // 既污染 ID 空间又会在第二次部署时撞主键（已实测：Duplicate entry '0' for key 'PRIMARY'）。
    private long _lastId = -1;
    private long _nextId;

    public DbIdGenerator(IPropertyStore propertyStore, int idBlockSize = DefaultIdBlockSize)
    {
        ArgumentNullException.ThrowIfNull(propertyStore);

        if (idBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(idBlockSize), idBlockSize, "区块大小必须为正整数。");
        }

        _propertyStore = propertyStore;
        _idBlockSize = idBlockSize;
    }

    /// <summary>本进程已发放的 id 数量（诊断用）。</summary>
    public long IssuedCount { get; private set; }

    /// <inheritdoc />
    public string NextId()
    {
        lock (_syncRoot)
        {
            // Activiti: if (lastId < nextId) { getNextIdBlock(); }
            if (_lastId < _nextId)
            {
                AcquireNextBlock();
            }

            IssuedCount++;
            return (_nextId++).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void AcquireNextBlock()
    {
        while (true)
        {
            var property = _propertyStore.FindProperty(NextDbIdPropertyName)
                ?? throw new EngineException(
                    $"ACT_GE_PROPERTY 中缺少 '{NextDbIdPropertyName}'，数据库脚本未正确初始化。" +
                    "请执行 db/ 下对应方言的初始化脚本。");

            if (!long.TryParse(property.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastValue))
            {
                throw new EngineException(
                    $"ACT_GE_PROPERTY.{NextDbIdPropertyName} 的值 '{property.Value}' 不是合法整数。");
            }

            var newValue = lastValue + _idBlockSize;
            var newValueText = newValue.ToString(CultureInfo.InvariantCulture);

            if (!_propertyStore.TryUpdateProperty(NextDbIdPropertyName, property.Revision, newValueText))
            {
                // 乐观锁冲突：另一实例刚占走一块，重读后重试
                continue;
            }

            _nextId = lastValue + 1;
            _lastId = newValue;
            return;
        }
    }
}

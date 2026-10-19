namespace NetBpm.Persistence;

/// <summary>
/// 容量受限的线程安全 LRU 缓存，用于不可变对象的进程内缓存（流程定义模型 / 租户）。
/// <para>
/// 为什么需要上限：流程定义 id 随每次重部署递增，无界字典在长期运行的宿主里
/// 会随部署次数线性增长（P1 评审项）。这里按容量淘汰最久未使用的条目。
/// </para>
/// </summary>
internal sealed class BoundedCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();

    public BoundedCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer ?? EqualityComparer<TKey>.Default);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // 命中即提升为最近使用
                _order.Remove(node);
                _order.AddFirst(node);

                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    /// <summary>移除条目（键不存在返回 <see langword="false"/>）。</summary>
    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_map.Remove(key, out var node))
            {
                return false;
            }

            _order.Remove(node);
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, value);
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            var node = _order.AddFirst(new Entry(key, value));
            _map[key] = node;

            if (_map.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    private readonly record struct Entry(TKey Key, TValue Value);
}

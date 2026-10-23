namespace NetBpm.Persistence.Database;

/// <summary>
/// 会话级状态容器：一级缓存、脏检查集合等「一个命令一份」的东西放这里。
/// </summary>
/// <remarks>
/// <para>
/// 生命周期与 <see cref="IDbSession"/> 严格一致：命令结束、会话释放，状态随之丢弃。
/// 这是 Activiti <c>DbSqlSession</c> 的 entity cache 在 .NET 侧的落点。
/// </para>
/// <para>
/// 之前执行管理器用 <c>ConditionalWeakTable&lt;IDbSession, SessionCache&gt;</c> 把缓存挂在会话外部，
/// 语义上等价但有两个毛病：其一是「会话到底持有什么」不可见，其二是别的仓储想共享会话级缓存
/// 时只能再造一张弱表。统一收到会话自己身上，任何仓储都能 <see cref="GetOrCreate{T}"/> 拿到
/// 同一份实例。
/// </para>
/// <para>线程安全：单个会话本来就不允许并发使用，这里仍加锁，避免误用导致字典损坏。</para>
/// </remarks>
public sealed class SessionState
{
    private readonly Dictionary<Type, object> _values = [];
    private readonly Lock _syncRoot = new();

    /// <summary>取某类型的会话级实例，不存在则用 <paramref name="factory"/> 创建。</summary>
    /// <remarks>
    /// 同一类型在一个会话内只会创建一次；并发首次获取时后到者拿到先到者创建的那份。
    /// </remarks>
    public T GetOrCreate<T>(Func<T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_syncRoot)
        {
            if (_values.TryGetValue(typeof(T), out var existing))
            {
                return (T)existing;
            }

            var created = factory();
            _values[typeof(T)] = created;

            return created;
        }
    }

    /// <summary>尝试取某类型的会话级实例。</summary>
    public bool TryGet<T>(out T? value)
        where T : class
    {
        lock (_syncRoot)
        {
            if (_values.TryGetValue(typeof(T), out var existing))
            {
                value = (T)existing;
                return true;
            }

            value = null;
            return false;
        }
    }

    /// <summary>清空（回滚后丢弃全部缓存用）。</summary>
    public void Clear()
    {
        lock (_syncRoot)
        {
            _values.Clear();
        }
    }
}

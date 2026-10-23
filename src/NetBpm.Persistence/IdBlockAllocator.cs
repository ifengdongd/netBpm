using System.Data;
using System.Globalization;
using Dapper;
using NetBpm.Engine;

namespace NetBpm.Persistence;

/// <summary>
/// 一个已占走的 ID 区块（闭区间）。
/// <para>对应 Activiti <c>DbIdGenerator</c> 的 <c>lastId</c> / <c>nextId</c> 游标。</para>
/// </summary>
/// <param name="FirstId">本块可发放的最小 id。</param>
/// <param name="LastId">本块可发放的最大 id（含）。</param>
public readonly record struct IdBlock(long FirstId, long LastId);

/// <summary>
/// ID 区块分配端口。
/// <para>
/// 🔑 <b>必须是独立事务</b>：Activiti 在命令事务里做「加锁读 + 更新」，行锁会一直持有到命令提交，
/// 高并发下所有命令都在 <c>ACT_GE_PROPERTY</c> 这一行上排队；反过来，命令外分配又是自动提交
/// （锁立刻释放），「加锁读已经把分配串行化」的假设并不成立。
/// 把分配放进自己的短事务，两个问题一起消失。
/// </para>
/// </summary>
public interface IIdBlockAllocator
{
    /// <summary>
    /// 占走一个区块：把 <paramref name="propertyName"/> 游标推进 <paramref name="blockSize"/>。
    /// </summary>
    /// <param name="propertyName">属性名（<c>next.dbid</c>）。</param>
    /// <param name="blockSize">区块大小。</param>
    /// <returns>可发放的 id 区间（闭区间）。</returns>
    IdBlock Allocate(string propertyName, int blockSize);
}

/// <summary>
/// 基于 <see cref="IPropertyStore"/> 的区块分配（走<b>当前会话</b>，因此参与命令事务）。
/// <para>
/// ⚠️ 这是<b>兼容路径</b>：保留它的是单元测试与「宿主自己装配仓储」的测试夹具。
/// 生产装配请用 <see cref="DapperIdBlockAllocator"/>（独立短事务，不把行锁带进业务命令）。
/// </para>
/// </summary>
public sealed class PropertyStoreIdBlockAllocator : IIdBlockAllocator
{
    /// <summary>乐观锁冲突时的最大重试次数。</summary>
    private const int MaxAttempts = 20;

    private readonly IPropertyStore _propertyStore;

    public PropertyStoreIdBlockAllocator(IPropertyStore propertyStore)
    {
        ArgumentNullException.ThrowIfNull(propertyStore);
        _propertyStore = propertyStore;
    }

    /// <inheritdoc />
    public IdBlock Allocate(string propertyName, int blockSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        for (var attempt = 0; ; attempt++)
        {
            // 必须加锁读：MySQL REPEATABLE READ 下普通 SELECT 读到的是事务开始时的快照，
            // 冲突后重读永远拿到旧 REV_，会死循环（见 DapperPropertyStore 类注释）。
            var property = _propertyStore.FindProperty(propertyName, forUpdate: true)
                ?? throw MissingProperty(propertyName);

            var lastValue = ParseCursor(property.Value);

            var newValue = lastValue + blockSize;
            var newValueText = newValue.ToString(CultureInfo.InvariantCulture);

            if (_propertyStore.TryUpdateProperty(propertyName, property.Revision, newValueText))
            {
                return new IdBlock(lastValue + 1, newValue);
            }

            if (attempt >= MaxAttempts)
            {
                throw new EngineException(
                    $"ID 区块分配连续冲突 {MaxAttempts} 次（ACT_GE_PROPERTY.{propertyName}）。" +
                    "请检查数据库行锁是否被禁用，或并发是否异常升高。");
            }

            // 冲突 = 另一实例刚占走一块。加锁读会拿到最新已提交版本，立即重读即可成功；
            // 这里刻意不 Sleep：阻塞式退避会把线程池线程钉住，而冲突窗口只有一个语句的时长。
        }
    }

    /// <summary>游标缺失时的统一错误（提示执行初始化脚本）。</summary>
    internal static EngineException MissingProperty(string propertyName) =>
        new($"ACT_GE_PROPERTY 中缺少 '{propertyName}'，数据库脚本未正确初始化。" +
            "请执行 db/ 下对应方言的初始化脚本。");

    /// <summary>游标值解析（非数字时响亮失败）。</summary>
    internal static long ParseCursor(string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new EngineException(
                $"ACT_GE_PROPERTY.next.dbid 的值 '{value}' 不是合法整数。");
        }

        return parsed;
    }
}

/// <summary>
/// 基于 ADO.NET 的区块分配：<b>自建连接 + 自建短事务</b>，提交后立刻释放。
/// </summary>
/// <remarks>
/// <para>
/// 关键点（对照 <see cref="PropertyStoreIdBlockAllocator"/>）：
/// </para>
/// <list type="bullet">
///   <item><description>不参与命令事务 ⇒ <c>ACT_GE_PROPERTY</c> 的行锁只持有到本方法提交，不会拖住整个业务命令；</description></item>
///   <item><description>命令内、命令外都走同一语义（加锁读 + 条件更新），不再有「自动提交下锁立刻释放」的缺口；</description></item>
///   <item><description>失败整体回滚，不会出现「游标推进了但区块丢了」。</description></item>
/// </list>
/// <para>
/// 保持同步是刻意的：<see cref="IIdGenerator.NextId"/> 是同步契约（Activiti 亦然），
/// 把它改成异步会污染所有 ID 分配点；而区块分配是每 2500 个 id 才有一次的冷路径。
/// </para>
/// </remarks>
public sealed class DapperIdBlockAllocator : IIdBlockAllocator
{
    private const int MaxAttempts = 20;

    private readonly IDbConnectionFactory _connections;

    public DapperIdBlockAllocator(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    /// <inheritdoc />
    public IdBlock Allocate(string propertyName, int blockSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        using var connection = _connections.CreateConnection();

        for (var attempt = 0; ; attempt++)
        {
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

            var property = connection.QuerySingleOrDefault<PropertyEntity>(
                PropertySql.ForSelect(forUpdate: true, _connections.DatabaseType),
                new { name = propertyName },
                transaction)
                ?? throw PropertyStoreIdBlockAllocator.MissingProperty(propertyName);

            var lastValue = PropertyStoreIdBlockAllocator.ParseCursor(property.Value);
            var newValue = lastValue + blockSize;

            var affected = connection.Execute(
                PropertySql.Update,
                new
                {
                    name = propertyName,
                    revision = property.Revision,
                    value = newValue.ToString(CultureInfo.InvariantCulture),
                },
                transaction);

            if (affected == 1)
            {
                transaction.Commit();
                return new IdBlock(lastValue + 1, newValue);
            }

            // 加锁读下几乎不可能走到这里（读与写同事务、同行）。保留 CAS 只是兜底：
            // 万一宿主把行锁模式关掉（例如某些只读副本 / 代理），也不会静默发重号。
            transaction.Rollback();

            if (attempt >= MaxAttempts)
            {
                throw new EngineException(
                    $"ID 区块分配连续冲突 {MaxAttempts} 次（ACT_GE_PROPERTY.{propertyName}）。" +
                    "请检查数据库行锁是否被禁用，或并发是否异常升高。");
            }
        }
    }
}

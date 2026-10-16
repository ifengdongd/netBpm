namespace NetBpm.Demo.Storage.DataAccess;

/// <summary>
/// 公共数据操作接口 —— 单表 CRUD 五件套，MySQL 与 SQL Server 各有一份实现：
/// <see cref="MySqlDataAccessor{TEntity}"/> / <see cref="SqlServerDataAccessor{TEntity}"/>。
/// </summary>
/// <remarks>
/// <para>
/// 约定（由 <see cref="IEntityMapper{TEntity}"/> 声明、两个方言实现共同遵守）：
/// </para>
/// <list type="bullet">
///   <item><description>主键由<b>业务侧生成</b>（本 Demo 的两张业务表都是应用发号、不用自增），
///     因此「新增」写入后返回实体上携带的主键值；</description></item>
///   <item><description>「删除」是<b>伪删除</b>：把记录的状态列（如 <c>deleted_flag</c>）置为已删除值，
///     不物理删行；列表与明细查询自动过滤已删除行；</description></item>
///   <item><description>查询条件与排序字段都过<b>列名白名单</b>（<see cref="EntityTableSpec.Columns"/>），
///     列名不进参数、值一律走参数化，防注入；</description></item>
///   <item><description>方言差异（标识符引用、limit / top）收敛在两个实现内部，
///     调用方无感知；使用哪个方言由 <c>NetBpm:DatabaseType</c> 决定（DI 注册时选择）。</description></item>
/// </list>
/// </remarks>
/// <typeparam name="TEntity">实体类型（由映射器翻译成表列）。</typeparam>
public interface IDataAccessor<TEntity> where TEntity : class
{
    /// <summary>1. 按查询条件获取列表（等值条件；<paramref name="criteria"/> 的键为列名、过白名单）。</summary>
    /// <param name="criteria">查询条件（列名 → 值；null 值翻译成 <c>is null</c>）；null 表示无条件。</param>
    /// <param name="orderBy">排序子句体（如 <c>"create_time desc"</c>；列名过白名单），缺省按主键倒序。</param>
    /// <param name="limit">行数上限（可省）。</param>
    ValueTask<IReadOnlyList<TEntity>> FindListAsync(
        IReadOnlyDictionary<string, object?>? criteria = null,
        string? orderBy = null,
        int? limit = null,
        CancellationToken ct = default);

    /// <summary>2. 通过 id 获取记录明细；查不到（或已伪删除）返回 <see langword="null"/>。</summary>
    ValueTask<TEntity?> FindByIdAsync(long id, CancellationToken ct = default);

    /// <summary>3. 新增并返回 id（主键由业务侧生成、随实体写入）。</summary>
    ValueTask<long> InsertAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>4. 修改（按主键更新映射器声明的可更新列）并返回影响记录行数（0 = 记录不存在）。</summary>
    ValueTask<int> UpdateAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>5. 删除 —— <b>伪删除</b>：把状态列置为已删除值（不物理删行），返回影响行数。</summary>
    ValueTask<int> DeleteAsync(long id, CancellationToken ct = default);
}

using System.Text;
using Dapper;
using NetBpm.Persistence;

namespace NetBpm.Demo.Storage.DataAccess;

/// <summary>
/// <see cref="IDataAccessor{TEntity}"/> 的公共实现：SQL 拼装（白名单校验 + 全参数化）、
/// 行读取与实体转换全部在这里；方言差异只有两处抽象成员：
/// <see cref="SelectHead"/> / <see cref="TailLimit"/>（MySQL <c>limit</c> / SQL Server <c>top</c>）
/// 与 <see cref="Quote"/>（反引号 / 方括号）。
/// </summary>
/// <typeparam name="TEntity">实体类型。</typeparam>
public abstract class DataAccessorBase<TEntity> : IDataAccessor<TEntity> where TEntity : class
{
    private readonly IDbConnectionFactory _connections;
    private readonly IEntityMapper<TEntity> _mapper;

    /// <summary>构造访问器（连接工厂与引擎同一个，方言随 <c>NetBpm:DatabaseType</c>）。</summary>
    protected DataAccessorBase(IDbConnectionFactory connections, IEntityMapper<TEntity> mapper)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(mapper);

        _connections = connections;
        _mapper = mapper;
    }

    /// <summary>select 头：MySQL 恒为 <c>select</c>；SQL Server 在带行数上限时为 <c>select top (n)</c>。</summary>
    protected abstract string SelectHead(int? limit);

    /// <summary>select 尾的行数上限：MySQL <c> limit n</c>；SQL Server 空串（top 已在 head）。</summary>
    protected abstract string TailLimit(int? limit);

    /// <summary>标识符引用（列名等）：MySQL 反引号 / SQL Server 方括号。不做 schema 前缀。</summary>
    protected abstract string Quote(string identifier);

    /// <summary>表名引用：在引用的基础上由方言补 schema 前缀（SQL Server <c>wf</c> / MySQL 无）。</summary>
    protected abstract string QuoteTable(string tableName);

    private EntityTableSpec Spec => _mapper.Table;

    // ───────────────────────── 五件套 ─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TEntity>> FindListAsync(
        IReadOnlyDictionary<string, object?>? criteria = null,
        string? orderBy = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var where = BuildWhere(criteria, includeNotDeleted: true, out var parameters);
        var sql = ComposeSelect(SelectColumnList(), where, NormalizeOrderBy(orderBy), limit);

        return (await QueryAsync(sql, parameters, ct).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<TEntity?> FindByIdAsync(long id, CancellationToken ct = default)
    {
        var where = BuildWhere(null, includeNotDeleted: true, out var parameters);
        where = (where.Length == 0 ? string.Empty : where + " and ")
            + $"{Quote(Spec.KeyColumn)} = @{Spec.KeyColumn}";
        (parameters ??= new Dictionary<string, object?>(StringComparer.Ordinal))[Spec.KeyColumn] = id;

        var sql = ComposeSelect(SelectColumnList(), where, orderBy: null, limit: 1);
        var rows = await QueryAsync(sql, parameters, ct).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public async ValueTask<long> InsertAsync(TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var columns = _mapper.ToColumns(entity);
        ValidateColumns(columns.Keys);

        var sql = new StringBuilder()
            .Append("insert into ").Append(QuoteTable(Spec.TableName))
            .Append(" (").AppendJoin(", ", columns.Keys.Select(Quote)).Append(')')
            .Append(" values (").AppendJoin(", ", columns.Keys.Select(name => $"@{name}")).Append(')')
            .ToString();

        await using var connection = _connections.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(sql, columns, cancellationToken: ct))
            .ConfigureAwait(false);

        return _mapper.GetKey(entity);
    }

    /// <inheritdoc />
    public async ValueTask<int> UpdateAsync(TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var columns = _mapper.ToUpdateColumns(entity);
        ValidateColumns(columns.Keys.Append(Spec.KeyColumn));

        var sql = new StringBuilder()
            .Append("update ").Append(QuoteTable(Spec.TableName))
            .Append(" set ").AppendJoin(", ", columns.Keys.Select(name => $"{Quote(name)} = @{name}"))
            .Append(" where ").Append(Quote(Spec.KeyColumn)).Append(" = @").Append(Spec.KeyColumn)
            .ToString();

        var parameters = new Dictionary<string, object?>(columns, StringComparer.Ordinal)
        {
            [Spec.KeyColumn] = _mapper.GetKey(entity),
        };

        await using var connection = _connections.CreateConnection();
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>伪删除：置状态列；表里有 modify_time（在可更新列里）就顺手刷新，留审计痕迹。</remarks>
    public async ValueTask<int> DeleteAsync(long id, CancellationToken ct = default)
    {
        var spec = Spec;
        if (spec.SoftDeleteColumn is not { } deleteColumn)
        {
            throw new NotSupportedException($"表 '{spec.TableName}' 未声明伪删除列（SoftDeleteColumn）。");
        }

        ValidateColumns([deleteColumn]);

        var sets = $"{Quote(deleteColumn)} = @{deleteColumn}";
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [deleteColumn] = spec.SoftDeleteValue,
            [spec.KeyColumn] = id,
        };

        if (spec.UpdateColumns.Contains("modify_time", StringComparer.OrdinalIgnoreCase))
        {
            sets += ", modify_time = @modify_time";
            parameters["modify_time"] = DateTime.UtcNow;
        }

        var sql = $"update {QuoteTable(Spec.TableName)} set {sets} "
            + $"where {Quote(spec.KeyColumn)} = @{spec.KeyColumn} "
            + $"and {Quote(deleteColumn)} <> @{deleteColumn}";

        await using var connection = _connections.CreateConnection();
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    // ───────────────────────── SQL 拼装（白名单 + 参数化）─────────────────────────

    private string SelectColumnList() =>
        string.Join(", ", Spec.Columns.Select(Quote));

    private string ComposeSelect(string columns, string where, string? orderBy, int? limit) =>
        $"{SelectHead(limit)} {columns} from {QuoteTable(Spec.TableName)}"
        + (where.Length == 0 ? string.Empty : $" where {where}")
        + (string.IsNullOrWhiteSpace(orderBy) ? string.Empty : $" order by {orderBy}")
        + TailLimit(limit);

    /// <summary>等值条件拼 where（null → is null）；顺手带上「未删除」过滤。</summary>
    private string BuildWhere(
        IReadOnlyDictionary<string, object?>? criteria,
        bool includeNotDeleted,
        out Dictionary<string, object?>? parameters)
    {
        parameters = null;
        var parts = new List<string>();

        if (criteria is { Count: > 0 })
        {
            ValidateColumns(criteria.Keys);

            foreach (var (column, value) in criteria)
            {
                if (value is null)
                {
                    parts.Add($"{Quote(column)} is null");
                    continue;
                }

                parts.Add($"{Quote(column)} = @{column}");
                (parameters ??= new Dictionary<string, object?>(StringComparer.Ordinal))[column] = value;
            }
        }

        if (includeNotDeleted && Spec.SoftDeleteColumn is { } deleteColumn)
        {
            parts.Add($"{Quote(deleteColumn)} <> @__not_deleted");
            (parameters ??= new Dictionary<string, object?>(StringComparer.Ordinal))["__not_deleted"]
                = Spec.SoftDeleteValue;
        }

        return string.Join(" and ", parts);
    }

    /// <summary>排序子句体校验 + 引用：<c>"create_time desc, business_id"</c> → 引用后的安全子句；缺省主键倒序。</summary>
    private string NormalizeOrderBy(string? orderBy)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            return $"{Quote(Spec.KeyColumn)} desc";
        }

        var items = orderBy
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                var tokens = item.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var column = tokens[0];
                if (!Spec.IsKnownColumn(column))
                {
                    throw new InvalidOperationException(
                        $"排序列 '{column}' 不在表 '{Spec.TableName}' 的列白名单里。");
                }

                var direction = tokens.Length switch
                {
                    1 => string.Empty,
                    2 when string.Equals(tokens[1], "asc", StringComparison.OrdinalIgnoreCase) => " asc",
                    2 when string.Equals(tokens[1], "desc", StringComparison.OrdinalIgnoreCase) => " desc",
                    _ => throw new InvalidOperationException(
                        $"排序方向只支持 asc / desc，当前为 '{item}'。"),
                };

                return $"{Quote(column)}{direction}";
            })
            .ToArray();

        return string.Join(", ", items);
    }

    /// <summary>列名白名单校验（条件 / 排序 / insert / update 共用）。</summary>
    private void ValidateColumns(IEnumerable<string> columns)
    {
        var unknown = columns
            .Where(column => !Spec.IsKnownColumn(column))
            .ToArray();

        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"列 '{string.Join(", ", unknown)}' 不在表 '{Spec.TableName}' 的列白名单里。");
        }
    }

    /// <summary>执行查询：dynamic 行 → 字典 → 实体（映射器转换）。</summary>
    private async ValueTask<IReadOnlyList<TEntity>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync(new CommandDefinition(
            sql,
            parameters is { Count: > 0 } ? new DynamicParameters(parameters) : null,
            cancellationToken: ct)).ConfigureAwait(false);

        var entities = new List<TEntity>();

        foreach (var row in rows)
        {
            if (row is IDictionary<string, object> dictionary)
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, value) in dictionary)
                {
                    values[key] = value;
                }

                var entity = _mapper.FromRow(values);
                if (entity is not null)
                {
                    entities.Add(entity);
                }
            }
        }

        return entities;
    }
}

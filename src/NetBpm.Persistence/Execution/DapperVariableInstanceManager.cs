using System.Globalization;
using Dapper;
using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence.Execution;

/// <summary>
/// <c>ACT_RU_VARIABLE</c> 的 Dapper 实现（P5）。SQL 逐条对应 Activiti 的
/// <c>db/mapping/entity/VariableInstance.xml</c>。
/// <para>
/// 几个必须照搬的点：
/// <list type="bullet">
///   <item><description><c>selectVariablesByExecutionId</c> 带 <c>TASK_ID_ is null</c> 过滤
///     —— 任务局部变量不算作作用域变量；</description></item>
///   <item><description><c>ACT_RU_VARIABLE.REV_</c>：实体创建时置 0，INSERT 写死 1，之后每次更新 +1（乐观锁）；</description></item>
///   <item><description>变量历史行（<c>ACT_HI_VARINST</c>）的 id 与运行行<b>相同</b>，在 INSERT 之后立即记录。</description></item>
/// </list>
/// </para>
/// <para>与 Activiti 的差异：更新时类型变化只写一次 UPDATE（Activiti 会先清空旧值列再写新值，最终行内容一致）。</para>
/// </summary>
public sealed class DapperVariableInstanceManager : IVariableInstanceManager
{
    private readonly IDbSessionProvider _sessions;
    private readonly IIdGenerator _idGenerator;
    private readonly IHistoryManager _historyManager;

    public DapperVariableInstanceManager(
        IDbSessionProvider sessions,
        IIdGenerator idGenerator,
        IHistoryManager historyManager)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(historyManager);

        _sessions = sessions;
        _idGenerator = idGenerator;
        _historyManager = historyManager;
    }

    /// <inheritdoc />
    public VariableInstance Create(string executionId, string? processInstanceId, string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new VariableInstance
        {
            Name = name,
            // 对应 MybatisVariableInstanceDataManager.create()：显式置 0（INSERT 语句再写死 1）
            Revision = 0,
            Value = value,
            TypeName = VariableTypeMapper.ToColumns(value).TypeName,
            ExecutionId = executionId,
            ProcessInstanceId = processInstanceId,
        };
    }

    /// <inheritdoc />
    public async ValueTask InsertAsync(VariableInstance variable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);

        if (string.IsNullOrEmpty(variable.Id))
        {
            variable.Id = _idGenerator.NextId();
        }

        var columns = VariableTypeMapper.ToColumns(variable.Value);
        variable.TypeName = columns.TypeName;

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                InsertSql,
                new
                {
                    id = variable.Id,
                    type = columns.TypeName,
                    name = variable.Name,
                    executionId = variable.ExecutionId,
                    processInstanceId = variable.ProcessInstanceId,
                    taskId = variable.TaskId,
                    longValue = columns.LongValue,
                    doubleValue = columns.DoubleValue,
                    textValue = columns.TextValue,
                    textValue2 = columns.TextValue2,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        variable.Revision = 1;

        // 对应 VariableScopeImpl.createVariableInstance：insert 之后紧接着 recordVariableCreate
        await _historyManager.RecordVariableCreateAsync(variable, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask UpdateAsync(VariableInstance variable, object? value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);

        var columns = VariableTypeMapper.ToColumns(value);
        var revision = variable.Revision;

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var affected = await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateSql,
                new
                {
                    id = variable.Id,
                    revisionNext = revision + 1,
                    revision,
                    type = columns.TypeName,
                    longValue = columns.LongValue,
                    doubleValue = columns.DoubleValue,
                    textValue = columns.TextValue,
                    textValue2 = columns.TextValue2,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (affected != 1)
        {
            throw new OptimisticLockingException(
                $"更新变量 '{variable.Name}'（执行 {variable.ExecutionId}）失败：REV_ = {revision} 的行不存在。"
                + "该行可能已被其它命令并发修改（乐观锁冲突）。");
        }

        variable.Value = value;
        variable.TypeName = columns.TypeName;
        variable.Revision = revision + 1;

        await _historyManager.RecordVariableUpdateAsync(variable, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<VariableInstance>> FindByExecutionIdAsync(
        string executionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(SelectByExecutionIdSql, new { executionId }, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var result = new List<VariableInstance>();

        foreach (IDictionary<string, object> row in rows)
        {
            result.Add(Read(row));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<VariableInstance?> FindByNameAsync(
        string executionId,
        string name,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(SelectByExecutionAndNameSql, new { executionId, name }, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        foreach (IDictionary<string, object> row in rows)
        {
            return Read(row);
        }

        return null;
    }

    private static VariableInstance Read(IDictionary<string, object> row)
    {
        var typeName = Text(row, "TYPE_");

        return new VariableInstance
        {
            Id = Text(row, "ID_") ?? string.Empty,
            Revision = Number(row, "REV_"),
            Name = Text(row, "NAME_") ?? string.Empty,
            TypeName = typeName,
            Value = VariableTypeMapper.FromColumns(
                typeName,
                Long(row, "LONG_"),
                Double(row, "DOUBLE_"),
                Text(row, "TEXT_")),
            ExecutionId = Text(row, "EXECUTION_ID_"),
            ProcessInstanceId = Text(row, "PROC_INST_ID_"),
            TaskId = Text(row, "TASK_ID_"),
        };
    }

    private static object? Raw(IDictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value : null;

    private static string? Text(IDictionary<string, object> row, string column) => Raw(row, column) as string;

    private static int Number(IDictionary<string, object> row, string column) =>
        Raw(row, column) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    private static long? Long(IDictionary<string, object> row, string column) =>
        Raw(row, column) is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;

    private static double? Double(IDictionary<string, object> row, string column) =>
        Raw(row, column) is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : null;

    // ─────────────────────────── SQL（对应 VariableInstance.xml）───────────────────────────

    private const string Columns =
        "ID_, REV_, TYPE_, NAME_, PROC_INST_ID_, EXECUTION_ID_, TASK_ID_, BYTEARRAY_ID_, DOUBLE_, LONG_, TEXT_, TEXT2_";

    /// <summary>INSERT 语句把 <c>REV_</c> 写死为 1（与 Activiti 的 XML 一致）。</summary>
    private const string InsertSql = """
        insert into ACT_RU_VARIABLE (
            ID_, REV_, TYPE_, NAME_, PROC_INST_ID_, EXECUTION_ID_, TASK_ID_,
            BYTEARRAY_ID_, DOUBLE_, LONG_, TEXT_, TEXT2_)
        values (
            @id, 1, @type, @name, @processInstanceId, @executionId, @taskId,
            null, @doubleValue, @longValue, @textValue, @textValue2)
        """;

    private const string UpdateSql = """
        update ACT_RU_VARIABLE set
            REV_ = @revisionNext,
            TYPE_ = @type,
            BYTEARRAY_ID_ = null,
            DOUBLE_ = @doubleValue,
            LONG_ = @longValue,
            TEXT_ = @textValue,
            TEXT2_ = @textValue2
        where ID_ = @id and REV_ = @revision
        """;

    private const string SelectByExecutionIdSql =
        "select " + Columns + " from ACT_RU_VARIABLE where EXECUTION_ID_ = @executionId and TASK_ID_ is null";

    private const string SelectByExecutionAndNameSql =
        "select " + Columns
        + " from ACT_RU_VARIABLE where EXECUTION_ID_ = @executionId and NAME_ = @name and TASK_ID_ is null";
}

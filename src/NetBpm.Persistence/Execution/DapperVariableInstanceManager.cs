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

        // longString：先建 ACT_GE_BYTEARRAY 行，变量行引用它（对应 Activiti 的 setByteArrayValue）
        variable.ByteArrayId = await InsertByteArrayIfNeededAsync(
            session, variable, columns.Bytes, ct).ConfigureAwait(false);

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
                    bytearrayId = variable.ByteArrayId,
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

        // 类型变化时（string ↔ longString 或换新内容）：先建新字节行 → 更新变量行引用 →
        // 最后删旧字节行（ACT_FK_VAR_BYTEARRAY 无级联，顺序不能颠倒）
        var oldByteArrayId = variable.ByteArrayId;
        variable.ByteArrayId = await InsertByteArrayIfNeededAsync(
            session, variable, columns.Bytes, ct).ConfigureAwait(false);

        var affected = await session.Connection.ExecuteAsync(
            new CommandDefinition(
                UpdateSql,
                new
                {
                    id = variable.Id,
                    revisionNext = revision + 1,
                    revision,
                    type = columns.TypeName,
                    bytearrayId = variable.ByteArrayId,
                    longValue = columns.LongValue,
                    doubleValue = columns.DoubleValue,
                    textValue = columns.TextValue,
                    textValue2 = columns.TextValue2,
                },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (oldByteArrayId is not null && oldByteArrayId != variable.ByteArrayId)
        {
            await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    DeleteByteArraySql,
                    new { id = oldByteArrayId },
                    session.Transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
        }

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

        var variables = new List<VariableInstance>();

        foreach (IDictionary<string, object> row in rows)
        {
            variables.Add(Read(row));
        }

        await DecodeLongStringsAsync(session, variables, ct).ConfigureAwait(false);

        return variables;
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
            var variable = Read(row);
            await DecodeLongStringsAsync(session, [variable], ct).ConfigureAwait(false);
            return variable;
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(VariableInstance variable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 先删变量行、再删字节行（ACT_FK_VAR_BYTEARRAY 为 RESTRICT，顺序不能反）；
        // 历史行保留（见接口注释的历史语义）
        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                DeleteVariableSql,
                new { id = variable.Id },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (variable.ByteArrayId is { } byteArrayId)
        {
            await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    DeleteByteArraySql,
                    new { id = byteArrayId },
                    session.Transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<VariableInstance>> FindByTaskIdAsync(
        string taskId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(SelectByTaskIdSql, new { taskId }, session.Transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        var variables = new List<VariableInstance>();

        foreach (IDictionary<string, object> row in rows)
        {
            variables.Add(Read(row));
        }

        await DecodeLongStringsAsync(session, variables, ct).ConfigureAwait(false);

        return variables;
    }

    /// <inheritdoc />
    public async ValueTask DeleteByTaskIdAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        // 先取字节行 id → 删变量行 → 按 id 删字节行（ACT_FK_VAR_BYTEARRAY 为 RESTRICT）；
        // 历史行保留（见接口注释的历史语义）
        var byteArrayIds = (await session.Connection.QueryAsync<string>(
            new CommandDefinition(
                SelectByteArrayIdsByTaskIdSql,
                new { taskId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false)).ToList();

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                DeleteVariablesByTaskIdSql,
                new { taskId },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (byteArrayIds.Count > 0)
        {
            await session.Connection.ExecuteAsync(
                new CommandDefinition(
                    DeleteByteArraysByIdsSql,
                    new { ids = byteArrayIds },
                    session.Transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
        }
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
            ByteArrayId = Text(row, "BYTEARRAY_ID_"),
        };
    }

    /// <summary>把 longString 变量的值从 <c>ACT_GE_BYTEARRAY</c> 取回并解码（一次批量查询）。</summary>
    private static async ValueTask DecodeLongStringsAsync(
        IDbSession session,
        IReadOnlyList<VariableInstance> variables,
        CancellationToken ct)
    {
        var ids = variables
            .Where(variable => variable.TypeName == VariableTypeMapper.TypeNames.LongString)
            .Select(variable => variable.ByteArrayId)
            .Where(id => id is not null)
            .Cast<string>()
            .ToList();

        if (ids.Count == 0)
        {
            return;
        }

        var bytesById = (await session.Connection
            .QueryAsync<(string Id, byte[] Bytes)>(
                new CommandDefinition(
                    SelectByteArrayBytesSql,
                    new { ids },
                    session.Transaction,
                    cancellationToken: ct))
            .ConfigureAwait(false))
            .ToDictionary(pair => pair.Id, pair => pair.Bytes);

        foreach (var variable in variables)
        {
            if (variable.TypeName != VariableTypeMapper.TypeNames.LongString)
            {
                continue;
            }

            variable.Value = VariableTypeMapper.FromColumns(
                variable.TypeName,
                null,
                null,
                null,
                variable.ByteArrayId is { } id && bytesById.TryGetValue(id, out var bytes) ? bytes : null);
        }
    }

    /// <summary>
    /// longString 变量：先建 <c>ACT_GE_BYTEARRAY</c> 行并返回其 id。
    /// <para>NAME_ 取变量名（Activiti 的 <c>VariableInstanceEntityImpl.setByteArrayValue</c> 同口径）。</para>
    /// </summary>
    private async ValueTask<string?> InsertByteArrayIfNeededAsync(
        IDbSession session,
        VariableInstance variable,
        byte[]? bytes,
        CancellationToken ct)
    {
        if (bytes is null)
        {
            return null;
        }

        var byteArrayId = _idGenerator.NextId();

        await session.Connection.ExecuteAsync(
            new CommandDefinition(
                InsertByteArraySql,
                new { id = byteArrayId, name = variable.Name, bytes },
                session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        return byteArrayId;
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
            @bytearrayId, @doubleValue, @longValue, @textValue, @textValue2)
        """;

    private const string UpdateSql = """
        update ACT_RU_VARIABLE set
            REV_ = @revisionNext,
            TYPE_ = @type,
            BYTEARRAY_ID_ = @bytearrayId,
            DOUBLE_ = @doubleValue,
            LONG_ = @longValue,
            TEXT_ = @textValue,
            TEXT2_ = @textValue2
        where ID_ = @id and REV_ = @revision
        """;

    /// <summary>变量级 bytearray：无部署归属（DEPLOYMENT_ID_ 为 null），REV_ 写死 1，GENERATED_ 为 0。</summary>
    private const string InsertByteArraySql = """
        insert into ACT_GE_BYTEARRAY (ID_, REV_, NAME_, DEPLOYMENT_ID_, BYTES_, GENERATED_)
        values (@id, 1, @name, null, @bytes, 0)
        """;

    private const string DeleteByteArraySql = "delete from ACT_GE_BYTEARRAY where ID_ = @id";

    /// <summary>longString 批量取回（Dapper 会把 <c>ids</c> 集合展开成 IN 列表）。</summary>
    private const string SelectByteArrayBytesSql =
        "select ID_ as Id, BYTES_ as Bytes from ACT_GE_BYTEARRAY where ID_ in @ids";

    private const string SelectByTaskIdSql =
        "select " + Columns + " from ACT_RU_VARIABLE where TASK_ID_ = @taskId";

    private const string DeleteVariableSql =
        "delete from ACT_RU_VARIABLE where ID_ = @id";

    private const string DeleteVariablesByTaskIdSql =
        "delete from ACT_RU_VARIABLE where TASK_ID_ = @taskId";

    private const string SelectByteArrayIdsByTaskIdSql =
        "select BYTEARRAY_ID_ from ACT_RU_VARIABLE where TASK_ID_ = @taskId and BYTEARRAY_ID_ is not null";

    private const string DeleteByteArraysByIdsSql =
        "delete from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is null and ID_ in @ids";

    private const string SelectByExecutionIdSql =
        "select " + Columns + " from ACT_RU_VARIABLE where EXECUTION_ID_ = @executionId and TASK_ID_ is null";

    private const string SelectByExecutionAndNameSql =
        "select " + Columns
        + " from ACT_RU_VARIABLE where EXECUTION_ID_ = @executionId and NAME_ = @name and TASK_ID_ is null";
}

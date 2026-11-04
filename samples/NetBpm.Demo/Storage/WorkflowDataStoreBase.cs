using System.Data.Common;
using System.Globalization;
using Dapper;
using NetBpm.Demo.Common;
using NetBpm.Persistence;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 双方言业务数据存储的<b>逻辑基类</b>：instances / instance_current_task / deal_history /
/// workflow_parameters 四张「流程」表的全部端口逻辑与共用工具。
/// </summary>
/// <remarks>
/// <para>
/// 为「一个宿主承载两条流程」做了两处泛化（区别于单流程 Demo 的实现）：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>流程参数不落业务单真列</b>：订单（product_name / amount / remark）与请假（days / reason）
///   的键集不同，统一落 <c>workflow_parameters</c>（0007 新增的「实例 × 参数名」键值表）；
///   </description></item>
///   <item><description>
///   <b>不做业务单 upsert</b>：订单行由 <see cref="IOrderBillStore"/> 在业务入口创建，
///   本实现只管四张「流程」表。
///   </description></item>
/// </list>
/// <para>
/// 方言差异（schema 前缀 / limit 1 vs top (1) / 保留字引用 / upsert 语法 / N 前缀字面量）
/// 全部收敛在 <see cref="IWorkflowStoreDialect"/> —— 子类只挑一个方言单例传进来：
/// <see cref="MySqlWorkflowDataStore"/> 与 <see cref="SqlServerWorkflowDataStore"/>。
/// </para>
/// <para>
/// <c>instances.workflow_type_id</c> 存流程信息枚举的流程类型 id（GUID 字符串）：
/// 写入取发起请求带的 <see cref="BaseModel.WorkflowTypeId"/>，兜底用流程定义 key。
/// </para>
/// </remarks>
public abstract class WorkflowDataStoreBase : IWorkflowDataStore, IWorkflowStoreInspector
{
    /// <summary><c>instances.status_id</c>：流程还在走。</summary>
    public const string StatusRunning = "审批中";

    /// <summary><c>instances.status_id</c>：流程已结束。</summary>
    public const string StatusFinished = "已完成";

    /// <summary><c>instances.describe</c> 是 200 字符上限，超了要截断再写。</summary>
    private const int DescribeMaxLength = 200;

    private static long _sequence;

    private readonly IWorkflowStoreDialect _sql;
    private readonly IDbConnectionFactory _connections;
    private readonly WorkflowRuntimeContext _runtime;
    private readonly IOrgDirectory _org;
    private readonly ILogger _logger;
    private readonly string _connectionString;
    private readonly long _tenantId;

    /// <summary>构造存储（子类传入方言单例与引擎侧同一个连接工厂）。</summary>
    protected WorkflowDataStoreBase(
        IWorkflowStoreDialect dialect,
        IDbConnectionFactory connections,
        WorkflowRuntimeContext runtime,
        IOrgDirectory org,
        IConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _sql = dialect;
        _connections = connections;
        _runtime = runtime;
        _org = org;
        _logger = logger;
        _connectionString = configuration["NetBpm:ConnectionString"] ?? "(未配置)";
        _tenantId = configuration.GetValue(ConstField.WorkflowTenantConfigKey, 1L);
    }

    /// <inheritdoc />
    public string Store => _sql.Store;

    /// <inheritdoc />
    public string Location => $"{_sql.DisplayName} · {DatabaseOf(_connectionString)}";

    // ───────────────────────── 端口：业务登记（instances）─────────────────────────

    /// <inheritdoc />
    public async ValueTask<WorkflowInstanceRecord?> FindInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await using var connection = _connections.CreateConnection();
        var row = await QueryInstanceAsync(connection, null, processInstanceId, ct).ConfigureAwait(false);

        return row is null ? null : new WorkflowInstanceRecord(processInstanceId, ToBaseModel(row));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowInstanceRecord>> FindInstancesAsync(
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection
            .QueryAsync<InstanceRow>(new CommandDefinition(_sql.SelectInstancesSql, cancellationToken: ct))
            .ConfigureAwait(false);

        return [.. rows.Select(row => new WorkflowInstanceRecord(row.WorkflowInstanceId, ToBaseModel(row)))];
    }

    /// <inheritdoc />
    /// <remarks>
    /// 一次事务里写 <c>instances</c>（没有就插、有就更新）并拼好 <c>describe</c>。
    /// 业务单行不在这一步写 —— 订单行归 <see cref="IOrderBillStore"/>，参数归参数表。
    /// </remarks>
    public async ValueTask SaveInstanceAsync(WorkflowInstanceRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var businessId = RequireBusinessId(record);
        var processInstanceId = record.ProcessInstanceId;
        var now = DateTime.UtcNow;
        var workflowTypeId = ResolveWorkflowTypeId(record);
        var describe = Describe(record);

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var applyId = await connection.QueryFirstOrDefaultAsync<long?>(
            new CommandDefinition(
                _sql.SelectApplyIdSql,
                new { processInstanceId },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (applyId is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                _sql.InsertInstanceSql,
                new
                {
                    TenantId = _tenantId,
                    ApplyId = NextId(),
                    record.Base.ApplyTime,
                    WorkflowTypeId = workflowTypeId,
                    Version = ExtractVersion(_runtime.ProcessDefinitionId),
                    Describe = describe,
                    ApplyEmpId = record.Base.ApplyUser ?? string.Empty,
                    ApplyDeptId = record.Base.ApplyDeptId ?? string.Empty,
                    BusinessId = businessId,
                    record.Base.BusinessDeptId,
                    StatusId = StatusRunning,
                    processInstanceId,
                    record.Base.ApplyUser,
                    Now = now,
                },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                _sql.UpdateInstanceSql,
                new
                {
                    ApplyId = applyId,
                    Version = ExtractVersion(_runtime.ProcessDefinitionId),
                    Describe = describe,
                    record.Base.BusinessDeptId,
                    Now = now,
                },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────── 端口：审批结论（status_id + deal_history 还原）─────────────────────────

    /// <inheritdoc />
    public async ValueTask<WorkflowOutcomeRecord?> FindResultAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await using var connection = _connections.CreateConnection();

        var row = await QueryInstanceAsync(connection, null, processInstanceId, ct).ConfigureAwait(false);

        if (row is null || !string.Equals(row.StatusId, StatusFinished, StringComparison.Ordinal))
        {
            return null;
        }

        var parameters = await FindParametersAsync(processInstanceId, ct).ConfigureAwait(false);

        return new WorkflowOutcomeRecord(
            ProcessInstanceId: processInstanceId,
            Base: ToBaseModel(row),
            Outcome: OutcomeOf(row.LastResult),
            ActivityId: _runtime.Nodes.FindId(row.CurrentStep) ?? string.Empty,
            ActivityName: row.CurrentStep ?? string.Empty,
            CompletedAt: row.LastAuditedTime ?? row.ApplyTime,
            Parameters: parameters);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 「落结论」= 把实例置为已完成 + 记下结束在哪个环节；参数快照同步回参数表。
    /// 跳转办结（Closed）没有经过审批环节，补一条「跳转办结」流水。
    /// </remarks>
    public async ValueTask SaveResultAsync(WorkflowOutcomeRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            _sql.FinishInstanceSql,
            new
            {
                ProcessInstanceId = record.ProcessInstanceId,
                CurrentStep = record.ActivityName,
                record.CompletedAt,
                ModifyEmpId = record.Base.ApplyUser,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await UpsertParametersAsync(connection, transaction, record.ProcessInstanceId, record.Parameters, ct)
            .ConfigureAwait(false);

        if (string.Equals(record.Outcome, WorkflowOutcomes.Closed, StringComparison.Ordinal))
        {
            var row = await QueryInstanceAsync(connection, transaction, record.ProcessInstanceId, ct)
                .ConfigureAwait(false);

            if (row is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    _sql.InsertDealHistorySql,
                    new
                    {
                        TenantId = _tenantId,
                        DealHistoryId = NextId(),
                        BusinessId = row.BusinessId,
                        ApplyId = row.ApplyId,
                        TaskNode = record.ActivityName,
                        DealEmpId = record.Base.ApplyUser,
                        DealTime = record.CompletedAt,
                        DealResult = WorkflowOutcomes.Closed,
                        DealComment = record.ActivityName,
                        FormKey = (string?)null,
                        DealEmpName = (string?)null,
                        DealDomainAccount = record.Base.ApplyUser,
                    },
                    transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────── 端口：流程参数表（workflow_parameters）─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, string?>> FindParametersAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<ParameterRow>(new CommandDefinition(
            _sql.SelectParametersSql,
            new { processInstanceId },
            cancellationToken: ct)).ConfigureAwait(false);

        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            parameters[row.ParamName] = row.ParamValue;
        }

        return parameters;
    }

    /// <inheritdoc />
    /// <remarks>「有值就按 key 更新」：逐键 upsert，没提到的键保持原样。</remarks>
    public async ValueTask SaveParametersAsync(
        string processInstanceId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return;
        }

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await UpsertParametersAsync(connection, transaction, processInstanceId, values, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────── 端口：审批记录（deal_history）─────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// 表里没有任务 id / 表单快照，<c>TaskId</c> 为空串、<c>Values</c> 只还原 result / comment；
    /// <c>NextSteps</c> 由下一条流水的环节反推（最后一条为空）。
    /// </remarks>
    public async ValueTask<IReadOnlyList<WorkflowApprovalRecord>> FindApprovalsAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await using var connection = _connections.CreateConnection();

        var rows = (await connection.QueryAsync<DealHistoryRow>(new CommandDefinition(
                _sql.SelectDealHistorySql,
                new { processInstanceId },
                cancellationToken: ct)).ConfigureAwait(false))
            .ToArray();

        var approvals = new List<WorkflowApprovalRecord>(rows.Length);

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var decision = ApprovalResults.TryParseOptionId(row.DealResult);
            var isLast = index + 1 == rows.Length;

            IReadOnlyList<WorkflowActivityRef> nextSteps = !isLast
                ? [new WorkflowActivityRef(
                    _runtime.Nodes.FindId(rows[index + 1].TaskNode) ?? string.Empty,
                    rows[index + 1].TaskNode)]
                : [];

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);

            if (row.DealResult is not null)
            {
                values[BaseModelVariables.Result] = row.DealResult;
            }

            if (row.DealComment is not null)
            {
                values[BaseModelVariables.Comment] = row.DealComment;
            }

            approvals.Add(new WorkflowApprovalRecord(
                ProcessInstanceId: processInstanceId,
                TaskId: string.Empty,
                ActivityId: _runtime.Nodes.FindId(row.TaskNode),
                ActivityName: row.TaskNode,
                FormKey: row.FormKey,
                Assignee: row.DealEmpId,
                Decision: row.DealResult,
                DecisionName: decision?.ToDisplayName(),
                Comment: row.DealComment,
                Values: values,
                NextSteps: nextSteps,
                Outcome: isLast && string.Equals(row.StatusId, StatusFinished, StringComparison.Ordinal)
                    ? OutcomeOf(row.DealResult)
                    : null,
                SubmittedAt: row.DealTime ?? DateTime.UtcNow));
        }

        return approvals;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 快照里真正能落的是 <c>result</c>（→ <c>deal_result</c>）与 <c>comment</c>（→ <c>deal_comment</c>）；
    /// 其余键丢弃并打日志。审批人姓名走 <see cref="IOrgDirectory"/>（平台不持有组织主数据，
    /// 原先的 <c>sys_emp</c> 联查随该表下线一并去掉），查不到只记域账号。
    /// </remarks>
    public async ValueTask SaveApprovalAsync(WorkflowApprovalRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var dropped = record.Values.Keys
            .Where(key => !string.Equals(key, BaseModelVariables.Result, StringComparison.Ordinal)
                && !string.Equals(key, BaseModelVariables.Comment, StringComparison.Ordinal))
            .ToArray();

        if (dropped.Length > 0)
        {
            _logger.LogInformation(
                "deal_history 没有表单快照列，本次提交值里 {Keys} 不落库（result / comment 已各自落到对应列）。",
                string.Join(", ", dropped));
        }

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var target = await connection.QueryFirstOrDefaultAsync<InstanceRef>(
            new CommandDefinition(
                _sql.SelectInstanceRefSql,
                new { record.ProcessInstanceId },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (target is null)
        {
            _logger.LogWarning(
                "实例 {ProcessInstanceId} 没有 instances 登记，审批记录（{Activity}）未落库。",
                record.ProcessInstanceId, record.ActivityName);

            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return;
        }

        var approverName = await ResolveApproverNameAsync(record.Assignee, ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            _sql.InsertDealHistorySql,
            new
            {
                TenantId = _tenantId,
                DealHistoryId = NextId(),
                BusinessId = target.BusinessId,
                ApplyId = target.ApplyId,
                TaskNode = record.ActivityName,
                DealEmpId = record.Assignee,
                DealTime = record.SubmittedAt,
                DealResult = record.Decision,
                DealComment = record.Comment,
                FormKey = record.FormKey,
                DealEmpName = approverName,
                DealDomainAccount = record.Assignee,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────── 端口：当前待办（instance_current_task）─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowCurrentTaskRecord>> FindCurrentTasksAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<CurrentTaskRow>(new CommandDefinition(
            _sql.SelectCurrentTasksSql,
            new { processInstanceId },
            cancellationToken: ct)).ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new WorkflowCurrentTaskRecord(
                ProcessInstanceId: processInstanceId,
                TaskId: row.TaskId ?? string.Empty,
                ActivityId: _runtime.Nodes.FindId(row.TaskNode),
                ActivityName: row.TaskNode,
                Approvers: Split(row.AssignEmpId),
                CandidateUsers: [],
                CandidateGroups: Split(row.TaskOwner),
                FormKey: row.FormKey,
                CreatedAt: row.CreateTime)),
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    /// 先按实例删干净再插新的；顺带刷新 <c>instances.current_step</c> / <c>assign_emp</c>。
    /// ⚠️ 待办为空时不动 <c>instances</c>：那表示实例正在结束，「结束在哪个环节」归
    /// <see cref="SaveResultAsync"/> 写，这里不能抹掉。
    /// </remarks>
    public async ValueTask ReplaceCurrentTasksAsync(
        string processInstanceId,
        IReadOnlyList<WorkflowCurrentTaskRecord> tasks,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentNullException.ThrowIfNull(tasks);

        var now = DateTime.UtcNow;

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var applyId = await connection.QueryFirstOrDefaultAsync<long?>(
            new CommandDefinition(
                _sql.SelectApplyIdSql,
                new { processInstanceId },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (applyId is null)
        {
            _logger.LogWarning(
                "实例 {ProcessInstanceId} 没有 instances 登记，当前待办（{Count} 条）未落库。",
                processInstanceId, tasks.Count);

            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            _sql.DeleteCurrentTasksSql,
            new { applyId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (tasks.Count == 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        foreach (var task in tasks)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                _sql.InsertCurrentTaskSql,
                new
                {
                    TenantId = _tenantId,
                    StepId = NextId(),
                    ApplyId = applyId,
                    TaskId = task.TaskId,
                    TaskNode = task.ActivityName,
                    TaskOwner = Join(task.CandidateGroups.Count > 0 ? task.CandidateGroups : task.CandidateUsers),
                    AssignEmpId = Join(task.Approvers),
                    FormKey = task.FormKey,
                    Now = now,
                },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            _sql.UpdateInstanceStateSql,
            new
            {
                ApplyId = applyId,
                CurrentStep = Join([.. tasks.Select(task => task.ActivityName).OfType<string>()]),
                AssignEmp = Join([.. tasks.SelectMany(task => task.Approvers).Distinct(StringComparer.Ordinal)]),
                Now = now,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────── 落点自述（演示页用）─────────────────────────

    /// <summary>演示页的「看库里写了什么」—— 四张表各 dump 一遍（只读，显式列名 + 别名）。</summary>
    public async ValueTask<IReadOnlyDictionary<string, object?>> SnapshotAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["instances"] = (await connection.QueryAsync<InstanceSnapshotRow>(new CommandDefinition(
                _sql.SelectInstanceSnapshotSql, cancellationToken: ct)).ConfigureAwait(false)).ToArray(),
            ["instance_current_task"] = (await connection.QueryAsync<CurrentTaskRow>(new CommandDefinition(
                _sql.SelectCurrentTaskSnapshotSql, cancellationToken: ct)).ConfigureAwait(false)).ToArray(),
            ["deal_history"] = (await connection.QueryAsync<DealHistoryRow>(new CommandDefinition(
                _sql.SelectDealHistorySnapshotSql, cancellationToken: ct)).ConfigureAwait(false)).ToArray(),
            ["workflow_parameters"] = (await connection.QueryAsync<ParameterRow>(new CommandDefinition(
                _sql.SelectParameterSnapshotSql, cancellationToken: ct)).ConfigureAwait(false)).ToArray(),
        };

        return snapshot;
    }

    // ───────────────────────── 内部工具 ─────────────────────────

    private async ValueTask<InstanceRow?> QueryInstanceAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string processInstanceId,
        CancellationToken ct) =>
        await connection.QueryFirstOrDefaultAsync<InstanceRow>(new CommandDefinition(
            _sql.SelectInstancesSql + " where i.workflow_instance_id = @processInstanceId",
            new { processInstanceId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

    /// <summary>逐键 upsert 参数（调用方负责事务）；没提到的键保持原样。语法差异见方言的 UpsertParameterSql。</summary>
    private async ValueTask UpsertParametersAsync(
        DbConnection connection,
        DbTransaction transaction,
        string processInstanceId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var (name, value) in values)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                _sql.UpsertParameterSql,
                new
                {
                    TenantId = _tenantId,
                    ParamName = name,
                    ParamValue = value,
                    Now = now,
                    processInstanceId,
                },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    /// <summary>流程类型 id：发起请求带的优先（流程信息枚举的 GUID），兜底用流程定义 key（如 order_process）。</summary>
    private string ResolveWorkflowTypeId(WorkflowInstanceRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Base.WorkflowTypeId))
        {
            return record.Base.WorkflowTypeId;
        }

        var fallback = _runtime.ProcessDefinitionId.Split(':')[0];
        _logger.LogWarning(
            "实例 {ProcessInstanceId} 的发起请求没带流程类型 id，回退用流程定义 key '{Fallback}'。",
            record.ProcessInstanceId, fallback);

        return fallback;
    }

    /// <summary>实例描述（<c>instances.describe</c>）：申请人 + 业务单号 + 流程类型 id，超长截断。</summary>
    private static string Describe(WorkflowInstanceRecord record)
    {
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(record.Base.ApplyUser) ? "未知申请人" : record.Base.ApplyUser,
        };

        if (record.Base.BusinessId is not null)
        {
            parts.Add($"业务单 {record.Base.BusinessId}");
        }

        if (record.Base.WorkflowTypeId is not null)
        {
            parts.Add($"类型 {record.Base.WorkflowTypeId}");
        }

        var text = string.Join(" · ", parts);

        return text.Length <= DescribeMaxLength ? text : text[..DescribeMaxLength];
    }

    /// <summary>从业务表还原 <see cref="BaseModel"/>（result / comment 来自最后一条流水）。</summary>
    private static BaseModel ToBaseModel(InstanceRow row) => new(
        BusinessId: row.BusinessId.ToString(CultureInfo.InvariantCulture),
        BusinessDeptId: row.BusinessDeptId,
        Result: ApprovalResults.TryParseOptionId(row.LastResult),
        Comment: row.LastComment,
        ApplyUser: row.ApplyEmpId,
        ApplyTime: AsUtc(row.ApplyTime),
        ApplyDeptId: row.ApplyDeptId,
        WorkflowTypeId: row.WorkflowTypeId);

    /// <summary>由「最后一次审批结果」推结论：以拒绝收尾就是拒绝，其余都是通过（Closed 先于审批结果判定）。</summary>
    private static string OutcomeOf(string? lastDecision) =>
        string.Equals(lastDecision, WorkflowOutcomes.Closed, StringComparison.Ordinal)
            ? WorkflowOutcomes.Closed
            : ApprovalResults.TryParseOptionId(lastDecision) == ApprovalResult.Refused
                ? WorkflowOutcomes.Refused
                : WorkflowOutcomes.Approved;

    /// <summary>业务 Id 必须是正整数 —— <c>instances.business_id</c> 是 bigint。</summary>
    private static long RequireBusinessId(WorkflowInstanceRecord record) =>
        long.TryParse(record.Base.BusinessId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new InvalidOperationException(
                $"业务 Id 必须是正整数（业务表里 business_id 是 bigint），当前为 '{record.Base.BusinessId}'。"
                + "请让调用方传数字业务单号。");

    /// <summary>从 <c>order_process:1:1</c> 里取出版本段（<c>1</c>）。</summary>
    private static string? ExtractVersion(string processDefinitionId)
    {
        var parts = processDefinitionId.Split(':');

        return parts.Length >= 2 ? parts[1] : null;
    }

    /// <summary>连接串 → 「主机[:端口]/库名」（不外泄密码）。</summary>
    private static string DatabaseOf(string connectionString)
    {
        var server = Field(connectionString, "Server")
            ?? Field(connectionString, "Host")
            ?? Field(connectionString, "Data Source")
            ?? "?";
        var port = Field(connectionString, "Port");
        var database = Field(connectionString, "Database")
            ?? Field(connectionString, "Initial Catalog")
            ?? "?";

        return port is null ? $"{server}/{database}" : $"{server}:{port}/{database}";
    }

    private static string? Field(string connectionString, string name) =>
        connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && string.Equals(pair[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair[1].Trim())
            .FirstOrDefault(value => value.Length > 0);

    private static string? Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : string.Join(",", values);

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>库里的 datetime 无时区：统一按 UTC 解释（写进去的就是 DateTime.UtcNow）。</summary>
    private static DateTime? AsUtc(DateTime? value) =>
        value is { } moment ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : null;

    /// <summary>域账号 → 姓名（查不到返回 <see langword="null"/>，视图层退回展示域账号）。</summary>
    private async ValueTask<string?> ResolveApproverNameAsync(string? domainAccount, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domainAccount))
        {
            return null;
        }

        var employee = await _org.FindEmployeeAsync(domainAccount, ct).ConfigureAwait(false);

        return employee?.Name;
    }

    /// <summary>发一个业务主键：毫秒时间戳 &lt;&lt; 20 | 同毫秒序号（单进程唯一；多实例部署要换统一发号）。</summary>
    private static long NextId()
    {
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return (milliseconds << 20) | (Interlocked.Increment(ref _sequence) & 0xFFFFF);
    }
}

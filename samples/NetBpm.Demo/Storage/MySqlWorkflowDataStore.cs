using System.Data.Common;
using System.Globalization;
using Dapper;
using NetBpm.Persistence;
using NetBpm.Workflow;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 业务数据落 <b>MySQL 业务表</b>的 <see cref="IWorkflowDataStore"/> 实现（合并 Demo 版）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>NetBpm.Demo.LeaveApproval</c> 里的同名实现同源，但为「一个宿主承载两条流程」做了两处泛化：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>流程参数不落业务单真列</b>：订单（product_name / amount / remark）与请假（days / reason）
///   的键集不同，统一落 <c>workflow_parameters</c>（0007 新增的「实例 × 参数名」键值表）；
///   </description></item>
///   <item><description>
///   <b>不做业务单 upsert</b>：订单行由 <see cref="IOrderBillStore"/>（MySqlOrderBillStore）在
///   业务入口创建，本实现只管 instances / instance_current_task / deal_history / workflow_parameters 四张「流程」表。
///   </description></item>
/// </list>
/// <para>
/// <c>instances.workflow_type_id</c>（varchar(64)）存流程信息枚举的流程类型 id（GUID 字符串）：
/// 写入取发起请求带的 <see cref="BaseModel.WorkflowTypeId"/>，兜底用流程定义 key。
/// </para>
/// </remarks>
public sealed class MySqlWorkflowDataStore : IWorkflowDataStore, IWorkflowStoreInspector
{
    /// <summary><c>instances.status_id</c>：流程还在走。</summary>
    public const string StatusRunning = "审批中";

    /// <summary><c>instances.status_id</c>：流程已结束。</summary>
    public const string StatusFinished = "已完成";

    /// <summary><c>instances.describe</c> 是 <c>varchar(200)</c>，超了要截断再写。</summary>
    private const int DescribeMaxLength = 200;

    private static long _sequence;

    private readonly IDbConnectionFactory _connections;
    private readonly WorkflowRuntimeContext _runtime;
    private readonly ILogger<MySqlWorkflowDataStore> _logger;
    private readonly string _connectionString;
    private readonly long _tenantId;

    /// <summary>构造存储。</summary>
    public MySqlWorkflowDataStore(
        IDbConnectionFactory connections,
        WorkflowRuntimeContext runtime,
        IConfiguration configuration,
        ILogger<MySqlWorkflowDataStore> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _runtime = runtime;
        _logger = logger;
        _connectionString = configuration["NetBpm:ConnectionString"] ?? "(未配置)";
        _tenantId = configuration.GetValue("Workflow:TenantId", 1L);
    }

    /// <inheritdoc />
    public string Store => "MySql";

    /// <inheritdoc />
    public string Location => $"MySQL · {DatabaseOf(_connectionString)}";

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
            .QueryAsync<InstanceRow>(new CommandDefinition(SelectInstancesSql, cancellationToken: ct))
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
                "select apply_id from instances where workflow_instance_id = @processInstanceId limit 1",
                new { processInstanceId },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (applyId is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertInstanceSql,
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
                UpdateInstanceSql,
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

    // ───────────────────────── 端口：审批结论（instances.status_id + deal_history 还原）─────────────────────────

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
            FinishInstanceSql,
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
                    InsertDealHistorySql,
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
            SelectParametersSql,
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
                SelectDealHistorySql,
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
    /// 其余键丢弃并打日志。审批人姓名从 sys_emp 联查，查不到只记域账号。
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
                "select apply_id as ApplyId, business_id as BusinessId from instances where workflow_instance_id = @processInstanceId limit 1",
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

        var approverName = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "select emp_name from sys_emp where tenant_id = @TenantId and domain_account = @DealEmpId limit 1",
            new { TenantId = _tenantId, DealEmpId = record.Assignee },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            InsertDealHistorySql,
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
            SelectCurrentTasksSql,
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
                "select apply_id from instances where workflow_instance_id = @processInstanceId limit 1",
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
            "delete from instance_current_task where apply_id = @applyId",
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
                InsertCurrentTaskSql,
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
            UpdateInstanceStateSql,
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
                SelectInstanceSnapshotSql, cancellationToken: ct)).ConfigureAwait(false)).ToArray(),
            ["instance_current_task"] = (await connection.QueryAsync<CurrentTaskRow>(new CommandDefinition(
                SelectCurrentTaskSnapshotSql, cancellationToken: ct)).ConfigureAwait(false)).ToArray(),
            ["deal_history"] = (await connection.QueryAsync<DealHistoryRow>(new CommandDefinition(
                SelectDealHistorySnapshotSql, cancellationToken: ct)).ConfigureAwait(false)).ToArray(),
            ["workflow_parameters"] = (await connection.QueryAsync<ParameterRow>(new CommandDefinition(
                SelectParametersSql.Replace(" where workflow_instance_id = @processInstanceId", string.Empty)
                    + " order by workflow_instance_id, param_name", cancellationToken: ct))
                .ConfigureAwait(false)).ToArray(),
        };

        return snapshot;
    }

    // ───────────────────────── SQL ─────────────────────────

    private const string SelectInstancesSql = """
        select i.apply_id             as ApplyId,
               i.business_id          as BusinessId,
               i.business_dept_id     as BusinessDeptId,
               i.apply_emp_id         as ApplyEmpId,
               i.apply_dept_id        as ApplyDeptId,
               i.apply_time           as ApplyTime,
               i.workflow_type_id     as WorkflowTypeId,
               i.workflow_instance_id as WorkflowInstanceId,
               i.status_id            as StatusId,
               i.current_step         as CurrentStep,
               i.last_audited_time    as LastAuditedTime,
               h.deal_result          as LastResult,
               h.deal_comment         as LastComment
          from instances i
          left join deal_history h
            on h.deal_history_id = (
                 select h2.deal_history_id
                   from deal_history h2
                  where h2.apply_id = i.apply_id
                    and h2.deal_result is not null
                  order by h2.deal_time desc, h2.deal_history_id desc
                  limit 1)
        """;

    private const string InsertInstanceSql = """
        insert into instances
            (tenant_id, apply_id, apply_time, workflow_type_id, workflow_version, `describe`,
             apply_emp_id, apply_dept_id, business_id, business_dept_id, status_id,
             workflow_instance_id, create_emp_id, create_time)
        values
            (@TenantId, @ApplyId, @ApplyTime, @WorkflowTypeId, @Version, @Describe,
             @ApplyEmpId, @ApplyDeptId, @BusinessId, @BusinessDeptId, @StatusId,
             @processInstanceId, @ApplyUser, @Now)
        """;

    private const string UpdateInstanceSql = """
        update instances
           set workflow_version = @Version,
               `describe`       = @Describe,
               business_dept_id = @BusinessDeptId,
               modify_time      = @Now
         where apply_id = @ApplyId
        """;

    private const string FinishInstanceSql = """
        update instances
           set status_id         = '已完成',
               current_step      = @CurrentStep,
               assign_emp        = null,
               last_audited_time = @CompletedAt,
               modify_emp_id     = @ModifyEmpId,
               modify_time       = @CompletedAt
         where workflow_instance_id = @ProcessInstanceId
        """;

    private const string UpdateInstanceStateSql = """
        update instances
           set current_step  = @CurrentStep,
               assign_emp    = @AssignEmp,
               modify_time   = @Now
         where apply_id = @ApplyId
        """;

    private const string SelectParametersSql = """
        select workflow_instance_id as WorkflowInstanceId,
               param_name           as ParamName,
               param_value          as ParamValue
          from workflow_parameters
         where workflow_instance_id = @processInstanceId
        """;

    private const string UpsertParameterSql = """
        insert into workflow_parameters
            (tenant_id, workflow_instance_id, param_name, param_value, create_time)
        values
            (@TenantId, @processInstanceId, @ParamName, @ParamValue, @Now)
        on duplicate key update
            param_value = values(param_value),
            modify_time = @Now
        """;

    private const string SelectDealHistorySql = """
        select h.deal_history_id as DealHistoryId,
               h.task_node       as TaskNode,
               h.deal_emp_id     as DealEmpId,
               h.deal_time       as DealTime,
               h.deal_result     as DealResult,
               h.deal_comment    as DealComment,
               h.form_key        as FormKey,
               i.status_id       as StatusId
          from deal_history h
          join instances i on i.apply_id = h.apply_id
         where i.workflow_instance_id = @processInstanceId
         order by h.deal_time asc, h.deal_history_id asc
        """;

    private const string InsertDealHistorySql = """
        insert into deal_history
            (tenant_id, deal_history_id, business_id, apply_id, task_node, deal_emp_id,
             deal_time, deal_result, deal_comment, form_key, deal_emp_name, deal_domain_account)
        values
            (@TenantId, @DealHistoryId, @BusinessId, @ApplyId, @TaskNode, @DealEmpId,
             @DealTime, @DealResult, @DealComment, @FormKey, @DealEmpName, @DealDomainAccount)
        """;

    private const string SelectCurrentTasksSql = """
        select t.task_id       as TaskId,
               t.task_node     as TaskNode,
               t.task_owner    as TaskOwner,
               t.assign_emp_id as AssignEmpId,
               t.form_key      as FormKey,
               t.create_time   as CreateTime
          from instance_current_task t
          join instances i on i.apply_id = t.apply_id
         where i.workflow_instance_id = @processInstanceId
         order by t.step_id asc
        """;

    private const string InsertCurrentTaskSql = """
        insert into instance_current_task
            (tenant_id, step_id, apply_id, task_id, task_node, task_owner, assign_emp_id, form_key, create_time)
        values
            (@TenantId, @StepId, @ApplyId, @TaskId, @TaskNode, @TaskOwner, @AssignEmpId, @FormKey, @Now)
        """;

    private const string SelectInstanceSnapshotSql = """
        select tenant_id             as TenantId,
               apply_id              as ApplyId,
               apply_time            as ApplyTime,
               workflow_type_id      as WorkflowTypeId,
               workflow_version      as WorkflowVersion,
               `describe`            as `Describe`,
               apply_emp_id          as ApplyEmpId,
               apply_dept_id         as ApplyDeptId,
               business_id           as BusinessId,
               business_dept_id      as BusinessDeptId,
               status_id             as StatusId,
               newest_flag           as NewestFlag,
               workflow_instance_id  as WorkflowInstanceId,
               last_audited_time     as LastAuditedTime,
               create_emp_id         as CreateEmpId,
               create_time           as CreateTime,
               current_step          as CurrentStep,
               assign_emp            as AssignEmp,
               modify_emp_id         as ModifyEmpId,
               modify_time           as ModifyTime,
               deleted_flag          as DeletedFlag,
               original_business_id  as OriginalBusinessId,
               flow_file_id          as FlowFileId
          from instances
         order by apply_id desc
        """;

    private const string SelectCurrentTaskSnapshotSql = """
        select tenant_id     as TenantId,
               step_id       as StepId,
               apply_id      as ApplyId,
               task_id       as TaskId,
               task_node     as TaskNode,
               task_owner    as TaskOwner,
               assign_emp_id as AssignEmpId,
               form_key      as FormKey,
               create_time   as CreateTime
          from instance_current_task
         order by step_id desc
        """;

    private const string SelectDealHistorySnapshotSql = """
        select tenant_id           as TenantId,
               deal_history_id     as DealHistoryId,
               business_id         as BusinessId,
               apply_id            as ApplyId,
               task_node           as TaskNode,
               deal_emp_id         as DealEmpId,
               deal_time           as DealTime,
               deal_result         as DealResult,
               deal_comment        as DealComment,
               form_key            as FormKey,
               deal_emp_name       as DealEmpName,
               deal_domain_account as DealDomainAccount
          from deal_history
         order by deal_history_id desc
        """;

    // ───────────────────────── 内部工具 ─────────────────────────

    private static async ValueTask<InstanceRow?> QueryInstanceAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string processInstanceId,
        CancellationToken ct) =>
        await connection.QueryFirstOrDefaultAsync<InstanceRow>(new CommandDefinition(
            SelectInstancesSql + " where i.workflow_instance_id = @processInstanceId limit 1",
            new { processInstanceId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

    /// <summary>逐键 upsert 参数（调用方负责事务）；没提到的键保持原样。</summary>
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
                UpsertParameterSql,
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
        var server = Field(connectionString, "Server") ?? Field(connectionString, "Host") ?? "?";
        var port = Field(connectionString, "Port");
        var database = Field(connectionString, "Database") ?? "?";

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

    /// <summary>发一个业务主键：毫秒时间戳 &lt;&lt; 20 | 同毫秒序号（单进程唯一；多实例部署要换统一发号）。</summary>
    private static long NextId()
    {
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return (milliseconds << 20) | (Interlocked.Increment(ref _sequence) & 0xFFFFF);
    }

    // ───────────────────────── 行映射 ─────────────────────────

    private sealed class InstanceRow
    {
        public long ApplyId { get; set; }

        public long BusinessId { get; set; }

        public string? BusinessDeptId { get; set; }

        public string ApplyEmpId { get; set; } = string.Empty;

        public string ApplyDeptId { get; set; } = string.Empty;

        public DateTime ApplyTime { get; set; }

        public string? WorkflowTypeId { get; set; }

        public string WorkflowInstanceId { get; set; } = string.Empty;

        public string StatusId { get; set; } = string.Empty;

        public string? CurrentStep { get; set; }

        public DateTime? LastAuditedTime { get; set; }

        public string? LastResult { get; set; }

        public string? LastComment { get; set; }
    }

    private sealed class InstanceSnapshotRow
    {
        public long TenantId { get; set; }

        public long ApplyId { get; set; }

        public DateTime ApplyTime { get; set; }

        public string? WorkflowTypeId { get; set; }

        public string? WorkflowVersion { get; set; }

        public string? Describe { get; set; }

        public string ApplyEmpId { get; set; } = string.Empty;

        public string ApplyDeptId { get; set; } = string.Empty;

        public long BusinessId { get; set; }

        public string? BusinessDeptId { get; set; }

        public string StatusId { get; set; } = string.Empty;

        public short NewestFlag { get; set; }

        public string? WorkflowInstanceId { get; set; }

        public DateTime? LastAuditedTime { get; set; }

        public string CreateEmpId { get; set; } = string.Empty;

        public DateTime CreateTime { get; set; }

        public string? CurrentStep { get; set; }

        public string? AssignEmp { get; set; }

        public string? ModifyEmpId { get; set; }

        public DateTime? ModifyTime { get; set; }

        public byte DeletedFlag { get; set; }

        public long? OriginalBusinessId { get; set; }

        public long? FlowFileId { get; set; }
    }

    private sealed class ParameterRow
    {
        public string WorkflowInstanceId { get; set; } = string.Empty;

        public string ParamName { get; set; } = string.Empty;

        public string? ParamValue { get; set; }
    }

    private sealed class DealHistoryRow
    {
        public long DealHistoryId { get; set; }

        public long? BusinessId { get; set; }

        public long? ApplyId { get; set; }

        public string? TaskNode { get; set; }

        public string? DealEmpId { get; set; }

        public DateTime? DealTime { get; set; }

        public string? DealResult { get; set; }

        public string? DealComment { get; set; }

        public string? FormKey { get; set; }

        /// <summary>来自 instances.status_id（join 出来）—— 判断这条流水是不是「让流程结束的那条」。</summary>
        public string? StatusId { get; set; }

        public string? DealEmpName { get; set; }

        public string? DealDomainAccount { get; set; }
    }

    private sealed class CurrentTaskRow
    {
        public long StepId { get; set; }

        public long? ApplyId { get; set; }

        public string? TaskId { get; set; }

        public string? TaskNode { get; set; }

        public string? TaskOwner { get; set; }

        public string? AssignEmpId { get; set; }

        public string? FormKey { get; set; }

        public DateTime CreateTime { get; set; }
    }

    private sealed class InstanceRef
    {
        public long ApplyId { get; set; }

        public long BusinessId { get; set; }
    }
}

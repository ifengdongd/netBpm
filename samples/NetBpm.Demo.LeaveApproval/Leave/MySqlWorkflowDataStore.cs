using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using NetBpm.Persistence;
using NetBpm.Workflow;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 业务数据落 <b>MySQL 业务表</b>的 <see cref="IWorkflowDataStore"/> 实现。
/// </summary>
/// <remarks>
/// <para>
/// 四张表各管一摊，与端口的四块数据一一对应（外加一张业务单表）：
/// </para>
/// <list type="table">
///   <item><description><c>instances</c> —— 实例登记：谁在申请、业务单是哪个、现在停在哪儿、谁在处理；</description></item>
///   <item><description><c>instance_current_task</c> —— 当前待办：<b>流程每到达一个环节就刷新一次</b>；</description></item>
///   <item><description><c>deal_history</c> —— 审批流水：每次提交追加一条；</description></item>
///   <item><description><c>leave_bill</c> —— 请假单本身：<c>days</c> / <c>reason</c> 是它的真列，其余流程参数进 <c>ext_params</c>。</description></item>
/// </list>
/// <para>
/// 🔑 <b>这个实现是「映射示例」，也是「适配器样版」</b>：它演示了怎么把一套通用端口
/// 落到一套<b>既有的、字段并不齐全</b>的业务表上。哪些字段能从表里还原、哪些还原不了，
/// 都在下面逐条注明 —— 换别的业务表时照着对一遍即可。
/// </para>
/// <para>
/// ⚠️ 两处刻意的「降级」，都是因为表里没那一列（不影响流程正确性）：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>审批记录的「本次提交值」只还原得出 <c>result</c> / <c>comment</c></b> ——
///   <c>deal_history</c> 没有表单快照列；
///   </description></item>
///   <item><description>
///   <b>「提交后流转到哪儿」（<c>NextSteps</c>）由下一条流水反推</b>，最后一条只能是空。
///   </description></item>
/// </list>
/// </remarks>
public sealed class MySqlWorkflowDataStore : IWorkflowDataStore, ILeaveBusinessInspector
{
    /// <summary><c>instances.status_id</c>：流程还在走。</summary>
    public const string StatusRunning = "审批中";

    /// <summary><c>instances.status_id</c>：流程已结束。</summary>
    public const string StatusFinished = "已完成";

    /// <summary><c>instances.describe</c> 是 <c>varchar(200)</c>，超了要截断再写。</summary>
    private const int DescribeMaxLength = 200;

    /// <summary>MySQL 5.7 的 <c>smallint</c> 上界（<c>days</c> 是 smallint）。</summary>
    private const int DaysMaxValue = short.MaxValue;

    /// <summary>
    /// 业务主键发号器：<c>时间戳(毫秒) &lt;&lt; 20 | 序号</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DDL 明确写了主键<b>不用 AUTO_INCREMENT</b>（与来源系统一致），所以得自己发号。
    /// 这里用「雪花算法的单机简化版」：毫秒时间戳保证递增、同毫秒内用 <see cref="Interlocked"/> 计数，
    /// 单进程内绝不重复，且重启后不回退。
    /// </para>
    /// <para>
    /// ⚠️ <b>多实例部署要换成统一发号服务</b>（或号段表）—— 这个实现只保证单进程唯一。
    /// </para>
    /// </remarks>
    private static long _sequence;

    private readonly IDbConnectionFactory _connections;
    private readonly WorkflowRuntimeContext _runtime;
    private readonly ILogger<MySqlWorkflowDataStore> _logger;
    private readonly string _connectionString;
    private readonly long _tenantId;
    private readonly long _workflowTypeId;

    /// <summary>构造存储。</summary>
    /// <param name="connections">连接工厂（由 <c>AddNetBpm</c> 按 <c>NetBpm:*</c> 配置注册）。</param>
    /// <param name="runtime">部署后才有值的运行时上下文（环节名 → id 映射）。</param>
    /// <param name="configuration">配置（租户 / 流程类型 / 连接串展示）。</param>
    /// <param name="logger">日志。</param>
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
        _tenantId = configuration.GetValue("Leave:TenantId", 1L);
        _workflowTypeId = configuration.GetValue("Leave:WorkflowTypeId", 1L);
    }

    /// <inheritdoc />
    public string Store => "MySql";

    /// <inheritdoc />
    /// <remarks>
    /// 连接串里带密码，所以只报「库名 + 主机」这半截 —— 演示页会把这句话显示在界面上。
    /// </remarks>
    public string Location => $"MySQL · {DatabaseOf(_connectionString)}";

    // ───────────────────────── 端口：业务登记（instances + leave_bill）─────────────────────────

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
    /// 一次事务里做三件事：写 <c>instances</c>（没有就插、有就更新）、
    /// 保证 <c>leave_bill</c> 有对应行、<c>describe</c> 用当前参数拼一句人能看懂的话。
    /// </remarks>
    public async ValueTask SaveInstanceAsync(WorkflowInstanceRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var businessId = RequireBusinessId(record);
        var processInstanceId = record.ProcessInstanceId;
        var now = DateTime.UtcNow;

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var applyId = await connection.QueryFirstOrDefaultAsync<long?>(
            new CommandDefinition(
                "select apply_id from instances where workflow_instance_id = @processInstanceId limit 1",
                new { processInstanceId },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        var parameters = await ReadParametersAsync(connection, transaction, processInstanceId, ct).ConfigureAwait(false);
        var describe = Describe(record.Base.ApplyUser, parameters);

        if (applyId is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertInstanceSql,
                new
                {
                    TenantId = _tenantId,
                    ApplyId = NextId(),
                    record.Base.ApplyTime,
                    WorkflowTypeId = _workflowTypeId,
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

        // 业务单：第一次见到就建行；已存在只刷新与申请相关的列。
        // ⚠️ 这里刻意<b>不动 days / reason / ext_params</b> —— 那是参数表的活（见 SaveParametersAsync），
        //    两处都写就会有两个来源，迟早对不上。
        await connection.ExecuteAsync(new CommandDefinition(
            UpsertLeaveBillSql,
            new
            {
                TenantId = _tenantId,
                BusinessId = businessId,
                ApplyUser = record.Base.ApplyUser ?? string.Empty,
                ApplyDeptId = record.Base.ApplyDeptId ?? string.Empty,
                record.Base.BusinessDeptId,
                Now = now,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────── 端口：审批结论（由 instances.status_id + deal_history 还原）─────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <b>没有「结论表」</b>：结论就是「<c>status_id = 已完成</c>」这件事本身，
    /// 加上最后一条带结果的审批流水。产生结论的环节名取自 <c>current_step</c>（结束时写的就是它），
    /// 环节 id 再借环节名映射还原回 BPMN 节点。
    /// </remarks>
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

        var parameters = await ReadParametersAsync(connection, null, processInstanceId, ct).ConfigureAwait(false);

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
    /// 「落结论」= 把实例置为已完成 + 记下结束在哪个环节；参数快照顺手同步回业务单，
    /// 免得流程走完后 <c>leave_bill</c> 里还是中途的旧值。
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

        await SaveParameterColumnsAsync(connection, transaction, record.ProcessInstanceId, record.Parameters, ct)
            .ConfigureAwait(false);

        // 跳转办结没有经过任何审批环节 ⇒ deal_history 里没有承载结论的流水；
        // 补一条「跳转办结」流水，让 FindResultAsync（按最后一条带结果流水还原）能读回 Closed。
        if (string.Equals(record.Outcome, WorkflowOutcomes.Closed, StringComparison.Ordinal))
        {
            // 实例行要在事务里再读一次：deal_history 的 business_id / apply_id 都从这里来
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

    // ───────────────────────── 端口：流程参数表（落到 leave_bill 的列 + ext_params）─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, string?>> FindParametersAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await using var connection = _connections.CreateConnection();

        return await ReadParametersAsync(connection, null, processInstanceId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <c>days</c> / <c>reason</c> 落到业务单的真列上，**其余键进 <c>ext_params</c>（JSON）** ——
    /// 真实系统里业务参数往往就是「几个固定列 + 一个扩展字段」这个形状。
    /// </para>
    /// <para>
    /// 「有值就按 key 更新」的语义靠<b>先读后写</b>实现（只覆盖本次提到的键），
    /// 读的时候加行锁，避免并发提交互相覆盖 <c>ext_params</c>。
    /// </para>
    /// </remarks>
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

        await SaveParameterColumnsAsync(connection, transaction, processInstanceId, values, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────── 端口：审批记录（deal_history）─────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ 表里没有任务 id / 表单快照，所以 <c>TaskId</c> 是空串、<c>Values</c> 只还原得出
    /// <c>result</c> / <c>comment</c>；<c>NextSteps</c> 由<b>下一条流水的环节</b>反推
    /// （最后一条只能是空 —— 它之后确实没有下一步了）。
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

            // 下一条流水停在哪个环节 = 本次提交把流程送到了哪儿
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
                // deal_history 没有任务 id 列：应用层会退化成「按环节名按序认领」引擎历史
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
                // 「让流程结束的那条」= 最后一条 + 实例已置为已完成；结论由审批结果推（表里没有结论列）
                Outcome: isLast && string.Equals(row.StatusId, StatusFinished, StringComparison.Ordinal)
                    ? OutcomeOf(row.DealResult)
                    : null,
                SubmittedAt: row.DealTime ?? DateTime.UtcNow));
        }

        return approvals;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 审批记录<b>没有「表单值快照」这一列</b>，快照里真正能落的是
    /// <c>result</c>（→ <c>deal_result</c>，存的是 BPMN 选项 id）与 <c>comment</c>（→ <c>deal_comment</c>）；
    /// 其余键丢弃并打日志 —— 要留全量快照得给表加列，这是业务表说了算的事。
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

        // 审批人姓名从组织架构表联查（deal_emp_name / deal_domain_account 两列此前一直空着）；
        // 员工不在 sys_emp 里（如测试账号）就只记域账号，不阻断落库。
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
                // deal_time 是「审批时间」——业务表里没有「提交时间」以外的时间可用
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
    /// <para>
    /// 整体替换 = <b>先按实例删干净，再插新的</b>：流程每前进一步，上一组待办就作废了，
    /// 逐条 diff 既啰嗦又容易漏（走到结束时要全删）。
    /// </para>
    /// <para>
    /// 顺带把 <c>instances.current_step</c> / <c>assign_emp</c> 刷成「当前环节 + 当前处理人」——
    /// 这两列就是给列表页和外部系统看的。
    /// </para>
    /// <para>
    /// ⚠️ <b>待办为空时不动 <c>instances</c></b>：那表示实例正在结束，
    /// 而「结束在哪个环节」是 <see cref="SaveResultAsync"/> 写的，这里不能把它抹掉。
    /// </para>
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
                    // 待办项归属人：候选组 / 候选人原样留着 —— 「该找谁认领」比「谁批的」更持久
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

    /// <summary>
    /// 演示页的「看库里写了什么」—— 四张表各 dump 一遍（只读）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>刻意不用 <c>select *</c></b>：Dapper 默认<b>不</b>把蛇形列名对到驼峰属性
    ///（<c>business_id</c> ↛ <c>BusinessId</c>，要靠 <c>MatchNamesWithUnderscores</c> 才认），
    /// 结果就是「行数对、字段全是默认值」这种最难查的错。显式起别名最稳，
    /// 顺带也让「改了哪列」在 <c>select</c> 里看得见。
    /// </remarks>
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
            ["leave_bill"] = (await connection.QueryAsync<LeaveBillRow>(new CommandDefinition(
                SelectLeaveBillSnapshotSql, cancellationToken: ct)).ConfigureAwait(false)).ToArray(),
        };

        return snapshot;
    }

    // ───────────────────────── SQL ─────────────────────────

    /// <summary>
    /// 查实例：顺带把「最后一次带结果的审批」拖出来 —— <c>result</c> / <c>comment</c> 不在
    /// <c>instances</c> 里，得从流水还原成 <see cref="BaseModel"/> 的一部分。
    /// </summary>
    private const string SelectInstancesSql = """
        select i.apply_id             as ApplyId,
               i.business_id          as BusinessId,
               i.business_dept_id     as BusinessDeptId,
               i.apply_emp_id         as ApplyEmpId,
               i.apply_dept_id        as ApplyDeptId,
               i.apply_time           as ApplyTime,
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

    /// <summary>业务单：没有就建，有就只刷新「申请」相关列（参数列归参数表管）。</summary>
    private const string UpsertLeaveBillSql = """
        insert into leave_bill
            (tenant_id, business_id, apply_user, apply_dept_id, business_dept_id, days, reason, ext_params, create_time, modify_time)
        values
            (@TenantId, @BusinessId, @ApplyUser, @ApplyDeptId, @BusinessDeptId, null, null, null, @Now, @Now)
        on duplicate key update
            apply_user        = values(apply_user),
            apply_dept_id     = values(apply_dept_id),
            business_dept_id  = values(business_dept_id),
            modify_time       = values(modify_time)
        """;

    private const string SelectParameterColumnsSql = """
        select b.apply_user as ApplyUser,
               b.days       as Days,
               b.reason     as Reason,
               b.ext_params as ExtParams
          from instances i
          join leave_bill b on b.business_id = i.business_id
         where i.workflow_instance_id = @processInstanceId
         limit 1
        """;

    private const string UpdateParameterColumnsSql = """
        update leave_bill b
          join instances i on i.business_id = b.business_id
           set b.days        = @Days,
               b.reason      = @Reason,
               b.ext_params  = @ExtParams,
               b.modify_time = @Now,
               i.`describe`  = @Describe,
               i.modify_time = @Now
         where i.workflow_instance_id = @processInstanceId
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

    // 快照（只读）：显式列出每一列并起别名，别用 select * —— 见 SnapshotAsync 的 remarks

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

    private const string SelectLeaveBillSnapshotSql = """
        select tenant_id         as TenantId,
               business_id       as BusinessId,
               apply_user        as ApplyUser,
               apply_dept_id     as ApplyDeptId,
               business_dept_id  as BusinessDeptId,
               days              as Days,
               reason            as Reason,
               ext_params        as ExtParams,
               create_time       as CreateTime,
               modify_time       as ModifyTime
          from leave_bill
         order by business_id desc
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

    /// <summary>参数表内容：<c>days</c> / <c>reason</c> 读列，其余从 <c>ext_params</c> 的 JSON 里展开。</summary>
    private static async ValueTask<Dictionary<string, string?>> ReadParametersAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string processInstanceId,
        CancellationToken ct)
    {
        var row = await connection.QueryFirstOrDefaultAsync<ParameterRow>(new CommandDefinition(
            SelectParameterColumnsSql,
            new { processInstanceId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? [] : Materialize(row.Days, row.Reason, row.ExtParams);
    }

    /// <summary>
    /// 业务单的参数三件套（<c>days</c> / <c>reason</c> / <c>ext_params</c>）摊成「参数名 → 值」。
    /// </summary>
    /// <remarks>读和写都要用同一套展开规则，否则「读出来再写回去」会悄悄改数据。</remarks>
    private static Dictionary<string, string?> Materialize(short? days, string? reason, string? extParams)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (days is { } value)
        {
            parameters[LeaveParameterNames.Days] = value.ToString(CultureInfo.InvariantCulture);
        }

        if (reason is not null)
        {
            parameters[LeaveParameterNames.Reason] = reason;
        }

        foreach (var (name, item) in ParseExtParams(extParams))
        {
            parameters[name] = item;
        }

        return parameters;
    }

    /// <summary>写参数列：<c>days</c> / <c>reason</c> 进真列，其余并进 <c>ext_params</c>。</summary>
    /// <remarks>
    /// 调用方负责事务。读的时候加 <c>for update</c> 行锁：<c>ext_params</c> 是「读-改-写」，
    /// 不加锁的并发提交会互相覆盖。
    /// </remarks>
    private async ValueTask SaveParameterColumnsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string processInstanceId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct)
    {
        var row = await connection.QueryFirstOrDefaultAsync<ParameterRow>(new CommandDefinition(
            SelectParameterColumnsSql + " for update",
            new { processInstanceId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (row is null)
        {
            _logger.LogWarning(
                "实例 {ProcessInstanceId} 还没有业务登记（instances / leave_bill 无行），"
                + "本次参数 {Keys} 未落库。",
                processInstanceId, string.Join(", ", values.Keys));

            return;
        }

        short? days = row.Days;
        var reason = row.Reason;
        var extended = new Dictionary<string, string?>(ParseExtParams(row.ExtParams), StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            switch (name)
            {
                case LeaveParameterNames.Days:
                    if (TryParseDays(value, out var parsed))
                    {
                        days = parsed;
                    }

                    break;

                case LeaveParameterNames.Reason:
                    reason = value;
                    break;

                default:
                    extended[name] = value;
                    break;
            }
        }

        var extendedJson = SerializeExtParams(extended);

        await connection.ExecuteAsync(new CommandDefinition(
            UpdateParameterColumnsSql,
            new
            {
                Days = days,
                Reason = reason,
                ExtParams = extendedJson,

                // 业务单描述跟着参数一起刷新 —— 它本来就是「拿参数拼出来的一句话」，
                // 只在建单时算一次的话，看列表的人会一直看到「还没填天数」。
                Describe = Describe(row.ApplyUser, Materialize(days, reason, extendedJson)),
                Now = DateTime.UtcNow,
                processInstanceId,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>业务单描述（<c>instances.describe</c>）：拿申请人 + 当前参数拼一句人能看懂的话，超长截断。</summary>
    private static string Describe(string? applyUser, Dictionary<string, string?> parameters)
    {
        var parts = new List<string> { string.IsNullOrWhiteSpace(applyUser) ? "未知申请人" : applyUser };

        if (parameters.ContainsKey(LeaveParameterNames.Days))
        {
            parts.Add($"请假 {LeaveParameterNames.DaysOf(parameters)} 天");
        }

        if (LeaveParameterNames.ReasonOf(parameters) is { Length: > 0 } reason)
        {
            parts.Add(reason);
        }

        var text = string.Join(" · ", parts);

        return text.Length <= DescribeMaxLength ? text : text[..DescribeMaxLength];
    }

    /// <summary>从业务表还原 <see cref="BaseModel"/>（<c>result</c> / <c>comment</c> 来自最后一条流水）。</summary>
    private static BaseModel ToBaseModel(InstanceRow row) => new(
        BusinessId: row.BusinessId.ToString(CultureInfo.InvariantCulture),
        BusinessDeptId: row.BusinessDeptId,
        Result: ApprovalResults.TryParseOptionId(row.LastResult),
        Comment: row.LastComment,
        ApplyUser: row.ApplyEmpId,
        ApplyTime: AsUtc(row.ApplyTime),
        ApplyDeptId: row.ApplyDeptId);

    /// <summary>
    /// 由「最后一次审批结果」推结论：<b>以拒绝收尾就是拒绝，其余都是通过</b>。
    /// </summary>
    /// <remarks>
    /// 表里没有「结论」这一列，结论本来就是「怎么结束的」这件事 ——
    /// 而能让流程走到结束的只有两个服务任务：审批通过、拒绝结束。
    /// </remarks>
    private static string OutcomeOf(string? lastDecision) =>
        // 「Closed」是流程跳转强制办结的结论（deal_history 里那条流水的 deal_result），
        // 不能落进审批取值域 —— 先于审批结果判定。
        string.Equals(lastDecision, WorkflowOutcomes.Closed, StringComparison.Ordinal)
            ? WorkflowOutcomes.Closed
            : ApprovalResults.TryParseOptionId(lastDecision) == ApprovalResult.Refused
                ? WorkflowOutcomes.Refused
                : WorkflowOutcomes.Approved;

    /// <summary><c>leave_bill.days</c> 是 <c>smallint</c>，越界或非数字都不写（免得把已有值弄坏）。</summary>
    private bool TryParseDays(string? value, out short days)
    {
        days = 0;

        if (value is null)
        {
            // 显式置空：0 表示「还没填」在这个 Demo 里说不清，宁可保留原值
            _logger.LogWarning("days 收到 null，保留原值（leave_bill.days 用 NULL 表示未填）。");
            return false;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            _logger.LogWarning("days 的值 '{Value}' 不是整数，忽略。", value);
            return false;
        }

        if (parsed is < 0 or > DaysMaxValue)
        {
            _logger.LogWarning("days 的值 {Value} 超出 smallint 范围（0~{Max}），忽略。", parsed, DaysMaxValue);
            return false;
        }

        days = (short)parsed;
        return true;
    }

    /// <summary>业务 Id 必须是正整数 —— <c>instances.business_id</c> / <c>leave_bill.business_id</c> 都是 bigint。</summary>
    private static long RequireBusinessId(WorkflowInstanceRecord record) =>
        long.TryParse(record.Base.BusinessId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new InvalidOperationException(
                $"业务 Id 必须是正整数（业务表里 business_id 是 bigint），当前为 '{record.Base.BusinessId}'。"
                + "请让调用方传数字业务单号。");

    /// <summary>从 <c>leave_process:1:1</c> 里取出版本段（<c>1</c>）。</summary>
    private static string? ExtractVersion(string processDefinitionId)
    {
        var parts = processDefinitionId.Split(':');

        return parts.Length >= 2 ? parts[1] : null;
    }

    /// <summary>
    /// 连接串 → 「主机[:端口]/库名」这半截。
    /// </summary>
    /// <remarks>
    /// 演示页会把落点显示在界面上，而连接串里带着 <c>Password</c> —— 所以这里<b>只挑三个键</b>拼，
    /// 绝不把整串透出去；认不出来就报「（连接串已配置）」，好看不重要，不外泄才重要。
    /// </remarks>
    private static string DatabaseOf(string connectionString)
    {
        var server = Field(connectionString, "Server") ?? Field(connectionString, "Host") ?? "?";
        var port = Field(connectionString, "Port");
        var database = Field(connectionString, "Database") ?? "?";

        return port is null ? $"{server}/{database}" : $"{server}:{port}/{database}";
    }

    /// <summary>按名取连接串里的一个值（不区分大小写；取不到或为空返回 <see langword="null"/>）。</summary>
    private static string? Field(string connectionString, string name) =>
        connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && string.Equals(pair[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair[1].Trim())
            .FirstOrDefault(value => value.Length > 0);

    private static Dictionary<string, string?> ParseExtParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
                ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }

    private static string? SerializeExtParams(Dictionary<string, string?> extended) =>
        extended.Count == 0 ? null : JsonSerializer.Serialize(extended);

    private static string? Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : string.Join(",", values);

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// 从库里读出来的 <c>datetime</c> 是 <see cref="DateTimeKind.Unspecified"/>（MySQL 不存时区），
    /// 统一按 UTC 解释 —— 写进去的就是 <c>DateTime.UtcNow</c>。
    /// </summary>
    private static DateTime? AsUtc(DateTime? value) =>
        value is { } moment ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : null;

    /// <summary>发一个业务主键：毫秒时间戳 &lt;&lt; 20 | 同毫秒序号。</summary>
    private static long NextId()
    {
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return (milliseconds << 20) | (Interlocked.Increment(ref _sequence) & 0xFFFFF);
    }

    // ───────────────────────── 行映射 ─────────────────────────

    /// <summary><c>instances</c> 的一行 + 最后一次审批的结论（用于还原 <see cref="BaseModel"/>）。</summary>
    private sealed class InstanceRow
    {
        public long ApplyId { get; set; }

        public long BusinessId { get; set; }

        public string? BusinessDeptId { get; set; }

        public string ApplyEmpId { get; set; } = string.Empty;

        public string ApplyDeptId { get; set; } = string.Empty;

        public DateTime ApplyTime { get; set; }

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

        public long WorkflowTypeId { get; set; }

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
        public string? ApplyUser { get; set; }

        public short? Days { get; set; }

        public string? Reason { get; set; }

        public string? ExtParams { get; set; }
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

        /// <summary>来自 <c>instances.status_id</c>（join 出来）—— 判断这条流水是不是「让流程结束的那条」。</summary>
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

    private sealed class LeaveBillRow
    {
        public long TenantId { get; set; }

        public long BusinessId { get; set; }

        public string ApplyUser { get; set; } = string.Empty;

        public string ApplyDeptId { get; set; } = string.Empty;

        public string? BusinessDeptId { get; set; }

        public short? Days { get; set; }

        public string? Reason { get; set; }

        public string? ExtParams { get; set; }

        public DateTime CreateTime { get; set; }

        public DateTime? ModifyTime { get; set; }
    }

    private sealed class InstanceRef
    {
        public long ApplyId { get; set; }

        public long BusinessId { get; set; }
    }
}

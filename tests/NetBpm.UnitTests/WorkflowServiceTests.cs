using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;
using NetBpm.Workflow;

// 这些用例全程跑在内存宿主上、同步完成，进程内取消没有可观测收益，
// 因此不逐处传 TestContext.Current.CancellationToken（避免把调用点淹没在样板里）。
#pragma warning disable xUnit1051

namespace NetBpm.UnitTests;

/// <summary>
/// 审批流程应用层（<see cref="WorkflowService"/>）的四项能力：
/// 发起流程申请、获取流程环节信息、提交流程审批、获取审批历史记录。
/// </summary>
/// <remarks>
/// <para>
/// 这里用<b>真实引擎</b>（内存宿主）+ <b>假数据端口</b>，而不是把应用层单独隔离出来测 ——
/// 因为它的价值恰恰在「怎么把引擎与业务数据串起来」，把引擎换成假货就什么都没测到。
/// </para>
/// <para>钉住的几件事：</para>
/// <list type="bullet">
///   <item><description>发起即可见首个环节的表单契约（选项来自 BPMN，不在代码里）；</description></item>
///   <item><description>提交的表单值按 <c>variable</c> 落成流程变量，流程按网关条件流转；</description></item>
///   <item><description>参数「有值就按 key 更新」，且审批元数据不进参数表；</description></item>
///   <item><description>流程结束的结论由服务任务写、且带参数快照；</description></item>
///   <item><description>审批历史在<b>没有</b> <c>ACT_HI_*</c> 的内存宿主下也能合成出环节序列。</description></item>
/// </list>
/// </remarks>
public sealed class WorkflowServiceTests
{
    private const string ProcessKey = "unit_process";

    /// <summary>
    /// 最小可用的审批流程：申请（days / reason）→ 审批（approvalResult / comment）→
    /// 排他网关（<c>${result == 1}</c> 通过，默认拒绝）→ 两个服务任务收尾。
    /// </summary>
    private const string BpmnXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="unit_process" name="单元测试审批流程" isExecutable="true">
            <startEvent id="start" name="发起" />
            <userTask id="task_apply" name="提交申请" activiti:assignee="${applyUser}">
              <extensionElements>
                <activiti:formProperty id="days" name="请假天数" type="long" required="true" variable="days" />
                <activiti:formProperty id="reason" name="请假事由" type="string" variable="reason" />
              </extensionElements>
            </userTask>
            <userTask id="task_approve" name="组长审批" activiti:candidateGroups="leaders">
              <extensionElements>
                <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                  <activiti:value id="1" name="通过" />
                  <activiti:value id="2" name="拒绝" />
                  <activiti:value id="3" name="驳回修改" />
                </activiti:formProperty>
                <activiti:formProperty id="comment" name="审批说明" type="string" variable="comment" />
              </extensionElements>
            </userTask>
            <exclusiveGateway id="gw_result" name="审批结果" default="flow_refuse" />
            <serviceTask id="task_approve_complete" name="审批通过" activiti:delegateExpression="test.Approve" />
            <serviceTask id="task_refuse" name="拒绝结束" activiti:delegateExpression="test.Refuse" />
            <endEvent id="end_approved" />
            <endEvent id="end_refused" />
            <sequenceFlow id="flow_start_apply" sourceRef="start" targetRef="task_apply" />
            <sequenceFlow id="flow_apply_approve" sourceRef="task_apply" targetRef="task_approve" />
            <sequenceFlow id="flow_approve_gw" sourceRef="task_approve" targetRef="gw_result" />
            <sequenceFlow id="flow_pass" sourceRef="gw_result" targetRef="task_approve_complete" name="通过">
              <conditionExpression xsi:type="tFormalExpression">${result == 1}</conditionExpression>
            </sequenceFlow>
            <sequenceFlow id="flow_refuse" sourceRef="gw_result" targetRef="task_refuse" name="拒绝" />
            <sequenceFlow id="flow_approve_end" sourceRef="task_approve_complete" targetRef="end_approved" />
            <sequenceFlow id="flow_refuse_end" sourceRef="task_refuse" targetRef="end_refused" />
          </process>
        </definitions>
        """;

    // ───────────────────────── 发起流程申请 ─────────────────────────

    [Fact]
    public async Task Start_ShouldReturnFirstTaskWithFormContract()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        var view = await StartAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假");

        Assert.True(view.Running);
        Assert.Equal("BIZ-1", view.BusinessKey);
        Assert.Equal("BIZ-1", view.Base.BusinessId);
        Assert.Equal("alice", view.Base.ApplyUser);
        Assert.Equal("alice", view.Variables["applyUser"]);

        // 环节信息：现在停在哪、要填什么
        Assert.Equal(["task_apply"], view.CurrentActivityIds);

        var task = Assert.Single(view.Tasks);
        Assert.Equal("提交申请", task.Name);
        Assert.Equal("alice", task.Assignee);
        Assert.Equal(["days", "reason"], task.Form.Select(property => property.Id));
        Assert.Equal(["days"], task.Form.Where(property => property.Required).Select(property => property.Id));
        Assert.Equal("days", task.Form[0].Variable);
    }

    [Fact]
    public async Task Start_ShouldPersistInstanceAndParameters()
    {
        using var provider = BuildProvider(out var store);
        await DeployAsync(provider);

        var view = await StartAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假");

        var record = await store.FindInstanceAsync(view.ProcessInstanceId);
        Assert.NotNull(record);
        Assert.Equal("alice", record.Base.ApplyUser);
        Assert.Equal(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["days"] = "2", ["reason"] = "年假" },
            await store.FindParametersAsync(view.ProcessInstanceId));

        // 发起时还没结束：没有结论
        Assert.Null(await store.FindResultAsync(view.ProcessInstanceId));
        Assert.Null(view.Result);

        // 参数同时展开成了扁平流程变量（网关条件要用）
        Assert.Equal("2", view.Variables["days"]);
        Assert.Equal("年假", view.Variables["reason"]);
    }

    [Fact]
    public async Task Start_ShouldRejectParameterNameConflictingWithBaseModel()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        var request = new StartWorkflowRequest(
            "alice",
            ParaDict: Json(("applyUser", "hacker")));

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            async () => await GetWorkflow(provider).StartAsync(ProcessKey, request));

        Assert.Contains("applyUser", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_ShouldRejectBlankApplicantAndUnknownDefinition()
    {
        using var provider = BuildProvider(out _);

        var workflow = GetWorkflow(provider);

        // 连流程定义都还没部署
        await Assert.ThrowsAsync<WorkflowValidationException>(
            async () => await workflow.StartAsync(ProcessKey, new StartWorkflowRequest("alice")));

        await DeployAsync(provider);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            async () => await workflow.StartAsync("not_deployed", new StartWorkflowRequest("alice")));

        var blank = await Assert.ThrowsAsync<WorkflowValidationException>(
            async () => await workflow.StartAsync(ProcessKey, new StartWorkflowRequest("   ")));
        Assert.Contains("applicant", blank.Message, StringComparison.Ordinal);
    }

    // ───────────────────────── 提交流程审批 ─────────────────────────

    [Fact]
    public async Task Submit_ShouldMapFormValuesAndMoveToNextActivity()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        var workflow = GetWorkflow(provider);
        var view = await StartAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假");
        view = await SubmitAsync(workflow, view, ("days", 2), ("reason", "年假"));

        Assert.Equal(["task_approve"], view.CurrentActivityIds);

        // long 表单字段经 formProperty 的 type=long 落成引擎里的长整数
        Assert.Equal(2L, view.Variables["days"]);

        // 审批环节的选项同样来自 BPMN，前端不写死
        var approval = Assert.Single(view.Tasks);
        var field = Assert.Single(approval.Form, property => property.Id == "approvalResult");
        Assert.Equal("result", field.Variable);
        Assert.Equal(
            [("1", "通过"), ("2", "拒绝"), ("3", "驳回修改")],
            field.Options.Select(option => (option.Id, option.Name)));

        Assert.Equal(["leaders"], approval.CandidateGroups);
    }

    [Fact]
    public async Task Submit_ShouldUpdateParametersByKeyAndKeepOthers()
    {
        using var provider = BuildProvider(out var store);
        await DeployAsync(provider);

        var workflow = GetWorkflow(provider);
        var view = await StartAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假");

        // 发起时带一个自定义参数
        view = await SubmitAsync(workflow, view, ("days", 2), ("reason", "年假"));

        // 审批时按 key 更新：city 覆盖、backup 新增、days 原样
        var task = Assert.Single(view.Tasks);
        view = await workflow.SubmitAsync(
            task.TaskId,
            new SubmitWorkflowTaskRequest(
                Values: Json(("approvalResult", "1")),
                ParaDict: Json(("reason", "改为事假"), ("backupUser", "bob")))) ?? throw new InvalidOperationException("提交后应当返回实例视图。");

        var parameters = await store.FindParametersAsync(view.ProcessInstanceId);
        Assert.Equal("改为事假", parameters["reason"]);
        Assert.Equal("bob", parameters["backupUser"]);
        Assert.Equal("2", parameters["days"]);

        // 审批元数据（result / comment）不进参数表 —— 它们归 BaseModel
        Assert.DoesNotContain(BaseModelVariables.Result, parameters.Keys);
        Assert.DoesNotContain(BaseModelVariables.Comment, parameters.Keys);
    }

    [Fact]
    public async Task Submit_ShouldRejectInvalidFormAndLeaveProcessUntouched()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        var workflow = GetWorkflow(provider);
        var view = await StartAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假");
        view = await SubmitAsync(workflow, view, ("days", 2), ("reason", "年假"));

        var task = Assert.Single(view.Tasks);

        // 非法枚举值
        var invalid = await Assert.ThrowsAsync<WorkflowValidationException>(
            async () => await workflow.SubmitAsync(
                task.TaskId, new SubmitWorkflowTaskRequest(Values: Json(("approvalResult", "9")))));
        Assert.Contains(invalid.Errors, error => error.Contains("不是合法选项", StringComparison.Ordinal));

        // 缺必填
        var missing = await Assert.ThrowsAsync<WorkflowValidationException>(
            async () => await workflow.SubmitAsync(task.TaskId, new SubmitWorkflowTaskRequest()));
        Assert.Contains(missing.Errors, error => error.Contains("approvalResult", StringComparison.Ordinal));

        // 未声明字段
        var unknown = await Assert.ThrowsAsync<WorkflowValidationException>(
            async () => await workflow.SubmitAsync(
                task.TaskId,
                new SubmitWorkflowTaskRequest(Values: Json(("approvalResult", "1"), ("hacker", "x")))));
        Assert.Contains(unknown.Errors, error => error.Contains("hacker", StringComparison.Ordinal));

        // 三次失败提交都没推动流程
        var current = await workflow.FindInstanceAsync(view.ProcessInstanceId);
        Assert.NotNull(current);
        Assert.Equal(["task_approve"], current.CurrentActivityIds);
        Assert.Null(current.Result);
    }

    [Fact]
    public async Task Submit_ShouldThrowNotFoundForUnknownTask()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            async () => await GetWorkflow(provider).SubmitAsync("no-such-task", null));
    }

    [Fact]
    public async Task Submit_ShouldRecordApprovalWithDecisionAndNextSteps()
    {
        using var provider = BuildProvider(out var store);
        await DeployAsync(provider);

        var workflow = GetWorkflow(provider);
        var view = await StartAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假");

        // 申请环节：没有审批结论，但记下了「流转到组长审批」
        view = await SubmitAsync(workflow, view, ("days", 2), ("reason", "年假"));

        var applied = Assert.Single(store.Approvals);
        Assert.Equal("task_apply", applied.ActivityId);
        Assert.Equal("alice", applied.Assignee);
        Assert.Null(applied.Decision);
        Assert.Equal(["task_approve"], applied.NextSteps.Select(step => step.ActivityId));
        Assert.Equal("年假", applied.Values[LeaveKeys.Reason]);

        // 审批环节：结论 + 说明，且让流程结束
        var task = Assert.Single(view.Tasks);
        view = await workflow.SubmitAsync(
            task.TaskId,
            new SubmitWorkflowTaskRequest(
                Values: Json(("approvalResult", "1"), ("comment", "同意")),
                Operator: "leader1")) ?? throw new InvalidOperationException("提交后应当返回实例视图。");

        var approved = store.Approvals[1];
        Assert.Equal("task_approve", approved.ActivityId);

        // 请求里的办理人优先 —— 按候选组审批的任务在认领前 assignee 是空的
        Assert.Equal("leader1", approved.Assignee);
        Assert.Equal("1", approved.Decision);
        Assert.Equal("通过", approved.DecisionName);
        Assert.Equal("同意", approved.Values[BaseModelVariables.Comment]);
        Assert.Empty(approved.NextSteps);
        Assert.Equal(WorkflowOutcomes.Approved, approved.Outcome);

        // 审批结论里的说明来自表单，落在 BaseModel 上
        Assert.NotNull(view.Result);
        Assert.Equal("同意", view.Result.Base.Comment);
        Assert.Equal(ApprovalResult.Approved, view.Result.Base.Result);
    }

    // ───────────────────────── 流程结束的结论 ─────────────────────────

    [Fact]
    public async Task Complete_ShouldPersistOutcomeWithParameterSnapshot()
    {
        using var provider = BuildProvider(out var store);
        await DeployAsync(provider);

        var view = await FinishAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假", approvalResult: "1");

        var result = await store.FindResultAsync(view.ProcessInstanceId);
        Assert.NotNull(result);
        Assert.Equal(WorkflowOutcomes.Approved, result.Outcome);
        Assert.Equal("task_approve_complete", result.ActivityId);
        Assert.Equal("审批通过", result.ActivityName);
        Assert.Equal(ApprovalResult.Approved, result.Base.Result);

        // 参数快照：流程结束后运行态变量就没了，天数只能在写结论时记下来
        Assert.Equal("2", result.Parameters[LeaveKeys.Days]);
        Assert.Equal("年假", result.Parameters[LeaveKeys.Reason]);
    }

    [Fact]
    public async Task Complete_ShouldBeSymmetricForRefusedPath()
    {
        using var provider = BuildProvider(out var store);
        await DeployAsync(provider);

        var view = await FinishAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假", approvalResult: "2");

        Assert.False(view.Running);
        Assert.NotNull(view.Result);
        Assert.Equal(WorkflowOutcomes.Refused, view.Result.Outcome);
        Assert.Equal("task_refuse", view.Result.ActivityId);
        Assert.Equal(ApprovalResult.Refused, view.Result.Base.Result);
        Assert.NotNull(await store.FindResultAsync(view.ProcessInstanceId));
    }

    // ───────────────────────── 获取流程环节信息 ─────────────────────────

    [Fact]
    public async Task FindInstance_ShouldReturnNullForUnknownInstance()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        Assert.Null(await GetWorkflow(provider).FindInstanceAsync("no-such-instance"));
    }

    [Fact]
    public async Task FindInstance_ShouldFallBackToBusinessDataAfterFinish()
    {
        using var provider = BuildProvider(out var store);
        await DeployAsync(provider);

        var view = await FinishAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假", approvalResult: "1");

        var finished = await GetWorkflow(provider).FindInstanceAsync(view.ProcessInstanceId);

        Assert.NotNull(finished);
        Assert.False(finished.Running);
        Assert.Empty(finished.Tasks);

        // 运行态行已删除 ⇒ 变量读不到了，业务数据兜住
        Assert.Empty(finished.Variables);
        Assert.Equal("alice", finished.Base.ApplyUser);
        Assert.Equal("BIZ-1", finished.BusinessKey);
        Assert.NotNull(finished.StartedAt);

        // 参数表是业务侧的留痕，流程结束后照样查得到
        Assert.Contains(LeaveKeys.Days, finished.Parameters.Keys);
        Assert.Equal("2", finished.Parameters[LeaveKeys.Days]);
        Assert.NotNull(await store.FindInstanceAsync(view.ProcessInstanceId));
    }

    // ───────────────────────── 获取审批历史记录 ─────────────────────────

    [Fact]
    public async Task FindHistory_ShouldSynthesizeStepsFromApprovalsAndActiveTask()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        var workflow = GetWorkflow(provider);
        var view = await StartAsync(provider, "alice", "BIZ-1", days: 5, reason: "长途休假");
        view = await SubmitAsync(workflow, view, ("days", 5), ("reason", "长途休假"));

        var history = await workflow.FindHistoryAsync(view.ProcessInstanceId);

        Assert.NotNull(history);

        // 内存宿主没有 ACT_HI_ACTINST，环节序列由「业务审批记录 + 当前待办」合成
        Assert.Equal(WorkflowStepSource.BusinessApprovals, history.StepsSource);
        Assert.True(history.Running);
        Assert.Null(history.EndedAt);
        Assert.Equal("BIZ-1", history.BusinessKey);
        Assert.Equal(["task_apply", "task_approve"], history.Steps.Select(step => step.ActivityId));

        // 前一个环节已结束，最后一个仍在进行
        Assert.False(history.Steps[0].IsActive);
        Assert.True(history.Steps[1].IsActive);
        Assert.Equal("alice", history.Steps[0].Assignee);
        Assert.NotNull(history.Steps[0].StartedAt);
        Assert.NotNull(history.Steps[0].EndedAt);

        var applied = Assert.Single(history.Approvals);
        Assert.Equal("5", applied.Values[LeaveKeys.Days]);
    }

    [Fact]
    public async Task FindHistory_ShouldCarryDecisionAndEndTimeAfterFinish()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        var view = await FinishAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假", approvalResult: "1");
        var history = await GetWorkflow(provider).FindHistoryAsync(view.ProcessInstanceId);

        Assert.NotNull(history);
        Assert.False(history.Running);
        Assert.NotNull(history.EndedAt);
        Assert.NotNull(history.DurationInMilliseconds);
        Assert.Equal(WorkflowOutcomes.Approved, history.Result?.Outcome);

        Assert.Equal(2, history.Steps.Count);

        // 审批环节带上结论（名字取自 BPMN 的选项名，而不是枚举）
        var approval = history.Steps[1];
        Assert.Equal("task_approve", approval.ActivityId);
        Assert.Equal("1", approval.Decision);
        Assert.Equal("通过", approval.DecisionName);
        Assert.False(approval.IsActive);
    }

    [Fact]
    public async Task FindHistory_ShouldReturnNullForUnknownInstance()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        Assert.Null(await GetWorkflow(provider).FindHistoryAsync("no-such-instance"));
    }

    // ───────────────────────── 待办与列表 ─────────────────────────

    [Fact]
    public async Task FindTasks_ShouldRejectQueryWithoutCriteria()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            async () => await GetWorkflow(provider).FindTasksAsync(new TaskQuery()));
    }

    [Fact]
    public async Task FindTasks_ShouldReturnTaskWithFormForCandidateUser()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        var workflow = GetWorkflow(provider);
        var view = await StartAsync(provider, "alice", "BIZ-1", days: 2, reason: "年假");
        await SubmitAsync(workflow, view, ("days", 2), ("reason", "年假"));

        // 候选组 leaders 上的任务：按候选人查得到（演示实现里的 leader1 属于 leaders 组）
        var tasks = await workflow.FindTasksAsync(new TaskQuery(CandidateUser: "leader1"));

        Assert.Empty(tasks);

        var own = await workflow.FindTasksAsync(new TaskQuery(ProcessInstanceId: view.ProcessInstanceId));
        Assert.Equal("task_approve", Assert.Single(own).DefinitionKey);
    }

    [Fact]
    public async Task FindInstances_ShouldSummarizeStatusFromOutcome()
    {
        using var provider = BuildProvider(out _);
        await DeployAsync(provider);

        var workflow = GetWorkflow(provider);

        var running = await StartAsync(provider, "alice", "BIZ-RUN", days: 2, reason: "年假");
        await FinishAsync(provider, "bob", "BIZ-DONE", days: 2, reason: "年假", approvalResult: "1");

        var summaries = await workflow.FindInstancesAsync();

        Assert.Equal(2, summaries.Count);
        Assert.Equal(
            WorkflowOutcomes.Running,
            Assert.Single(summaries, summary => summary.ProcessInstanceId == running.ProcessInstanceId).Status);
        Assert.Equal(
            WorkflowOutcomes.Approved,
            Assert.Single(summaries, summary => summary.Base.BusinessId == "BIZ-DONE").Status);
    }

    // ───────────────────────── 测试基建 ─────────────────────────

    /// <summary>只在本文件里用到的参数名（Demo 的那份在 LeaveParameterNames）。</summary>
    private static class LeaveKeys
    {
        internal const string Days = "days";
        internal const string Reason = "reason";
    }

    private static ServiceProvider BuildProvider(out FakeWorkflowDataStore store)
    {
        var dataStore = new FakeWorkflowDataStore();
        store = dataStore;

        var services = new ServiceCollection();

        // 引擎（内存宿主）+ 假日志（引擎的拦截器要一个 ILogger<T>）
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddNetBpm(options => options.UseInMemoryExecutionTree = true);

        // 应用层：数据端口换成内存实现
        services.AddSingleton<IWorkflowDataStore>(dataStore);
        services.AddSingleton<IWorkflowService, WorkflowService>();

        // 两个服务任务委托：与 BPMN 里的 delegateExpression 一一对应
        services.AddKeyedSingleton<object>(
            "test.Approve",
            (provider, _) => new ApproveDelegate(provider.GetRequiredService<IWorkflowService>()));
        services.AddKeyedSingleton<object>(
            "test.Refuse",
            (provider, _) => new RefuseDelegate(provider.GetRequiredService<IWorkflowService>()));

        return services.BuildServiceProvider();
    }

    private static IWorkflowService GetWorkflow(IServiceProvider provider) =>
        provider.GetRequiredService<IWorkflowService>();

    private static async ValueTask<string> DeployAsync(IServiceProvider provider)
    {
        var engine = provider.GetRequiredService<IProcessEngine>();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(BpmnXml));

        var definition = await engine
            .DeployAsync(stream, "unit-process.bpmn20.xml")
            .ConfigureAwait(false);

        return definition.Id;
    }

    private static async ValueTask<WorkflowInstanceView> StartAsync(
        IServiceProvider provider,
        string applicant,
        string businessKey,
        int days,
        string reason) =>
        await GetWorkflow(provider)
            .StartAsync(
                ProcessKey,
                new StartWorkflowRequest(
                    applicant,
                    businessKey,
                    BusinessDeptId: "D-SALES",
                    ApplyDeptId: "D-SALES",
                    ParaDict: Json((LeaveKeys.Days, days), (LeaveKeys.Reason, reason))))
            .ConfigureAwait(false);

    /// <summary>提交指定环节（用表单字段名 → 值）。</summary>
    private static async ValueTask<WorkflowInstanceView> SubmitAsync(
        IWorkflowService workflow,
        WorkflowInstanceView view,
        params (string Name, object? Value)[] values)
    {
        var task = Assert.Single(view.Tasks);

        return await workflow.SubmitAsync(task.TaskId, new SubmitWorkflowTaskRequest(Values: Json(values)))
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("提交后应当返回实例视图。");
    }

    /// <summary>走完整条链：发起 → 提交申请 → 审批（按 approvalResult 决定走哪条线）。</summary>
    private static async ValueTask<WorkflowInstanceView> FinishAsync(
        IServiceProvider provider,
        string applicant,
        string businessKey,
        int days,
        string reason,
        string approvalResult)
    {
        var workflow = GetWorkflow(provider);

        var view = await StartAsync(provider, applicant, businessKey, days, reason).ConfigureAwait(false);
        view = await SubmitAsync(workflow, view, (LeaveKeys.Days, days), (LeaveKeys.Reason, reason)).ConfigureAwait(false);
        view = await SubmitAsync(
            workflow,
            view,
            ("approvalResult", approvalResult),
            ("comment", "同意，注意交接")).ConfigureAwait(false);

        return view;
    }

    private static Dictionary<string, JsonElement> Json(params (string Name, object? Value)[] values)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            map[name] = JsonSerializer.SerializeToElement(value);
        }

        return map;
    }

    /// <summary>服务任务 <c>test.Approve</c>：把「流程审批通过」落成业务结论。</summary>
    [ServiceTaskName("test.Approve")]
    private sealed class ApproveDelegate : IServiceTaskDelegate
    {
        private readonly IWorkflowService _workflow;

        public ApproveDelegate(IWorkflowService workflow) => _workflow = workflow;

        public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
            _workflow.CompleteAsync(execution, WorkflowOutcomes.Approved, ct);
    }

    /// <summary>服务任务 <c>test.Refuse</c>：把「流程被拒绝」落成业务结论。</summary>
    [ServiceTaskName("test.Refuse")]
    private sealed class RefuseDelegate : IServiceTaskDelegate
    {
        private readonly IWorkflowService _workflow;

        public RefuseDelegate(IWorkflowService workflow) => _workflow = workflow;

        public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
            _workflow.CompleteAsync(execution, WorkflowOutcomes.Refused, ct);
    }

    /// <summary>内存版业务数据端口（Demo 那份落 JSON，这份只放在字典里）。</summary>
    private sealed class FakeWorkflowDataStore : IWorkflowDataStore
    {
        private readonly Dictionary<string, WorkflowInstanceRecord> _instances = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkflowOutcomeRecord> _results = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string?>> _parameters = new(StringComparer.Ordinal);
        private readonly List<WorkflowApprovalRecord> _approvals = [];

        /// <summary>已写入的审批记录（按提交先后）。</summary>
        internal List<WorkflowApprovalRecord> Approvals => _approvals;

        public ValueTask<WorkflowInstanceRecord?> FindInstanceAsync(
            string processInstanceId,
            CancellationToken ct = default) =>
            ValueTask.FromResult(_instances.TryGetValue(processInstanceId, out var record) ? record : null);

        public ValueTask<IReadOnlyList<WorkflowInstanceRecord>> FindInstancesAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<WorkflowInstanceRecord>>(
                [.. _instances.Values.OrderByDescending(record => record.Base.ApplyTime)]);

        public ValueTask SaveInstanceAsync(WorkflowInstanceRecord record, CancellationToken ct = default)
        {
            _instances[record.ProcessInstanceId] = record;

            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkflowOutcomeRecord?> FindResultAsync(
            string processInstanceId,
            CancellationToken ct = default) =>
            ValueTask.FromResult(_results.TryGetValue(processInstanceId, out var record) ? record : null);

        public ValueTask SaveResultAsync(WorkflowOutcomeRecord record, CancellationToken ct = default)
        {
            _results[record.ProcessInstanceId] = record;

            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyDictionary<string, string?>> FindParametersAsync(
            string processInstanceId,
            CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyDictionary<string, string?>>(
                new Dictionary<string, string?>(
                    _parameters.TryGetValue(processInstanceId, out var map)
                        ? map
                        : new Dictionary<string, string?>(StringComparer.Ordinal),
                    StringComparer.Ordinal));

        public ValueTask SaveParametersAsync(
            string processInstanceId,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken ct = default)
        {
            if (values.Count == 0)
            {
                return ValueTask.CompletedTask;
            }

            if (!_parameters.TryGetValue(processInstanceId, out var map))
            {
                map = new Dictionary<string, string?>(StringComparer.Ordinal);
                _parameters[processInstanceId] = map;
            }

            foreach (var (name, value) in values)
            {
                map[name] = value;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<WorkflowApprovalRecord>> FindApprovalsAsync(
            string processInstanceId,
            CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<WorkflowApprovalRecord>>(
                [.. _approvals.Where(record =>
                    string.Equals(record.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))]);

        public ValueTask SaveApprovalAsync(WorkflowApprovalRecord record, CancellationToken ct = default)
        {
            _approvals.Add(record);

            return ValueTask.CompletedTask;
        }
    }
}

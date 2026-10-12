using Dapper;
using NetBpm.Engine;
using NetBpm.Identity;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P7 的库级验收：用户任务在真实 MySQL 上产生的 <c>ACT_RU_TASK</c> 与
/// <c>ACT_RU_IDENTITYLINK</c> 行必须与 Activiti 的语义一致，且完成后被清理干净。
/// </summary>
[Collection("MySql")]
public sealed class MySqlTaskPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UserTask_ShouldPersistTaskRowAndIdentityLinks()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(UserTaskProcess(), Ct);

        // 任务行
        using var session = harness.SessionFactory.Create();

        var taskRows = session.Connection
            .Query("select " + TaskColumnsAll + " from ACT_RU_TASK")
            .ToList();

        Assert.Single(taskRows);

        var taskRow = RowOf(taskRows[0]);

        Assert.Equal("approve", TextOf(taskRow, "TASK_DEF_KEY_"));
        Assert.Equal("审批", TextOf(taskRow, "NAME_"));           // name 走表达式求值
        Assert.Equal("kermit", TextOf(taskRow, "ASSIGNEE_"));
        Assert.Equal("approveForm", TextOf(taskRow, "FORM_KEY_"));
        Assert.Equal(80, NumberOf(taskRow, "PRIORITY_"));
        Assert.Equal("BIZ-DB-001", TextOf(taskRow, "BUSINESS_KEY_"));
        Assert.Equal(processInstanceId, TextOf(taskRow, "PROC_INST_ID_"));
        // INSERT 写死 REV_ = 1；随后 handleAssignments 把 assignee 从 null 改成 kermit，
        // 走的是 changeTaskAssigneeNoEvents → update，所以最终 REV_ = 2。
        // ⚠️ 这是对拍时必须对齐的细节：带 assignee 的用户任务建出来就是 REV_ = 2，不是 1。
        Assert.Equal(2, NumberOf(taskRow, "REV_"));
        Assert.Equal(1, NumberOf(taskRow, "SUSPENSION_STATE_"));
        Assert.NotNull(TextOf(taskRow, "EXECUTION_ID_"));
        Assert.NotNull(TimeOf(taskRow, "CREATE_TIME_"));

        // 身份关系：任务级 4 条 'candidate' + 流程实例级 3 条 'participant'
        var links = session.Connection
            .Query("select TYPE_, USER_ID_, GROUP_ID_, TASK_ID_, PROC_INST_ID_ from ACT_RU_IDENTITYLINK")
            .Select(RowOf)
            .ToList();

        Assert.Equal(7, links.Count);

        var taskLevel = links.Where(link => TextOf(link, "TASK_ID_") is not null).ToList();
        Assert.Equal(4, taskLevel.Count);
        Assert.All(taskLevel, link => Assert.Equal(IdentityLinkType.Candidate, TextOf(link, "TYPE_")));
        Assert.Contains(taskLevel, link => TextOf(link, "USER_ID_") == "alice");
        Assert.Contains(taskLevel, link => TextOf(link, "USER_ID_") == "bob");
        Assert.Contains(taskLevel, link => TextOf(link, "GROUP_ID_") == "managers");
        Assert.Contains(taskLevel, link => TextOf(link, "GROUP_ID_") == "hr");

        var instanceLevel = links.Where(link => TextOf(link, "TASK_ID_") is null).ToList();
        Assert.Equal(3, instanceLevel.Count);
        Assert.All(instanceLevel, link =>
        {
            Assert.Equal(IdentityLinkType.Participant, TextOf(link, "TYPE_"));
            Assert.Equal(processInstanceId, TextOf(link, "PROC_INST_ID_"));
        });
        Assert.Contains(instanceLevel, link => TextOf(link, "USER_ID_") == "kermit");

        // 执行树：根 + 等任务的分支
        Assert.Equal(2, harness.CountRows("ACT_RU_EXECUTION"));
    }

    [Fact]
    public async Task CompleteTask_ShouldCleanUpTaskLinksAndEndProcessInstance()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        await harness.DeployAndStartAsync(UserTaskProcess(), Ct);

        var tasks = await harness.TaskManager.FindTasksByAssigneeAsync("kermit", Ct);
        var task = Assert.Single(tasks);

        await harness.CompleteTaskAsync(task.Id, ct: Ct);

        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));
        Assert.Equal(0, harness.CountRows("ACT_RU_IDENTITYLINK"));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));

        // 部署数据必须保留（流程定义不能随实例结束而消失）
        Assert.Equal(1, harness.CountRows("ACT_RE_PROCDEF"));
        Assert.Equal(1, harness.CountRows("ACT_RE_DEPLOYMENT"));
    }

    [Fact]
    public async Task ClaimTask_ShouldPersistAssigneeAndClaimTime()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        await harness.DeployAndStartAsync(UserTaskProcess(assignee: null), Ct);

        var tasks = await harness.TaskManager.FindTasksByCandidateUserAsync("alice", [], Ct);
        var task = Assert.Single(tasks);
        Assert.Null(task.Assignee);

        await harness.ClaimTaskAsync(task.Id, "alice", Ct);

        using var session = harness.SessionFactory.Create();
        var row = RowOf(session.Connection.QuerySingle(
            "select REV_, ASSIGNEE_, CLAIM_TIME_ from ACT_RU_TASK where ID_ = @taskId",
            new { taskId = task.Id }));

        Assert.Equal("alice", TextOf(row, "ASSIGNEE_"));
        Assert.NotNull(TimeOf(row, "CLAIM_TIME_"));
        Assert.Equal(2, NumberOf(row, "REV_"));   // 认领写了一次 ⇒ REV_ 从 1 涨到 2

        // 取消认领
        await harness.ClaimTaskAsync(task.Id, null, Ct);

        var unclaimed = RowOf(session.Connection.QuerySingle(
            "select REV_, ASSIGNEE_, CLAIM_TIME_ from ACT_RU_TASK where ID_ = @taskId",
            new { taskId = task.Id }));

        Assert.Null(TextOf(unclaimed, "ASSIGNEE_"));
        Assert.Null(TimeOf(unclaimed, "CLAIM_TIME_"));
        Assert.Equal(3, NumberOf(unclaimed, "REV_"));
    }

    /// <summary>带候选关系的用户任务流程。</summary>
    private static string UserTaskProcess(string? assignee = "kermit") => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="user-task-db" name="用户任务（库级验证）" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="审批"{{(assignee is null ? string.Empty : $" activiti:assignee=\"{assignee}\"")}} activiti:candidateUsers="alice,bob" activiti:candidateGroups="managers, hr" activiti:formKey="approveForm" activiti:priority="80" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    private const string TaskColumnsAll =
        "ID_, REV_, NAME_, BUSINESS_KEY_, PRIORITY_, CREATE_TIME_, ASSIGNEE_, EXECUTION_ID_, PROC_INST_ID_, " +
        "TASK_DEF_KEY_, SUSPENSION_STATE_, FORM_KEY_, CLAIM_TIME_";

    private static Dictionary<string, object?> RowOf(object row) =>
        ((IDictionary<string, object>)row).ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value,
            StringComparer.Ordinal);

    private static string? TextOf(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value as string : null;

    private static int NumberOf(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

    private static DateTime? TimeOf(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is System.DateTime time ? time : null;
}

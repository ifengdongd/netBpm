using NetBpm.Identity;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P5 后半段的库级验收：<c>ACT_RU_VARIABLE</c>（变量跨命令读回）与 5 张 <c>ACT_HI_*</c> 表的行内容。
/// <para>
/// 这里最重要的是第一条用例：变量必须在<b>上一个命令提交后</b>还能被下一个命令读到 ——
/// 这正是 P7 引入等待型节点后暴露出来的架构缺口（变量只活在进程内时，完成任务时读不到流程变量）。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class MySqlVariableAndHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task VariableSetAtStart_ShouldBeReadByGatewayInLaterCommand()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            ApprovalProcess(),
            new Dictionary<string, object?> { ["approved"] = true },
            Ct);

        // 命令 1 提交后：变量行已落库，且类型/值列与 Activiti 的映射一致
        using (var session = harness.SessionFactory.Create())
        {
            var variables = RowValues.Query(session, "select ID_, REV_, TYPE_, NAME_, PROC_INST_ID_, EXECUTION_ID_, TASK_ID_, LONG_, TEXT_ from ACT_RU_VARIABLE");

            var row = Assert.Single(variables);

            Assert.Equal("approved", RowValues.Text(row, "NAME_"));
            Assert.Equal("boolean", RowValues.Text(row, "TYPE_"));
            Assert.Equal(1L, RowValues.Long(row, "LONG_"));
            Assert.Equal(1, RowValues.Int(row, "REV_"));

            // 变量由「实际持有它的作用域」持有 —— setVariable 上溯到流程实例执行
            Assert.Equal(processInstanceId, RowValues.Text(row, "EXECUTION_ID_"));
            Assert.Equal(processInstanceId, RowValues.Text(row, "PROC_INST_ID_"));
            Assert.True(RowValues.IsNull(row, "TASK_ID_"));
        }

        // 命令 2（完成任务）：网关条件必须能读到命令 1 写入的变量
        var task = Assert.Single(await harness.TaskManager.FindTasksByAssigneeAsync("kermit", Ct));
        await harness.CompleteTaskAsync(task.Id, ct: Ct);

        using (var session = harness.SessionFactory.Create())
        {
            var activities = RowValues.Query(session, "select ACT_ID_ from ACT_HI_ACTINST");
            var activityIds = RowValues.Column(activities, "ACT_ID_");

            Assert.Contains("approvedPath", activityIds);
            Assert.DoesNotContain("rejectedPath", activityIds);

            // 实例结束 ⇒ 运行时变量清空
            Assert.Empty(RowValues.Query(session, "select ID_ from ACT_RU_VARIABLE"));
        }
    }

    [Fact]
    public async Task Documentation_ShouldLandInDefinitionAndHistoricTask()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        await harness.DeployAndStartAsync(DocumentedProcess(), null, Ct);

        using var session = harness.SessionFactory.Create();

        // ① 流程级 <documentation> → ACT_RE_PROCDEF.DESCRIPTION_
        var definitions = RowValues.Query(session, "select KEY_, DESCRIPTION_ from ACT_RE_PROCDEF");

        Assert.Equal("流程说明", RowValues.Text(Assert.Single(definitions), "DESCRIPTION_"));

        // ② userTask 级 <documentation> → ACT_HI_TASKINST.DESCRIPTION_
        var tasks = RowValues.Query(session, "select TASK_DEF_KEY_, DESCRIPTION_ from ACT_HI_TASKINST");

        Assert.Equal("审批环节说明", RowValues.Text(Assert.Single(tasks), "DESCRIPTION_"));

        // ③ 运行任务行的 DESCRIPTION_ 必须仍是 null：Activiti 的 ACT_RU_TASK 这一列也是 null，
        //    文档只落历史行 —— 混用会凭空造出对拍差异
        var running = RowValues.Query(session, "select DESCRIPTION_ from ACT_RU_TASK");

        Assert.True(RowValues.IsNull(Assert.Single(running), "DESCRIPTION_"));
    }

    [Fact]
    public async Task Variables_ShouldMapClrTypesToActivitiColumns()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var dueDate = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await harness.DeployAndStartAsync(
            ApprovalProcess(),
            new Dictionary<string, object?>
            {
                ["text"] = "hello",
                ["number"] = 42,
                ["big"] = 9_000_000_000L,
                ["ratio"] = 1.5d,
                ["flag"] = true,
                ["moment"] = dueDate,
                ["nothing"] = null,
            },
            Ct);

        using var session = harness.SessionFactory.Create();

        var rows = RowValues.Query(
            session,
            "select NAME_, TYPE_, LONG_, DOUBLE_, TEXT_ from ACT_RU_VARIABLE order by NAME_");

        var byName = rows.ToDictionary(row => RowValues.Text(row, "NAME_")!, StringComparer.Ordinal);

        // 列布局逐条对应 VariableType 实现（见 VariableTypeMapper 的注释）
        Assert.Equal("string", RowValues.Text(byName["text"], "TYPE_"));
        Assert.Equal("hello", RowValues.Text(byName["text"], "TEXT_"));
        Assert.True(RowValues.IsNull(byName["text"], "LONG_"));

        Assert.Equal("integer", RowValues.Text(byName["number"], "TYPE_"));
        Assert.Equal(42L, RowValues.Long(byName["number"], "LONG_"));
        Assert.Equal("42", RowValues.Text(byName["number"], "TEXT_"));   // 数字也会写字符串副本

        Assert.Equal("long", RowValues.Text(byName["big"], "TYPE_"));
        Assert.Equal(9_000_000_000L, RowValues.Long(byName["big"], "LONG_"));

        Assert.Equal("double", RowValues.Text(byName["ratio"], "TYPE_"));
        Assert.Equal(1.5d, RowValues.Double(byName["ratio"], "DOUBLE_"));
        Assert.True(RowValues.IsNull(byName["ratio"], "TEXT_"));         // double 不写 TEXT_

        Assert.Equal("boolean", RowValues.Text(byName["flag"], "TYPE_"));
        Assert.Equal(1L, RowValues.Long(byName["flag"], "LONG_"));

        Assert.Equal("date", RowValues.Text(byName["moment"], "TYPE_"));
        Assert.Equal(new DateTimeOffset(dueDate).ToUnixTimeMilliseconds(), RowValues.Long(byName["moment"], "LONG_"));

        Assert.Equal("null", RowValues.Text(byName["nothing"], "TYPE_"));
        Assert.True(RowValues.IsNull(byName["nothing"], "LONG_"));
        Assert.True(RowValues.IsNull(byName["nothing"], "TEXT_"));
    }

    [Fact]
    public async Task VariableUpdate_ShouldKeepHistoricRevisionAtZero()
    {
        // 🔑 2026-10-12 真值比对的修正（§15.10 R3）：历史变量行的 REV_ 是 **Activiti 的
        // copyAndInsert 复制的运行时内存值** —— 创建那一刻是 0（DB 的 REV_=1 是 INSERT 写死的），
        // 且 copyVariableValue 更新时不碰 revision ⇒ 历史行 REV_ 恒为 0，与运行行无关。
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        await harness.DeployAndStartAsync(
            ApprovalProcess(),
            new Dictionary<string, object?> { ["approved"] = false },
            Ct);

        var task = Assert.Single(await harness.TaskManager.FindTasksByAssigneeAsync("kermit", Ct));

        // 同名变量再次写入：走 UPDATE 而非新增
        await harness.CompleteTaskAsync(
            task.Id,
            new Dictionary<string, object?> { ["approved"] = true },
            Ct);

        using var session = harness.SessionFactory.Create();

        var rows = RowValues.Query(
            session,
            "select ID_, NAME_, REV_, VAR_TYPE_, LONG_, CREATE_TIME_, LAST_UPDATED_TIME_ from ACT_HI_VARINST");

        var row = Assert.Single(rows);

        Assert.Equal("approved", RowValues.Text(row, "NAME_"));
        Assert.Equal("boolean", RowValues.Text(row, "VAR_TYPE_"));
        Assert.Equal(1L, RowValues.Long(row, "LONG_"));
        Assert.Equal(0, RowValues.Int(row, "REV_"));   // 创建即 0，更新也不变（对应 copyVariableValue 不碰 revision）
        Assert.NotNull(RowValues.Time(row, "CREATE_TIME_"));
        Assert.NotNull(RowValues.Time(row, "LAST_UPDATED_TIME_"));
    }

    [Fact]
    public async Task History_ShouldRecordAllFiveTables()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            ApprovalProcess(),
            new Dictionary<string, object?> { ["approved"] = false },
            Ct);

        var task = Assert.Single(await harness.TaskManager.FindTasksByAssigneeAsync("kermit", Ct));
        await harness.CompleteTaskAsync(task.Id, ct: Ct);

        using var session = harness.SessionFactory.Create();

        AssertHistoricProcessInstance(session, processInstanceId);
        AssertHistoricActivityInstances(session, task);
        AssertHistoricTaskInstance(session, task);
        AssertHistoricVariableInstances(session, processInstanceId);
        AssertHistoricIdentityLinks(session, processInstanceId);

        // 历史活动读取（流程图着色数据源，§6.2 第 5 项）
        var activities = await harness.HistoryManager.FindActivityInstancesAsync(processInstanceId, Ct);

        Assert.Equal(
            ["start", "approve", "gw", "rejectedPath", "end"],
            activities.Select(activity => activity.ActivityId));
        Assert.All(activities, activity => Assert.NotNull(activity.EndTime));
        Assert.Equal("userTask", activities.Single(a => a.ActivityId == "approve").ActivityType);

        // 运行时表全部清空，历史表保留
        Assert.Empty(RowValues.Query(session, "select ID_ from ACT_RU_EXECUTION"));
        Assert.Empty(RowValues.Query(session, "select ID_ from ACT_RU_TASK"));
        Assert.Empty(RowValues.Query(session, "select ID_ from ACT_RU_VARIABLE"));
        Assert.Empty(RowValues.Query(session, "select ID_ from ACT_RU_IDENTITYLINK"));
    }

    private static void AssertHistoricProcessInstance(NetBpm.Persistence.Database.IDbSession session, string processInstanceId)
    {
        var rows = RowValues.Query(
            session,
            "select ID_, PROC_INST_ID_, PROC_DEF_ID_, START_TIME_, END_TIME_, DURATION_, START_USER_ID_, "
            + "START_ACT_ID_, END_ACT_ID_, DELETE_REASON_, TENANT_ID_ from ACT_HI_PROCINST");

        var row = Assert.Single(rows);

        // 🔑 ID_ == PROC_INST_ID_ == 流程实例 id（不是新生成 id）
        Assert.Equal(processInstanceId, RowValues.Text(row, "ID_"));
        Assert.Equal(processInstanceId, RowValues.Text(row, "PROC_INST_ID_"));
        Assert.Equal("start", RowValues.Text(row, "START_ACT_ID_"));
        Assert.Equal("end", RowValues.Text(row, "END_ACT_ID_"));
        Assert.Equal("tester", RowValues.Text(row, "START_USER_ID_"));
        Assert.Equal(string.Empty, RowValues.Text(row, "TENANT_ID_"));
        Assert.True(RowValues.IsNull(row, "DELETE_REASON_"));   // 正常结束不带删除原因
        Assert.NotNull(RowValues.Time(row, "START_TIME_"));
        Assert.NotNull(RowValues.Time(row, "END_TIME_"));
        Assert.NotNull(RowValues.Long(row, "DURATION_"));
    }

    private static void AssertHistoricActivityInstances(
        NetBpm.Persistence.Database.IDbSession session,
        NetBpm.Engine.Tasks.TaskEntity task)
    {
        var rows = RowValues.Query(
            session,
            "select ID_, EXECUTION_ID_, ACT_ID_, TASK_ID_, ACT_NAME_, ACT_TYPE_, ASSIGNEE_, START_TIME_, END_TIME_ "
            + "from ACT_HI_ACTINST order by START_TIME_, ID_");

        var byActivity = rows.ToDictionary(row => RowValues.Text(row, "ACT_ID_")!, StringComparer.Ordinal);

        // 走过的节点各一行：start → approve（用户任务）→ gw（排他网关）→ rejectedPath → end
        Assert.Equal(
            ["start", "approve", "gw", "rejectedPath", "end"],
            RowValues.Column(rows, "ACT_ID_").Where(id => id is not null));

        // ACT_TYPE_ = 元素类名首字母小写（不是 getProperty("type")）
        Assert.Equal("startEvent", RowValues.Text(byActivity["start"], "ACT_TYPE_"));
        Assert.Equal("userTask", RowValues.Text(byActivity["approve"], "ACT_TYPE_"));
        Assert.Equal("exclusiveGateway", RowValues.Text(byActivity["gw"], "ACT_TYPE_"));
        Assert.Equal("manualTask", RowValues.Text(byActivity["rejectedPath"], "ACT_TYPE_"));
        Assert.Equal("endEvent", RowValues.Text(byActivity["end"], "ACT_TYPE_"));

        // 用户任务行：TASK_ID_ 与 ASSIGNEE_ 在建行之后被回填
        Assert.Equal(task.Id, RowValues.Text(byActivity["approve"], "TASK_ID_"));
        Assert.Equal("kermit", RowValues.Text(byActivity["approve"], "ASSIGNEE_"));
        Assert.Equal("审批", RowValues.Text(byActivity["approve"], "ACT_NAME_"));

        // 全部收口：历史活动行不允许留 END_TIME_ 为空
        Assert.All(rows, row => Assert.NotNull(RowValues.Time(row, "END_TIME_")));

        // 活动历史行的 id 是新生成的（与执行/任务 id 无关）
        Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(RowValues.Text(row, "ID_"))));
    }

    private static void AssertHistoricTaskInstance(
        NetBpm.Persistence.Database.IDbSession session,
        NetBpm.Engine.Tasks.TaskEntity task)
    {
        var rows = RowValues.Query(
            session,
            "select ID_, TASK_DEF_KEY_, PROC_INST_ID_, EXECUTION_ID_, NAME_, ASSIGNEE_, OWNER_, PRIORITY_, "
            + "FORM_KEY_, START_TIME_, END_TIME_, DURATION_, DELETE_REASON_ from ACT_HI_TASKINST");

        var row = Assert.Single(rows);

        // 🔑 ID_ 与 ACT_RU_TASK.ID_ 相同
        Assert.Equal(task.Id, RowValues.Text(row, "ID_"));
        Assert.Equal("approve", RowValues.Text(row, "TASK_DEF_KEY_"));
        Assert.Equal(task.ProcessInstanceId, RowValues.Text(row, "PROC_INST_ID_"));
        Assert.Equal("审批", RowValues.Text(row, "NAME_"));
        Assert.Equal("kermit", RowValues.Text(row, "ASSIGNEE_"));
        Assert.Equal("approveForm", RowValues.Text(row, "FORM_KEY_"));
        Assert.Equal(80, RowValues.Int(row, "PRIORITY_"));
        Assert.True(RowValues.IsNull(row, "DELETE_REASON_"));
        Assert.NotNull(RowValues.Time(row, "START_TIME_"));
        Assert.NotNull(RowValues.Time(row, "END_TIME_"));
        Assert.NotNull(RowValues.Long(row, "DURATION_"));
    }

    private static void AssertHistoricVariableInstances(
        NetBpm.Persistence.Database.IDbSession session,
        string processInstanceId)
    {
        var rows = RowValues.Query(
            session,
            "select ID_, PROC_INST_ID_, EXECUTION_ID_, TASK_ID_, NAME_, REV_, VAR_TYPE_, LONG_, CREATE_TIME_, "
            + "LAST_UPDATED_TIME_ from ACT_HI_VARINST");

        var row = Assert.Single(rows);

        Assert.False(string.IsNullOrEmpty(RowValues.Text(row, "ID_")));
        Assert.Equal(processInstanceId, RowValues.Text(row, "PROC_INST_ID_"));
        Assert.Equal(processInstanceId, RowValues.Text(row, "EXECUTION_ID_"));
        Assert.Equal("approved", RowValues.Text(row, "NAME_"));
        Assert.Equal("boolean", RowValues.Text(row, "VAR_TYPE_"));
        Assert.Equal(0L, RowValues.Long(row, "LONG_"));
        Assert.True(RowValues.IsNull(row, "TASK_ID_"));
        Assert.Equal(0, RowValues.Int(row, "REV_"));   // 历史行 REV_ 恒为 0（见 VariableUpdate_ShouldKeepHistoricRevisionAtZero）
        Assert.NotNull(RowValues.Time(row, "CREATE_TIME_"));
        Assert.NotNull(RowValues.Time(row, "LAST_UPDATED_TIME_"));
    }

    private static void AssertHistoricIdentityLinks(
        NetBpm.Persistence.Database.IDbSession session,
        string processInstanceId)
    {
        var rows = RowValues.Query(
            session,
            "select ID_, TYPE_, USER_ID_, GROUP_ID_, TASK_ID_, PROC_INST_ID_ from ACT_HI_IDENTITYLINK");

        // 与运行期完全同构：任务级 4 条 candidate + 流程实例级 3 条 participant
        Assert.Equal(7, rows.Count);

        var taskLevel = rows.Where(row => !RowValues.IsNull(row, "TASK_ID_")).ToList();
        Assert.Equal(4, taskLevel.Count);
        Assert.All(taskLevel, row => Assert.Equal(IdentityLinkType.Candidate, RowValues.Text(row, "TYPE_")));
        Assert.Contains(taskLevel, row => RowValues.Text(row, "USER_ID_") == "alice");
        Assert.Contains(taskLevel, row => RowValues.Text(row, "GROUP_ID_") == "managers");

        var instanceLevel = rows.Where(row => RowValues.IsNull(row, "TASK_ID_")).ToList();
        Assert.Equal(3, instanceLevel.Count);
        Assert.All(instanceLevel, row =>
        {
            Assert.Equal(IdentityLinkType.Participant, RowValues.Text(row, "TYPE_"));
            Assert.Equal(processInstanceId, RowValues.Text(row, "PROC_INST_ID_"));
        });

        // 🔑 实例结束后历史关系行**保留**（Activiti 的 deleteIdentityLinksByTaskId 传 cascadeHistory=false）
        Assert.Contains(instanceLevel, row => RowValues.Text(row, "USER_ID_") == "kermit");
    }

    [Fact]
    public async Task LongStringVariable_ShouldRoundTripThroughByteArray()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        // > 4000 字符 ⇒ 降级为 longString（VariableTypeMapper 的降级阈值）
        var longText = new string('长', 2500) + new string('x', 2000);
        Assert.True(longText.Length > 4000);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            ApprovalProcess(),
            new Dictionary<string, object?> { ["memo"] = longText },
            Ct);

        string? byteArrayId;

        using (var session = harness.SessionFactory.Create())
        {
            var rows = RowValues.Query(
                session,
                "select TYPE_, TEXT_, BYTEARRAY_ID_ from ACT_RU_VARIABLE");

            var row = Assert.Single(rows);

            Assert.Equal("longString", RowValues.Text(row, "TYPE_"));
            Assert.True(RowValues.IsNull(row, "TEXT_"));

            byteArrayId = RowValues.Text(row, "BYTEARRAY_ID_");
            Assert.False(string.IsNullOrEmpty(byteArrayId));

            // 字节行：DEPLOYMENT_ID_ 为 null（区别于部署资源），内容是 UTF-8 字节
            var bytearrayRows = RowValues.Query(
                session,
                "select NAME_, DEPLOYMENT_ID_, BYTES_ from ACT_GE_BYTEARRAY where ID_ = '" + byteArrayId + "'");

            var bytearrayRow = Assert.Single(bytearrayRows);
            Assert.Equal("memo", RowValues.Text(bytearrayRow, "NAME_"));
            Assert.True(RowValues.IsNull(bytearrayRow, "DEPLOYMENT_ID_"));
            Assert.Equal(longText, RowValues.TextFromBytes(bytearrayRow, "BYTES_"));

            // 历史变量行引用同一个字节行
            var historyRows = RowValues.Query(
                session,
                "select VAR_TYPE_, BYTEARRAY_ID_ from ACT_HI_VARINST");

            var historyRow = Assert.Single(historyRows);
            Assert.Equal("longString", RowValues.Text(historyRow, "VAR_TYPE_"));
            Assert.Equal(byteArrayId, RowValues.Text(historyRow, "BYTEARRAY_ID_"));
        }

        // 引擎 API 读回：跨命令取值正确（对应超长审批意见等真实业务场景）
        var variables = await harness.VariableInstances
            .FindByExecutionIdAsync(processInstanceId, Ct);

        var memo = Assert.Single(variables);
        Assert.Equal("memo", memo.Name);
        Assert.Equal("longString", memo.TypeName);
        Assert.Equal(longText, memo.Value);

        // 更新为短字符串：类型回退 string，旧字节行被删除
        await harness.VariableInstances.UpdateAsync(memo, "已批，同意。", Ct);

        using (var session = harness.SessionFactory.Create())
        {
            var rows = RowValues.Query(
                session,
                "select TYPE_, TEXT_, BYTEARRAY_ID_ from ACT_RU_VARIABLE");

            var row = Assert.Single(rows);

            Assert.Equal("string", RowValues.Text(row, "TYPE_"));
            Assert.Equal("已批，同意。", RowValues.Text(row, "TEXT_"));
            Assert.True(RowValues.IsNull(row, "BYTEARRAY_ID_"));
            Assert.Empty(RowValues.Query(
                session,
                "select ID_ from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is null"));
        }
    }

    /// <summary>用户任务 → 排他网关（条件 ${approved} + 默认流）→ 两条手动任务 → 结束。</summary>
    /// <summary>带 <c>&lt;documentation&gt;</c> 的两环节流程：流程级一段、userTask 级一段。</summary>
    private static string DocumentedProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="documented" isExecutable="true">
            <documentation>流程说明</documentation>
            <startEvent id="start" />
            <userTask id="approve" name="审批" activiti:assignee="kermit">
              <documentation>审批环节说明</documentation>
            </userTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    private static string ApprovalProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="var-history" name="变量与历史（库级验证）" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="审批" activiti:assignee="kermit" activiti:candidateUsers="alice,bob" activiti:candidateGroups="managers,hr" activiti:formKey="approveForm" activiti:priority="80" />
            <exclusiveGateway id="gw" default="flowRejected" />
            <manualTask id="approvedPath" />
            <manualTask id="rejectedPath" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="gw" />
            <sequenceFlow id="flowApproved" sourceRef="gw" targetRef="approvedPath">
              <conditionExpression xsi:type="tFormalExpression">${approved}</conditionExpression>
            </sequenceFlow>
            <sequenceFlow id="flowRejected" sourceRef="gw" targetRef="rejectedPath" />
            <sequenceFlow id="f3" sourceRef="approvedPath" targetRef="end" />
            <sequenceFlow id="f4" sourceRef="rejectedPath" targetRef="end" />
          </process>
        </definitions>
        """;
}

using NetBpm.Bpmn.Model;
using NetBpm.Engine.Command;
using NetBpm.Runtime;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 历史 / 运行实例查询的<b>服务端下推</b>：过滤、排序、分页与 count 必须由 SQL 完成，
/// 不再「取全表 + 内存 Where/Skip/Take」。
/// </summary>
[Collection("MySql")]
public sealed class HistoryQueryPushdownTests
{
    private const string SimpleProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="history_pushdown" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <userTask id="approve" name="审批" activiti:assignee="alice" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
            <endEvent id="end" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task HistoricQueries_ShouldFilterPageAndCountInDatabase()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(SimpleProcess)));

        var definition = await harness.Definitions.DeployAsync(model, "history-pushdown.bpmn20.xml");

        var aliceInstance = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-a", "alice"));

        await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-b", "bob"));

        // 按发起人过滤 + 分页：只应有 alice 那一条
        var page = await harness.Engine.FindHistoricProcessInstancesAsync(
            new HistoricProcessInstanceQuery(StartUserId: "alice", FirstResult: 0, MaxResults: 1));

        var instance = Assert.Single(page);
        Assert.Equal(aliceInstance, instance.Id);
        Assert.Equal("alice", instance.StartUserId);

        // count 与列表同口径：不能为了一个数字把整表读进内存
        Assert.Equal(1, await harness.Engine.CountHistoricProcessInstancesAsync(
            new HistoricProcessInstanceQuery(StartUserId: "alice")));

        // 按 key 过滤（门面把 key 展开为全部版本 id）
        Assert.Equal(2, await harness.Engine.CountHistoricProcessInstancesAsync(
            new HistoricProcessInstanceQuery(ProcessDefinitionKey: "history_pushdown")));

        // key 解析不到定义 ⇒ 零匹配，不能退化成「不加过滤」
        Assert.Empty(await harness.Engine.FindHistoricProcessInstancesAsync(
            new HistoricProcessInstanceQuery(ProcessDefinitionKey: "no_such_key")));
        Assert.Equal(0, await harness.Engine.CountHistoricProcessInstancesAsync(
            new HistoricProcessInstanceQuery(ProcessDefinitionKey: "no_such_key")));

        // 历史任务：按实例过滤 + 办理人过滤 + 分页
        var tasks = await harness.Engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(ProcessInstanceId: aliceInstance, Assignee: "alice", MaxResults: 5));

        var task = Assert.Single(tasks);
        Assert.Equal("approve", task.TaskDefinitionKey);

        Assert.Equal(1, await harness.Engine.CountHistoricTasksAsync(
            new HistoricTaskQuery(ProcessInstanceId: aliceInstance, Assignee: "alice")));

        Assert.Equal(0, await harness.Engine.CountHistoricTasksAsync(
            new HistoricTaskQuery(ProcessInstanceId: aliceInstance, Assignee: "nobody")));

        // 运行中实例：服务端过滤 + 分页
        var running = await harness.Engine.FindProcessInstancesAsync(
            new ProcessInstanceQuery(StartUserId: "bob", MaxResults: 10));

        Assert.Single(running);
        Assert.Equal(1, await harness.Engine.CountProcessInstancesAsync(
            new ProcessInstanceQuery(StartUserId: "bob")));
    }
}

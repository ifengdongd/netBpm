using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Engine.Command;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 引擎层命令幂等的库级验收。
/// </summary>
/// <remarks>
/// <para>
/// 与 HTTP 幂等中间件（<c>Idempotency-Key</c>）的区别：中间件只能挡住打到同一实例的重复 HTTP 请求，
/// 而 <c>ACT_EVT_OUTBOX</c> 重投、异步作业重试、消息重复消费都会<b>绕过 HTTP 直接驱动引擎</b>。
/// 本组用例验证的是后者 —— 命令自带幂等键时，重复执行只生效一次。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class CommandIdempotencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string UserTaskProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="idem" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="审批" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task CompleteTask_WithSameCommandId_ShouldOnlyTakeEffectOnce()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(UserTaskProcess, ct: Ct);
        var task = Assert.Single(
            await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        // 首次完成：生效
        await harness.Executor
            .ExecuteAsync(new CompleteTaskCommand(task.Id, commandId: "cmd-1"), Ct)
            .ConfigureAwait(false);

        Assert.Empty(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        // 同键重投：不报错、不重复生效（任务此时已不存在，若没被去重会抛 EngineObjectNotFoundException）
        await harness.Executor
            .ExecuteAsync(new CompleteTaskCommand(task.Id, commandId: "cmd-1"), Ct)
            .ConfigureAwait(false);

        // 换一个键：说明上面的静默返回确实来自去重，而不是「完成已删除任务也不报错」
        await Assert.ThrowsAsync<EngineObjectNotFoundException>(async () =>
            await harness.Executor
                .ExecuteAsync(new CompleteTaskCommand(task.Id, commandId: "cmd-2"), Ct)
                .ConfigureAwait(false));
    }

    [Fact]
    public async Task CompleteTask_WithoutCommandId_ShouldBehaveAsBefore()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(UserTaskProcess, ct: Ct);
        var task = Assert.Single(
            await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        await harness.CompleteTaskAsync(task.Id, ct: Ct);

        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));
    }

    /// <summary>
    /// 幂等占位必须与业务写入同事务：命令失败回滚后，占位行也要消失，客户端可用同键重试。
    /// </summary>
    [Fact]
    public async Task FailedCommand_ShouldNotLeaveIdempotencyRecord()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        // 不存在的任务 ⇒ 命令抛 EngineObjectNotFoundException。
        // 但幂等占位是在「任务存在性校验通过之后」才写的，因此这里不应留下记录。
        await Assert.ThrowsAsync<EngineObjectNotFoundException>(async () =>
            await harness.Executor
                .ExecuteAsync(new CompleteTaskCommand("missing-task", commandId: "cmd-fail"), Ct)
                .ConfigureAwait(false));

        Assert.Null(await harness.CommandIdempotency.FindAsync(
            CommandIdempotencyKeys.ForTask("cmd-fail", "missing-task"), Ct));
    }
}

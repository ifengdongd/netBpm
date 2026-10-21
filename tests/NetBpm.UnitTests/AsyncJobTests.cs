using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Tasks;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.UnitTests;

/// <summary>
/// 异步作业（<c>asyncBefore</c> / <c>asyncAfter</c>）：断点、恢复、重试与死信。
/// </summary>
public sealed class AsyncJobTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string Process(string flags, string delegateName = "svc") => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="async_job" isExecutable="true">
            <startEvent id="start" />
            <serviceTask id="svc" activiti:delegateExpression="{{delegateName}}" {{flags}} />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="svc" />
            <sequenceFlow id="f2" sourceRef="svc" targetRef="end" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task AsyncBefore_ShouldDeferActivityUntilJobRuns()
    {
        var host = TestEngine.Create();
        var instanceId = await host.StartProcessAsync(Process("activiti:asyncBefore=\"true\""), ct: Ct);

        // 命令已提交，但活动行为还没执行：委托未跑、作业已落库、执行停在活动上
        Assert.Empty(host.ExecutedDelegates);
        Assert.Equal(1, await host.AsyncJobs.CountAsync(Ct));

        var job = Assert.Single(await host.AsyncJobs.FindDueAsync(DateTime.UtcNow, 10, Ct));
        Assert.Equal(AsyncJobPhase.Before, job.Phase);
        Assert.Equal("svc", job.ActivityId);
        Assert.Equal(instanceId, job.ProcessInstanceId);

        // 作业执行后：行为跑完、流程结束、作业被消费
        Assert.Equal(1, await host.ExecuteDueAsyncJobsAsync(DateTime.UtcNow, 10, Ct));
        Assert.Equal(["svc"], host.ExecutedDelegates);
        Assert.Equal(0, await host.AsyncJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task AsyncAfter_ShouldRunBehaviorSynchronously_AndDeferLeaving()
    {
        var host = TestEngine.Create();
        var instanceId = await host.StartProcessAsync(Process("activiti:asyncAfter=\"true\""), ct: Ct);

        // 活动行为在命令内执行；离开节点（历史结束 + 出流）推迟到作业
        Assert.Equal(["svc"], host.ExecutedDelegates);
        Assert.Equal(1, await host.AsyncJobs.CountAsync(Ct));

        var job = Assert.Single(await host.AsyncJobs.FindDueAsync(DateTime.UtcNow, 10, Ct));
        Assert.Equal(AsyncJobPhase.After, job.Phase);

        var execution = await host.ExecutionManager.FindByIdAsync(instanceId, Ct);
        Assert.NotNull(execution);
        Assert.False(execution!.IsEnded);

        Assert.Equal(1, await host.ExecuteDueAsyncJobsAsync(DateTime.UtcNow, 10, Ct));
        Assert.Equal(0, await host.AsyncJobs.CountAsync(Ct));

        // 委托只执行一次：asyncAfter 不重跑行为
        Assert.Equal(["svc"], host.ExecutedDelegates);
    }

    [Fact]
    public async Task AsyncBeforeAndAfter_ShouldCreateTwoJobsInSequence()
    {
        var host = TestEngine.Create();
        await host.StartProcessAsync(
            Process("activiti:asyncBefore=\"true\" activiti:asyncAfter=\"true\""),
            ct: Ct);

        Assert.Empty(host.ExecutedDelegates);

        // 第一条：进入活动
        Assert.Equal(1, await host.ExecuteDueAsyncJobsAsync(DateTime.UtcNow, 10, Ct));
        Assert.Equal(["svc"], host.ExecutedDelegates);
        Assert.Equal(1, await host.AsyncJobs.CountAsync(Ct));

        var second = Assert.Single(await host.AsyncJobs.FindDueAsync(DateTime.UtcNow, 10, Ct));
        Assert.Equal(AsyncJobPhase.After, second.Phase);

        // 第二条：离开活动
        Assert.Equal(1, await host.ExecuteDueAsyncJobsAsync(DateTime.UtcNow, 10, Ct));
        Assert.Equal(0, await host.AsyncJobs.CountAsync(Ct));
        Assert.Equal(["svc"], host.ExecutedDelegates);
    }

    [Fact]
    public async Task FailingJob_ShouldRetryWithBackoff_ThenEnterDeadLetter()
    {
        var host = TestEngine.Create();
        host.DelegateResolver.Register("boom", new ThrowingDelegate());

        await host.StartProcessAsync(
            Process("activiti:asyncBefore=\"true\"", "boom"),
            ct: Ct);

        // 第一次失败：剩余 2 次，未进死信，按退避顺延
        Assert.Equal(0, await host.ExecuteDueAsyncJobsAsync(DateTime.UtcNow, 10, Ct));

        var job = Assert.Single(await host.AsyncJobs.FindAllAsync(Ct));
        Assert.Equal(AsyncJobRetry.DefaultMaxRetries - 1, job.Retries);
        Assert.False(job.DeadLetter);
        Assert.Contains("boom", job.ExceptionMessage, StringComparison.OrdinalIgnoreCase);

        // 再失败两次（每次用未来时间越过退避）→ 死信
        await host.ExecuteDueAsyncJobsAsync(DateTime.UtcNow.AddMinutes(10), 10, Ct);
        await host.ExecuteDueAsyncJobsAsync(DateTime.UtcNow.AddMinutes(20), 10, Ct);

        var dead = Assert.Single(await host.AsyncJobs.FindAsync(
            new AsyncJobQuery(DeadLetter: true), Ct));

        Assert.True(dead.DeadLetter);
        Assert.Equal(0, dead.Retries);
        Assert.Empty(await host.AsyncJobs.FindDueAsync(DateTime.UtcNow.AddDays(1), 10, Ct));

        // 人工重试：复位重试次数、立即到期
        Assert.True(await host.AsyncJobs.RetryDeadLetterAsync(dead.Id, DateTime.UtcNow, Ct));
        Assert.Single(await host.AsyncJobs.FindDueAsync(DateTime.UtcNow, 10, Ct));
    }

    private sealed class ThrowingDelegate : IServiceTaskDelegate
    {
        public ValueTask ExecuteAsync(Engine.Execution.IDelegateExecution execution, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }
}

internal static class AsyncJobTestExtensions
{
    /// <summary>测试辅助：取全部作业（含死信）。</summary>
    public static async ValueTask<IReadOnlyList<AsyncJobEntity>> FindAllAsync(
        this InMemoryAsyncJobManager jobs,
        CancellationToken ct) =>
        await jobs.FindAsync(new AsyncJobQuery(MaxResults: 100), ct);
}

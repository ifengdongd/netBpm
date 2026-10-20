using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.UnitTests;

/// <summary>
/// 边界定时器 + 终止结束事件：解析往返、部署期校验与运行期语义。
/// </summary>
public sealed class BoundaryTimerAndTerminateTests
{
    private const string TimerProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="timer_process" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <userTask id="approve" name="审批" activiti:assignee="alice" />
            <boundaryEvent id="timeout" attachedToRef="approve" cancelActivity="true">
              <timerEventDefinition>
                <timeDuration>PT2H</timeDuration>
              </timerEventDefinition>
            </boundaryEvent>
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end_ok" />
            <sequenceFlow id="f3" sourceRef="timeout" targetRef="end_timeout" />
            <endEvent id="end_ok" />
            <endEvent id="end_timeout" />
          </process>
        </definitions>
        """;

    [Fact]
    public void Converter_ShouldRoundTripBoundaryTimerAndTerminate()
    {
        var process = Parse(TimerProcess);

        var boundary = Assert.Single(process.FindFlowElementsOfType<BoundaryEvent>());
        Assert.Equal("approve", boundary.AttachedToRef);
        Assert.True(boundary.CancelActivity);
        Assert.Equal("PT2H", boundary.Timer?.TimeDuration);
        Assert.Same(process.GetFlowElement("approve"), boundary.AttachedTo);

        var model = new BpmnModel();
        model.AddProcess(process);

        using var output = new MemoryStream();
        new BpmnXmlConverter().ConvertToXml(model, output);
        var xml = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("boundaryEvent", xml, StringComparison.Ordinal);
        Assert.Contains("attachedToRef=\"approve\"", xml, StringComparison.Ordinal);
        Assert.Contains("timeDuration", xml, StringComparison.Ordinal);
        Assert.Contains("PT2H", xml, StringComparison.Ordinal);

        var roundTripped = Parse(xml);
        var boundaryAgain = Assert.Single(roundTripped.FindFlowElementsOfType<BoundaryEvent>());
        Assert.Equal("PT2H", boundaryAgain.Timer?.TimeDuration);
        Assert.Same(roundTripped.GetFlowElement("approve"), boundaryAgain.AttachedTo);
    }

    [Fact]
    public void ValidTimerProcess_ShouldPassValidation()
    {
        Assert.Empty(new ProcessValidator().Validate(Parse(TimerProcess)));
    }

    [Theory]
    [InlineData("timerEventDefinition", "PT30M", "serviceTask")]
    [InlineData("timerEventDefinition", "PT30M", "no-attach")]
    [InlineData("timerEventDefinition", "R3/PT1H", "userTask")]
    [InlineData("messageEventDefinition", null, "userTask")]
    public void InvalidBoundaryEvent_ShouldBeReported(string definition, string? duration, string host)
    {
        var hostElement = host switch
        {
            "serviceTask" => """<serviceTask id="approve" activiti:class="X" />""",
            "no-attach" => """<userTask id="approve" activiti:assignee="alice" />""",
            _ => """<userTask id="approve" activiti:assignee="alice" />""",
        };

        var definitions = definition == "timerEventDefinition"
            ? $"""
              <bpmn:timerEventDefinition>
                {(duration is null ? string.Empty : $"<bpmn:timeDuration>{duration}</bpmn:timeDuration>")}
              </bpmn:timerEventDefinition>
              """
            : "<bpmn:messageEventDefinition />";

        var attachedTo = host == "no-attach" ? string.Empty : """ attachedToRef="approve" cancelActivity="false" """;

        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p1" isExecutable="true">
                <startEvent id="start" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                {{hostElement}}
                <boundaryEvent id="boundary"{{attachedTo}}>
                  {{definitions}}
                </boundaryEvent>
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
                <sequenceFlow id="f3" sourceRef="boundary" targetRef="end" />
                <endEvent id="end" />
              </process>
            </definitions>
            """;

        var errors = new ProcessValidator().Validate(Parse(xml)).Where(error => error.IsError).ToList();

        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.ElementId == "boundary");
    }

    [Fact]
    public void TerminateWithOutgoing_ShouldBeReported()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="http://netbpm/test">
              <process id="p1" isExecutable="true">
                <startEvent id="start" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="term" />
                <endEvent id="term">
                  <terminateEventDefinition />
                </endEvent>
                <sequenceFlow id="f2" sourceRef="term" targetRef="other" />
                <endEvent id="other" />
              </process>
            </definitions>
            """;

        var errors = new ProcessValidator().Validate(Parse(xml)).ToList();

        Assert.Contains(errors, error => error.Rule == "terminate-end-event" && error.ElementId == "term");
    }

    [Fact]
    public async Task Timer_ShouldFire_AndCancelTask()
    {
        var engine = TestEngine.Create();
        var processInstanceId = await engine.StartProcessAsync(TimerProcess);

        var tasks = await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId);
        Assert.Single(tasks);
        Assert.Equal(1, await engine.TimerJobs.CountAsync());

        // 未到期不触发
        Assert.Equal(0, await engine.TriggerDueTimersAsync(DateTime.UtcNow));

        var fired = await engine.TriggerDueTimersAsync(DateTime.UtcNow.AddHours(3));

        Assert.Equal(1, fired);
        Assert.Empty(await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));
        Assert.Null(await engine.ExecutionManager.FindByIdAsync(processInstanceId));
        Assert.Equal(0, await engine.TimerJobs.CountAsync());
    }

    [Fact]
    public async Task CompletingTask_ShouldCancelScheduledTimer()
    {
        var engine = TestEngine.Create();
        var processInstanceId = await engine.StartProcessAsync(TimerProcess);

        var task = Assert.Single(await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));

        await engine.CompleteTaskAsync(task.Id);

        Assert.Empty(await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));
        Assert.Equal(0, await engine.TimerJobs.CountAsync());

        // 作业没了，即使时间到了也不会有任何动作
        Assert.Equal(0, await engine.TriggerDueTimersAsync(DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task TimerDurationExpression_ShouldBeEvaluatedAtCreation()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p1" isExecutable="true">
                <startEvent id="start" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <userTask id="approve" activiti:assignee="alice" />
                <boundaryEvent id="timeout" attachedToRef="approve">
                  <timerEventDefinition>
                    <timeDuration>${timeout}</timeDuration>
                  </timerEventDefinition>
                </boundaryEvent>
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end_ok" />
                <sequenceFlow id="f3" sourceRef="timeout" targetRef="end_timeout" />
                <endEvent id="end_ok" />
                <endEvent id="end_timeout" />
              </process>
            </definitions>
            """;

        var engine = TestEngine.Create();
        var processInstanceId = await engine.StartProcessAsync(
            xml,
            new Dictionary<string, object?> { ["timeout"] = "PT30M" });

        var job = Assert.Single(await engine.TimerJobs.FindDueAsync(DateTime.UtcNow.AddHours(1), 10));
        Assert.InRange(job.DueDate, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));

        Assert.Equal(1, await engine.TriggerDueTimersAsync(DateTime.UtcNow.AddHours(1)));
        Assert.Null(await engine.ExecutionManager.FindByIdAsync(processInstanceId));
    }

    [Fact]
    public async Task TerminateEndEvent_ShouldKillAllBranches()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="p1" isExecutable="true">
                <startEvent id="start" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
                <parallelGateway id="fork" />
                <sequenceFlow id="f2" sourceRef="fork" targetRef="task_a" />
                <sequenceFlow id="f3" sourceRef="fork" targetRef="task_b" />
                <userTask id="task_a" activiti:assignee="alice" />
                <userTask id="task_b" activiti:assignee="bob" />
                <boundaryEvent id="timer_b" attachedToRef="task_b">
                  <timerEventDefinition>
                    <timeDuration>PT1H</timeDuration>
                  </timerEventDefinition>
                </boundaryEvent>
                <sequenceFlow id="f4" sourceRef="task_a" targetRef="term" />
                <sequenceFlow id="f5" sourceRef="task_b" targetRef="end_b" />
                <sequenceFlow id="f6" sourceRef="timer_b" targetRef="end_b" />
                <endEvent id="term">
                  <terminateEventDefinition />
                </endEvent>
                <endEvent id="end_b" />
              </process>
            </definitions>
            """;

        var engine = TestEngine.Create();
        var processInstanceId = await engine.StartProcessAsync(xml);

        var tasks = await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId);
        Assert.Equal(2, tasks.Count);
        Assert.Equal(1, await engine.TimerJobs.CountAsync());

        var taskA = tasks.Single(task => task.TaskDefinitionKey == "task_a");
        await engine.CompleteTaskAsync(taskA.Id);

        // 终止结束事件：整个实例（含 task_b 分支与其定时作业）一并消失
        Assert.Empty(await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));
        Assert.Null(await engine.ExecutionManager.FindByIdAsync(processInstanceId));
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
        Assert.Equal(0, await engine.TimerJobs.CountAsync());
    }

    private static Process Parse(string xml)
    {
        var model = new BpmnXmlConverter().ConvertToBpmnModel(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        return model.GetMainProcess()
            ?? throw new InvalidOperationException("流程解析失败。");
    }
}

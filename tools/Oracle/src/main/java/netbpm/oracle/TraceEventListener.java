package netbpm.oracle;

import org.activiti.engine.delegate.event.ActivitiActivityEvent;
import org.activiti.engine.delegate.event.ActivitiEntityEvent;
import org.activiti.engine.delegate.event.ActivitiEvent;
import org.activiti.engine.delegate.event.ActivitiEventListener;
import org.activiti.engine.delegate.event.ActivitiEventType;
import org.activiti.engine.delegate.event.ActivitiSequenceFlowTakenEvent;
import org.activiti.engine.delegate.event.ActivitiVariableEvent;
import org.activiti.engine.task.Task;

import java.util.ArrayList;
import java.util.List;

/**
 * 维度③「事件序列」的采集器（开发计划 §9.2）。
 *
 * <p>对应 C# 侧的 {@code TraceRecordingHistoryManager}：那一边用"装饰历史管理器"实现，
 * 这一边用 Activiti 自带的 {@link ActivitiEventListener}（引擎在同样这些时机分发事件）。</p>
 *
 * <p>⚠️ <b>口径校准声明</b>：两端的"触发时机与字段填充"尚未经真机跑批校准。首次跑批后，
 * 维度③ 的差异清单必须逐条确认属于"口径差异"（例如 {@code SEQUENCEFLOW_TAKEN} 的
 * {@code activityId} 到底放源节点还是流 id）还是真实缺陷，然后把结论回写到本文件与 C# 侧。</p>
 *
 * <p>字段提取一律 <b>fail-soft</b>：拿不到就留空（null），不抛异常 —— 差异比对的规则是
 * "只有两侧都填了的字段才比"，因此留空是安全的口径差异，而抛异常会让整批跑不下去。</p>
 */
public final class TraceEventListener implements ActivitiEventListener {

    /** 与 C# 侧对齐的事件子集（其余事件不入序列：它们要么是通用 ENTITY_*，要么已被维度②覆盖）。 */
    private static final List<ActivitiEventType> TRACED = List.of(
            ActivitiEventType.PROCESS_STARTED,
            ActivitiEventType.PROCESS_COMPLETED,
            ActivitiEventType.ACTIVITY_STARTED,
            ActivitiEventType.ACTIVITY_COMPLETED,
            ActivitiEventType.SEQUENCEFLOW_TAKEN,
            ActivitiEventType.TASK_CREATED,
            ActivitiEventType.TASK_ASSIGNED,
            ActivitiEventType.TASK_COMPLETED,
            ActivitiEventType.VARIABLE_CREATED,
            ActivitiEventType.VARIABLE_UPDATED);

    private final List<Snapshot.TraceEvent> events = new ArrayList<>();

    public List<Snapshot.TraceEvent> events() {
        return events;
    }

    public void reset() {
        events.clear();
    }

    @Override
    public void onEvent(ActivitiEvent event) {
        ActivitiEventType type = event.getType();

        if (type == null || !TRACED.contains(type)) {
            return;
        }

        Snapshot.TraceEvent traceEvent = new Snapshot.TraceEvent(type.name());

        // ⚠️ Activiti 9 的事件 API（已核实，见 activiti-engine/.../delegate/event/）：
        //   · ActivitiActivityEvent          → getActivityId()
        //   · ActivitiSequenceFlowTakenEvent → getId()（流 id）+ getSource/TargetActivityId()
        // 基类 ActivitiEvent 只有 getType()，因此必须按事件类型转成具体接口取值。
        // 这与 C# 侧的口径逐字对齐：SEQUENCEFLOW_TAKEN 只填 flowId（流 id），activityId 留空。
        if (event instanceof ActivitiSequenceFlowTakenEvent flowEvent) {
            traceEvent.flowId = flowEvent.getId();
        } else if (event instanceof ActivitiActivityEvent activityEvent) {
            traceEvent.activityId = activityEvent.getActivityId();
        }

        if (type == ActivitiEventType.TASK_CREATED
                || type == ActivitiEventType.TASK_ASSIGNED
                || type == ActivitiEventType.TASK_COMPLETED) {
            traceEvent.taskDefinitionKey = taskDefinitionKey(event);
        }

        if (type == ActivitiEventType.VARIABLE_CREATED || type == ActivitiEventType.VARIABLE_UPDATED) {
            traceEvent.variableName = variableName(event);
        }

        events.add(traceEvent);
    }

    @Override
    public boolean isFailOnException() {
        // 采集失败不应影响语料执行
        return false;
    }

    private static String taskDefinitionKey(ActivitiEvent event) {
        try {
            if (event instanceof ActivitiEntityEvent entityEvent
                    && entityEvent.getEntity() instanceof Task task) {
                return task.getTaskDefinitionKey();
            }
        } catch (RuntimeException ignored) {
            // 留空：口径差异由差异清单裁定
        }

        return null;
    }

    private static String variableName(ActivitiEvent event) {
        try {
            if (event instanceof ActivitiVariableEvent variableEvent) {
                return variableEvent.getVariableName();
            }
        } catch (RuntimeException ignored) {
            // 同上
        }

        return null;
    }
}

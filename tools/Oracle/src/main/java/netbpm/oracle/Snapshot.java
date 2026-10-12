package netbpm.oracle;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * 三维度快照（与 C# 的 {@code OracleSnapshot} 一一对应）。
 *
 * <p>字段名必须与 C# 侧完全相同：契约由两端的 record/类定义共同承担，
 * 任一侧改名都会让对拍直接报"结构性差异"。</p>
 */
public final class Snapshot {

    private Snapshot() {
    }

    public static final class OracleSnapshot {
        public String caseName;
        public ApiView api = new ApiView();
        public Map<String, List<Map<String, String>>> tables = new LinkedHashMap<>();
        public List<TraceEvent> events = new ArrayList<>();
    }

    public static final class ApiView {
        public List<ApiProcessInstance> processInstances = new ArrayList<>();
        public List<ApiTask> tasks = new ArrayList<>();
        public List<ApiVariable> variables = new ArrayList<>();
    }

    public static final class ApiProcessInstance {
        public String businessKey;
        public boolean isEnded;

        /**
         * 当前未结束的活动 id（有序去重）。
         * <p>取法与 C# 侧同口径：{@code HistoricActivityInstanceQuery.unfinished()}。</p>
         */
        public List<String> unfinishedActivityIds = new ArrayList<>();
    }

    public static final class ApiTask {
        public String taskDefinitionKey;
        public String assignee;

        public ApiTask() {
        }

        public ApiTask(String taskDefinitionKey, String assignee) {
            this.taskDefinitionKey = taskDefinitionKey;
            this.assignee = assignee;
        }
    }

    public static final class ApiVariable {
        public String name;
        public String type;
        public String value;

        public ApiVariable() {
        }

        public ApiVariable(String name, String type, String value) {
            this.name = name;
            this.type = type;
            this.value = value;
        }
    }

    /** 事件类型名一律取 {@code ActivitiEventType} 的枚举名（PROCESS_STARTED / SEQUENCEFLOW_TAKEN…）。 */
    public static final class TraceEvent {
        public String type;
        public String activityId;
        public String taskDefinitionKey;
        public String flowId;
        public String variableName;

        public TraceEvent() {
        }

        public TraceEvent(String type) {
            this.type = type;
        }
    }
}

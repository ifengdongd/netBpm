package netbpm.oracle;

import org.activiti.engine.HistoryService;
import org.activiti.engine.ProcessEngine;
import org.activiti.engine.ProcessEngineConfiguration;
import org.activiti.engine.RepositoryService;
import org.activiti.engine.RuntimeService;
import org.activiti.engine.TaskService;
import org.activiti.engine.impl.cfg.ProcessEngineConfigurationImpl;
import org.activiti.engine.impl.history.HistoryLevel;
import org.activiti.engine.repository.ProcessDefinition;
import org.activiti.engine.runtime.ProcessInstance;
import org.activiti.engine.task.Task;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.SQLException;
import java.sql.Statement;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;

/**
 * Java 预言机（开发计划 §9.2 / P10）：用真 Activiti 跑同一批语料，产出三维度快照（真值）。
 *
 * <pre>
 * 用法：
 *   mvn -f tools/Oracle/pom.xml compile exec:java \
 *       -Dexec.args="--corpus tests/corpus --connection 'Server...'"
 *
 * 参数：
 *   --corpus <dir>        语料根目录（默认 tests/corpus）
 *   --connection <url>    JDBC URL（默认取环境变量 NETBPM_ORACLE_JDBC）
 *   --user / --password   JDBC 账号（默认 root / 空）
 *   --only <fullName>     只跑一个语料（调试用）
 * </pre>
 *
 * <p>产出：在每个语料旁写 <code>&lt;stem&gt;.oracle.json</code>（与 C# 侧的执行器读取位置一致）。</p>
 *
 * <p>⚠️ 运行前请<b>清空引擎表</b>（<code>tools/NetBpm.DbMigrate purge</code>）：预言机不做清理，
 * 以免误删你的数据；不清表会让维度②的行数对不上。</p>
 */
public final class OracleRunner {

    private OracleRunner() {
    }

    public static void main(String[] args) throws Exception {
        Arguments arguments = Arguments.parse(args);

        Path corpusRoot = Paths.get(arguments.corpus).toAbsolutePath().normalize();
        List<CorpusCase> cases = CorpusCase.load(corpusRoot);

        System.out.println("语料根目录：" + corpusRoot);
        System.out.println("找到语料：" + cases.size() + " 个");

        TraceEventListener listener = new TraceEventListener();

        ProcessEngineConfigurationImpl configuration = (ProcessEngineConfigurationImpl)
                ProcessEngineConfiguration.createStandaloneInMemProcessEngineConfiguration();

        // ⚠️ 不能链式调用：setJdbcUrl() 等返回的是接口类型 ProcessEngineConfiguration，
        // 后续调用会在接口上解析（实现类独有的 setEnableEventDispatcher 就找不到了）
        configuration.setJdbcUrl(arguments.connection);
        configuration.setJdbcDriver("com.mysql.cj.jdbc.Driver");
        configuration.setJdbcUsername(arguments.user);
        configuration.setJdbcPassword(arguments.password);
        // 表结构由 db/mysql 脚本负责（§7.2），预言机不建表
        configuration.setDatabaseSchemaUpdate(ProcessEngineConfiguration.DB_SCHEMA_UPDATE_FALSE);
        // 历史级别必须与 NetBpm 的默认一致（§7.2：AUDIT）
        configuration.setHistoryLevel(HistoryLevel.AUDIT);
        configuration.setEnableEventDispatcher(true);
        configuration.setEnableDatabaseEventLogging(false);

        // ⚠️ 事件监听器必须在 buildProcessEngine() **之前**用 setEventListeners 注册：
        // 字段 eventListeners 默认是 null（initEventDispatcher 只消费它），
        // 而 build 之后再 getEventListeners() 拿到的仍是那个 null 字段。
        configuration.setEventListeners(List.of(listener));

        ProcessEngine engine = configuration.buildProcessEngine();

        int succeeded = 0;
        List<String> failures = new ArrayList<>();

        try {
            for (CorpusCase corpusCase : cases) {
                if (arguments.only != null && !arguments.only.equals(corpusCase.fullName())) {
                    continue;
                }

                try {
                    runCase(engine, listener, arguments, corpusCase);
                    succeeded++;
                } catch (Exception exception) {
                    // 🔑 与 C# 侧 DifferentialRunner 同口径：一个语料跑不动不能拖垮整批。
                    // 失败原因照实打印（它往往直接指出引擎侧缺什么），且不写 .oracle.json
                    // ⇒ C# 侧看到"没有真值"就不会把这个语料算进一致率。
                    String message = exception.getMessage() == null
                            ? exception.getClass().getSimpleName()
                            : exception.getMessage().lines().findFirst().orElse("");

                    failures.add(corpusCase.fullName() + " ← " + exception.getClass().getSimpleName() + ": " + message);
                    System.out.println("  ✗ " + corpusCase.fullName() + " ← " + message);
                }
            }
        } finally {
            engine.close();
        }

        System.out.println();
        System.out.println("预言机执行完毕：成功 " + succeeded + " / " + cases.size() + "，失败 " + failures.size() + " 个");

        for (String failure : failures) {
            System.out.println("  ✗ " + failure);
        }

        if (!failures.isEmpty()) {
            // 非零退出码让脚本能感知；但真值文件已经生成，C# 侧可以继续比对
            System.exit(1);
        }
    }

    private static void runCase(
            ProcessEngine engine,
            TraceEventListener listener,
            Arguments arguments,
            CorpusCase corpusCase) throws Exception {

        listener.reset();

        // ⚠️ 与 C# 侧同口径：每个语料开跑前清空引擎表。
        // 预言机原本"不清表"是为了避免误删用户数据，但对拍场景下**必须清** ——
        // 否则维度②的快照会累积前面语料的行，与 C# 侧（每个语料 Reset）必然对不上。
        // 清表清单与 C# 侧 EngineTableCleaner.OrderedTables 逐字一致（FK 安全顺序）。
        try (Connection connection = DriverManager.getConnection(
                arguments.connection, arguments.user, arguments.password)) {
            purge(connection);

            Snapshot.OracleSnapshot snapshot = runCase(engine, listener, corpusCase);

            snapshot.tables = TableDumper.dump(connection);

            Path oracleFile = corpusCase.caseFile.resolveSibling(
                    corpusCase.name + ".oracle.json");

            Files.writeString(oracleFile, Contract.MAPPER.writeValueAsString(snapshot) + System.lineSeparator());

            System.out.println("  ✓ " + corpusCase.fullName() + " → " + oracleFile.getFileName());
        }
    }

    private static Snapshot.OracleSnapshot runCase(
            ProcessEngine engine,
            TraceEventListener listener,
            CorpusCase corpusCase) throws Exception {

        RepositoryService repositoryService = engine.getRepositoryService();
        RuntimeService runtimeService = engine.getRuntimeService();
        TaskService taskService = engine.getTaskService();
        HistoryService historyService = engine.getHistoryService();

        String resourceName = corpusCase.resourceName;

        var deployment = repositoryService.createDeployment()
                .name(corpusCase.fullName())
                .addBytes(resourceName, corpusCase.readBpmn().getBytes(StandardCharsets.UTF_8))
                .deploy();

        // ⚠️ 用 list().get(0) 而不是 singleResult()：singleResult() 会设 maxResults=1，
        // 于是 Activiti 9 的 MyBatis 映射会套一层 ROW_NUMBER() OVER (...)——那是 MySQL 8 才有的
        // 窗口函数（本机 MySQL 5.7 直接语法错）。语义等价，只是避开分页包装 SQL。
        List<ProcessDefinition> definitions = repositoryService.createProcessDefinitionQuery()
                .deploymentId(deployment.getId())
                .list();

        if (definitions.isEmpty()) {
            throw new IllegalStateException("部署 " + deployment.getId() + " 没有产生流程定义");
        }

        ProcessDefinition definition = definitions.get(0);

        // businessKey 必须与 C# 侧完全一致（语料标识）
        ProcessInstance instance = runtimeService.startProcessInstanceByKey(
                definition.getKey(),
                corpusCase.fullName(),
                corpusCase.variables);

        String processInstanceId = instance.getId();

        for (CorpusCase.Action action : corpusCase.actions) {
            apply(taskService, processInstanceId, action);
        }

        Snapshot.OracleSnapshot snapshot = new Snapshot.OracleSnapshot();
        snapshot.caseName = corpusCase.fullName();
        snapshot.api = buildApiView(taskService, runtimeService, historyService, corpusCase, processInstanceId);
        snapshot.events = listener.events();

        return snapshot;
    }

    /** 对拍清表清单（与 C# 侧 {@code EngineTableCleaner.OrderedTables} 逐字一致）。 */
    private static final List<String> PURGE_TABLES = List.of(
            "ACT_HI_IDENTITYLINK",
            "ACT_HI_VARINST",
            "ACT_HI_ACTINST",
            "ACT_HI_TASKINST",
            "ACT_HI_PROCINST",
            "ACT_RU_VARIABLE",
            "ACT_RU_IDENTITYLINK",
            "ACT_RU_TASK",
            "ACT_RU_EXECUTION",
            "ACT_RE_PROCDEF",
            "ACT_GE_BYTEARRAY",
            "ACT_RE_DEPLOYMENT");

    private static void purge(Connection connection) throws SQLException {
        try (Statement statement = connection.createStatement()) {
            for (String table : PURGE_TABLES) {
                statement.executeUpdate("delete from " + table);
            }
        }
    }

    private static void apply(TaskService taskService, String processInstanceId, CorpusCase.Action action) {
        switch (action.kind) {
            case "completeTask" -> {
                List<Task> tasks = findTasks(taskService, action);

                if (tasks.isEmpty()) {
                    throw new IllegalStateException(
                            "按 assignee=" + action.assignee + " / taskDefinitionKey=" + action.taskDefinitionKey
                                    + " 找不到待办（语料与流程不匹配）");
                }

                if (tasks.size() > 1) {
                    throw new IllegalStateException(
                            "按 assignee=" + action.assignee + " 查到 " + tasks.size()
                                    + " 个待办，语料有歧义：请用 taskDefinitionKey 指定节点");
                }

                taskService.complete(tasks.get(0).getId(), action.variables);
            }

            case "completeAllTasks" -> {
                // ⚠️ 与 C# 侧同规则：这里**不要求唯一**（查到几个就完成几个）——
                // "必须唯一"的检查只属于 completeTask。并行语料天然会有多个待办同时挂在同一办理人名下。
                List<Task> tasks = findTasks(taskService, action);

                // 排序保证确定性：同一办理人手上多个任务时，完成顺序不能依赖 id（两端 id 不同）
                tasks.sort(Comparator
                        .comparing((Task task) -> task.getTaskDefinitionKey() == null ? "" : task.getTaskDefinitionKey())
                        .thenComparing(Task::getId));

                List<Task> selected = action.limit == null
                        ? tasks
                        : tasks.subList(0, Math.min(action.limit, tasks.size()));

                for (Task task : selected) {
                    taskService.complete(task.getId(), action.variables);
                }
            }

            case "completeAnyTask" -> {
                // 与 C# 侧 DifferentialRunner 完全同规则：
                //   1) 取该实例的**全部**待办（TaskQuery.processInstanceId）；
                //   2) 按 (taskDefinitionKey, assignee) 的序数排序；
                //   3) 一个都没有 ⇒ 脚本到此为止（不算失败）；
                //   4) 最小的 key 有并列 ⇒ 响亮失败（无法确定性挑选）。
                List<Task> open = new ArrayList<>(
                        taskService.createTaskQuery().processInstanceId(processInstanceId).list());

                open.sort(Comparator
                        .comparing((Task task) -> task.getTaskDefinitionKey() == null ? "" : task.getTaskDefinitionKey())
                        .thenComparing(task -> task.getAssignee() == null ? "" : task.getAssignee()));

                if (open.isEmpty()) {
                    return;
                }

                if (open.size() > 1
                        && java.util.Objects.equals(open.get(0).getTaskDefinitionKey(), open.get(1).getTaskDefinitionKey())) {
                    throw new IllegalStateException(
                            "流程实例上有 " + open.size() + " 个同 key（'" + open.get(0).getTaskDefinitionKey()
                                    + "'）的待办，无法确定性地挑选；请改用 completeTask + taskDefinitionKey 明确指定。");
                }

                taskService.complete(open.get(0).getId(), action.variables);
            }

            default -> throw new IllegalStateException("未知的操作脚本动作：" + action.kind);
        }
    }

    private static List<Task> findTasks(TaskService taskService, CorpusCase.Action action) {
        if (action.assignee == null || action.assignee.isBlank()) {
            throw new IllegalStateException("操作脚本必须先声明 assignee（TaskQuery 要求至少一个条件）");
        }

        List<Task> tasks = new ArrayList<>(
                taskService.createTaskQuery().taskAssignee(action.assignee).list());

        if (action.taskDefinitionKey != null && !action.taskDefinitionKey.isBlank()) {
            tasks.removeIf(task -> !action.taskDefinitionKey.equals(task.getTaskDefinitionKey()));
        }

        return tasks;
    }

    /** 维度①：一律走 API（与 C# 侧同口径），不直接读表。 */
    private static Snapshot.ApiView buildApiView(
            TaskService taskService,
            RuntimeService runtimeService,
            HistoryService historyService,
            CorpusCase corpusCase,
            String processInstanceId) {

        Snapshot.ApiView view = new Snapshot.ApiView();

        // ⚠️ "是否已结束"用**运行时**查询判定，而不是历史实例查询：
        // Activiti 9 的 HistoricProcessInstance.xml 把 ROW_NUMBER() OVER (...) 硬编码在映射里
        // （不受方言 properties 影响），MySQL 5.7 没有窗口函数，走那条路必然语法错。
        // 语义等价：运行时查不到该实例 ⇔ 实例已结束（与 C# 侧 HistoricProcessInstance.IsRunning 同口径）。
        boolean isEnded = runtimeService.createProcessInstanceQuery()
                .processInstanceId(processInstanceId)
                .count() == 0;

        Snapshot.ApiProcessInstance instanceView = new Snapshot.ApiProcessInstance();
        instanceView.businessKey = corpusCase.fullName();
        instanceView.isEnded = isEnded;
        instanceView.unfinishedActivityIds = historyService.createHistoricActivityInstanceQuery()
                .processInstanceId(processInstanceId)
                .unfinished()
                .list()
                .stream()
                .map(activity -> activity.getActivityId())
                .distinct()
                .sorted()
                .toList();

        view.processInstances.add(instanceView);

        // 待办：该实例当前的**全部**待办（对应 C# 侧的 TaskQuery.processInstanceId）——
        // 比"逐个办理人查"更完整，且两端同一条 API 语义。
        // ⚠️ 必须排序：并行分支下待办多于一个时，DB 的返回顺序不可依赖，不排就会出现假差异。
        List<Snapshot.ApiTask> tasks = new ArrayList<>();

        taskService.createTaskQuery().processInstanceId(processInstanceId).list()
                .forEach(task -> tasks.add(new Snapshot.ApiTask(task.getTaskDefinitionKey(), task.getAssignee())));

        tasks.sort(Comparator
                .comparing((Snapshot.ApiTask task) -> task.taskDefinitionKey == null ? "" : task.taskDefinitionKey)
                .thenComparing(task -> task.assignee == null ? "" : task.assignee));

        view.tasks = tasks;

        // 变量：实例结束后运行时变量已清除，查询会抛异常 —— 那正是"已结束"的语义
        if (!isEnded) {
            Map<String, Object> variables = runtimeService.getVariables(processInstanceId);

            List<String> names = new ArrayList<>(variables.keySet());
            names.sort(Comparator.naturalOrder());

            for (String name : names) {
                Object value = variables.get(name);

                view.variables.add(new Snapshot.ApiVariable(
                        name,
                        typeName(value),
                        Contract.normalizeValue(value)));
            }
        }

        return view;
    }

    /** 变量类型名（与 <code>ACT_RU_VARIABLE.TYPE_</code> 同口径）。 */
    private static String typeName(Object value) {
        if (value == null) {
            return "null";
        }

        if (value instanceof String) {
            return "string";
        }

        if (value instanceof Integer) {
            return "integer";
        }

        if (value instanceof Long) {
            return "long";
        }

        if (value instanceof Double) {
            return "double";
        }

        if (value instanceof Boolean) {
            return "boolean";
        }

        if (value instanceof java.util.Date) {
            return "date";
        }

        return value.getClass().getSimpleName();
    }

    private static final class Arguments {
        private String corpus = "tests/corpus";
        private String connection = System.getenv().getOrDefault("NETBPM_ORACLE_JDBC", "");
        private String user = "root";
        private String password = "";
        private String only;

        private static Arguments parse(String[] args) {
            Arguments parsed = new Arguments();

            for (int index = 0; index < args.length; index++) {
                switch (args[index]) {
                    case "--corpus" -> parsed.corpus = args[++index];
                    case "--connection" -> parsed.connection = args[++index];
                    case "--user" -> parsed.user = args[++index];
                    case "--password" -> parsed.password = args[++index];
                    case "--only" -> parsed.only = args[++index];
                    default -> throw new IllegalArgumentException("未知参数：" + args[index]);
                }
            }

            if (parsed.connection.isBlank()) {
                throw new IllegalArgumentException(
                        "必须提供 --connection（或用环境变量 NETBPM_ORACLE_JDBC），"
                                + "例如 jdbc:mysql://127.0.0.1:3306/netbpm?useUnicode=true&characterEncoding=utf8");
            }

            return parsed;
        }
    }
}

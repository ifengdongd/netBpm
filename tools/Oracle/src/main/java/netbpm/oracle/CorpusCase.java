package netbpm.oracle;

import com.fasterxml.jackson.databind.JsonNode;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.stream.Stream;

/**
 * 语料（与 C# 的 {@code CorpusCase} / {@code CorpusEntry} 对齐）。
 *
 * <p>目录约定：{@code tests/corpus/<分组>/<stem>.bpmn20.xml} + 同名 {@code .case.json}；
 * 语料标识 = {@code <分组>/<stem>}，同时也是<b>业务主键</b>（两端必须一致，否则实例对不上）。</p>
 */
public final class CorpusCase {

    public String name;
    public String group;
    public String description;
    public Map<String, Object> variables = new LinkedHashMap<>();
    public List<Action> actions = new ArrayList<>();
    public String resourceName = "case.bpmn20.xml";

    /** 文件位置与内容。 */
    public transient Path caseFile;
    public transient Path bpmnFile;

    public static final class Action {
        public String kind;
        public String assignee;
        public String taskDefinitionKey;
        public Map<String, Object> variables = new LinkedHashMap<>();
        public Integer limit;
    }

    /**
     * 语料标识（= businessKey，两端必须逐字一致）。
     *
     * <p>分组由<b>相对语料根的目录</b>推出（与 C# 侧同规则），而不是读 case.json 里的 group 字段
     * —— 目录才是唯一的事实来源，避免两处不一致时对拍悄悄错位。</p>
     */
    public String fullName() {
        return group.isEmpty() ? name : group + "/" + name;
    }

    public String readBpmn() throws IOException {
        return Files.readString(bpmnFile, StandardCharsets.UTF_8);
    }

    /**
     * 扫描语料根目录。
     *
     * <p>排序按语料标识的序数比较，保证两端处理顺序一致（差异清单的可读性依赖它）。</p>
     */
    public static List<CorpusCase> load(Path corpusRoot) throws IOException {
        List<CorpusCase> cases = new ArrayList<>();

        try (Stream<Path> files = Files.walk(corpusRoot)) {
            List<Path> caseFiles = files
                    .filter(path -> path.getFileName().toString().endsWith(".case.json"))
                    .sorted()
                    .toList();

            for (Path caseFile : caseFiles) {
                String fileName = caseFile.getFileName().toString();
                String stem = fileName.substring(0, fileName.length() - ".case.json".length());
                Path bpmnFile = caseFile.resolveSibling(stem + ".bpmn20.xml");

                if (!Files.exists(bpmnFile)) {
                    throw new IllegalStateException("语料 " + caseFile + " 缺少同名 BPMN 文件：" + bpmnFile);
                }

                JsonNode node = Contract.MAPPER.readTree(Files.readString(caseFile, StandardCharsets.UTF_8));
                CorpusCase parsed = parse(node, stem);

                parsed.caseFile = caseFile;
                parsed.bpmnFile = bpmnFile;
                parsed.group = corpusRoot.relativize(caseFile.getParent()).toString().replace('\\', '/');

                cases.add(parsed);
            }
        }

        cases.sort(Comparator.comparing(CorpusCase::fullName));

        return cases;
    }

    private static CorpusCase parse(JsonNode node, String stem) {
        CorpusCase parsed = new CorpusCase();

        parsed.name = node.path("name").asText(stem);
        parsed.description = node.path("description").asText("");
        parsed.resourceName = node.path("resourceName").asText(stem + ".bpmn20.xml");

        if (node.hasNonNull("variables")) {
            parsed.variables = Contract.MAPPER.convertValue(
                    node.get("variables"),
                    Contract.MAPPER.getTypeFactory().constructMapType(LinkedHashMap.class, String.class, Object.class));
        }

        for (JsonNode actionNode : node.path("actions")) {
            Action action = new Action();

            action.kind = actionNode.path("kind").asText("");
            action.assignee = actionNode.hasNonNull("assignee") ? actionNode.get("assignee").asText() : null;
            action.taskDefinitionKey = actionNode.hasNonNull("taskDefinitionKey")
                    ? actionNode.get("taskDefinitionKey").asText()
                    : null;
            action.limit = actionNode.hasNonNull("limit") ? actionNode.get("limit").asInt() : null;

            if (actionNode.hasNonNull("variables")) {
                action.variables = Contract.MAPPER.convertValue(
                        actionNode.get("variables"),
                        Contract.MAPPER.getTypeFactory().constructMapType(LinkedHashMap.class, String.class, Object.class));
            }

            parsed.actions.add(action);
        }

        return parsed;
    }
}

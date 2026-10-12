package netbpm.oracle;

import com.fasterxml.jackson.annotation.JsonInclude;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;

import java.math.BigDecimal;
import java.sql.Timestamp;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import java.util.Map;

/**
 * 与 C# 侧共用的三维度契约（开发计划 §9.2 / P10）。
 *
 * <p>🔒 这里有四处"必须逐字一致"的约定，任何一处走样都会造成大面积假差异：</p>
 * <ol>
 *   <li>JSON 字段命名 camelCase、<b>null 字段省略</b>（对应 C# 的 {@code DefaultIgnoreCondition = WhenWritingNull}）；</li>
 *   <li>列值的规范化（{@link #normalizeValue}）—— 见 C# 的 {@code DifferentialEngine.StringValueOf}；</li>
 *   <li>行快照的<b>规范排序</b>（{@link #canonicalRows}）—— 见 C# 的 {@code RowOrdering}；</li>
 *   <li>语料标识 = businessKey = {@code <分组>/<名>}（如 {@code authored/gateway/parallel-fork-join-2}）。</li>
 * </ol>
 */
public final class Contract {

    private Contract() {
    }

    public static final ObjectMapper MAPPER = new ObjectMapper()
            .setSerializationInclusion(JsonInclude.Include.NON_NULL)
            .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false)
            .configure(SerializationFeature.INDENT_OUTPUT, true);

    /**
     * C# 侧忽略的列，Java 侧必须用**同一集**做规范排序（两边不一致就会出现假差异）。
     *
     * <p>三类：① 时间戳与由它派生的 {@code DURATION_}；② ID 具体值（Activiti 用 UUID、
     * NetBpm 用计数器，必然不同 —— §9.4 明确允许这一类）；③ {@code ENGINE_VERSION_}（版本串必然不同）。</p>
     */
    public static final List<String> IGNORED_COLUMNS = List.of(
            "START_TIME_", "END_TIME_", "DUE_DATE_", "CREATE_TIME_", "CLAIM_TIME_",
            "LAST_UPDATED_TIME_", "LOCK_TIME_", "DEPLOY_TIME_", "DURATION_",
            "ENGINE_VERSION_",
            "ID_", "PROC_DEF_ID_", "PROC_INST_ID_", "EXECUTION_ID_", "TASK_ID_",
            "DEPLOYMENT_ID_", "BYTEARRAY_ID_", "PARENT_ID_", "SUPER_EXEC_",
            // 已登记的有意偏离：Activiti 存原始 XML 字节，NetBpm 存重新序列化的 XML（内容保真但非字节一致）
            "BYTES_");

    /** 参与维度②比对的表（与 C# 的 {@code DifferentialEngine.SnapshotTables} 一致）。 */
    public static final List<String> SNAPSHOT_TABLES = List.of(
            "ACT_RU_EXECUTION", "ACT_RU_TASK", "ACT_RU_IDENTITYLINK", "ACT_RU_VARIABLE",
            "ACT_HI_PROCINST", "ACT_HI_ACTINST", "ACT_HI_TASKINST", "ACT_HI_VARINST",
            "ACT_HI_IDENTITYLINK", "ACT_RE_PROCDEF", "ACT_RE_DEPLOYMENT", "ACT_GE_BYTEARRAY");

    /**
     * 列值规范化：null → null；布尔 → {@code "1"}/{@code "0"}；数值 → 十进制字符串；
     * 时间 → {@code yyyy-MM-dd HH:mm:ss.fff}；二进制 → Base64；其余 → {@code String.valueOf}。
     */
    public static String normalizeValue(Object value) {
        if (value == null) {
            return null;
        }

        if (value instanceof Boolean bool) {
            return bool ? "1" : "0";
        }

        if (value instanceof byte[] bytes) {
            return java.util.Base64.getEncoder().encodeToString(bytes);
        }

        if (value instanceof Timestamp timestamp) {
            return timestamp.toLocalDateTime().format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSS"));
        }

        if (value instanceof LocalDateTime local) {
            return local.format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSS"));
        }

        if (value instanceof java.util.Date date) {
            return new Timestamp(date.getTime()).toLocalDateTime()
                    .format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSS"));
        }

        if (value instanceof BigDecimal decimal) {
            return decimal.stripTrailingZeros().toPlainString();
        }

        if (value instanceof Double || value instanceof Float) {
            return new BigDecimal(value.toString()).stripTrailingZeros().toPlainString();
        }

        if (value instanceof Number number) {
            return number.toString();
        }

        return String.valueOf(value);
    }

    /**
     * 行快照的规范排序（🔒 与 C# {@code RowOrdering.CanonicalKey} 同规则）：
     * 对每一行按"忽略列之外"的列名升序拼接 {@code 列名=值}（null 写作空串），用序数比较。
     */
    public static List<Map<String, String>> canonicalRows(Collection<Map<String, String>> rows) {
        List<Map<String, String>> sorted = new ArrayList<>(rows);

        sorted.sort((left, right) -> {
            String leftKey = canonicalKey(left);
            String rightKey = canonicalKey(right);

            return leftKey.compareTo(rightKey);
        });

        return sorted;
    }

    private static String canonicalKey(Map<String, String> row) {
        List<String> columns = new ArrayList<>(row.keySet());
        columns.sort(String::compareTo);

        StringBuilder builder = new StringBuilder();

        for (String column : columns) {
            if (IGNORED_COLUMNS.contains(column)) {
                continue;
            }

            String value = row.get(column);

            builder.append(column).append('=').append(value == null ? "" : value).append('\u001f');
        }

        return builder.toString();
    }
}

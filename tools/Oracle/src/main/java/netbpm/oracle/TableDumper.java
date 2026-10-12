package netbpm.oracle;

import java.sql.Connection;
import java.sql.ResultSet;
import java.sql.ResultSetMetaData;
import java.sql.SQLException;
import java.sql.Statement;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * 维度②「{@code ACT_*} 行快照」的 dump（开发计划 §9.2 的"杀手锏"维度）。
 *
 * <p>与 C# 侧 {@code DifferentialEngine.ReadTables} 同规则：同样的表清单、同样的值规范化、
 * 同样的规范排序。列顺序取 {@code select *} 的返回顺序（两端 DDL 相同，见 §7.1）。</p>
 */
public final class TableDumper {

    private TableDumper() {
    }

    /**
     * 读 <b>正在执行中</b> 的行快照。
     *
     * <p>⚠️ 必须在语料执行完、且 Activiti 的事务已经提交之后再调用（本工具的调用点在
     * {@code engine.getTaskService().complete(...)} 返回之后），否则会看到未提交状态。</p>
     */
    public static Map<String, List<Map<String, String>>> dump(Connection connection) throws SQLException {
        Map<String, List<Map<String, String>>> tables = new LinkedHashMap<>();

        for (String table : Contract.SNAPSHOT_TABLES) {
            tables.put(table, Contract.canonicalRows(readTable(connection, table)));
        }

        return tables;
    }

    private static List<Map<String, String>> readTable(Connection connection, String table) throws SQLException {
        List<Map<String, String>> rows = new ArrayList<>();

        try (Statement statement = connection.createStatement();
             ResultSet resultSet = statement.executeQuery("select * from " + table)) {

            ResultSetMetaData metaData = resultSet.getMetaData();
            int columnCount = metaData.getColumnCount();

            while (resultSet.next()) {
                Map<String, String> row = new LinkedHashMap<>();

                for (int index = 1; index <= columnCount; index++) {
                    row.put(metaData.getColumnLabel(index), Contract.normalizeValue(resultSet.getObject(index)));
                }

                rows.add(row);
            }
        }

        return rows;
    }
}

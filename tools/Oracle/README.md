# NetBpm Oracle（Java 预言机）

开发计划 **§9.2 / P10** 的"真值来源"：用**真 Activiti** 跑同一批语料，产出三维度快照。
对拍执行器（C# 侧 `NetBpm.DifferentialTests`）读同一份 JSON 做比对 —— **一致率 ≥99.5%、差异清单 100% 有结论** 才算达标。

本工具**不参与 .NET 构建**（它不是 `NetBpm.slnx` 的一部分）。

---

## 一、前置条件

| 项 | 要求 |
|---|---|
| JDK | 17+ |
| Maven | 3.8+ |
| MySQL | 本地可达，且**已执行 `db/mysql` 的建表脚本**（表结构见 §7.1/§7.2；预言机不建表） |
| Activiti | `org.activiti:activiti-engine:9.0.0` 可解析 |

Activiti 9.0.0 **未必在 Maven 中央仓库**。本机已有源码时，先装进本地仓库：

```powershell
mvn -f E:\DataCode\Activiti-9.0.0\pom.xml install -DskipTests -pl activiti-core/activiti-engine -am
```

若你的构件坐标不同，改 `pom.xml` 里的 `<activiti.version>`（或 groupId/artifactId）。

---

## 二、跑一遍

```powershell
# 1) 清表：预言机自己不清表（避免误删数据），但不清会让维度②的行数对不上
dotnet run --project tools\NetBpm.DbMigrate -- purge --provider mysql `
  --connection "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=;"

# 2) 生成真值（写在每个语料旁的 <stem>.oracle.json）
mvn -f tools\Oracle\pom.xml compile exec:java `
  -Dexec.args="--corpus tests/corpus --connection jdbc:mysql://127.0.0.1:3306/netbpm?useUnicode=true&characterEncoding=utf8 --user root --password ''"

# 3) 对拍（读上面的真值，输出 artifacts/differential/report.md|json）
dotnet test tests\NetBpm.DifferentialTests\NetBpm.DifferentialTests.csproj
```

只跑单个语料（调试用）：加 `--only authored/gateway/parallel-fork-join-2`。

---

## 三、🔒 契约中"必须逐字一致"的四处

任一处走样都会造成**大面积假差异**，因此它们被两侧各写一遍并互相盯着：

| # | 约定 | C# 侧 | Java 侧 |
|---|---|---|---|
| 1 | 语料标识 = businessKey = `<分组>/<名>`（分组由**相对语料根的目录**推出，如 `authored/gateway/parallel-fork-join-2`） | `CorpusEntry.FullName` | `CorpusCase.fullName()` |
| 2 | 列值规范化：null→`null`；布尔→`1`/`0`；数值→十进制；时间→`yyyy-MM-dd HH:mm:ss.fff`；二进制→Base64 | `DifferentialEngine.StringValueOf` | `Contract.normalizeValue` |
| 3 | 行快照**规范排序**：按忽略列之外的列名升序拼 `列名=值`（null 写作空串），序数比较 | `RowOrdering` | `Contract.canonicalRows` |
| 4 | JSON 命名 camelCase、**null 字段省略** | `OracleSnapshotJson.Options` | `Contract.MAPPER` |

参与比对的 12 张表与 9 个忽略列（时间戳 + `ENGINE_VERSION_`）同样两侧一致。

---

## 四、⚠️ 尚未校准的一项（首次跑批必做）

维度③「事件序列」的**触发时机与字段填充**还没有经过真机跑批校准。已知两个待确认点：

1. `SEQUENCEFLOW_TAKEN` 的 `activityId` 在 Activiti 事件里指向**流本身**还是**目标节点**？
   C# 侧把流 id 放在 `flowId` 字段。若不一致，会表现为该事件的 `flowId` 与 `activityId` 两列差异。
2. `TASK_COMPLETED` 在 C# 侧拿不到 `taskDefinitionKey`（历史端口只给 taskId），事件比对规则是
   "只有两侧都填了的字段才比"，因此该字段的差异会自动被忽略。

**处理流程**：首次 `report.md` 出来后，逐条确认差异属于"口径差异"还是"真实缺陷"；
口径差异要**回写两侧代码**（而不是登记成台账），真实缺陷按 §9.4 修复。
台账（`tests/corpus/known-issues.json`）只用于**确实无法消除**的差异（例如 ID 分配时序不同）。

---

## 四点五、什么时候必须重跑真值（真值是一次性产物）

真值**不是**流水线的产物：它需要 JDK 21 + 本地 install 的 Activiti 9.0.0
（`mvn install` 进本地仓库，中央仓库未必有 9.0.0），因此只能由维护者在本地生成后**随语料一起提交**。
代价是真值会过期，而过期之后的差异全部是假的。

以下任一件事发生，**必须先重跑真值再谈对拍结论**：

| 触发 | 为什么 |
|---|---|
| 语料被改（BPMN / `case.json`，含生成器重新 `--check` 后的重生成） | 比对的前提是两端跑同一个语料；语料换了，旧真值就是另一道题的答案 |
| 生成器（`tests/corpus/generator`）的模板/组合规则变了 | 同上：语料会被重新生成，内容变了 |
| 预言机侧改了（`tools/Oracle/src`、Activiti 版本） | 真值的口径变了，旧真值与新真值不可混用 |
| 引擎的表结构变了（`db/mysql` 增删列） | 维度②比的是列；多一列/少一列会让整批行的比对失去意义 |

**自动检查**：`NetBpm.DifferentialTests.OracleFreshnessTests` 比对「语料的最后提交时间」与
「真值的最后提交时间」，语料更晚即失败（列出可疑语料）。CI 的 `differential` job 在跑批**之前**执行它。

⚠️ 两处局限要记住：
1. 它读的是 **git 提交时间**而不是文件 mtime —— git 不保留 mtime，用 mtime 会在本地恒红、在 CI 恒绿，两头都没意义；
2. 因此需要**完整历史**，浅克隆（`--depth 1`）里所有文件的提交时间相同，检查会静默通过
   （CI 的该 job 已显式 `fetch-depth: 0`）。

---

## 五、与 C# 侧的分工

```
tests/corpus/**/<stem>.case.json + .bpmn20.xml      ← 输入（同一份，脚本生成，见 tests/corpus/generator）
        │
        ├──► Java 预言机 ──► <stem>.oracle.json      ← 真值
        │
        └──► C# 对拍执行器 ──► artifacts/differential/<case>.actual.json
                              │
                              ▼
                        三维度比对 ──► report.md / report.json ──► §9.4 门禁
```

# contracts/ —— 对外契约的「不可静默变更」区

这个目录放的是**对外承诺的产物**，不是生成缓存的副本。里面的文件被改动，
就等于平台对外宣布「接口变了」，因此它们必须和代码一起被评审、一起提交。

```
contracts/
└── openapi/
    └── workflow-v1.json     # /workflow/v1 业务面的 OpenAPI 3.x 快照
```

## 一、为什么要有一份快照

`/workflow/v1` 一旦接入，对接方就按它写死了反序列化、分支与校验。
改一个字段名、把一个可选字段改成必填，在代码评审里都是「一行小改动」，
上线后却是对接方半夜打电话。文本 diff 抓不住这件事——生成器本来就会调键序、补描述，
每天几十行噪声，看两次之后就没人看了。

所以这里存的不是「当前长什么样」，而是**「已经承诺过什么样」**。
守卫它的用例是 `tests/NetBpm.IntegrationTests/ContractSnapshotTests.cs`：
当前实现生成的文档与这份快照之间只要有差异，CI 就红，并把差异按
**破坏性 / 兼容**两类逐条列出来，评审时看到的是一句人话，而不是几百行 JSON。

## 二、覆盖范围

| 收 | 不收 |
|---|---|
| `/workflow/v1/**`（业务面，22 条路径） | `/api/**`（引擎原语直通面，标了 experimental，随引擎演进） |
| `components.schemas`（被业务面引用的模型） | `/admin/v1/**`（过渡形态，写路径尚未定型） |
| `security` / `info`（固定 `title` + `version: v1`） | 程序集版本号、`servers`（每次构建都变，属于噪声） |

归一化规则写在 `ContractSnapshotTests.Normalize`：只留治理前缀的路径，
所有对象**递归按键名排序**（数组保持原序，因为 `parameters` 的顺序有语义），
落盘统一 **CRLF、无 BOM**（`.editorconfig`：`end_of_line = crlf`）。

## 三、怎么更新快照

确认变更是**有意的、可接受的**之后：

```bash
NETBPM_UPDATE_CONTRACTS=1 dotnet run --project tests/NetBpm.IntegrationTests \
  -- -class NetBpm.IntegrationTests.ContractSnapshotTests
```

快照就地重写。把它和代码放进同一个提交，commit message 里写清「哪个契约变了、为什么」。

> ⚠️ **不要为了方便刷新快照。** 顺手 `NETBPM_UPDATE_CONTRACTS=1` 让流水线变绿，
> 等于把「契约治理」降级成「自动接受一切」——那这份快照就只剩装饰作用。
> 刷新之前先读一遍 CI 打印的差异清单：如果里面出现了「破坏性」，走下一节的流程。

## 四、破坏性 vs 兼容

判定口径与 `docs/platform-api-refactoring-plan.md` §7.2 的兼容策略一致，
实现在 `tests/NetBpm.IntegrationTests/ContractDiff.cs`。

### 兼容（只需更新快照 + 说明）

- 新增路径 / 新增 HTTP 方法
- 新增**可选**请求参数、新增**可选**请求字段
- 新增响应码、新增响应字段
- 新增枚举取值、新增组件（在未被引用前）
- 请求体由必填变可选

前提是老调用方不受影响：客户端必须容忍未知字段、容忍未知枚举值——
这条写进了 §7.2，也是 SDK 生成器的默认行为（`JsonExtensionData` / 未知值兜底）。

### 破坏性（先过流程，再改快照）

- 删除路径 / 方法 / 参数 / 响应码
- 删除响应字段（读者会拿到 `null`）、删除请求字段（还在传的调用方会被拒）
- 参数或字段**由可选变必填**、请求体由可选变必填
- 类型改变（`string` → `object`、`integer` → `string` …）
- `$ref` 换了（结构可能整体不同）
- 枚举取值减少（调用方按它写的分支会失配）

遇到破坏性变更，按 §7.2 走：

1. **不改 v1，加 v2 路径**（首选）。`/workflow/v2/...` 与 v1 并行一段时间，
   v1 标记 `Deprecated` 并给出下线日期；两边共用同一批服务，只是 DTO 与适配层分开。
2. **确实必须改 v1 时**，走主版本升级 + 对接方公告 + 迁移窗口，并在
   `docs/platform-api-refactoring-plan.md` 的变更记录里留下原因与时间点。
3. **不能做的**：直接改 v1 并刷新快照。这条是硬约束，CI 的红灯就是为它设的。

## 五、谁在守它

| 位置 | 作用 |
|---|---|
| `ContractSnapshotTests.Contract_ShouldMatchCommittedSnapshot` | 快照与实现的一致性；差异按严重程度分类输出 |
| `ContractSnapshotTests.Snapshot_ShouldCoverBusinessSurface` | 快照必须真的守着东西：业务面 ≥ 20 条路径、每条都在快照里、且不含 `/api/**`（防止过滤逻辑放宽后快照悄悄「空心化」） |
| `.github/workflows/ci.yml` 的 `contract-governance` job | 上面两项在每次 push / PR 上跑；单独成 job，红了能一眼看出是「契约」而不是「编译」 |
| `ContractDiff.cs` | 差异的严重程度判定（纯函数，可单测） |

## 六、与后续工作的关系

- **M15 官方 SDK** 依赖这份快照：SDK 的模型代码从这里生成，
  兼容性才有唯一依据（否则「上一版 SDK 打新服务」这条验收没有判据）。
- `CompatibilityTests`（§9 契约层，尚未落地）会拿**上一版生成的 SDK** 打**当前服务**，
  它是这份快照的运行时补充：快照管「声明变了没」，它管「声明没变但行为变了没」。

# CI 在哪里跑、跑什么、红了怎么读

## 0. 一句话

流水线定义只有 **GitHub Actions**（`.github/workflows/ci.yml`）；主远端是 **Gitee**，Gitee 上没有
`.workflow/` 目录，因此**没有任何服务会自动跑这些 Job**。要看到红绿灯，必须把仓库镜像到 GitHub。

## 1. 四个 Job

| Job | 跑什么 | 需要什么 |
|---|---|---|
| `frontend` | `web/designer` 构建；`web/admin` lint + typecheck + 单测 + 构建；产物 `wwwroot` 作为 artifact | Node 20 |
| `build-and-unit-tests` | `dotnet build NetBpm.slnx` + `NetBpm.UnitTests` | .NET 10 |
| `contract-governance` | 契约快照 diff、基线兼容、SDK 产物一致；**再编译**生成的 TS / Java SDK | .NET 10 + Node 20 + JDK 21（`setup-java` temurin） |
| `integration-tests` | `DbMigrate` 建 MySQL 库 → `NetBpm.IntegrationTests` | MySQL 8.4 service container；SqlServer 由测试用 Testcontainers 拉起（runner 自带 Docker） |

触发器是 `push: branches: ["*"]` 与 `pull_request` —— 镜像推上去就会跑，不需要改 workflow。

## 2. 镜像到 GitHub

前置（需要仓库属主的账号，脚本不代劳）：

1. GitHub 上建**空仓库**（不要勾 README / .gitignore / license，否则首次 push 会冲突）；
2. 本机有凭据：HTTPS 走 `git config --global credential.helper manager`，或把 `~/.ssh/id_rsa.pub` 加到账号。

然后：

```powershell
./tools/ci/mirror-to-github.ps1 -RepositoryUrl https://github.com/<owner>/<repo>.git
```

脚本是**幂等且不破坏性**的：不 `--force`、不用 `--mirror`（那会删掉 GitHub 侧多出来的分支）；
remote 已存在但地址不同时直接报错，不擅自改指向。

推完去仓库 **Settings → Actions → General** 确认 workflow 未被禁用，Actions 页面应已出现一次 `ci` 运行。

## 3. 红了怎么读

- **契约 Job 红**：`ContractSnapshotTests` 是快照 diff，`CompatibilityTests` 是「上一版基线还被满足」。
  确需变更契约时，本地 `NETBPM_UPDATE_CONTRACTS=1` 重跑 `ContractSnapshotTests`，把
  `contracts/openapi/workflow-v1.json` 的改动当代码一起评审提交（破坏性变更先走
  `docs/platform-api-refactoring-plan.md` §7.2 的公告流程）。**不要**靠改基线放行。
- **SDK 编译红**：生成器是字符串模板，`SdkGenerationTests` 只证明「与快照一致」，证明不了「能编译」。
  TS 子步骤红 → 看 `sdk/typescript` 生成的类型；Java 子步骤红 → 看 `sdk/java`。
- **集成测试红**：先看是不是**环境**（MySQL service 没起来 → 连接失败；runner 的 Docker 不可用 →
  SqlServer 用例会动态跳过而不是失败，跳过数变多是正常的）。数据库不可用时测试**动态跳过**，
  所以「跳过很多」不等于绿，要对照跳过数。
- **只有 `sqlserver` 变体的用例红**：先别怀疑产品代码。CI 容器的默认 collation 是
  `SQL_Latin1_General_CP1_CI_AS`，而中文版 SQL Server 本机通常是 `Chinese_PRC_CI_AS` ——
  两者对「SQL 里的中文」处理不同：**无 `N` 前缀的 `'中文'` 字面量在 Latin1 下按 varchar 转成 `?`**，
  于是 `like '%中文%'` 永远匹配不到；本机代码页 936 恰好能表示中文，所以本地永远绿。
  规则：测试里的断言不要靠 SQL 匹配中文，把值读回来在 C# 里 `Contains`。
  本地复现只需换个 collation：`create database x collate SQL_Latin1_General_CP1_CI_AS`，
  再把 `NETBPM_SQLSERVER_CONNECTION` 指过去（本机装了 SQL Server 时比等 CI 快得多）。
- **`Create schema` 步红（2 秒内就退出）**：几乎都不是迁移脚本的问题，而是**连不上库**。
  官方 `mysql` 镜像默认只建 `root@localhost`，而 service 容器把端口映射到宿主机后，
  连接来源是 docker 网关地址（`172.x.x.x`）——不是 `localhost`，于是 `Access denied`。
  所以 `MYSQL_ROOT_HOST: "%"` 与固定的 `MYSQL_ROOT_PASSWORD` **缺一不可**；
  health check 也必须带 `-u/-p`：`mysqladmin ping` 不带凭据时只要端口在监听就返回成功，
  会出现「service 显示 healthy、一连就 Access denied」的假就绪。本地直连 localhost 复现不了这个。
- **单测红**：与本地一致；注意已知的跨测试并发抖动（`OutboxBacklogMetricsTests` 的 MeterListener
  静态状态、`WorkflowServiceTests` 与 `EngineFacadeTests`），单类复跑若绿则大概率是抖动，别急着改代码。
- **三个 .NET Job 全挂在 `Build` 步，日志里是 `NU1902` / `NU1903`**：这不是编译错误，是 **NuGet 审计**
  把「依赖树里有已知漏洞的包」报成还原警告，`TreatWarningsAsErrors=true` 再把它升级成 error。
  最容易踩的地方是：CI 每次全新还原都会拉最新漏洞库，而**本地因为漏洞库走缓存往往一直看不到**，
  于是「本地绿、CI 红」，还很容易误判成代码问题。定位只需在本地做一次等价还原：
  `dotnet restore NetBpm.slnx --no-cache --force`（要更彻底就加 `--packages <全新目录>` 强制重下）。
  修法是**提升版本**，不要用 `NoWarn` 盖掉：
  直接依赖 → 在 `Directory.Packages.props` 升版本；
  传递依赖 → 加一条 `PackageVersion` 即可（`CentralPackageTransitivePinningEnabled=true` 会把它提到该版本，
  不用在被引用的工程里加 `PackageReference`）。

## 4. 为什么不做 Gitee Go

Gitee 的流水线要在 `.workflow/` 下写自己的 YAML 方言（触发器、stage/step 结构与 GitHub Actions 不同），
等于维护两套等价流水线。当前选择是「一份定义 + 镜像执行」，代价是需要手动或定时把提交推到 GitHub。

如果以后要自动化同步，两种做法：GitHub 侧加定时拉取（pull mirror），或 Gitee 侧加一个 push 后
`git push github` 的流水线 —— 后者又回到了双份配置，所以优先推荐前者。

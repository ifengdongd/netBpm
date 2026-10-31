using System.Collections.Generic;

namespace NetBpm.DbInit.Schema;

/// <summary>
/// 数据库脚本的<b>单一数据源</b>。
/// <para>
/// 表 / 列 / 索引 / 外键 / 注释 / 种子数据只在这里定义一次；
/// <see cref="SqlGenerator"/> 据此分别渲染出 <c>db/mysql</c> 与 <c>db/sqlserver</c> 两套脚本。
/// 两份方言结构严格一一对应，差异仅为方言语法（类型名 / 注释写法 / 批分隔 / 幂等守卫）。
/// </para>
/// </summary>
internal static class SchemaCatalog
{
    /// <summary>种子数据里「当前时间」占位符（渲染为 utc_timestamp() / getutcdate()）。
    /// 必须声明在引用它的种子字段之前，避免静态初始化顺序导致引用到 null。</summary>
    internal static readonly object Now = new();

    // ────────────────────────────────────────────────────────────────────────
    //  引擎表（0001，13 张 ACT_*；与 Activiti 逐字一致）
    // ────────────────────────────────────────────────────────────────────────

    internal static readonly IReadOnlyList<TableDef> EngineTables =
    [
        new TableDef(
            "ACT_GE_PROPERTY",
            "通用属性表：存放 schema 版本、schema 历史与 ID 区块游标等全局键值对",
            [C("NAME_", ColType.Varchar, 64, false), C("VALUE_", ColType.Varchar, 300), C("REV_", ColType.Int)],
            ["NAME_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_GE_BYTEARRAY",
            "二进制数据表：存放部署的流程定义资源（BPMN XML）等字节内容",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("REV_", ColType.Int),
                C("NAME_", ColType.Varchar, 255),
                C("DEPLOYMENT_ID_", ColType.Varchar, 64),
                C("BYTES_", ColType.Blob),
                C("GENERATED_", ColType.TinyInt),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_RE_DEPLOYMENT",
            "部署信息表：每次流程部署产生一条记录",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("NAME_", ColType.Varchar, 255),
                C("CATEGORY_", ColType.Varchar, 255),
                C("KEY_", ColType.Varchar, 255),
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
                C("DEPLOY_TIME_", ColType.Timestamp),
                C("ENGINE_VERSION_", ColType.Varchar, 255),
                C("VERSION_", ColType.Int, def: "1"),
                C("PROJECT_RELEASE_VERSION_", ColType.Varchar, 255),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_RE_PROCDEF",
            "流程定义表：部署解析后的流程定义（KEY_ + VERSION_ + TENANT_ID_ 唯一）",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("REV_", ColType.Int),
                C("CATEGORY_", ColType.Varchar, 255),
                C("NAME_", ColType.Varchar, 255),
                C("KEY_", ColType.Varchar, 255, false),
                C("VERSION_", ColType.Int, nullable: false),
                C("DEPLOYMENT_ID_", ColType.Varchar, 64),
                C("RESOURCE_NAME_", ColType.Varchar, 4000),
                C("DGRM_RESOURCE_NAME_", ColType.Varchar, 4000),
                C("DESCRIPTION_", ColType.Varchar, 4000),
                C("HAS_START_FORM_KEY_", ColType.TinyInt),
                C("HAS_GRAPHICAL_NOTATION_", ColType.TinyInt),
                C("SUSPENSION_STATE_", ColType.Int),
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
                C("ENGINE_VERSION_", ColType.Varchar, 255),
                C("APP_VERSION_", ColType.Int),
            ],
            ["ID_"],
            [],
            [new IndexDef("ACT_UNIQ_PROCDEF", ["KEY_", "VERSION_", "TENANT_ID_"])],
            [],
            EngineCharset: true),

        new TableDef(
            "ACT_RU_EXECUTION",
            "运行时执行实例表：执行树，引擎唯一的运行态（流程实例 / 并发分支 / 作用域）",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("REV_", ColType.Int),
                C("PROC_INST_ID_", ColType.Varchar, 64),
                C("BUSINESS_KEY_", ColType.Varchar, 255),
                C("PARENT_ID_", ColType.Varchar, 64),
                C("PROC_DEF_ID_", ColType.Varchar, 64),
                C("SUPER_EXEC_", ColType.Varchar, 64),
                C("ROOT_PROC_INST_ID_", ColType.Varchar, 64),
                C("ACT_ID_", ColType.Varchar, 255),
                C("IS_ACTIVE_", ColType.TinyInt),
                C("IS_CONCURRENT_", ColType.TinyInt),
                C("IS_SCOPE_", ColType.TinyInt),
                C("IS_EVENT_SCOPE_", ColType.TinyInt),
                C("IS_MI_ROOT_", ColType.TinyInt),
                C("SUSPENSION_STATE_", ColType.Int),
                C("CACHED_ENT_STATE_", ColType.Int),
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
                C("NAME_", ColType.Varchar, 255),
                C("START_TIME_", ColType.DateTime3),
                C("START_USER_ID_", ColType.Varchar, 255),
                C("LOCK_TIME_", ColType.Timestamp),
                C("IS_COUNT_ENABLED_", ColType.TinyInt),
                C("EVT_SUBSCR_COUNT_", ColType.Int),
                C("TASK_COUNT_", ColType.Int),
                C("JOB_COUNT_", ColType.Int),
                C("TIMER_JOB_COUNT_", ColType.Int),
                C("SUSP_JOB_COUNT_", ColType.Int),
                C("DEADLETTER_JOB_COUNT_", ColType.Int),
                C("VAR_COUNT_", ColType.Int),
                C("ID_LINK_COUNT_", ColType.Int),
                C("APP_VERSION_", ColType.Int),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_RU_TASK",
            "运行时任务表：当前待办的用户任务",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("REV_", ColType.Int),
                C("EXECUTION_ID_", ColType.Varchar, 64),
                C("PROC_INST_ID_", ColType.Varchar, 64),
                C("PROC_DEF_ID_", ColType.Varchar, 64),
                C("NAME_", ColType.Varchar, 255),
                C("BUSINESS_KEY_", ColType.Varchar, 255),
                C("PARENT_TASK_ID_", ColType.Varchar, 64),
                C("DESCRIPTION_", ColType.Varchar, 4000),
                C("TASK_DEF_KEY_", ColType.Varchar, 255),
                C("OWNER_", ColType.Varchar, 255),
                C("ASSIGNEE_", ColType.Varchar, 255),
                C("DELEGATION_", ColType.Varchar, 64),
                C("PRIORITY_", ColType.Int),
                C("CREATE_TIME_", ColType.Timestamp),
                C("DUE_DATE_", ColType.DateTime3),
                C("CATEGORY_", ColType.Varchar, 255),
                C("SUSPENSION_STATE_", ColType.Int),
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
                C("FORM_KEY_", ColType.Varchar, 255),
                C("CLAIM_TIME_", ColType.DateTime3),
                C("APP_VERSION_", ColType.Int),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_RU_IDENTITYLINK",
            "运行时身份关联表：任务的用户 / 组关联（办理人、候选人与候选组）",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("REV_", ColType.Int),
                C("GROUP_ID_", ColType.Varchar, 255),
                C("TYPE_", ColType.Varchar, 255),
                C("USER_ID_", ColType.Varchar, 255),
                C("TASK_ID_", ColType.Varchar, 64),
                C("PROC_INST_ID_", ColType.Varchar, 64),
                C("PROC_DEF_ID_", ColType.Varchar, 64),
                C("DETAILS_", ColType.Blob),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_RU_VARIABLE",
            "运行时变量表：流程实例、执行与任务级变量",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("REV_", ColType.Int),
                C("TYPE_", ColType.Varchar, 255, false),
                C("NAME_", ColType.Varchar, 255, false),
                C("EXECUTION_ID_", ColType.Varchar, 64),
                C("PROC_INST_ID_", ColType.Varchar, 64),
                C("TASK_ID_", ColType.Varchar, 64),
                C("BYTEARRAY_ID_", ColType.Varchar, 64),
                C("DOUBLE_", ColType.Double),
                C("LONG_", ColType.Long),
                C("TEXT_", ColType.Varchar, 4000),
                C("TEXT2_", ColType.Varchar, 4000),
                // 0011：补租户列。变量承载业务数据，原先是全引擎唯一「不带租户」的运行表，
                // 跨租户查询会看到彼此的变量（安全缺陷）。缺省 '' 与其余 ACT_* 表同口径。
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_HI_PROCINST",
            "历史流程实例表：记录已启动过的流程实例",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("PROC_INST_ID_", ColType.Varchar, 64, false),
                C("BUSINESS_KEY_", ColType.Varchar, 255),
                C("PROC_DEF_ID_", ColType.Varchar, 64, false),
                C("START_TIME_", ColType.DateTime3, nullable: false),
                C("END_TIME_", ColType.DateTime3),
                C("DURATION_", ColType.Long),
                C("START_USER_ID_", ColType.Varchar, 255),
                C("START_ACT_ID_", ColType.Varchar, 255),
                C("END_ACT_ID_", ColType.Varchar, 255),
                C("SUPER_PROCESS_INSTANCE_ID_", ColType.Varchar, 64),
                C("DELETE_REASON_", ColType.Varchar, 4000),
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
                C("NAME_", ColType.Varchar, 255),
            ],
            ["ID_"], [], [new IndexDef("", ["PROC_INST_ID_"])], [],
            EngineCharset: true),

        new TableDef(
            "ACT_HI_ACTINST",
            "历史活动实例表：流程图着色数据源",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("PROC_DEF_ID_", ColType.Varchar, 64, false),
                C("PROC_INST_ID_", ColType.Varchar, 64, false),
                C("EXECUTION_ID_", ColType.Varchar, 64, false),
                C("ACT_ID_", ColType.Varchar, 255, false),
                C("TASK_ID_", ColType.Varchar, 64),
                C("CALL_PROC_INST_ID_", ColType.Varchar, 64),
                C("ACT_NAME_", ColType.Varchar, 255),
                C("ACT_TYPE_", ColType.Varchar, 255, false),
                C("ASSIGNEE_", ColType.Varchar, 255),
                C("START_TIME_", ColType.DateTime3, nullable: false),
                C("END_TIME_", ColType.DateTime3),
                C("DURATION_", ColType.Long),
                C("DELETE_REASON_", ColType.Varchar, 4000),
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_HI_TASKINST",
            "历史任务实例表：记录已产生过的用户任务",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("PROC_DEF_ID_", ColType.Varchar, 64),
                C("TASK_DEF_KEY_", ColType.Varchar, 255),
                C("PROC_INST_ID_", ColType.Varchar, 64),
                C("EXECUTION_ID_", ColType.Varchar, 64),
                C("NAME_", ColType.Varchar, 255),
                C("PARENT_TASK_ID_", ColType.Varchar, 64),
                C("DESCRIPTION_", ColType.Varchar, 4000),
                C("OWNER_", ColType.Varchar, 255),
                C("ASSIGNEE_", ColType.Varchar, 255),
                C("START_TIME_", ColType.DateTime3, nullable: false),
                C("CLAIM_TIME_", ColType.DateTime3),
                C("END_TIME_", ColType.DateTime3),
                C("DURATION_", ColType.Long),
                C("DELETE_REASON_", ColType.Varchar, 4000),
                C("PRIORITY_", ColType.Int),
                C("DUE_DATE_", ColType.DateTime3),
                C("FORM_KEY_", ColType.Varchar, 255),
                C("CATEGORY_", ColType.Varchar, 255),
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_HI_VARINST",
            "历史变量实例表：变量在流程结束时的最终值",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("PROC_INST_ID_", ColType.Varchar, 64),
                C("EXECUTION_ID_", ColType.Varchar, 64),
                C("TASK_ID_", ColType.Varchar, 64),
                C("NAME_", ColType.Varchar, 255, false),
                C("VAR_TYPE_", ColType.Varchar, 100),
                C("REV_", ColType.Int),
                C("BYTEARRAY_ID_", ColType.Varchar, 64),
                C("DOUBLE_", ColType.Double),
                C("LONG_", ColType.Long),
                C("TEXT_", ColType.Varchar, 4000),
                C("TEXT2_", ColType.Varchar, 4000),
                C("CREATE_TIME_", ColType.DateTime3),
                C("LAST_UPDATED_TIME_", ColType.DateTime3),
                // 0011：补租户列（与 ACT_RU_VARIABLE 同因）
                C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),

        new TableDef(
            "ACT_HI_IDENTITYLINK",
            "历史身份关联表：历史任务的用户 / 组关联",
            [
                C("ID_", ColType.Varchar, 64, false),
                C("GROUP_ID_", ColType.Varchar, 255),
                C("TYPE_", ColType.Varchar, 255),
                C("USER_ID_", ColType.Varchar, 255),
                C("TASK_ID_", ColType.Varchar, 64),
                C("PROC_INST_ID_", ColType.Varchar, 64),
                C("DETAILS_", ColType.Blob),
            ],
            ["ID_"], [], [], [],
            EngineCharset: true),
    ];

    // 0001 索引（与上游 Activiti MySQL 清单逐字一致；ACT_IDC_EXEC_ROOT 的 IDC 为上游拼写）
    internal static readonly IReadOnlyList<TableIndexDef> EngineIndexes =
    [
        TI("ACT_IDX_EXEC_BUSKEY", "ACT_RU_EXECUTION", ["BUSINESS_KEY_"]),
        TI("ACT_IDC_EXEC_ROOT", "ACT_RU_EXECUTION", ["ROOT_PROC_INST_ID_"]),
        TI("ACT_IDX_TASK_CREATE", "ACT_RU_TASK", ["CREATE_TIME_"]),
        TI("ACT_IDX_IDENT_LNK_USER", "ACT_RU_IDENTITYLINK", ["USER_ID_"]),
        TI("ACT_IDX_IDENT_LNK_GROUP", "ACT_RU_IDENTITYLINK", ["GROUP_ID_"]),
        TI("ACT_IDX_IDENT_LNK_TASK", "ACT_RU_IDENTITYLINK", ["TASK_ID_"]),
        TI("ACT_IDX_VARIABLE_TASK_ID", "ACT_RU_VARIABLE", ["TASK_ID_"]),
        TI("ACT_IDX_ATHRZ_PROCEDEF", "ACT_RU_IDENTITYLINK", ["PROC_DEF_ID_"]),
        TI("ACT_IDX_RE_PROCDEF_DEPLOYMENT_ID", "ACT_RE_PROCDEF", ["DEPLOYMENT_ID_", "ID_"]),
        TI("ACT_IDX_HI_PRO_INST_END", "ACT_HI_PROCINST", ["END_TIME_"]),
        TI("ACT_IDX_HI_PRO_I_BUSKEY", "ACT_HI_PROCINST", ["BUSINESS_KEY_"]),
        TI("ACT_IDX_HI_ACT_INST_START", "ACT_HI_ACTINST", ["START_TIME_"]),
        TI("ACT_IDX_HI_ACT_INST_END", "ACT_HI_ACTINST", ["END_TIME_"]),
        TI("ACT_IDX_HI_ACT_INST_PROCINST", "ACT_HI_ACTINST", ["PROC_INST_ID_", "ACT_ID_"]),
        TI("ACT_IDX_HI_ACT_INST_EXEC", "ACT_HI_ACTINST", ["EXECUTION_ID_", "ACT_ID_"]),
        TI("ACT_IDX_HI_PROCVAR_PROC_INST", "ACT_HI_VARINST", ["PROC_INST_ID_"]),
        TI("ACT_IDX_HI_PROCVAR_NAME_TYPE", "ACT_HI_VARINST", ["NAME_", "VAR_TYPE_"]),
        TI("ACT_IDX_HI_PROCVAR_TASK_ID", "ACT_HI_VARINST", ["TASK_ID_"]),
        TI("ACT_IDX_HI_IDENT_LNK_USER", "ACT_HI_IDENTITYLINK", ["USER_ID_"]),
        TI("ACT_IDX_HI_IDENT_LNK_TASK", "ACT_HI_IDENTITYLINK", ["TASK_ID_"]),
        TI("ACT_IDX_HI_IDENT_LNK_PROCINST", "ACT_HI_IDENTITYLINK", ["PROC_INST_ID_"]),
        TI("ACT_IDX_HI_TASK_INST_PROCINST", "ACT_HI_TASKINST", ["PROC_INST_ID_"]),
    ];

    // 0001 唯一约束与外键（两方言关系一一对应；MySQL 对自引用执行树 FK 渲染 on delete cascade，
    // SQL Server 因「多重级联路径」限制渲染为 NO ACTION —— 引擎删除本身先子后父，不依赖 DB 级联）
    internal static readonly IReadOnlyList<ForeignKeyDef> EngineForeignKeys =
    [
        F("ACT_FK_BYTEARR_DEPL", "ACT_GE_BYTEARRAY", "DEPLOYMENT_ID_", "ACT_RE_DEPLOYMENT", "ID_"),
        F("ACT_FK_EXE_PROCINST", "ACT_RU_EXECUTION", "PROC_INST_ID_", "ACT_RU_EXECUTION", "ID_", cascadeDelete: true, cascadeUpdate: true),
        F("ACT_FK_EXE_PARENT", "ACT_RU_EXECUTION", "PARENT_ID_", "ACT_RU_EXECUTION", "ID_", cascadeDelete: true),
        F("ACT_FK_EXE_SUPER", "ACT_RU_EXECUTION", "SUPER_EXEC_", "ACT_RU_EXECUTION", "ID_", cascadeDelete: true),
        F("ACT_FK_EXE_PROCDEF", "ACT_RU_EXECUTION", "PROC_DEF_ID_", "ACT_RE_PROCDEF", "ID_"),
        F("ACT_FK_TSKASS_TASK", "ACT_RU_IDENTITYLINK", "TASK_ID_", "ACT_RU_TASK", "ID_"),
        F("ACT_FK_ATHRZ_PROCEDEF", "ACT_RU_IDENTITYLINK", "PROC_DEF_ID_", "ACT_RE_PROCDEF", "ID_"),
        F("ACT_FK_IDL_PROCINST", "ACT_RU_IDENTITYLINK", "PROC_INST_ID_", "ACT_RU_EXECUTION", "ID_"),
        F("ACT_FK_TASK_EXE", "ACT_RU_TASK", "EXECUTION_ID_", "ACT_RU_EXECUTION", "ID_"),
        F("ACT_FK_TASK_PROCINST", "ACT_RU_TASK", "PROC_INST_ID_", "ACT_RU_EXECUTION", "ID_"),
        F("ACT_FK_TASK_PROCDEF", "ACT_RU_TASK", "PROC_DEF_ID_", "ACT_RE_PROCDEF", "ID_"),
        F("ACT_FK_VAR_EXE", "ACT_RU_VARIABLE", "EXECUTION_ID_", "ACT_RU_EXECUTION", "ID_"),
        F("ACT_FK_VAR_PROCINST", "ACT_RU_VARIABLE", "PROC_INST_ID_", "ACT_RU_EXECUTION", "ID_"),
        F("ACT_FK_VAR_BYTEARRAY", "ACT_RU_VARIABLE", "BYTEARRAY_ID_", "ACT_GE_BYTEARRAY", "ID_"),
    ];

    // 0009 运行期查询索引（性能增强；对齐后为两方言并集，20 个）
    internal static readonly IReadOnlyList<TableIndexDef> PerfIndexes =
    [
        TI("ACT_IDX_EXEC_PROC_INST_PARENT", "ACT_RU_EXECUTION", ["PROC_INST_ID_", "PARENT_ID_"]),
        TI("ACT_IDX_EXEC_PARENT", "ACT_RU_EXECUTION", ["PARENT_ID_"]),
        TI("ACT_IDX_EXEC_ACT_PROCINST_ACTIVE", "ACT_RU_EXECUTION", ["ACT_ID_", "PROC_INST_ID_", "IS_ACTIVE_"]),
        TI("ACT_IDX_EXEC_PROC_INST_ID", "ACT_RU_EXECUTION", ["PROC_INST_ID_"]),
        TI("ACT_IDX_EXECUTION_PROC", "ACT_RU_EXECUTION", ["PROC_DEF_ID_"]),
        TI("ACT_IDX_EXECUTION_SUPER", "ACT_RU_EXECUTION", ["SUPER_EXEC_"]),
        TI("ACT_IDX_EXECUTION_IDANDREV", "ACT_RU_EXECUTION", ["ID_", "REV_"]),
        TI("ACT_IDX_TASK_PROCINST", "ACT_RU_TASK", ["PROC_INST_ID_"]),
        TI("ACT_IDX_TASK_EXEC", "ACT_RU_TASK", ["EXECUTION_ID_"]),
        TI("ACT_IDX_TASK_PROC_DEF_ID", "ACT_RU_TASK", ["PROC_DEF_ID_"]),
        TI("ACT_IDX_TASK_ASSIGNEE", "ACT_RU_TASK", ["ASSIGNEE_"]),
        TI("ACT_IDX_VARIABLE_EXEC_NAME", "ACT_RU_VARIABLE", ["EXECUTION_ID_", "NAME_"]),
        TI("ACT_IDX_VARIABLE_EXEC", "ACT_RU_VARIABLE", ["EXECUTION_ID_"]),
        TI("ACT_IDX_VARIABLE_PROCINST", "ACT_RU_VARIABLE", ["PROC_INST_ID_"]),
        TI("ACT_IDX_VARIABLE_BA", "ACT_RU_VARIABLE", ["BYTEARRAY_ID_"]),
        TI("ACT_IDX_IDENT_LNK_PROCINST", "ACT_RU_IDENTITYLINK", ["PROC_INST_ID_"]),
        // 待办 / 已办主路径：按办理人取数并按创建时间排序（此前只能全量取回内存排序）
        TI("ACT_IDX_TASK_ASSIGNEE_CREATE", "ACT_RU_TASK", ["ASSIGNEE_", "CREATE_TIME_"]),
        TI("ACT_IDX_TASK_PROCINST_DEFKEY", "ACT_RU_TASK", ["PROC_INST_ID_", "TASK_DEF_KEY_"]),
        TI("ACT_IDX_TASK_TENANT", "ACT_RU_TASK", ["TENANT_ID_"]),
        TI("ACT_IDX_TASK_DUEDATE", "ACT_RU_TASK", ["DUE_DATE_"]),
        TI("ACT_IDX_HI_TASK_ASSIGNEE_END", "ACT_HI_TASKINST", ["ASSIGNEE_", "END_TIME_"]),
        TI("ACT_IDX_HI_TASK_PROCINST_END", "ACT_HI_TASKINST", ["PROC_INST_ID_", "END_TIME_"]),
        TI("ACT_IDX_HI_TASK_DEFKEY", "ACT_HI_TASKINST", ["TASK_DEF_KEY_"]),
        TI("ACT_IDX_HI_PROC_TENANT_END", "ACT_HI_PROCINST", ["TENANT_ID_", "END_TIME_"]),
        // 历史 / 已办列表默认按 START_TIME_ 倒序分页（原索引只覆盖 END_TIME_，排序要吃 filesort）
        TI("ACT_IDX_HI_PRO_INST_START", "ACT_HI_PROCINST", ["START_TIME_", "ID_"]),
        TI("ACT_IDX_HI_TASK_ASSIGNEE_START", "ACT_HI_TASKINST", ["ASSIGNEE_", "START_TIME_"]),
        // 办人变更时按「实例 + 用户」count 候选人 / 办理人，原索引只有 PROC_INST_ID_ 前缀
        TI("ACT_IDX_IDENT_LNK_PROCINST_USER", "ACT_RU_IDENTITYLINK", ["PROC_INST_ID_", "USER_ID_"]),
        // outbox 待发布扫描：过滤 PUBLISHED_ / DEAD_LETTER_ / NEXT_ATTEMPT_（NEXT_ATTEMPT_ 兼作发布租约）
        TI("ACT_IDX_OUTBOX_CLAIM", "ACT_EVT_OUTBOX", ["PUBLISHED_", "DEAD_LETTER_", "NEXT_ATTEMPT_"]),
        // 注意：ACT_HI_MIGRATION 由 0007 建表，索引必须跟着表的脚本走，
        // 否则新库按 0004 → 0007 顺序执行时会在不存在的表上建索引而失败。
        TI("ACT_IDX_BYTEARRAY_DEPLOYMENT", "ACT_GE_BYTEARRAY", ["DEPLOYMENT_ID_"]),
        TI("ACT_IDX_DEPLOYMENT_TIME", "ACT_RE_DEPLOYMENT", ["DEPLOY_TIME_"]),
    ];

    /// <summary>
    /// 0011 迁移：变量表的租户列及其索引。
    /// <para>
    /// 之所以<b>不放进 <see cref="PerfIndexes"/></b>：那一节在 ④ 渲染，而补列（ALTER）在 ⑥，
    /// 老库升级时会先跑 ④ —— 此时列还不存在，建索引直接报
    /// <c>Key column 'TENANT_ID_' doesn't exist in table</c>。
    /// 放在 ⑥ 的补列之后即可保证「先加列、再建索引」。
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyList<TableIndexDef> VariableTenantIndexes =
    [
        TI("ACT_IDX_VARIABLE_TENANT_EXEC", "ACT_RU_VARIABLE", ["TENANT_ID_", "EXECUTION_ID_"]),
        TI("ACT_IDX_HI_VARINST_TENANT", "ACT_HI_VARINST", ["TENANT_ID_"]),
    ];

    // ────────────────────────────────────────────────────────────────────────
    //  流程实例业务表（0003，3 张）
    // ────────────────────────────────────────────────────────────────────────

    internal static readonly IReadOnlyList<TableDef> InstanceTables =
    [
        new TableDef(
            "instances",
            "流程实例表（订单审批/请假审批流程共用；workflow_type_id=流程信息枚举的流程类型id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）",
            [
                C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户id"),
                C("apply_id", ColType.BigInt, nullable: false, comment: "申请id"),
                C("apply_time", ColType.DateTime, nullable: false, comment: "申请时间"),
                C("workflow_type_id", ColType.Varchar, 64, false, comment: "流程类型id（业务侧流程信息枚举 Id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）"),
                C("workflow_version", ColType.Varchar, 50, comment: "流程版本"),
                C("describe", ColType.Varchar, 200, false, comment: "流程实例描述"),
                C("apply_emp_id", ColType.Varchar, 50, false, comment: "申请人id"),
                C("apply_dept_id", ColType.Varchar, 50, false, comment: "申请人部门"),
                C("business_id", ColType.BigInt, nullable: false, comment: "对应的业务id"),
                C("business_dept_id", ColType.Varchar, 50, comment: "业务部门id"),
                C("status_id", ColType.Varchar, 50, false, comment: "流程实例状态 审批中、已完成"),
                C("newest_flag", ColType.SmallInt, nullable: false, def: "1", comment: "是否是最新流程实例"),
                C("workflow_instance_id", ColType.Varchar, 64, comment: "流程实例id(对应于流程引擎中的id）"),
                C("last_audited_time", ColType.DateTime, comment: "最后审批完成时间"),
                C("create_emp_id", ColType.Varchar, 50, false, comment: "创建人"),
                C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
                C("current_step", ColType.Varchar, 200, comment: "当前步骤名称"),
                C("assign_emp", ColType.Varchar, 200, comment: "当前处理人"),
                C("modify_emp_id", ColType.Varchar, 50, comment: "修改人"),
                C("modify_time", ColType.DateTime, comment: "修改时间"),
                C("deleted_flag", ColType.TinyInt, nullable: false, def: "0", comment: "删除标志"),
                C("original_business_id", ColType.BigInt, comment: "原始业务ID"),
                C("flow_file_id", ColType.BigInt, comment: "流程图文件ID"),
            ],
            ["apply_id"],
            [
                new IndexDef("ix_instances_workflow_instance_id", ["workflow_instance_id"]),
                new IndexDef("ix_instances_business_id", ["business_id"]),
                new IndexDef("ix_instances_status_id", ["tenant_id", "status_id"]),
            ],
            [], [],
            EngineCharset: false),

        new TableDef(
            "instance_current_task",
            "当前审批步骤及审批人（订单审批/请假审批流程共用）",
            [
                C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户ID"),
                C("step_id", ColType.BigInt, nullable: false, comment: "数据行id"),
                C("apply_id", ColType.BigInt, comment: "申请id"),
                C("task_id", ColType.Varchar, 64, comment: "步骤ID"),
                C("task_node", ColType.Varchar, 50, comment: "当前步骤名称"),
                C("task_owner", ColType.Varchar, 200, comment: "待办项归属人"),
                C("assign_emp_id", ColType.Varchar, 200, comment: "当前审批者"),
                C("form_key", ColType.Varchar, 100, comment: "流程步骤对应的环节配置"),
                C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            ],
            ["step_id"],
            [
                new IndexDef("ix_instance_current_task_apply_id", ["apply_id"]),
                new IndexDef("ix_instance_current_task_task_id", ["task_id"]),
            ],
            [], [],
            EngineCharset: false),

        new TableDef(
            "deal_history",
            "审批历史记录（订单审批/请假审批流程共用）",
            [
                C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户ID"),
                C("deal_history_id", ColType.BigInt, nullable: false, comment: "数据行id"),
                C("business_id", ColType.BigInt, comment: "对应业务表单的ID"),
                C("apply_id", ColType.BigInt, comment: "申请id"),
                C("task_node", ColType.Varchar, 50, comment: "步骤名称"),
                C("deal_emp_id", ColType.Varchar, 50, comment: "审批者员工id"),
                C("deal_time", ColType.DateTime, comment: "审批时间"),
                C("deal_result", ColType.Varchar, 50, comment: "审批结果"),
                C("deal_comment", ColType.Varchar, 2000, comment: "审批说明"),
                C("form_key", ColType.Varchar, 200, comment: "流程步骤对应的环节配置"),
                C("deal_emp_name", ColType.Varchar, 50, comment: "审批人姓名"),
                C("deal_domain_account", ColType.Varchar, 50, comment: "审批人域帐号"),
            ],
            ["deal_history_id"],
            [
                new IndexDef("ix_deal_history_apply_id", ["apply_id"]),
                new IndexDef("ix_deal_history_deal_emp_id", ["tenant_id", "deal_emp_id"]),
            ],
            [], [],
            EngineCharset: false),
    ];

    // 0004 请假单
    internal static readonly TableDef LeaveBillTable = new(
        "leave_bill",
        "请假单（业务表单；days/reason 对应请假流程参数；流程类型id=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）",
        [
            C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户id"),
            C("business_id", ColType.BigInt, nullable: false, comment: "业务id（请假单号，由业务侧生成）"),
            C("apply_user", ColType.Varchar, 50, false, comment: "申请人"),
            C("apply_dept_id", ColType.Varchar, 50, false, comment: "申请部门"),
            C("business_dept_id", ColType.Varchar, 50, comment: "业务部门"),
            C("days", ColType.SmallInt, comment: "请假天数（对应流程参数 days）；NULL = 还没填"),
            C("reason", ColType.Varchar, 500, comment: "请假事由（对应流程参数 reason）"),
            C("ext_params", ColType.Varchar, 2000, comment: "扩展流程参数（JSON 对象）：除 days / reason 外的键值对"),
            C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            C("modify_time", ColType.DateTime, comment: "最后修改时间"),
        ],
        ["business_id"],
        [new IndexDef("ix_leave_bill_apply_user", ["tenant_id", "apply_user"])],
        [], [],
        EngineCharset: false);

    // ────────────────────────────────────────────────────────────────────────
    //  组织架构表（0005，4 张，幂等）
    // ────────────────────────────────────────────────────────────────────────

    internal static readonly IReadOnlyList<TableDef> OrgTables =
    [
        new TableDef(
            "sys_dept",
            "部门信息表",
            [
                C("dept_id", ColType.BigInt, nullable: false, comment: "部门ID"),
                C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户ID"),
                C("dept_name", ColType.Varchar, 100, false, comment: "部门名称"),
                C("parent_id", ColType.BigInt, comment: "上级部门ID（NULL=顶级）"),
                C("sort_order", ColType.Int, nullable: false, def: "0", comment: "排序号"),
                C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            ],
            ["dept_id"],
            [new IndexDef("idx_sys_dept_tenant", ["tenant_id", "parent_id"])],
            [], [],
            EngineCharset: false),

        new TableDef(
            "sys_emp",
            "员工信息表",
            [
                C("emp_id", ColType.BigInt, nullable: false, comment: "员工ID"),
                C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户ID"),
                C("emp_name", ColType.Varchar, 50, false, comment: "员工姓名"),
                C("domain_account", ColType.Varchar, 50, false, comment: "域账号（审批人/申请人在流程里的用户ID）"),
                C("dept_id", ColType.BigInt, nullable: false, comment: "所属部门ID（sys_dept.dept_id）"),
                C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            ],
            ["emp_id"],
            [new IndexDef("idx_sys_emp_dept", ["dept_id"])],
            [new IndexDef("uk_sys_emp_account", ["tenant_id", "domain_account"], Unique: true)],
            [],
            EngineCharset: false),

        new TableDef(
            "sys_emp_group",
            "用户组表（审批组）",
            [
                C("group_id", ColType.Varchar, 50, false, comment: "组标识（流程定义 candidateGroups 引用的就是它）"),
                C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户ID"),
                C("group_name", ColType.Varchar, 100, false, comment: "组名称（如：组长 / 经理）"),
                C("sort_order", ColType.Int, nullable: false, def: "0", comment: "排序号"),
                C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            ],
            ["group_id"],
            [], [], [],
            EngineCharset: false),

        new TableDef(
            "sys_emp_group_member",
            "用户组成员表",
            [
                C("member_id", ColType.BigInt, nullable: false, comment: "成员行ID"),
                C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户ID"),
                C("group_id", ColType.Varchar, 50, false, comment: "组标识（sys_emp_group.group_id）"),
                C("emp_id", ColType.BigInt, nullable: false, comment: "员工ID（sys_emp.emp_id）"),
                C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            ],
            ["member_id"],
            [new IndexDef("idx_sys_emp_group_member_emp", ["emp_id"])],
            [new IndexDef("uk_sys_emp_group_member", ["group_id", "emp_id"], Unique: true)],
            [],
            EngineCharset: false),
    ];

    internal static readonly IReadOnlyList<SeedTable> OrgSeeds =
    [
        new SeedTable(
            "sys_dept",
            ["dept_id", "tenant_id", "dept_name", "parent_id", "sort_order", "create_time"],
            [
                [101L, 1L, "信息技术部", null, 1L, Now],
                [102L, 1L, "成本管理部", null, 2L, Now],
                [103L, 1L, "综合管理部", null, 3L, Now],
            ],
            ["dept_id"],
            ["dept_name", "parent_id", "sort_order"]),

        new SeedTable(
            "sys_emp",
            ["emp_id", "tenant_id", "emp_name", "domain_account", "dept_id", "create_time"],
            [
                [1001L, 1L, "王小爱", "alice", 101L, Now],
                [1002L, 1L, "李小波", "bob", 101L, Now],
                [1003L, 1L, "赵志强", "leader1", 101L, Now],
                [1004L, 1L, "钱有为", "leader2", 102L, Now],
                [1005L, 1L, "孙建国", "manager1", 103L, Now],
            ],
            ["emp_id"],
            ["emp_name", "dept_id"]),

        new SeedTable(
            "sys_emp_group",
            ["group_id", "tenant_id", "group_name", "sort_order", "create_time"],
            [
                ["leaders", 1L, "组长", 1L, Now],
                ["managers", 1L, "经理", 2L, Now],
            ],
            ["group_id"],
            ["group_name", "sort_order"]),

        new SeedTable(
            "sys_emp_group_member",
            ["member_id", "tenant_id", "group_id", "emp_id", "create_time"],
            [
                [1L, 1L, "leaders", 1003L, Now],
                [2L, 1L, "leaders", 1004L, Now],
                [3L, 1L, "managers", 1005L, Now],
            ],
            ["group_id", "emp_id"],
            []),
    ];

    // 0006 订单单
    internal static readonly TableDef OrderBillTable = new(
        "order_bill",
        "订单表（业务表单；状态随订单审批流程回写：1=审批中，-1=审批拒绝，100=审批通过；流程类型id=02830358-46ef-40e5-b3b9-4d5c4eb23f9f）",
        [
            C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户id"),
            C("business_id", ColType.BigInt, nullable: false, comment: "业务id（订单单号，由业务侧生成）"),
            C("product_name", ColType.Varchar, 200, comment: "订购产品（对应流程参数 product_name）"),
            C("amount", ColType.Money, comment: "订单金额（对应流程参数 amount）"),
            C("remark", ColType.Varchar, 500, comment: "备注（对应流程参数 remark）"),
            C("status", ColType.SmallInt, nullable: false, def: "1", comment: "状态：1=审批中，-1=审批拒绝，100=审批通过"),
            C("approve_comment", ColType.Varchar, 500, comment: "最终审批意见（审批结束时随状态一起写回）"),
            C("apply_user", ColType.Varchar, 50, false, comment: "申请人"),
            C("apply_dept_id", ColType.Varchar, 50, false, comment: "申请部门"),
            C("business_dept_id", ColType.Varchar, 50, comment: "业务部门"),
            C("process_instance_id", ColType.Varchar, 64, comment: "流程实例 id（提交申请后回填，用于反查审批进度）"),
            C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            C("modify_time", ColType.DateTime, comment: "最后修改时间"),
            C("deleted_flag", ColType.TinyInt, nullable: false, def: "0", comment: "伪删除标志（0 正常 / 1 已删除；公共数据操作接口 DeleteAsync 置位，列表与明细自动过滤）"),
        ],
        ["business_id"],
        [
            new IndexDef("ix_order_bill_apply_user", ["tenant_id", "apply_user"]),
            new IndexDef("ix_order_bill_status", ["tenant_id", "status"]),
        ],
        [], [],
        EngineCharset: false);

    // 0007 流程参数表
    internal static readonly TableDef WorkflowParametersTable = new(
        "workflow_parameters",
        "流程参数表（订单/请假审批流程共用；「实例 × 参数名」键值对）",
        [
            C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户id"),
            C("workflow_instance_id", ColType.Varchar, 64, false, comment: "流程实例id（对应 instances.workflow_instance_id）"),
            C("param_name", ColType.Varchar, 100, false, comment: "参数名（如 days / reason / product_name）"),
            C("param_value", ColType.LongText, comment: "参数值（null = 显式置空）"),
            C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            C("modify_time", ColType.DateTime, comment: "最后修改时间"),
        ],
        ["workflow_instance_id", "param_name"],
        [new IndexDef("ix_workflow_parameters_instance", ["workflow_instance_id"])],
        [], [],
        EngineCharset: false);

    // 0008 流程类型表
    internal static readonly TableDef WorkflowTypeTable = new(
        "workflow_type",
        "流程类型表（流程信息枚举 ProcessInfoEnum 的数据源；系统启动时加载）",
        [
            C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户id"),
            C("id", ColType.Varchar, 64, false, comment: "流程类型id（流程信息枚举 Id，如 02830358-46ef-40e5-b3b9-4d5c4eb23f9f）"),
            C("name", ColType.Varchar, 50, false, comment: "流程名称（流程 key，= BPMN process/@id，如 order_process）"),
            C("describe", ColType.Varchar, 200, false, comment: "流程描述"),
            C("view_url", ColType.Varchar, 500, false, comment: "业务单查看页地址（{businessId}/{processInstanceId} 占位符）"),
            C("approve_url", ColType.Varchar, 500, false, comment: "流程审批页地址（{taskId}/{processInstanceId} 占位符）"),
            C("sort_order", ColType.Int, nullable: false, def: "0", comment: "展示顺序（小在前）"),
            C("enabled", ColType.TinyInt, nullable: false, def: "1", comment: "是否启用（0=停用，加载时跳过）"),
            C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            C("modify_time", ColType.DateTime, comment: "最后修改时间"),
        ],
        ["id"],
        [new IndexDef("ix_workflow_type_sort", ["tenant_id", "sort_order"])],
        [], [],
        EngineCharset: false);

    internal static readonly SeedTable WorkflowTypeSeeds = new(
        "workflow_type",
        ["tenant_id", "id", "name", "describe", "view_url", "approve_url", "sort_order", "enabled", "create_time"],
        [
            [1L, "02830358-46ef-40e5-b3b9-4d5c4eb23f9f", "order_process", "订单审批流程", "/bill.html?process=order_process&businessId={businessId}", "/approve.html?process=order_process&taskId={taskId}", 1L, 1L, Now],
            [1L, "3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7", "leave_process", "请假申请流程", "/bill.html?process=leave_process&businessId={businessId}", "/approve.html?process=leave_process&taskId={taskId}", 2L, 1L, Now],
        ],
        ["id"],
        // 流程类型是「配置型」种子：重复执行时把可变字段同步成脚本里的值（create_time 保留）
        ["name", "describe", "view_url", "approve_url", "sort_order", "enabled", "modify_time"]);

    // 管理端 · 流程模型表（设计器草稿/模型的服务端落点，随 0002 业务表一起建）
    internal static readonly TableDef BpmnModelTable = new(
        "bpmn_model",
        "流程模型表（管理端流程设计器的模型落点；BPMN XML 的编辑源，部署后产生 ACT_RE_PROCDEF 版本）",
        [
            C("id", ColType.Varchar, 64, false, comment: "模型 ID（设计器 modelId，GUID 字符串）"),
            C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户 id"),
            C("name", ColType.Varchar, 200, false, comment: "流程名称（BPMN process/@name）"),
            C("process_key", ColType.Varchar, 50, false, comment: "流程 key（= BPMN process/@id，如 order_process）"),
            C("description", ColType.Varchar, 500, comment: "流程描述"),
            C("bpmn_xml", ColType.LongText, nullable: false, comment: "BPMN 2.0 XML 内容"),
            C("svg", ColType.LongText, comment: "流程图 SVG 预览（前端导出，可选）"),
            C("create_time", ColType.DateTime, nullable: false, comment: "创建时间"),
            C("modify_time", ColType.DateTime, comment: "最后修改时间"),
        ],
        ["id"],
        [new IndexDef("ix_bpmn_model_modify", ["tenant_id", "modify_time"])],
        // 同一租户下一个 key 只能对应一个模型：否则发布后同一 key 下会混入多来源版本
        [new IndexDef("ux_bpmn_model_key", ["tenant_id", "process_key"], Unique: true)],
        [],
        EngineCharset: false);

    // 0008 流程模型版本历史（管理端发布治理）
    internal static readonly TableDef ModelHistoryTable = new(
        "bpmn_model_history",
        "流程模型版本历史：每次保存/回滚追加一版，支持回滚与变更追溯",
        [
            C("id", ColType.Varchar, 64, false, comment: "历史版本 id"),
            C("tenant_id", ColType.BigInt, nullable: false, comment: "多租户 id"),
            C("model_id", ColType.Varchar, 64, false, comment: "模型 id（bpmn_model.id）"),
            C("revision", ColType.Int, nullable: false, comment: "版本号（同一模型内递增，从 1 开始）"),
            C("name", ColType.Varchar, 200, false, comment: "保存时的流程名称"),
            C("process_key", ColType.Varchar, 50, false, comment: "保存时的流程 key"),
            C("description", ColType.Varchar, 500, comment: "保存时的流程描述"),
            C("bpmn_xml", ColType.LongText, nullable: false, comment: "BPMN XML 快照"),
            C("svg", ColType.LongText, comment: "流程图 SVG 快照"),
            C("action", ColType.Varchar, 32, nullable: false, comment: "动作：save / rollback"),
            C("operator", ColType.Varchar, 255, comment: "操作人（请求身份）"),
            C("created_at", ColType.DateTime3, nullable: false, comment: "写入时间（UTC）"),
        ],
        ["id"],
        [new IndexDef("ix_bpmn_model_history_tenant", ["tenant_id", "model_id"])],
        [new IndexDef("ux_bpmn_model_history_rev", ["model_id", "revision"], Unique: true)],
        [],
        EngineCharset: false);

    // 0009 管理动作审计（RBAC 治理：转办 / 跳转 / 变量直写）
    internal static readonly TableDef AdminAuditTable = new(
        "admin_audit_log",
        "管理动作审计：转办 / 跳转 / 变量直写等高风险动作的操作人、目标与结果",
        [
            C("id", ColType.Varchar, 64, false, comment: "审计记录 id"),
            C("tenant_id", ColType.Varchar, 255, comment: "租户（与 ACT_* 的 TENANT_ID_ 同口径）"),
            C("actor", ColType.Varchar, 255, comment: "操作人（JWT sub / X-User-Id）"),
            C("auth_mode", ColType.Varchar, 16, nullable: false, comment: "鉴权方式：jwt / api_key / none"),
            C("action", ColType.Varchar, 64, nullable: false, comment: "动作标识，如 task.reassign / instance.jump / variable.write"),
            C("target_type", ColType.Varchar, 32, nullable: false, comment: "目标类型：task / process_instance / variable / history"),
            C("target_id", ColType.Varchar, 128, nullable: false, comment: "目标 id"),
            C("process_instance_id", ColType.Varchar, 64, comment: "关联流程实例 id（便于按实例追溯）"),
            C("detail", ColType.MediumText, comment: "动作明细（JSON；不记录变量值）"),
            C("success", ColType.Int, nullable: false, comment: "1=成功，0=失败"),
            C("error", ColType.Varchar, 500, comment: "失败原因"),
            C("created_at", ColType.DateTime3, nullable: false, comment: "写入时间（UTC）"),
        ],
        ["id"],
        [
            new IndexDef("ix_admin_audit_tenant_time", ["tenant_id", "created_at"]),
            new IndexDef("ix_admin_audit_actor", ["tenant_id", "actor", "created_at"]),
            new IndexDef("ix_admin_audit_action", ["action"]),
            new IndexDef("ix_admin_audit_target", ["target_type", "target_id"]),
            new IndexDef("ix_admin_audit_instance", ["process_instance_id"]),
        ],
        [], [],
        EngineCharset: false);

    /// <summary>
    /// 控制台账号（账号密码登录的落点）。注意：这里只存<b>口令散列</b>，
    /// 明文口令不会落库也不会进审计；散列格式见 <c>PasswordHasher</c>。
    /// </summary>
    internal static readonly TableDef AdminUserTable = new(
        "admin_user",
        "控制台账号：账号密码登录的用户表（只存 PBKDF2 口令散列，不存明文）",
        [
            C("id", ColType.Varchar, 64, false, comment: "账号 id"),
            C("username", ColType.Varchar, 64, nullable: false, comment: "登录名（全局唯一）"),
            C("display_name", ColType.Varchar, 128, comment: "显示名"),
            C("password_hash", ColType.Varchar, 255, nullable: false,
                comment: "PBKDF2 散列：pbkdf2$迭代次数$salt(base64)$hash(base64)"),
            C("scopes", ColType.Varchar, 1000, nullable: false, def: "''",
                comment: "授权范围（空格分隔，如 netbpm.admin）"),
            C("tenant_id", ColType.Varchar, 255, comment: "所属租户（为空表示不限租户）"),
            C("enabled", ColType.Int, nullable: false, def: "1", comment: "1=启用，0=停用"),
            C("failed_attempts", ColType.Int, nullable: false, def: "0", comment: "连续失败次数（成功后清零）"),
            C("locked_until", ColType.DateTime3, comment: "锁定到期时间（UTC；暴力破解保护）"),
            C("last_login_at", ColType.DateTime3, comment: "上次登录时间（UTC）"),
            C("created_at", ColType.DateTime3, nullable: false, comment: "创建时间（UTC）"),
            C("updated_at", ColType.DateTime3, nullable: false, comment: "更新时间（UTC）"),
        ],
        ["id"],
        [new IndexDef("ix_admin_user_tenant", ["tenant_id"])],
        [new IndexDef("ux_admin_user_username", ["username"])],
        [],
        EngineCharset: false);

    // 0009 历史归档（把已结束实例的 ACT_HI_* 行搬进 JSON 快照后删除原行）
    internal static readonly TableDef HistoryArchiveTable = new(
        "admin_history_archive",
        "历史归档：已结束实例的 ACT_HI_* 行快照（JSON），原历史行在归档后删除",
        [
            C("id", ColType.Varchar, 64, false, comment: "归档行 id"),
            C("process_instance_id", ColType.Varchar, 64, false, comment: "流程实例 id"),
            C("tenant_id", ColType.Varchar, 255, comment: "租户"),
            C("process_definition_id", ColType.Varchar, 64, comment: "流程定义 id（启动时版本）"),
            C("business_key", ColType.Varchar, 255, comment: "业务主键"),
            C("start_user_id", ColType.Varchar, 255, comment: "发起人"),
            C("start_time", ColType.DateTime3, comment: "实例开始时间"),
            C("end_time", ColType.DateTime3, comment: "实例结束时间"),
            C("archived_at", ColType.DateTime3, nullable: false, comment: "归档时间（UTC）"),
            C("archived_by", ColType.Varchar, 255, comment: "归档操作人"),
            C("payload", ColType.MediumText, nullable: false, comment: "历史行快照（processInstance / activities / tasks / variables / identityLinks）"),
        ],
        ["id"],
        [
            new IndexDef("ix_admin_history_archive_tenant", ["tenant_id", "end_time"]),
            new IndexDef("ix_admin_history_archive_business", ["business_key"]),
            new IndexDef("ix_admin_history_archive_archived", ["archived_at"]),
        ],
        [new IndexDef("ux_admin_history_archive_instance", ["process_instance_id"], Unique: true)],
        [],
        EngineCharset: false);

    // 平台接入三表（见 docs/platform-api-refactoring-plan.md §2.5 / §7）
    //
    // 命名一律 nb_ 前缀，与 ACT_*（引擎表）严格分开：引擎升级永远不会碰到这三张表，
    // 跨表 join 只允许「平台表 → 引擎表」单向进行（引擎表不认识 nb_*）。
    //
    // 状态列一律存对外 JSON 词汇（ACTIVE / SUSPENDED ...），不存枚举序号：
    // 序号会随枚举增删漂移，而字符串是契约的一部分，改它必须改契约。
    internal static readonly TableDef TenantTable = new(
        "nb_tenant",
        "平台租户：数据隔离的最外层（一期固定 default 一行，仍按多租户建模）",
        [
            C("id", ColType.Varchar, 64, false, comment: "租户 id（同时是引擎 TENANT_ID_ 的取值）"),
            C("name", ColType.Varchar, 128, false, comment: "租户名称（审计与看板展示）"),
            C("status", ColType.Varchar, 16, false, def: "'ACTIVE'",
                comment: "ACTIVE=可用；SUSPENDED=停用（其下客户端鉴权一律失败）"),
            C("quota_per_minute", ColType.Int, nullable: false, def: "600",
                comment: "默认每分钟请求配额（客户端未单独配置时取此值）"),
            C("created_at", ColType.DateTime3, nullable: false, comment: "创建时间（UTC）"),
            C("updated_at", ColType.DateTime3, nullable: false, comment: "更新时间（UTC）"),
        ],
        ["id"], [], [], [],
        EngineCharset: false);

    internal static readonly TableDef ApiClientTable = new(
        "nb_api_client",
        "接入客户端：一系统一客户端一密钥（只存 SHA-256 摘要，明文只在签发响应里出现一次）",
        [
            C("id", ColType.Varchar, 64, false, comment: "客户端 id（对外 clientId，出现在令牌 azp）"),
            C("tenant_id", ColType.Varchar, 64, false, comment: "所属租户（刻意不做外键：租户下线不该连带删客户端）"),
            C("name", ColType.Varchar, 128, false, comment: "接入系统名称（进审计与运维看板）"),
            C("status", ColType.Varchar, 16, false, def: "'PENDING'",
                comment: "PENDING/ACTIVE/SUSPENDED/REVOKED（与对外 JSON 词汇一致，不存枚举序号）"),
            C("scopes", ColType.Varchar, 1000, false, def: "''",
                comment: "授权范围（空格分隔，取值见 PlatformScopes.All）"),
            C("quota_per_minute", ColType.Int, nullable: false, def: "600",
                comment: "每分钟请求配额（0 表示取租户默认值）"),
            C("secret_hash", ColType.Varchar, 64, false, comment: "当前密钥的 SHA-256（base64）"),
            C("secret_expires_at", ColType.DateTime3, comment: "当前密钥到期时间（UTC）"),
            C("previous_secret_hash", ColType.Varchar, 64,
                comment: "上一代密钥摘要（轮换宽限期只有一代，连续轮换会让更早的那把立即失效）"),
            C("previous_secret_expires_at", ColType.DateTime3, comment: "上一代密钥到期时间（UTC）"),
            C("created_at", ColType.DateTime3, nullable: false, comment: "创建时间（UTC）"),
            C("updated_at", ColType.DateTime3, nullable: false, comment: "更新时间（UTC）"),
            C("last_used_at", ColType.DateTime3, comment: "最近一次成功鉴权时间（UTC；按分钟节流回写）"),
        ],
        ["id"],
        [new IndexDef("ix_nb_api_client_tenant", ["tenant_id", "status"])],
        [], [],
        EngineCharset: false);

    internal static readonly TableDef ClientProcessGrantTable = new(
        "nb_client_process_grant",
        "客户端流程白名单：一行一条授权；无行表示「不限流程」（不推荐，接入时应显式授权）",
        [
            C("client_id", ColType.Varchar, 64, false, comment: "客户端 id"),
            C("process_key", ColType.Varchar, 255, false, comment: "流程 key（= BPMN process/@id）"),
            C("granted_at", ColType.DateTime3, nullable: false, comment: "授权时间（UTC）"),
            C("granted_by", ColType.Varchar, 255, comment: "授权操作人（审计）"),
        ],
        ["client_id", "process_key"],
        // 反向索引：发布一条流程前要查「谁在调它」，影响面清单全靠这个索引
        [new IndexDef("ix_nb_client_process_grant_process", ["process_key"])],
        [], [],
        EngineCharset: false);

    // 平台流程目录（见 docs/platform-api-refactoring-plan.md §4.1 / §7.1）
    //
    // 刻意做成「覆盖表」而不是唯一真源：流程名 / 入口 / 校验和都能从部署的 BPMN 派生，
    // 因此**新流程部署完就自动出现在目录里**（零配置可用）；这一行只在需要精修时登记
    // （分类、图标、业务页面地址、显式状态、本地化标签、参数 schema 版本）。
    //
    // 语义约定：可空列 = NULL 表示「跟随引擎 / BPMN」，非 NULL 才是覆盖值。
    // 不要用空串表达「未设置」——空串是合法的展示名，会和「没配」混为一谈。
    internal static readonly TableDef ProcessCatalogTable = new(
        "nb_process_catalog",
        "流程目录覆盖层：按租户登记流程名片（无行则全部由部署的 BPMN 派生，实现「部署即可见」）",
        [
            C("tenant_id", ColType.Varchar, 64, false, comment: "租户"),
            C("process_key", ColType.Varchar, 255, false, comment: "流程 key（= BPMN process/@id，对外稳定标识）"),
            C("name", ColType.Varchar, 255, comment: "展示名；NULL 取流程定义里的 name"),
            C("category", ColType.Varchar, 64, comment: "分类（分组展示用）；NULL = 未分类"),
            C("description", ColType.Varchar, 1000, comment: "说明，允许 Markdown；NULL 表示无说明"),
            C("icon", ColType.Varchar, 512, comment: "图标标识或 URL"),
            C("page_urls_json", ColType.MediumText,
                comment: "业务页面地址模板（JSON：start / view / approve，值可带 {businessKey} / {taskId} 占位符）"),
            C("entry_points_json", ColType.MediumText,
                comment: "入口声明（JSON 数组）；NULL = 由 BPMN 的 startEvent 派生"),
            C("status", ColType.Varchar, 16,
                comment: "显式状态 DRAFT/ACTIVE/SUSPENDED/RETIRED；NULL = 跟随引擎部署态（挂起即 SUSPENDED）"),
            C("sort_order", ColType.Int, nullable: false, def: "0", comment: "展示排序（小的在前；相同则按 process_key）"),
            C("labels_json", ColType.Varchar, 1000, comment: "本地化标签（JSON：语言 → 展示名）"),
            C("parameter_schema_version", ColType.Int, nullable: false, def: "0",
                comment: "参数规范版本；0 表示未声明（调用方按「不校验额外参数」处理）"),
            C("created_at", ColType.DateTime3, nullable: false, comment: "创建时间（UTC）"),
            C("updated_at", ColType.DateTime3, nullable: false, comment: "更新时间（UTC）"),
        ],
        ["tenant_id", "process_key"],
        [new IndexDef("ix_nb_process_catalog_category", ["tenant_id", "category"])],
        [], [],
        EngineCharset: false);

    // 审批语义字典（见 docs/platform-api-refactoring-plan.md §4.2 / §7.1）
    //
    // 三码模型落库：optionId（流程定义拥有，activiti:value/@id）→ semantic（平台稳定枚举）
    // → outcomeClass（由 semantic 推导，统计口径）。node_id 为空 = 流程级默认，非空 = 节点级覆盖。
    //
    // 语义映射「只在平台层」：result 变量仍写 optionId 字符串，${result == 2} 照样成立。
    // 本表是**补充**（流程自定义选项的语义声明），不是重复存 1/2/3 的映射——
    // 那些由 ApprovalSemantics 默认字典覆盖，未登记的选项一律 CUSTOM（不猜）。
    internal static readonly TableDef ApprovalDictionaryTable = new(
        "nb_approval_dictionary",
        "审批语义字典：把流程自定义的 optionId 映射到平台语义（node_id 为空 = 流程级默认）",
        [
            C("tenant_id", ColType.Varchar, 64, false, comment: "租户"),
            C("process_key", ColType.Varchar, 255, false, comment: "流程 key（= BPMN process/@id）"),
            C("node_id", ColType.Varchar, 255, comment: "环节 id（BPMN userTask/@id）；NULL = 流程级默认"),
            C("option_id", ColType.Varchar, 128, false, comment: "选项 id（activiti:value/@id）"),
            C("semantic", ColType.Varchar, 32, false,
                comment: "平台语义 APPROVE/REJECT/RETURN/DELEGATE/CUSTOM（对外 JSON 词汇，不存枚举序号）"),
            C("outcome_class", ColType.Varchar, 16, false,
                comment: "统计口径 POSITIVE/NEGATIVE/NEUTRAL；与 semantic 推导一致时才落非空（冗余便于查询）"),
            C("label", ColType.Varchar, 128, comment: "按钮文案；NULL 取 BPMN activiti:value/@name"),
            C("requires_comment", ColType.TinyInt, nullable: false, def: "0",
                comment: "是否必须填写审批意见（0/1）"),
            C("requires_target_node", ColType.TinyInt, nullable: false, def: "0",
                comment: "是否必须指定目标节点（退回/跳转类，0/1）"),
            C("allowed_target_nodes_json", ColType.MediumText,
                comment: "可选目标节点白名单（JSON 数组）；NULL = 按流程定义默认"),
            C("attributes_json", ColType.MediumText, comment: "流程自定义附加属性（JSON 对象）"),
            C("created_at", ColType.DateTime3, nullable: false, comment: "创建时间（UTC）"),
            C("updated_at", ColType.DateTime3, nullable: false, comment: "更新时间（UTC）"),
        ],
        ["tenant_id", "process_key", "node_id", "option_id"],
        [new IndexDef("ix_nb_approval_dictionary_process", ["tenant_id", "process_key"])],
        [], [],
        EngineCharset: false);

    // 审批记录时间线（见 docs/platform-api-refactoring-plan.md §4.6）
    //
    // 为什么要有这张表：引擎历史（ACT_HI_ACTINST）只记「谁在什么时候办完了任务」，
    // **不认识审批意见与选项语义**（那是业务概念）。而业务侧审批记录又不认识引擎的环节流转。
    // 对外要一条完整时间线，只能在写入的那一刻把两份信息合成好并定稿——读时再拼永远拼不稳。
    //
    // append-only：只 insert，没有 update / delete。审计场景里「改历史」等于没有历史。
    // 单调 sequence 由唯一键 (process_instance_id, sequence) 物理保证；
    // 调用方的增量拉取游标就是这个 sequence（比时间戳可靠：同一毫秒可能有多条）。
    //
    // 刻意不做外键：引擎删实例（结束即删运行态行）不该连带删审计记录。
    internal static readonly TableDef InstanceTimelineTable = new(
        "nb_instance_timeline",
        "审批记录时间线（append-only）：同一实例内 sequence 单调递增，增量拉取以它为游标",
        [
            C("process_instance_id", ColType.Varchar, 64, false, comment: "流程实例 id（= 根执行 id）"),
            // 列名用 seq 而不是 sequence：SEQUENCE 在部分方言 / 版本里是保留字，
            // 建表与 Dapper 拼 SQL 都得给它加引号，两种方言的引号还不一样。换个短名最省事。
            C("seq", ColType.BigInt, nullable: false,
                comment: "实例内单调序号（对外名 sequence，从 1 开始）；主键 (process_instance_id, seq) 是单调性的物理保证"),
            C("tenant_id", ColType.Varchar, 64, false, def: "'default'",
                comment: "租户（平台表一律带租户，便于按租户归档 / 清理）"),
            C("kind", ColType.Varchar, 32, false,
                comment: "条目类型 INSTANCE_STARTED / TASK_COMPLETED / INSTANCE_COMPLETED ...（对外 JSON 词汇，不存枚举序号）"),
            C("activity_id", ColType.Varchar, 255, comment: "相关环节 id（BPMN 元素 id）"),
            C("activity_name", ColType.Varchar, 255,
                comment: "环节名（写入时定稿：定义升级后历史条目仍显示当时的名字）"),
            C("task_id", ColType.Varchar, 64, comment: "相关任务 id"),
            C("actor_id", ColType.Varchar, 255,
                comment: "实际操作人（令牌 subject）；纯服务令牌产生的条目为空，合规报表要单列"),
            C("actor_caller_id", ColType.Varchar, 64, comment: "提交该动作的调用系统（clientId）"),
            C("option_id", ColType.Varchar, 128, comment: "审批选项 id（仅 TASK_COMPLETED）"),
            C("semantic", ColType.Varchar, 32,
                comment: "审批语义（写入时按字典定稿；NULL = 该条目不表达审批意见）"),
            C("comment_text", ColType.Varchar, 2000, comment: "审批意见 / 说明（对外名 comment）"),
            C("target_node_id", ColType.Varchar, 255, comment: "目标节点（退回 / 转办类动作）"),
            C("form_snapshot_task_id", ColType.Varchar, 64,
                comment: "表单快照任务 id；非空时调用方可再拉 /tasks/{id}/form 取当时的字段定义"),
            C("payload_json", ColType.MediumText,
                comment: "附加载荷（JSON 对象）：保留尚未建模的字段，避免为一个新字段改表"),
            C("occurred_at", ColType.DateTime3, nullable: false, comment: "发生时间（UTC）"),
        ],
        ["process_instance_id", "seq"],
        [new IndexDef("ix_nb_instance_timeline_occurred", ["tenant_id", "occurred_at"])],
        [], [],
        EngineCharset: false);

    // 表单快照（见 docs/platform-api-refactoring-plan.md §4.5）
    //
    // 为什么必须落库：流程定义会升级，实例却停在旧版本上。若按「当前定义」渲染旧实例的办理页，
    // 字段就对不上（旧单据显示新字段、新必填项没有值）。快照让历史页永远按当时的定义渲染。
    //
    // 键是 task_id 而不是 (instance_id, activity_id)：同一个环节被退回重办会产生多个任务，
    // 「这一次提交长什么样」的唯一锚点就是任务 id——它同时是时间线条目里的 formSnapshotTaskId。
    //
    // append-only：快照代表「当时的事实」，更新它等于改写历史。并发重复提交时保留第一次写入。
    //
    // 刻意不做外键：实例被删除（含归档清理）时，快照仍要能对来访者解释「这张单当时长什么样」。
    internal static readonly TableDef ApprovalSnapshotTable = new(
        "nb_approval_snapshot",
        "表单快照：提交审批时的字段定义与表单值（键 = task_id，历史页按当时定义渲染）",
        [
            C("task_id", ColType.Varchar, 64, false, comment: "引擎任务 id（主键；= 时间线的 formSnapshotTaskId）"),
            C("process_instance_id", ColType.Varchar, 64, false, comment: "流程实例 id（= 根执行 id）"),
            C("tenant_id", ColType.Varchar, 64, false, def: "'default'",
                comment: "租户（平台表一律带租户，便于按租户归档 / 清理）"),
            C("process_key", ColType.Varchar, 255, comment: "流程 key（写入时定稿：定义改 key 后仍显示当时的名字）"),
            C("process_definition_id", ColType.Varchar, 64,
                comment: "流程定义 id（引擎内部标识，用于反查原始 BPMN）"),
            C("definition_version", ColType.Int, nullable: false, def: "1",
                comment: "提交时的定义版本（同 key 多版本的第几版；旧定义上发起的单据按旧版渲染）"),
            C("activity_id", ColType.Varchar, 255, comment: "环节 id（BPMN userTask/@id）"),
            C("activity_name", ColType.Varchar, 255, comment: "环节名（写入时定稿）"),
            C("actor_id", ColType.Varchar, 255, comment: "提交人（令牌 subject）"),
            C("actor_caller_id", ColType.Varchar, 64, comment: "提交该动作的调用系统（clientId）"),
            C("option_id", ColType.Varchar, 128, comment: "本次审批选项 id"),
            C("schema_json", ColType.MediumText,
                comment: "当时的表单定义（JSON 数组：fieldId/type/label/required/options…）"),
            C("values_json", ColType.MediumText, comment: "当时的表单值（JSON 对象，值一律字符串）"),
            C("attachments_json", ColType.MediumText,
                comment: "当时的附件引用（JSON 数组；平台只存引用，不存文件本体）"),
            C("captured_at", ColType.DateTime3, nullable: false, comment: "定稿时间（UTC）"),
        ],
        ["task_id"],
        [
            new IndexDef("ix_nb_approval_snapshot_instance", ["tenant_id", "process_instance_id"]),
            // 保留期清理按 captured_at 删（AdminArchiveCleanupService，与历史归档同一切点）。
            // 这张表只增不减且行宽可观，没有这个索引就是每 6 小时全表扫一次。
            new IndexDef("ix_nb_approval_snapshot_captured", ["captured_at"]),
        ],
        [], [],
        EngineCharset: false);

    // 0014 外部任务表（`activiti:topic` 的等待点）
    internal static readonly TableDef ExternalTaskTable = new(
        "nb_external_task",
        "外部任务：serviceTask 标记 activiti:topic 时登记的待领取记录（独立部署下由 worker 拉走执行并回执）",
        [
            C("id", ColType.Varchar, 64, false, comment: "主键"),
            C("process_instance_id", ColType.Varchar, 64, false, comment: "流程实例 id（= 根执行 id）"),
            C("execution_id", ColType.Varchar, 64, false,
                comment: "停留在该节点的执行 id（回执后靠它推进流程，不能只存实例 id）"),
            C("process_definition_id", ColType.Varchar, 64, comment: "流程定义 id"),
            C("process_definition_key", ColType.Varchar, 255, comment: "流程 key（回显用，可空）"),
            C("activity_id", ColType.Varchar, 255, false, comment: "节点 id（BPMN serviceTask/@id）"),
            C("activity_name", ColType.Varchar, 255, comment: "节点名（回显用）"),
            C("topic", ColType.Varchar, 255, false, comment: "主题（activiti:topic，worker 按它订阅）"),
            C("tenant_id", ColType.Varchar, 64, false, def: "'default'", comment: "租户"),
            C("business_key", ColType.Varchar, 255, comment: "业务键（回显用）"),
            C("payload_json", ColType.MediumText,
                comment: "领取时交给 worker 的变量快照（JSON 对象；快照而非实时视图，两侧不互相覆盖）"),
            C("priority", ColType.Int, nullable: false, def: "0", comment: "优先级，大的先领"),
            C("retries", ColType.Int, nullable: false, def: "3", comment: "剩余重试次数；0 且再次失败进死信"),
            C("status", ColType.Varchar, 32, nullable: false, def: "'PENDING'",
                comment: "状态词汇：PENDING / LOCKED / COMPLETED / DEAD_LETTER（存字符串不存序号）"),
            C("worker_id", ColType.Varchar, 255, comment: "锁持有者（worker id）；无人持有时为空"),
            C("lock_expires_at", ColType.DateTime3,
                comment: "锁到期时间（UTC）；持有者崩溃后不续租即到期，任务可被别的 worker 接管"),
            C("next_due_at", ColType.DateTime3, comment: "失败退避后可再被领取的时间（UTC）；空=立即可领"),
            C("error_message", ColType.Varchar, 1000, comment: "最近一次失败原因（摘要）"),
            C("error_details", ColType.MediumText, comment: "最近一次失败详情（堆栈 / 上下文）"),
            C("created_at", ColType.DateTime3, nullable: false, comment: "登记时间（UTC）"),
            C("completed_at", ColType.DateTime3, comment: "回执成功时间（UTC）"),
        ],
        ["id"],
        [
            // fetch & lock 的主查询：按状态 + 主题找可领取的，再按退避时间与优先级排序
            new IndexDef("ix_nb_external_task_fetch", ["status", "topic", "next_due_at"]),
            // 锁过期扫描（worker 崩溃后回收）
            new IndexDef("ix_nb_external_task_lock", ["lock_expires_at"]),
            // 实例删除 / 终止时清理（流程已不存在，留着是孤儿数据）
            new IndexDef("ix_nb_external_task_instance", ["tenant_id", "process_instance_id"]),
        ],
        [], [],
        EngineCharset: false);

    // 0010 HTTP 幂等键表（幂等，无表注释）
    internal static readonly TableDef IdempotencyTable = new(
        "ACT_IDEMPOTENCY",
        "",
        [
            C("IDEMPOTENCY_KEY_", ColType.Varchar, 120, false),
            C("REQUEST_HASH_", ColType.Varchar, 64, false),
            C("STATUS_CODE_", ColType.Int),
            C("CONTENT_TYPE_", ColType.Varchar, 200),
            C("RESPONSE_BODY_", ColType.MediumText),
            C("CREATED_AT_", ColType.DateTime2, nullable: false),
            C("EXPIRES_AT_", ColType.DateTime2, nullable: false),
        ],
        ["IDEMPOTENCY_KEY_"],
        [new IndexDef("ACT_IDX_IDEMP_EXPIRES", ["EXPIRES_AT_"])],
        [], [],
        EngineCharset: false);

    // 0012 命令级幂等表（引擎扩展；与 HTTP 幂等键表区分，见 CommandIdempotencyKeys）
    internal static readonly TableDef CommandIdempotencyTable = new(
        "ACT_CMD_IDEMPOTENCY",
        "命令级幂等：同一 commandId 重复执行同一命令只生效一次（outbox 重投 / 作业重试 / 消息重复消费）",
        [
            C("ID_", ColType.Varchar, 64, nullable: false, comment: "记录 id（IIdGenerator 分配）"),
            C("COMMAND_KEY_", ColType.Varchar, 200, nullable: false, comment: "去重键（cmd:<commandId>:<targetId>）"),
            C("COMMAND_NAME_", ColType.Varchar, 120, nullable: false, comment: "命令类型名（同一键只允许同一类命令占位）"),
            C("RESULT_PAYLOAD_", ColType.MediumText, comment: "首次执行的结果快照（审计 / 诊断）"),
            C("CREATED_AT_", ColType.DateTime2, nullable: false, comment: "首次占位时间（UTC）"),
            C("EXPIRES_AT_", ColType.DateTime2, nullable: false, comment: "过期时间（UTC）"),
        ],
        ["ID_"],
        [new IndexDef("ACT_IDX_CMD_IDEMP_EXPIRES", ["EXPIRES_AT_"])],
        [new IndexDef("ACT_IDX_CMD_IDEMP_KEY", ["COMMAND_KEY_"])],
        [],
        EngineCharset: false);

    // 0006 定时作业表（引擎扩展，Activiti 原表裁列后保留核心列）
    internal static readonly TableDef TimerJobTable = new(
        "ACT_RU_TIMER_JOB",
        "定时作业表：边界定时器（一次性）到点后触发",
        [
            C("ID_", ColType.Varchar, 64, false),
            C("REV_", ColType.Int, def: "0"),
            C("TYPE_", ColType.Varchar, 255, false),
            C("EXECUTION_ID_", ColType.Varchar, 64, false),
            C("PROCESS_INSTANCE_ID_", ColType.Varchar, 64, false),
            C("PROC_DEF_ID_", ColType.Varchar, 64),
            C("ACTIVITY_ID_", ColType.Varchar, 255),
            C("BOUNDARY_EVENT_ID_", ColType.Varchar, 255),
            C("DUEDATE_", ColType.DateTime3, nullable: false),
            C("TENANT_ID_", ColType.Varchar, 255, def: "''"),
            C("CREATE_TIME_", ColType.DateTime3, nullable: false),
            // 可靠性列：失败重试与死信（毒作业不能无限重试）
            C("RETRIES_", ColType.Int, def: "3", comment: "剩余重试次数；0 表示已进死信"),
            C("DEAD_LETTER_", ColType.TinyInt, def: "0", comment: "1=已进死信，不再参与到期轮询"),
            C("EXCEPTION_MSG_", ColType.Varchar, 1000, comment: "最后一次失败原因"),
            C("LAST_FAILURE_", ColType.DateTime3, comment: "最后一次失败时间（UTC）"),
            // 0012 作业租约：执行前只标记归属，执行成功后才删除（宿主崩溃后作业自动回收）
            C("LOCK_OWNER_", ColType.Varchar, 64, comment: "持有该作业租约的宿主实例 id"),
            C("LOCK_EXP_TIME_", ColType.DateTime3, comment: "租约到期时间（UTC）；为 null 表示无人持有"),
        ],
        ["ID_"],
        [
            new IndexDef("ACT_IDX_TIMER_JOB_DUEDATE", ["DUEDATE_"]),
            new IndexDef("ACT_IDX_TIMER_JOB_EXECUTION", ["EXECUTION_ID_"]),
            new IndexDef("ACT_IDX_TIMER_JOB_PROCINST", ["PROCESS_INSTANCE_ID_"]),
            new IndexDef("ACT_IDX_TIMER_JOB_DEADLINE", ["DEAD_LETTER_", "DUEDATE_"]),
        ],
        [], [],
        EngineCharset: true);

    // 0007 流程实例迁移审计表（引擎扩展）
    internal static readonly TableDef MigrationTable = new(
        "ACT_HI_MIGRATION",
        "流程实例迁移审计表：记录源/目标定义、活动映射、原因与操作人",
        [
            C("ID_", ColType.Varchar, 64, false),
            C("PROC_INST_ID_", ColType.Varchar, 64, false),
            C("SOURCE_PROC_DEF_ID_", ColType.Varchar, 255, false),
            C("TARGET_PROC_DEF_ID_", ColType.Varchar, 255, false),
            C("ACTIVITY_MAPPINGS_", ColType.Varchar, 4000),
            C("REASON_", ColType.Varchar, 400),
            C("USER_ID_", ColType.Varchar, 255),
            C("MIGRATED_AT_", ColType.DateTime3, nullable: false),
        ],
        ["ID_"],
        [
            new IndexDef("ACT_IDX_MIGRATION_PROCINST", ["PROC_INST_ID_"]),
            // 迁移预演 / 审计按流程实例 + 时间倒序取数
            new IndexDef("ACT_IDX_MIGRATION_PROCINST_TIME", ["PROC_INST_ID_", "MIGRATED_AT_"]),
        ],
        [], [],
        EngineCharset: true);

    // 异步作业表（引擎扩展：asyncBefore / asyncAfter；一个作业 = 一个命令）
    internal static readonly TableDef AsyncJobTable = new(
        "ACT_RU_JOB",
        "异步作业表：asyncBefore / asyncAfter 作业（独占，一个作业 = 一个命令）",
        [
            C("ID_", ColType.Varchar, 64, nullable: false, comment: "作业 id（IIdGenerator 分配）"),
            C("REV_", ColType.Int, nullable: false, def: "1", comment: "乐观锁版本"),
            C("EXECUTION_ID_", ColType.Varchar, 64, nullable: false, comment: "执行 id"),
            C("PROCESS_INSTANCE_ID_", ColType.Varchar, 64, nullable: false, comment: "流程实例 id"),
            C("PROC_DEF_ID_", ColType.Varchar, 255, comment: "流程定义 id"),
            C("ACTIVITY_ID_", ColType.Varchar, 255, nullable: false, comment: "活动 id"),
            C("PHASE_", ColType.Varchar, 16, nullable: false, comment: "before / after"),
            C("DUEDATE_", ColType.DateTime3, nullable: false, comment: "到期时间（UTC）"),
            C("TENANT_ID_", ColType.Varchar, 64, nullable: false, def: "''", comment: "租户"),
            C("CREATE_TIME_", ColType.DateTime3, nullable: false, comment: "创建时间（UTC）"),
            C("RETRIES_", ColType.Int, nullable: false, def: "3", comment: "剩余重试次数"),
            C("DEAD_LETTER_", ColType.TinyInt, nullable: false, def: "0", comment: "1=已进死信"),
            C("EXCEPTION_MSG_", ColType.Varchar, 1000, comment: "最后一次失败原因"),
            C("LAST_FAILURE_", ColType.DateTime3, comment: "最后一次失败时间（UTC）"),
            // 0012 作业租约：执行前只标记归属，执行成功后才删除（宿主崩溃后作业自动回收）
            C("LOCK_OWNER_", ColType.Varchar, 64, comment: "持有该作业租约的宿主实例 id"),
            C("LOCK_EXP_TIME_", ColType.DateTime3, comment: "租约到期时间（UTC）；为 null 表示无人持有"),
        ],
        ["ID_"],
        [
            new IndexDef("ACT_IDX_JOB_DUE", ["DUEDATE_", "DEAD_LETTER_"]),
            new IndexDef("ACT_IDX_JOB_EXECUTION", ["EXECUTION_ID_"]),
            new IndexDef("ACT_IDX_JOB_PROC_INST", ["PROCESS_INSTANCE_ID_"]),
        ],
        [], [],
        EngineCharset: true);

    // 事务性 outbox（引擎事件的事务性外发队列，至少一次投递）
    internal static readonly TableDef EventOutboxTable = new(
        "ACT_EVT_OUTBOX",
        "事务性事件 outbox：引擎事件与业务写入同事务落库，由发布器至少一次投递",
        [
            C("ID_", ColType.Varchar, 64, nullable: false, comment: "事件 id（订阅方幂等键）"),
            C("EVENT_TYPE_", ColType.Varchar, 64, nullable: false, comment: "事件类型（PROCESS_COMPLETED / TASK_CREATED ...）"),
            C("ENTITY_ID_", ColType.Varchar, 64, comment: "实体 id（任务 / 活动 / 实例）"),
            C("PROCESS_INSTANCE_ID_", ColType.Varchar, 64, comment: "流程实例 id"),
            C("PROC_DEF_ID_", ColType.Varchar, 255, comment: "流程定义 id"),
            C("EXECUTION_ID_", ColType.Varchar, 64, comment: "执行 id"),
            C("ACTIVITY_ID_", ColType.Varchar, 255, comment: "活动 id"),
            C("TENANT_ID_", ColType.Varchar, 64, nullable: false, def: "''", comment: "租户"),
            C("PAYLOAD_", ColType.MediumText, comment: "事件载荷（JSON）"),
            C("CREATE_TIME_", ColType.DateTime3, nullable: false, comment: "创建时间（UTC）"),
            C("PUBLISHED_", ColType.TinyInt, nullable: false, def: "0", comment: "1=已发布"),
            C("PUBLISH_ATTEMPTS_", ColType.Int, nullable: false, def: "0", comment: "发布尝试次数"),
            C("DEAD_LETTER_", ColType.TinyInt, nullable: false, def: "0", comment: "1=投递失败进死信"),
            C("LAST_ERROR_", ColType.Varchar, 1000, comment: "最后一次发布失败原因"),
            C("NEXT_ATTEMPT_", ColType.DateTime3, comment: "下次可发布时间（UTC）"),
            C("PUBLISHED_TIME_", ColType.DateTime3, comment: "发布时间（UTC）"),
        ],
        ["ID_"],
        [
            new IndexDef("ACT_IDX_OUTBOX_PENDING", ["PUBLISHED_", "DEAD_LETTER_", "CREATE_TIME_"]),
            new IndexDef("ACT_IDX_OUTBOX_PROC_INST", ["PROCESS_INSTANCE_ID_"]),
        ],
        [], [],
        EngineCharset: true);

    // 0002 引擎初值（schema.version / schema.history / next.dbid）
    internal static readonly IReadOnlyList<IReadOnlyList<object?>> PropertySeeds =
    [
        ["schema.version", "8.1.4", "1"],
        ["schema.history", "create(8.1.4)", "1"],
        ["next.dbid", "1", "1"],
    ];

    private static Column C(
        string name,
        ColType type,
        int? len = null,
        bool nullable = true,
        string? def = null,
        string? comment = null) => new(name, type, len, nullable, def, comment);

    private static TableIndexDef TI(string name, string table, IReadOnlyList<string> columns) =>
        new(table, name, columns);

    private static ForeignKeyDef F(
        string name,
        string table,
        string column,
        string referencedTable,
        string referencedColumn,
        bool cascadeDelete = false,
        bool cascadeUpdate = false) => new(table, name, column, referencedTable, referencedColumn, cascadeDelete, cascadeUpdate);
}

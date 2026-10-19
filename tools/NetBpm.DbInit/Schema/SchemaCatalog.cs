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

    // 0009 运行期查询索引（性能增强；对齐后为两方言并集，16 个）
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
        []);

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

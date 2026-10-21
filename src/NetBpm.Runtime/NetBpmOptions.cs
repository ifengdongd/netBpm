using NetBpm.Persistence;

namespace NetBpm.Runtime;

/// <summary>引擎宿主配置。</summary>
public sealed class NetBpmOptions
{
    /// <summary>
    /// 数据库方言。只支持 SQL Server 与 MySQL（开发计划 §1.3 第 12 条）。
    /// </summary>
    public DatabaseType DatabaseType { get; set; } = DatabaseType.SqlServer;

    /// <summary>连接字符串。数据库模式下必填。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>ID 区块大小，默认 2500（必须与 Activiti 的 <c>idBlockSize</c> 一致，见 §7.3）。</summary>
    public int IdBlockSize { get; set; } = DbIdGenerator.DefaultIdBlockSize;

    /// <summary>
    /// 启用历史记录（<c>ACT_HI_*</c>）。默认关闭 —— 历史写入实现在 P5/P6 的后半段。
    /// </summary>
    public bool EnableHistory { get; set; }

    /// <summary>
    /// 启用事务性 outbox（<c>ACT_EVT_OUTBOX</c>）：引擎事件与业务变更同事务落库，
    /// 由后台发布器投递给 <see cref="NetBpm.Engine.Events.IOutboxEventHandler"/>。
    /// <para>
    /// 默认关闭：关闭时事件只走进程内 <c>activiti:eventListener</c>，不做跨进程投递。
    /// 开启后每个事件多一次 insert（同命令事务，不额外开连接）。
    /// </para>
    /// </summary>
    public bool EnableOutbox { get; set; }

    /// <summary>
    /// 使用内存执行树（无数据库宿主）。
    /// <para>
    /// <see langword="false"/> 时进入数据库模式：执行树 / 流程定义走 Dapper 仓储，
    /// 且每个命令开启一个数据库事务。此模式下 <see cref="ConnectionString"/> 必填。
    /// </para>
    /// </summary>
    public bool UseInMemoryExecutionTree { get; set; } = true;

    /// <summary>启动时校验 <c>ACT_GE_PROPERTY.schema.version</c>。</summary>
    public bool ValidateSchemaOnStartup { get; set; } = true;

    /// <summary>启动时执行 <c>db/&lt;dialect&gt;/netbpm_init.sql</c>（幂等：库里缺什么补什么）。</summary>
    public bool AutoMigrate { get; set; }

    /// <summary>是否使用数据库持久化。</summary>
    public bool UsesDatabase => !UseInMemoryExecutionTree;
}

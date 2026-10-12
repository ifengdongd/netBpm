namespace NetBpm.Persistence;

/// <summary>
/// 支持的数据库方言。Activiti 支持 8 种，本方案只做 2 种（开发计划 §1.3 第 12 条）。
/// </summary>
public enum DatabaseType
{
    /// <summary>SQL Server（脚本见 <c>db/sqlserver</c>）。</summary>
    SqlServer = 1,

    /// <summary>MySQL（脚本见 <c>db/mysql</c>）。</summary>
    MySql = 2,
}

using System.Data.Common;
using Dapper;
using NetBpm.Identity;
using NetBpm.Persistence;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// MySQL 版组织架构：落到 0005 脚本建的 <c>sys_dept</c> / <c>sys_emp</c> /
/// <c>sys_emp_group</c> / <c>sys_emp_group_member</c> 四张表。
/// </summary>
/// <remarks>
/// <para>
/// 一个类顶两块契约：<see cref="IOrgDirectory"/>（业务查询：部门 / 员工 / 审批人名单）
/// 与 <see cref="IUserGroupManager"/>（引擎查待办要用的「用户 ↔ 组」展开）——
/// 两边读的是同一批表，分开两个类只会各自写一份 SQL。
/// </para>
/// <para>
/// 🔑 流程里的用户 id = <c>sys_emp.domain_account</c>（域账号），
/// 组 id = <c>sys_emp_group.group_id</c>（流程定义 <c>candidateGroups</c> 写的就是它）。
/// 真实项目接入既有权限体系（AD / 权限中台）时，把这里的 SQL 换成对应服务的调用即可，
/// 端点与流程定义一行不用改。
/// </para>
/// </remarks>
public sealed class MySqlOrgDirectory : IUserGroupManager, IOrgDirectory
{
    private readonly IDbConnectionFactory _connections;
    private readonly long _tenantId;

    /// <summary>构造组织架构目录。</summary>
    /// <param name="connections">连接工厂（由 <c>AddNetBpm</c> 注册）。</param>
    /// <param name="configuration">配置（租户 id）。</param>
    public MySqlOrgDirectory(IDbConnectionFactory connections, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(configuration);

        _connections = connections;
        _tenantId = configuration.GetValue("Leave:TenantId", 1L);
    }

    // ───────────────────────── IOrgDirectory ─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OrgDepartment>> FindDepartmentsAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<OrgDepartment>(new CommandDefinition(
            """
            select dept_id   as DeptId,
                   dept_name as Name,
                   parent_id as ParentId
              from sys_dept
             where tenant_id = @tenantId
             order by sort_order, dept_id
            """,
            new { tenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OrgEmployee>> FindEmployeesAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<EmployeeRow>(new CommandDefinition(
            SelectEmployeesSqlHead + """
                 group by e.emp_id, e.domain_account, e.emp_name, d.dept_name, d.sort_order
                 order by d.sort_order, e.emp_id
            """,
            new { tenantId = _tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(ToEmployee)];
    }

    /// <inheritdoc />
    public async ValueTask<OrgEmployee?> FindEmployeeAsync(string domainAccount, CancellationToken ct = default)
    {
        // 空账号是合法输入（旧数据 / 未登录场景）—— 查不到就返回 null，不要抛
        if (string.IsNullOrWhiteSpace(domainAccount))
        {
            return null;
        }

        await using var connection = _connections.CreateConnection();

        var row = await connection.QueryFirstOrDefaultAsync<EmployeeRow>(new CommandDefinition(
            SelectEmployeesSqlHead + """
                 and e.domain_account = @domainAccount
                 group by e.emp_id, e.domain_account, e.emp_name, d.dept_name
                 limit 1
            """,
            new { tenantId = _tenantId, domainAccount },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : ToEmployee(row);
    }

    // ───────────────────────── IUserGroupManager（引擎契约）─────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<string>> FindGroupIdsByUserAsync(
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await using var connection = _connections.CreateConnection();

        var groups = await connection.QueryAsync<string>(new CommandDefinition(
            """
            select g.group_id
              from sys_emp e
              join sys_emp_group_member m on m.emp_id = e.emp_id
              join sys_emp_group g on g.group_id = m.group_id
             where e.tenant_id = @tenantId
               and e.domain_account = @userId
             order by g.group_id
            """,
            new { tenantId = _tenantId, userId },
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. groups];
    }

    /// <inheritdoc />
    public async ValueTask<UserGroup?> FindGroupByIdAsync(string groupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        await using var connection = _connections.CreateConnection();

        var name = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "select group_name from sys_emp_group where group_id = @groupId limit 1",
            new { groupId },
            cancellationToken: ct)).ConfigureAwait(false);

        return name is null ? null : new UserGroup(groupId, name);
    }

    /// <inheritdoc />
    public async ValueTask<string?> FindUserIdAsync(string userId, CancellationToken ct = default)
    {
        var employee = await FindEmployeeAsync(userId, ct).ConfigureAwait(false);

        return employee?.DomainAccount;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 「流程走到组长审批时，审批人到底是谁」就是靠它答出来的 —— 任务上挂的是候选组，
    /// 引擎/应用层借这一步把组展开成域账号清单，再写进 <c>instance_current_task.assign_emp_id</c>。
    /// </remarks>
    public async ValueTask<IReadOnlyCollection<string>> FindUserIdsByGroupAsync(
        string groupId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        await using var connection = _connections.CreateConnection();

        var accounts = await connection.QueryAsync<string>(new CommandDefinition(
            """
            select e.domain_account
              from sys_emp_group_member m
              join sys_emp e on e.emp_id = m.emp_id
             where m.tenant_id = @tenantId
               and m.group_id = @groupId
             order by e.domain_account
            """,
            new { tenantId = _tenantId, groupId },
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. accounts];
    }

    // ───────────────────────── SQL 与行映射 ─────────────────────────

    /// <summary>员工 + 部门名 + 所属组（<b>只有 head</b>：from / join / where 租户；group by 由调用方按需拼）。</summary>
    private const string SelectEmployeesSqlHead = """
        select e.domain_account                                    as DomainAccount,
               e.emp_name                                          as Name,
               d.dept_name                                         as DeptName,
               group_concat(g.group_id order by g.group_id)        as GroupsCsv
          from sys_emp e
          join sys_dept d on d.dept_id = e.dept_id and d.tenant_id = e.tenant_id
          left join sys_emp_group_member m on m.emp_id = e.emp_id
          left join sys_emp_group g on g.group_id = m.group_id
         where e.tenant_id = @tenantId
        """;

    private static OrgEmployee ToEmployee(EmployeeRow row) => new(
        row.DomainAccount,
        row.Name,
        row.DeptName,
        string.IsNullOrWhiteSpace(row.GroupsCsv)
            ? []
            : [.. row.GroupsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

    private sealed class EmployeeRow
    {
        public string DomainAccount { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string DeptName { get; set; } = string.Empty;

        public string? GroupsCsv { get; set; }
    }
}

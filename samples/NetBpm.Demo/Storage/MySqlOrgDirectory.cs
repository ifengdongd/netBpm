using Dapper;
using NetBpm.Demo.Common;
using NetBpm.Identity;
using NetBpm.Persistence;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Storage;

/// <summary>
/// MySQL 模式的组织架构：直接读 0003 建的四张 sys_* 表
/// （sys_dept / sys_emp / sys_emp_group / sys_emp_group_member）。
/// </summary>
/// <remarks>
/// <para>
/// 与业务数据存储一起按 <c>NetBpm:DatabaseType</c> 切换（见
/// <see cref="DemoServiceCollectionExtensions.AddDemoStores"/>）：MySQL 模式用本实现，
/// JSON / SqlServer 演示模式沿用 <see cref="InMemoryOrgDirectory"/>。
/// 同时实现 <see cref="IOrgDirectory"/>（业务查询）与 <see cref="IUserGroupManager"/>
/// （引擎查候选组待办要用的「用户 ↔ 组」展开）—— 同一份表数据，两个视角。
/// </para>
/// <para>
/// 语义与内存版对齐：种子数据就是部门 101-103、员工 1001-1005、组 leaders/managers，
/// 所以「换库不换人」—— 请假审批人仍是 leader1（赵志强）、订单审批人仍是 manager1（孙建国）。
/// 排序口径也一致：员工按部门 sort_order + emp_id，组成员按域账号序。
/// </para>
/// <para>
/// 租户 id 取配置 <see cref="ConstField.WorkflowTenantConfigKey"/>（缺省 1），
/// 与业务存储的租户口径一致；<c>domain_account</c> / <c>group_id</c> 按唯一键精确匹配
/// （与 <see cref="IWorkflowStoreDialect.SelectApproverNameSql"/> 的既有口径相同）。
/// </para>
/// </remarks>
public sealed class MySqlOrgDirectory : IUserGroupManager, IOrgDirectory
{
    private readonly IDbConnectionFactory _connections;
    private readonly long _tenantId;

    /// <summary>构造组织架构（与引擎侧同一个连接工厂）。</summary>
    public MySqlOrgDirectory(IDbConnectionFactory connections, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(configuration);

        _connections = connections;
        _tenantId = configuration.GetValue(ConstField.WorkflowTenantConfigKey, 1L);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OrgDepartment>> FindDepartmentsAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<DeptRow>(
            new CommandDefinition(
                """
                select dept_id   as DeptId,
                       dept_name as Name,
                       parent_id as ParentId
                  from sys_dept
                 where tenant_id = @TenantId
                 order by sort_order, dept_id
                """,
                new { TenantId = _tenantId },
                cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(row => new OrgDepartment(row.DeptId, row.Name, row.ParentId))];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OrgEmployee>> FindEmployeesAsync(CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var rows = (await connection.QueryAsync<EmpRow>(
            new CommandDefinition(
                """
                select e.domain_account as DomainAccount,
                       e.emp_name       as Name,
                       d.dept_name      as DeptName
                  from sys_emp e
                  join sys_dept d on d.dept_id = e.dept_id
                 where e.tenant_id = @TenantId
                 order by d.sort_order, d.dept_id, e.emp_id
                """,
                new { TenantId = _tenantId },
                cancellationToken: ct)).ConfigureAwait(false)).AsList();

        // 组成员单独查（一名员工可属多组），再按域账号拼回去
        var memberships = await connection.QueryAsync<GroupMemberRow>(
            new CommandDefinition(
                """
                select m.group_id       as GroupId,
                       e.domain_account as DomainAccount
                  from sys_emp_group_member m
                  join sys_emp e on e.emp_id = m.emp_id
                  join sys_emp_group g on g.group_id = m.group_id
                 where m.tenant_id = @TenantId
                 order by g.sort_order, g.group_id
                """,
                new { TenantId = _tenantId },
                cancellationToken: ct)).ConfigureAwait(false);

        var groupsByAccount = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var membership in memberships)
        {
            if (!groupsByAccount.TryGetValue(membership.DomainAccount, out var groups))
            {
                groups = [];
                groupsByAccount.Add(membership.DomainAccount, groups);
            }

            groups.Add(membership.GroupId);
        }

        return
        [
            .. rows.Select(row => new OrgEmployee(
                row.DomainAccount,
                row.Name,
                row.DeptName,
                groupsByAccount.TryGetValue(row.DomainAccount, out var groups) ? groups : [])),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<OrgEmployee?> FindEmployeeAsync(string domainAccount, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainAccount);

        await using var connection = _connections.CreateConnection();

        var row = await connection.QuerySingleOrDefaultAsync<EmpRow>(
            new CommandDefinition(
                """
                select e.domain_account as DomainAccount,
                       e.emp_name       as Name,
                       d.dept_name      as DeptName
                  from sys_emp e
                  join sys_dept d on d.dept_id = e.dept_id
                 where e.tenant_id = @TenantId
                   and e.domain_account = @Account
                """,
                new { TenantId = _tenantId, Account = domainAccount },
                cancellationToken: ct)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var groups = await connection.QueryAsync<string>(
            new CommandDefinition(
                """
                select m.group_id
                  from sys_emp_group_member m
                  join sys_emp e on e.emp_id = m.emp_id
                  join sys_emp_group g on g.group_id = m.group_id
                 where m.tenant_id = @TenantId
                   and e.domain_account = @Account
                 order by g.sort_order, g.group_id
                """,
                new { TenantId = _tenantId, Account = domainAccount },
                cancellationToken: ct)).ConfigureAwait(false);

        return new OrgEmployee(row.DomainAccount, row.Name, row.DeptName, [.. groups]);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<string>> FindGroupIdsByUserAsync(
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await using var connection = _connections.CreateConnection();

        var groupIds = await connection.QueryAsync<string>(
            new CommandDefinition(
                """
                select g.group_id
                  from sys_emp_group_member m
                  join sys_emp e on e.emp_id = m.emp_id
                  join sys_emp_group g on g.group_id = m.group_id
                 where m.tenant_id = @TenantId
                   and e.domain_account = @Account
                 order by g.sort_order, g.group_id
                """,
                new { TenantId = _tenantId, Account = userId },
                cancellationToken: ct)).ConfigureAwait(false);

        return groupIds.AsList();
    }

    /// <inheritdoc />
    public async ValueTask<UserGroup?> FindGroupByIdAsync(string groupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        await using var connection = _connections.CreateConnection();

        var name = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                select group_name
                  from sys_emp_group
                 where tenant_id = @TenantId
                   and group_id = @GroupId
                """,
                new { TenantId = _tenantId, GroupId = groupId },
                cancellationToken: ct)).ConfigureAwait(false);

        return name is null ? null : new UserGroup(groupId, name);
    }

    /// <inheritdoc />
    public async ValueTask<string?> FindUserIdAsync(string userId, CancellationToken ct = default) =>
        (await FindEmployeeAsync(userId, ct).ConfigureAwait(false))?.DomainAccount;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<string>> FindUserIdsByGroupAsync(
        string groupId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        await using var connection = _connections.CreateConnection();

        var accounts = await connection.QueryAsync<string>(
            new CommandDefinition(
                """
                select e.domain_account
                  from sys_emp_group_member m
                  join sys_emp e on e.emp_id = m.emp_id
                 where m.tenant_id = @TenantId
                   and m.group_id = @GroupId
                """,
                new { TenantId = _tenantId, GroupId = groupId },
                cancellationToken: ct)).ConfigureAwait(false);

        // 组成员按域账号稳定排序（与内存版一致）—— 「流程走到组长审批时，审批人到底是谁」靠它答出
        return [.. accounts.OrderBy(account => account, StringComparer.Ordinal)];
    }

    private sealed record DeptRow(long DeptId, string Name, long? ParentId);

    private sealed record EmpRow(string DomainAccount, string Name, string DeptName);

    private sealed record GroupMemberRow(string GroupId, string DomainAccount);
}

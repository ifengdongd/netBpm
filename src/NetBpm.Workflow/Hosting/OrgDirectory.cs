using NetBpm.Identity;

namespace NetBpm.Workflow.Hosting;

/// <summary>部门。</summary>
/// <param name="DeptId">部门 id（业务表 <c>apply_dept_id</c> 存的是它）。</param>
/// <param name="Name">部门名称。</param>
/// <param name="ParentId">上级部门 id；<see langword="null"/> 表示顶级。</param>
public sealed record OrgDepartment(long DeptId, string Name, long? ParentId);

/// <summary>员工（含部门名与所属审批组）。</summary>
/// <param name="DomainAccount">域账号 —— <b>流程里的用户 id</b>（发起人 / 审批人 / 候选人都是它）。</param>
/// <param name="Name">员工姓名。</param>
/// <param name="DeptName">部门名称。</param>
/// <param name="Groups">所属审批组 id（对应流程定义里的 <c>candidateGroups</c>）。</param>
public sealed record OrgEmployee(string DomainAccount, string Name, string DeptName, IReadOnlyList<string> Groups);

/// <summary>
/// 组织架构查询端口：Demo 的「真实环境」数据源 —— 部门 / 员工 / 审批组。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IUserGroupManager"/>（引擎查待办要用的「用户 ↔ 组」展开）分得很清：
/// 后者是<b>流程引擎的契约</b>、只知道组 id 与用户 id；本端口是<b>业务自己的</b>，
/// 负责把这两样东西翻译成人能看懂的姓名 / 部门 / 审批人名单。
/// </para>
/// <para>
/// Demo 有两份实现，随业务数据存储按 <c>NetBpm:DatabaseType</c> 一起切换（见 Demo 的
/// <c>AddDemoStores</c>）：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>MySql</b>：<c>MySqlOrgDirectory</c>（Demo 存储层）—— 直接读 0003 建的四张
///   <c>sys_dept</c> / <c>sys_emp</c> / <c>sys_emp_group</c> / <c>sys_emp_group_member</c> 表，
///   按租户过滤，演示语义与内存版一致（换库不换人）；
///   </description></item>
///   <item><description>
///   <b>SqlServer / JSON</b>：<see cref="InMemoryOrgDirectory"/> —— 内置一套与 e2e /
///   演示页对齐的固定数据（SqlServer 模式的 sys_* 表结构相同，需要真表版时照
///   <c>MySqlOrgDirectory</c> 平移一份即可）。
///   </description></item>
/// </list>
/// </remarks>
public interface IOrgDirectory
{
    /// <summary>全部部门（按展示顺序）。</summary>
    ValueTask<IReadOnlyList<OrgDepartment>> FindDepartmentsAsync(CancellationToken ct = default);

    /// <summary>全部员工（含部门名与所属组，按部门排序）。</summary>
    ValueTask<IReadOnlyList<OrgEmployee>> FindEmployeesAsync(CancellationToken ct = default);

    /// <summary>按域账号查一个员工；查不到返回 <see langword="null"/>。</summary>
    ValueTask<OrgEmployee?> FindEmployeeAsync(string domainAccount, CancellationToken ct = default);
}

/// <summary>
/// JSON 演示模式的组织架构：内置一套与 e2e / 演示页对齐的固定数据。
/// </summary>
/// <remarks>
/// 用户 id（域账号）与旧 <see cref="IUserGroupManager"/> 演示实现完全一致
/// （alice / bob / leader1 / leader2 / manager1），换 MySQL 模式后语义不变。
/// </remarks>
public sealed class InMemoryOrgDirectory : IUserGroupManager, IOrgDirectory
{
    private static readonly IReadOnlyList<OrgDepartment> Departments =
    [
        new OrgDepartment(101, "信息技术部", null),
        new OrgDepartment(102, "成本管理部", null),
        new OrgDepartment(103, "综合管理部", null),
    ];

    private static readonly IReadOnlyList<OrgEmployee> Employees =
    [
        new OrgEmployee("alice", "王小爱", "信息技术部", []),
        new OrgEmployee("bob", "李小波", "信息技术部", []),
        new OrgEmployee("leader1", "赵志强", "信息技术部", ["leaders"]),
        new OrgEmployee("leader2", "钱有为", "成本管理部", ["leaders"]),
        new OrgEmployee("manager1", "孙建国", "综合管理部", ["managers"]),
    ];

    private static readonly Dictionary<string, string> GroupNames = new(StringComparer.Ordinal)
    {
        ["leaders"] = "组长",
        ["managers"] = "经理",
    };

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OrgDepartment>> FindDepartmentsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(Departments);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OrgEmployee>> FindEmployeesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(Employees);

    /// <inheritdoc />
    public ValueTask<OrgEmployee?> FindEmployeeAsync(string domainAccount, CancellationToken ct = default) =>
        ValueTask.FromResult(
            Employees.FirstOrDefault(employee => string.Equals(employee.DomainAccount, domainAccount, StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<string>> FindGroupIdsByUserAsync(string userId, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyCollection<string>>(
            Employees
                .FirstOrDefault(employee => string.Equals(employee.DomainAccount, userId, StringComparison.OrdinalIgnoreCase))
                ?.Groups
            ?? []);

    /// <inheritdoc />
    public ValueTask<UserGroup?> FindGroupByIdAsync(string groupId, CancellationToken ct = default) =>
        ValueTask.FromResult(
            GroupNames.TryGetValue(groupId, out var name) ? new UserGroup(groupId, name) : null);

    /// <inheritdoc />
    public ValueTask<string?> FindUserIdAsync(string userId, CancellationToken ct = default) =>
        ValueTask.FromResult(
            Employees.Any(employee => string.Equals(employee.DomainAccount, userId, StringComparison.OrdinalIgnoreCase))
                ? userId
                : null);

    /// <inheritdoc />
    /// <remarks>「流程走到组长审批时，审批人到底是谁」就是靠它答出来的 —— 组成员按域账号稳定排序。</remarks>
    public ValueTask<IReadOnlyCollection<string>> FindUserIdsByGroupAsync(string groupId, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyCollection<string>>(
            [.. Employees
                .Where(employee => employee.Groups.Contains(groupId, StringComparer.Ordinal))
                .Select(employee => employee.DomainAccount)
                .OrderBy(account => account, StringComparer.Ordinal)]);
}

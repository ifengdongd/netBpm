extern alias DemoHost;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.Configuration;
using NetBpm.Identity;
using NetBpm.Persistence;
using NetBpm.Workflow.Hosting;
using MySqlOrgDirectory = DemoHost::NetBpm.Demo.Storage.MySqlOrgDirectory;

namespace NetBpm.IntegrationTests;

/// <summary>
/// <c>MySqlOrgDirectory</c>（MySQL 模式的组织架构，读 sys_dept / sys_emp /
/// sys_emp_group / sys_emp_group_member 四张表）的库级验收。
/// <para>
/// 测试用独立租户（<see cref="TenantId"/>）插自己的行再清理，不碰演示种子数据 ——
/// 顺便验证租户过滤：种子租户（1）的账号在隔离租户下必须查不到。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class MySqlOrgDirectoryTests : IAsyncLifetime
{
    /// <summary>测试专用租户 id（避开种子数据所在的租户 1）。</summary>
    private const long TenantId = 990011;

    private readonly IDbConnectionFactory _connections =
        new DbConnectionFactory(DatabaseType.MySql, MySqlTestDatabase.ConnectionString);

    private readonly MySqlOrgDirectory _directory;

    public MySqlOrgDirectoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Workflow:TenantId", TenantId.ToString(CultureInfo.InvariantCulture))])
            .Build();
        _directory = new MySqlOrgDirectory(_connections, configuration);
    }

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);
        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = _connections.CreateConnection();
        await connection.ExecuteAsync(
            """
            delete from sys_emp_group_member where tenant_id = @TenantId;
            delete from sys_emp_group where tenant_id = @TenantId;
            delete from sys_emp where tenant_id = @TenantId;
            delete from sys_dept where tenant_id = @TenantId;
            """,
            new { TenantId });
    }

    private async Task SeedAsync()
    {
        await using var connection = _connections.CreateConnection();
        await connection.ExecuteAsync(
            """
            delete from sys_emp_group_member where tenant_id = @TenantId;
            delete from sys_emp_group where tenant_id = @TenantId;
            delete from sys_emp where tenant_id = @TenantId;
            delete from sys_dept where tenant_id = @TenantId;

            insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
            values (990101, @TenantId, '研发一部', null, 1, utc_timestamp()),
                   (990102, @TenantId, '研发二部', null, 2, utc_timestamp());

            insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
            values (990001, @TenantId, '张三', 'a1', 990101, utc_timestamp()),
                   (990002, @TenantId, '李四', 'b2', 990102, utc_timestamp()),
                   (990003, @TenantId, '王五', 'c3', 990101, utc_timestamp());

            insert into sys_emp_group (group_id, tenant_id, group_name, sort_order, create_time)
            values ('t990011-leaders', @TenantId, '组长', 1, utc_timestamp()),
                   ('t990011-managers', @TenantId, '经理', 2, utc_timestamp());

            insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
            values (9900001, @TenantId, 't990011-leaders', 990001, utc_timestamp()),
                   (9900002, @TenantId, 't990011-leaders', 990003, utc_timestamp()),
                   (9900003, @TenantId, 't990011-managers', 990001, utc_timestamp()),
                   (9900004, @TenantId, 't990011-managers', 990003, utc_timestamp());
            """,
            new { TenantId });
    }

    /// <summary>
    /// 员工行投影成可比元组：<see cref="OrgEmployee.Groups"/> 是
    /// <c>IReadOnlyList</c> 属性，记录相等按引用比较集合实例，不能整记录比。
    /// </summary>
    private static List<(string Account, string Name, string Dept, string Groups)> Project(
        IEnumerable<OrgEmployee> employees) =>
        [.. employees.Select(e => (e.DomainAccount, e.Name, e.DeptName, string.Join('|', e.Groups)))];

    [Fact]
    public async Task FindDepartments_ShouldReadSysDeptOrderedBySort()
    {
        IReadOnlyList<OrgDepartment> expected =
        [
            new(990101, "研发一部", null),
            new(990102, "研发二部", null),
        ];

        var departments = await _directory.FindDepartmentsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, departments);
    }

    [Fact]
    public async Task FindEmployees_ShouldJoinDeptAndGroups()
    {
        var employees = await _directory.FindEmployeesAsync(TestContext.Current.CancellationToken);

        // 排序口径与内存版一致：部门 sort_order → emp_id
        Assert.Equal(
        [
            ("a1", "张三", "研发一部", "t990011-leaders|t990011-managers"),
            ("c3", "王五", "研发一部", "t990011-leaders|t990011-managers"),
            ("b2", "李四", "研发二部", ""),
        ], Project(employees));
    }

    [Fact]
    public async Task FindEmployee_ShouldReturnNullForUnknownAccount()
    {
        var ct = TestContext.Current.CancellationToken;

        var found = await _directory.FindEmployeeAsync("a1", ct);
        Assert.Equal(
            ("a1", "张三", "研发一部", "t990011-leaders|t990011-managers"),
            found is null ? default : (found.DomainAccount, found.Name, found.DeptName, string.Join('|', found.Groups)));

        // 不存在的账号 → null（业务端「查不到留空」的语义基础）
        Assert.Null(await _directory.FindEmployeeAsync("nobody", ct));
    }

    [Fact]
    public async Task FindGroupIdsByUser_ShouldExpandMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        IReadOnlyCollection<string> bothGroups = ["t990011-leaders", "t990011-managers"];

        // a1 / c3 双组（按组 sort_order 排）；b2 无组
        Assert.Equal(bothGroups, await _directory.FindGroupIdsByUserAsync("a1", ct));
        Assert.Empty(await _directory.FindGroupIdsByUserAsync("b2", ct));
    }

    [Fact]
    public async Task FindGroupById_ShouldReadGroupName()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(
            new UserGroup("t990011-leaders", "组长"),
            await _directory.FindGroupByIdAsync("t990011-leaders", ct));
        Assert.Null(await _directory.FindGroupByIdAsync("t990011-nobody", ct));
    }

    [Fact]
    public async Task FindUserIdAndGroupMembers_ShouldFollowOrdinalSemantics()
    {
        var ct = TestContext.Current.CancellationToken;
        IReadOnlyCollection<string> members = ["a1", "c3"];

        Assert.Equal("a1", await _directory.FindUserIdAsync("a1", ct));
        Assert.Null(await _directory.FindUserIdAsync("nobody", ct));

        // 组成员按域账号稳定排序 —— 与内存版（InMemoryOrgDirectory）的口径一致
        Assert.Equal(members, await _directory.FindUserIdsByGroupAsync("t990011-leaders", ct));
    }

    [Fact]
    public async Task TenantFilter_ShouldHideSeedTenantRows()
    {
        // 种子租户（1）的演示账号（alice / leaders）在隔离租户下必须查不到
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await _directory.FindEmployeeAsync("alice", ct));
        Assert.Null(await _directory.FindGroupByIdAsync("leaders", ct));
        Assert.Empty(await _directory.FindUserIdsByGroupAsync("leaders", ct));
    }
}

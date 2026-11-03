using NetBpm.Engine.Tasks;

namespace NetBpm.UnitTests;

/// <summary>
/// <see cref="ApprovalChainContext"/> 携带 businessKey + 流程变量快照的行为验收：
/// 动态审批人来源据此「按业务归属 + 职位」查外部系统定审批人。
/// </summary>
public sealed class ApprovalChainContextTests
{
    [Fact]
    public void Should_Carry_BusinessKey_And_Variables()
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applyDeptId"] = "102",
            ["days"] = "3",
        };

        var context = new ApprovalChainContext(
            "def-1",
            "task_approve",
            StartUserId: "alice",
            TenantId: "acme",
            BusinessKey: "LEAVE-9001",
            Variables: variables);

        Assert.Equal("LEAVE-9001", context.BusinessKey);
        Assert.Equal("102", context.GetStringVariable("applyDeptId"));
        Assert.Equal("3", context.GetStringVariable("days"));
    }

    [Fact]
    public void GetVariable_Should_Return_Default_When_Missing()
    {
        var context = new ApprovalChainContext(
            "def-1",
            "task_approve",
            StartUserId: "alice",
            TenantId: "acme",
            BusinessKey: null,
            Variables: null);

        Assert.Null(context.GetVariable("applyDeptId"));
        Assert.Equal("fallback", context.GetVariable("applyDeptId", "fallback"));
        Assert.Null(context.GetStringVariable("applyDeptId"));
    }

    [Fact]
    public void Should_Remain_Backward_Compatible_With_Four_Arg_Ctor()
    {
        // 旧调用方（只有 4 个位置参数）仍能构造，businessKey / variables 默认 null
        var context = new ApprovalChainContext("def-1", "approve", "u1", "acme");

        Assert.Null(context.BusinessKey);
        Assert.Null(context.Variables);
        Assert.Null(context.GetStringVariable("applyDeptId"));
    }
}

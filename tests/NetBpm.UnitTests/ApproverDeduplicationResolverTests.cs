using NetBpm.Engine.Tasks;

namespace NetBpm.UnitTests;

/// <summary>
/// 审批链预处理（去重 / 自动跳过 / 空审批人兜底）的行为验收。
/// </summary>
public sealed class ApproverDeduplicationResolverTests
{
    private static ApprovalChainContext Context(string? startUserId = "u1") =>
        new("def-1", "approve", startUserId, "acme");

    [Fact]
    public async Task Should_Remove_Initiator_From_Approvers()
    {
        var resolver = new ApproverDeduplicationResolver();

        var result = await resolver.PostProcessAsync(Context("u1"), ["u1", "u2"]);

        Assert.Equal(["u2"], result);
    }

    [Fact]
    public async Task Should_Skip_Node_When_Initiator_Is_The_Only_Approver()
    {
        var resolver = new ApproverDeduplicationResolver();

        var result = await resolver.PostProcessAsync(Context("u1"), ["u1"]);

        // 空集 = 自动跳过该节点（自己审自己没有意义）
        Assert.Empty(result);
    }

    [Fact]
    public async Task Should_Merge_Consecutive_Duplicates_Preserving_Order()
    {
        var resolver = new ApproverDeduplicationResolver();

        var result = await resolver.PostProcessAsync(Context("u9"), ["u1", "u1", "u2", "u1", "u3", "u3"]);

        Assert.Equal(["u1", "u2", "u1", "u3"], result);
    }

    [Fact]
    public async Task Should_Trim_And_Drop_Blanks()
    {
        var resolver = new ApproverDeduplicationResolver();

        var result = await resolver.PostProcessAsync(Context("u9"), [" u1 ", "", "  ", "u2"]);

        Assert.Equal(["u1", "u2"], result);
    }

    [Fact]
    public async Task Should_Fallback_To_Admin_When_No_Approver_Configured()
    {
        var resolver = new ApproverDeduplicationResolver(
            new ApproverDeduplicationOptions { FallbackAdminUserId = "admin" });

        var result = await resolver.PostProcessAsync(Context("u1"), []);

        Assert.Equal(["admin"], result);
    }

    /// <summary>
    /// 关键语义：兜底管理员只在「节点原本就没配人」时生效；
    /// 若审批人是被「发起人跳过」规则摘空的，则应跳过节点而不是升级给管理员 ——
    /// 否则「发起人审批」这类节点会凭空多出一个管理员待办。
    /// </summary>
    [Fact]
    public async Task Should_Not_Escalate_When_Approvers_Removed_By_Initiator_Rule()
    {
        var resolver = new ApproverDeduplicationResolver(
            new ApproverDeduplicationOptions { FallbackAdminUserId = "admin" });

        var result = await resolver.PostProcessAsync(Context("u1"), ["u1"]);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Should_Keep_Approvers_When_No_Initiator_Context()
    {
        var resolver = new ApproverDeduplicationResolver(
            new ApproverDeduplicationOptions { FallbackAdminUserId = "admin" });

        // 没有发起人（如系统触发）⇒ 跳过规则无从判断，保持原样
        var result = await resolver.PostProcessAsync(Context(startUserId: null), ["u1", "u2"]);

        Assert.Equal(["u1", "u2"], result);
    }
}

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 写一条审批语义映射的请求（<c>PUT /admin/v1/dictionaries/{processKey}/options/{optionId}</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 三个字段刻意<b>不在请求体里</b>：
/// <list type="bullet">
///   <item><description><c>tenantId</c>——取当前管理身份所属租户。允许请求体指定就等于允许
///     一个租户的管理员改另一个租户的审批口径；</description></item>
///   <item><description><c>processKey</c> / <c>optionId</c>——在路径里，不在体里。
///     同一份数据有两个来源时，改了一半的请求最难排查；</description></item>
///   <item><description><c>outcomeClass</c>——由 <see cref="Semantic"/> 推导（<see cref="ApprovalSemantics.OutcomeClassOf"/>）。
///     允许单独指定就等于允许「语义是通过、统计口径是负向」，那种行的通过率报表没人敢用。</description></item>
/// </list>
/// </para>
/// <para>
/// <see cref="ApprovalSemantic.Closed"/> 不接受：它只由平台在「结论三级回落」的最后一级产生，
/// 表达「平台只知道这单结束了」。让它出现在字典里，等于允许人工伪造一个平台专属结论。
/// </para>
/// </remarks>
/// <param name="NodeId">
/// 限定环节（BPMN <c>userTask/@id</c>），<see langword="null"/> 表示流程级默认。
/// 节点级映射优先于流程级——同一个选项在「初审」与「复核」可以是不同语义。
/// </param>
/// <param name="Semantic">平台语义（必填，决定后续分支怎么判断）。</param>
/// <param name="Label">按钮文案；为空取 BPMN <c>activiti:value/@name</c>，再为空取 optionId。</param>
/// <param name="RequiresComment">提交该选项是否必须填写审批意见。</param>
/// <param name="RequiresTargetNode">提交该选项是否必须指定目标节点（退回 / 跳转类）。</param>
/// <param name="AllowedTargetNodes">可选目标节点白名单；为空表示按流程定义默认。</param>
/// <param name="Attributes">流程自定义附加属性（原样存与回显，平台不解释）。</param>
public sealed record ApprovalDictionaryUpsertRequest(
    string? NodeId,
    ApprovalSemantic Semantic,
    string? Label = null,
    bool RequiresComment = false,
    bool RequiresTargetNode = false,
    IReadOnlyList<string>? AllowedTargetNodes = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>
/// 流程定义里声明的一个审批选项（<c>GET /admin/v1/dictionaries/{processKey}/declared</c>）。
/// </summary>
/// <param name="OptionId">选项 id（<c>activiti:value/@id</c>）。</param>
/// <param name="Name">选项名（<c>activiti:value/@name</c>）；未写时为 <see langword="null"/>。</param>
public sealed record DeclaredApprovalOption(string OptionId, string? Name);

/// <summary>
/// 一个环节上声明的审批选项。
/// </summary>
/// <remarks>
/// 只有带 <c>approvalResult</c> 表单属性的用户任务才出现——别的环节没有「审批选项」这个概念，
/// 列出来只会让管理端以为那里可以配字典。
/// </remarks>
/// <param name="ActivityId">环节 id。</param>
/// <param name="ActivityName">环节名。</param>
/// <param name="Options">该环节声明的选项（按定义里的顺序）。</param>
public sealed record DeclaredNodeApprovalOptions(
    string ActivityId,
    string? ActivityName,
    IReadOnlyList<DeclaredApprovalOption> Options);

/// <summary>
/// 某流程<b>当前生效版本</b>声明的全部审批选项（字典维护的「可选范围」）。
/// </summary>
/// <remarks>
/// 这个端点的作用是让「配字典」这件事有据可依：字典是按 optionId 配的，
/// 而 optionId 只有 BPMN 里有；没有它，管理端只能让运维去翻流程文件或盲猜。
/// 同时也是写路径的校验依据——声明里没有的 optionId 写进去永远不生效，
/// 那种「配好了但不工作」的配置比没配更难发现。
/// </remarks>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="ProcessDefinitionId">当前生效定义 id。</param>
/// <param name="DefinitionVersion">当前生效版本号。</param>
/// <param name="Nodes">声明了审批选项的环节。</param>
public sealed record DeclaredApprovalOptionsView(
    string ProcessKey,
    string ProcessDefinitionId,
    int DefinitionVersion,
    IReadOnlyList<DeclaredNodeApprovalOptions> Nodes);

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 目录覆盖行的<b>写入</b>请求（<c>PUT /admin/v1/processes/{processKey}</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 语义是<b>整行替换</b>，不是逐字段合并。每个字段都有三种状态，必须能分别表达：
/// <list type="bullet">
///   <item><description>给了值 → 用它覆盖 BPMN 派生结果；</description></item>
///   <item><description>给 <see langword="null"/> → 该字段回到「跟随 BPMN 派生」。</description></item>
/// </list>
/// 如果改成 PATCH 式的「只改传了的字段」，「把某个字段改回跟随派生」就表达不出来——
/// 这是 PATCH 语义的经典空洞（null 与「没传」在不同序列化器下还不一致）。整行替换让 PUT 天然幂等。
/// </para>
/// <para>
/// <b>没有任何覆盖字段</b>的请求按 422 拒绝（<see cref="ErrorCodes.CatalogOverrideEmpty"/>）：
/// 空覆盖与「没有覆盖行」在合并结果上完全等价，允许它只会往表里堆一行什么都不做的记录。
/// 要清除覆盖请用 <c>DELETE</c>。
/// </para>
/// <para>
/// 没有乐观并发字段：覆盖行是单行管理操作，没有状态机、没有业务推进，
/// 加 <c>If-Match</c> 只会让「两个人同时精修名片」变成一次 412 而不是后写者胜。
/// </para>
/// </remarks>
/// <param name="Name">展示名；为空取流程定义里的 name。</param>
/// <param name="Category">分类（分组展示用）。</param>
/// <param name="Description">说明（允许 Markdown）。</param>
/// <param name="Icon">图标标识或 URL。</param>
/// <param name="PageUrls">业务页面地址模板（键 = <c>start</c> / <c>view</c> / <c>approve</c>）。</param>
/// <param name="EntryPoints">入口声明；为空表示由 BPMN 的 startEvent 派生。</param>
/// <param name="Status">
/// 显式状态。只能把流程标成草稿 / 已下线；<b>不能</b>把引擎挂起的流程说成可发起——
/// 那个闸门在引擎里，目录盖过去只会让调用方在提交时才吃到 409。
/// </param>
/// <param name="SortOrder">展示排序（小的在前）。</param>
/// <param name="Labels">本地化标签（语言 → 展示名）。</param>
/// <param name="ParameterSchemaVersion">参数规范版本；0 表示未声明。</param>
public sealed record ProcessCatalogOverrideRequest(
    string? Name = null,
    string? Category = null,
    string? Description = null,
    string? Icon = null,
    IReadOnlyDictionary<string, string>? PageUrls = null,
    IReadOnlyList<ProcessEntryPoint>? EntryPoints = null,
    ProcessStatus? Status = null,
    int? SortOrder = null,
    IReadOnlyDictionary<string, string>? Labels = null,
    int? ParameterSchemaVersion = null)
{
    /// <summary>是否至少覆盖了一个字段（全空等于「没有覆盖」，应拒绝写入）。</summary>
    /// <remarks>
    /// <see cref="SortOrder"/> 是可空整数：<c>0</c> 是合法覆盖值（显式排到最前），
    /// 因此判断的是「有没有给」而不是「值是否非零」。
    /// </remarks>
    public bool HasAnyOverride =>
        Name is not null
        || Category is not null
        || Description is not null
        || Icon is not null
        || PageUrls is { Count: > 0 }
        || EntryPoints is { Count: > 0 }
        || Status is not null
        || SortOrder is not null
        || Labels is { Count: > 0 }
        || ParameterSchemaVersion is not null;
}

/// <summary>
/// 目录覆盖行（管理面读形态）。
/// </summary>
/// <remarks>
/// 与 <see cref="ProcessDescriptor"/> 的区别：描述符是<b>合并三层之后</b>的对外名片，
/// 这里只返回<b>覆盖行本身</b>。管理端要判断「这一项是精修过的还是 BPMN 派生的」，
/// 只能看覆盖行——拿到描述符永远分不清 <c>name</c> 是定义的还是人填的。
/// </remarks>
/// <param name="TenantId">租户。</param>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="Name">覆盖的展示名。</param>
/// <param name="Category">覆盖的分类。</param>
/// <param name="Description">覆盖的说明。</param>
/// <param name="Icon">覆盖的图标。</param>
/// <param name="PageUrls">覆盖的业务页面地址。</param>
/// <param name="EntryPoints">覆盖的入口声明。</param>
/// <param name="Status">覆盖的显式状态。</param>
/// <param name="SortOrder">展示排序。</param>
/// <param name="Labels">本地化标签。</param>
/// <param name="ParameterSchemaVersion">参数规范版本。</param>
/// <param name="CreatedAt">首次写入时间。</param>
/// <param name="UpdatedAt">最后修改时间。</param>
public sealed record ProcessCatalogOverride(
    string TenantId,
    string ProcessKey,
    string? Name,
    string? Category,
    string? Description,
    string? Icon,
    IReadOnlyDictionary<string, string>? PageUrls,
    IReadOnlyList<ProcessEntryPoint>? EntryPoints,
    ProcessStatus? Status,
    int SortOrder,
    IReadOnlyDictionary<string, string>? Labels,
    int ParameterSchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

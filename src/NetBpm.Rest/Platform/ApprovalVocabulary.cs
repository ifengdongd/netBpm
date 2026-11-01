using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 审批语义字典（<c>IApprovalVocabulary</c>）——三码模型的解析入口。
/// </summary>
/// <remarks>
/// <para>
/// 给定「哪个流程 / 哪个环节 / 哪个选项 id」，给出调用方能直接渲染按钮的
/// <see cref="ApprovalOption"/>（label / semantic / outcomeClass / 校验规则）。
/// 调用方永远只按 <see cref="ApprovalOption.Semantic"/> 与
/// <see cref="ApprovalOption.OutcomeClass"/> 做分支，不按 optionId 硬编码。
/// </para>
/// <para>
/// 两层合并（后层覆盖前层）：
/// <list type="number">
///   <item><description><b>默认字典</b>（<see cref="ApprovalSemantics"/>）：数字 1/2/3 与常见英文词；</description></item>
///   <item><description><b>流程声明</b>（<c>nb_approval_dictionary</c>）：自定义选项的语义与校验规则，
///     节点级（<c>node_id</c> 非空）覆盖流程级（<c>node_id</c> 为空）。</description></item>
/// </list>
/// </para>
/// <para>
/// 未识别的选项一律 <see cref="ApprovalSemantic.Custom"/>（<b>不猜</b>）——猜错的后果是
/// 「把退回当成通过」，比返回「自定义」严重得多。
/// </para>
/// </remarks>
public interface IApprovalVocabulary
{
    /// <summary>
    /// 解析一个环节上的<b>全部</b>可选项（按 BPMN <c>activiti:value</c> 顺序），
    /// 供 <c>TaskView.Options</c> 直接渲染按钮。
    /// </summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="tenantId">租户；<see langword="null"/> 表示不查流程声明（只用默认字典）。</param>
    /// <param name="activityId">环节 id（BPMN userTask 的 id）。</param>
    /// <param name="optionIds">该环节的选项 id（来自 <c>approvalResult</c> 的 <c>activiti:value/@id</c>），
    /// 附带对应的展示名（<c>@name</c>），供未声明 label 时兜底。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ApprovalOption>> ResolveOptionsAsync(
        string processKey,
        string? tenantId,
        string? activityId,
        IReadOnlyList<(string OptionId, string? Name)> optionIds,
        CancellationToken ct = default);

    /// <summary>解析<b>单个</b>选项；该环节的选项集里没有它时返回 <see langword="null"/>。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="tenantId">租户。</param>
    /// <param name="activityId">环节 id。</param>
    /// <param name="optionId">选项 id。</param>
    /// <param name="fallbackLabel">未声明 label 时的兜底文案（来自 BPMN <c>@name</c>）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ApprovalOption?> ResolveOptionAsync(
        string processKey,
        string? tenantId,
        string? activityId,
        string optionId,
        string? fallbackLabel,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ApprovalVocabulary : IApprovalVocabulary
{
    /// <summary>流程声明的缓存时长。字典是低频变更，缓存久一点没问题。</summary>
    private static readonly TimeSpan DictionaryCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly IApprovalDictionaryStore _store;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ApprovalVocabulary> _logger;

    /// <summary>构造。</summary>
    /// <param name="store">字典表。</param>
    /// <param name="cache">流程声明的缓存。</param>
    /// <param name="logger">日志。</param>
    public ApprovalVocabulary(
        IApprovalDictionaryStore store,
        IMemoryCache cache,
        ILogger<ApprovalVocabulary> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ApprovalOption>> ResolveOptionsAsync(
        string processKey,
        string? tenantId,
        string? activityId,
        IReadOnlyList<(string OptionId, string? Name)> optionIds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(optionIds);

        var declarations = await LoadDeclarationsAsync(processKey, tenantId, ct).ConfigureAwait(false);
        var result = new List<ApprovalOption>(optionIds.Count);

        foreach (var (optionId, name) in optionIds)
        {
            if (string.IsNullOrWhiteSpace(optionId))
            {
                continue;
            }

            result.Add(BuildOption(declarations, activityId, optionId, name));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalOption?> ResolveOptionAsync(
        string processKey,
        string? tenantId,
        string? activityId,
        string optionId,
        string? fallbackLabel,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);

        var declarations = await LoadDeclarationsAsync(processKey, tenantId, ct).ConfigureAwait(false);

        return BuildOption(declarations, activityId, optionId, fallbackLabel);
    }

    /// <summary>
    /// 加载某流程的声明（节点级 + 流程级）。表不可读时降级为空声明——只记警告，不阻断。
    /// </summary>
    private async ValueTask<IReadOnlyList<ApprovalDictionaryEntry>> LoadDeclarationsAsync(
        string processKey,
        string? tenantId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return [];
        }

        var cacheKey = ApprovalVocabularyCache.Key(tenantId, processKey);

        if (_cache.TryGetValue(cacheKey, out CachedDeclarations? cached)
            && cached is not null)
        {
            // 缓存命中不等于「还新鲜」：写路径只能失效写者自己的进程，
            // 所以用一次聚合查询（主键前缀）跨副本校验水位。
            if (await IsStillCurrentAsync(cached.Revision, tenantId, processKey, ct).ConfigureAwait(false))
            {
                return cached.Entries;
            }
        }

        try
        {
            var entries = await _store.ListAsync(tenantId, processKey, ct).ConfigureAwait(false);
            var revision = await _store.GetRevisionAsync(tenantId, processKey, ct).ConfigureAwait(false);

            _cache.Set(cacheKey, new CachedDeclarations(entries, revision), DictionaryCacheLifetime);

            return entries;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 老库可能还没跑补表脚本。语义解析靠默认字典仍可工作，只是自定义选项落回 CUSTOM。
            _logger.LogWarning(
                exception,
                "审批语义字典表不可读（{TenantId}/{ProcessKey}）：本次只按默认字典解析，"
                + "请执行 db/{Dialect}/netbpm_init.sql 补齐 nb_approval_dictionary。",
                tenantId,
                processKey,
                "mysql|sqlserver");

            return [];
        }
    }

    /// <summary>
    /// 缓存里的这份声明是否仍等于库里的水位（行数 + 最近更新时间）。
    /// </summary>
    /// <remarks>
    /// 水位查询失败时<b>沿用缓存</b>而不是抛：字典是审批语义的辅助数据，
    /// 一次读库抖动不该让所有办理页 500；最坏情况是读到旧语义，且下一次请求还会重试校验。
    /// </remarks>
    private async ValueTask<bool> IsStillCurrentAsync(
        ApprovalDictionaryRevision cached,
        string tenantId,
        string processKey,
        CancellationToken ct)
    {
        try
        {
            var current = await _store.GetRevisionAsync(tenantId, processKey, ct).ConfigureAwait(false);

            return current == cached;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(
                exception,
                "审批语义字典水位查询失败（{TenantId}/{ProcessKey}）：本次沿用进程内缓存。",
                tenantId,
                processKey);

            return true;
        }
    }

    /// <summary>缓存条目：声明 + 取这份声明时的水位。</summary>
    private sealed record CachedDeclarations(
        IReadOnlyList<ApprovalDictionaryEntry> Entries,
        ApprovalDictionaryRevision Revision);

    /// <summary>两层合并出一个 <see cref="ApprovalOption"/>。</summary>
    private static ApprovalOption BuildOption(
        IReadOnlyList<ApprovalDictionaryEntry> declarations,
        string? activityId,
        string optionId,
        string? fallbackLabel)
    {
        // 节点级覆盖优先于流程级（node_id 空 = 流程级默认）
        var entry = declarations
            .FirstOrDefault(item => string.Equals(item.OptionId, optionId, StringComparison.Ordinal)
                && string.Equals(item.NodeId, activityId, StringComparison.Ordinal))
            ?? declarations
                .FirstOrDefault(item => string.Equals(item.OptionId, optionId, StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(item.NodeId));

        var semantic = entry?.Semantic ?? ApprovalSemantics.Resolve(optionId);
        var outcomeClass = entry?.OutcomeClass ?? ApprovalSemantics.OutcomeClassOf(semantic);

        return new ApprovalOption(
            OptionId: optionId,
            Label: entry?.Label ?? fallbackLabel ?? optionId,
            Semantic: semantic,
            OutcomeClass: outcomeClass,
            RequiresComment: entry?.RequiresComment ?? false,
            RequiresTargetNode: entry?.RequiresTargetNode ?? false,
            AllowedTargetNodes: entry?.AllowedTargetNodes,
            CommentPrompt: null,
            Attributes: entry?.Attributes);
    }
}

/// <summary>
/// 字典缓存的键与失效入口。
/// </summary>
/// <remarks>
/// 写路径（<see cref="ApprovalDictionaryAdmin"/>）与读路径必须用<b>同一个</b>键构造函数：
/// 两边各拼一份字符串，改了一边忘了另一边，表现是「写完不生效」——
/// 那是最容易被当成缓存 bug 而查错方向的一类问题。
/// </remarks>
internal static class ApprovalVocabularyCache
{
    private const string Prefix = "netbpm:vocabulary:";

    /// <summary>缓存键（租户 + 流程；节点维度不参与，一次取回整流程的声明）。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="processKey">流程 key。</param>
    internal static string Key(string tenantId, string processKey) => Prefix + tenantId + ":" + processKey;

    /// <summary>让某流程的声明缓存立即失效（写路径调用）。</summary>
    /// <param name="cache">缓存实例。</param>
    /// <param name="tenantId">租户；为空时不做事（解析时也不会查库）。</param>
    /// <param name="processKey">流程 key。</param>
    internal static void Invalidate(IMemoryCache cache, string? tenantId, string processKey)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return;
        }

        cache.Remove(Key(tenantId, processKey));
    }
}

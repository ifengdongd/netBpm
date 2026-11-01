using NetBpm.Platform.Contracts;

namespace NetBpm.Platform.Client;

/// <summary>
/// 流程目录（<c>/workflow/v1/processes</c>）：一张名片 + 定义 / 版本 / 参数规范 / 表单规范 / 流程图。
/// </summary>
/// <remarks>
/// 这一组是接入的<b>第一步</b>：拿到 <see cref="ProcessDescriptor"/> 才知道能发起什么、
/// 发起时要填哪些参数、审批页要渲染哪些字段。因此这些读方法都没有副作用，也不接受幂等键。
/// </remarks>
public sealed class ProcessesClient
{
    private const string BasePath = "workflow/v1/processes";

    private readonly PlatformTransport _transport;

    internal ProcessesClient(PlatformTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
    }

    /// <summary>分页查流程目录（服务端已按调用方白名单过滤）。</summary>
    /// <param name="query">查询条件；为空表示默认第一页。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<CursorPage<ProcessDescriptor>> ListAsync(
        ProcessQuery? query = null,
        CancellationToken ct = default)
    {
        query ??= new ProcessQuery();

        List<KeyValuePair<string, string?>> parameters =
        [
            new("keyword", query.Keyword),
            new("category", query.Category),
            new("status", PlatformTransport.EnumText(query.Status)),
            new("cursor", query.Cursor),
            new("limit", query.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];

        return _transport.GetJsonAsync<CursorPage<ProcessDescriptor>>(BasePath, parameters, ct);
    }

    /// <summary>
    /// 逐页拉完整个目录。
    /// </summary>
    /// <remarks>
    /// 自己跟游标而不是让调用方写循环：游标是不透明串，跟着 <c>HasMore</c> 走一遍就够了；
    /// 把它暴露给调用方只会催生「取下一页时忘了带上一次的条件」这类分页 bug。
    /// </remarks>
    /// <param name="query">查询条件（<see cref="ProcessQuery.Cursor"/> 会被忽略）。</param>
    /// <param name="ct">取消令牌。</param>
    public async IAsyncEnumerable<ProcessDescriptor> EnumerateAsync(
        ProcessQuery? query = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        query ??= new ProcessQuery();

        string? cursor = null;

        do
        {
            var page = await ListAsync(query with { Cursor = cursor }, ct).ConfigureAwait(false);

            foreach (var item in page.Items)
            {
                yield return item;
            }

            cursor = page.HasMore ? page.NextCursor : null;
        }
        while (cursor is not null);
    }

    /// <summary>查单条流程名片。不存在或未授权都抛 404（<see cref="ErrorCodes.ProcessNotFound"/>）。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<ProcessDescriptor> GetAsync(string processKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        return _transport.GetJsonAsync<ProcessDescriptor>(Path(processKey), null, ct);
    }

    /// <summary>查当前生效定义；<paramref name="includeXml"/> 为 <see langword="true"/> 时带 BPMN 原文。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="includeXml">是否索取 BPMN 原文（几十 KB，按需拉）。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<ProcessDefinitionInfo> GetDefinitionAsync(
        string processKey,
        bool includeXml = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        List<KeyValuePair<string, string?>> parameters =
        [
            new("includeXml", includeXml ? "true" : "false"),
        ];

        return _transport.GetJsonAsync<ProcessDefinitionInfo>(
            Path(processKey) + "/definition",
            parameters,
            ct);
    }

    /// <summary>查全部版本（新到旧），含每版的校验和与是否当前生效。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<IReadOnlyList<ProcessVersionInfo>> ListVersionsAsync(
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        return _transport.GetJsonAsync<IReadOnlyList<ProcessVersionInfo>>(
            Path(processKey) + "/versions",
            null,
            ct);
    }

    /// <summary>
    /// 查当前生效版本的参数规范（声明 + 约束 + 谁能写 / 谁能看）。
    /// </summary>
    /// <remarks>
    /// 历史单据的参数面板不要用这个方法：它给的是<b>当前版本</b>的声明，
    /// 应当走 <see cref="InstancesClient.GetParametersAsync"/>（按实例停在的那一版渲染）。
    /// </remarks>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<ParameterSchema> GetParameterSchemaAsync(
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        return _transport.GetJsonAsync<ParameterSchema>(Path(processKey) + "/parameters/schema", null, ct);
    }

    /// <summary>查各环节的表单规范（审批页的输入项）。<paramref name="version"/> 为空取当前生效版本。</summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="version">定义版本；为空表示当前生效版本。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<ProcessFormSchema> GetFormSchemaAsync(
        string processKey,
        int? version = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        List<KeyValuePair<string, string?>> parameters =
        [
            new("version", version?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];

        return _transport.GetJsonAsync<ProcessFormSchema>(
            Path(processKey) + "/form/schema",
            parameters,
            ct);
    }

    /// <summary>
    /// 取流程图 SVG（可直接塞进 <c>&lt;img src="data:image/svg+xml;...&gt;</c> 或写文件）。
    /// </summary>
    /// <param name="processKey">流程 key。</param>
    /// <param name="version">定义版本；为空表示当前生效版本。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<string> GetDiagramSvgAsync(
        string processKey,
        int? version = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        var path = Path(processKey) + "/diagram.svg";

        if (version is { } wanted)
        {
            path += "?version=" + wanted.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return _transport.GetTextAsync(path, ct);
    }

    private static string Path(string processKey) =>
        BasePath + "/" + Uri.EscapeDataString(processKey);
}

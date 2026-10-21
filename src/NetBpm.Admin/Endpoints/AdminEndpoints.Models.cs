using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetBpm.Admin.Storage;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Deployment;
using NetBpm.Rest;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点（partial）：流程模型（bpmn_model）与流程类型（workflow_type 注册表）的处理器。
/// 路由表见 <see cref="AdminEndpoints.MapAdminEndpoints"/>。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 分页查模型列表（按最近修改倒序；<c>keyword</c> 模糊匹配名称 / 流程 Key / 描述）。
    /// <para>
    /// 分页协议与引擎列表端点一致：默认裸数组 + <c>X-Total-Count</c>，
    /// <c>envelope=true</c> 返回 <see cref="PagedResponse{T}"/> 信封。
    /// </para>
    /// </summary>
    private static async Task<IResult> ListModelsAsync(
        string? keyword,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        HttpResponse response,
        CancellationToken ct)
    {
        var first = Math.Max(0, firstResult ?? 0);
        var size = Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize);

        var page = await store
            .ListAsync(first, size, keyword, ct)
            .ConfigureAwait(false);

        await EnrichPublishStatesAsync(page.Items, store, engine, requestContext, ct).ConfigureAwait(false);

        return PagedResults.Create(page.Items, page.Total, first, size, response, envelope == true);
    }

    private static async Task<IResult> GetModelAsync(
        string id,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        CancellationToken ct)
    {
        var model = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        model.Publish = await ResolvePublishStateAsync(
                store,
                engine,
                requestContext,
                model.Id,
                model.ProcessKey,
                ct)
            .ConfigureAwait(false);

        return Results.Ok(model);
    }

    private static async Task<IResult> SaveModelAsync(
        string id,
        SaveBpmnModelRequest request,
        HttpRequest httpRequest,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BpmnXml))
        {
            return Results.BadRequest(new ErrorResponse("bpmnXml 不能为空。"));
        }

        var conflict = await CheckRevisionAsync(httpRequest, id, store, ct).ConfigureAwait(false);

        if (conflict is not null)
        {
            return conflict;
        }

        if (await EnsureProcessKeyAvailableAsync(request.ProcessKey, id, store, ct).ConfigureAwait(false)
            is { } keyConflict)
        {
            return keyConflict;
        }

        IReadOnlyList<ValidationError> issues;

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.BpmnXml));
            issues = await engine.ValidateAsync(stream, ct).ConfigureAwait(false);
        }
        catch (BpmnXmlParseException exception)
        {
            return Results.BadRequest(new ErrorResponse($"BPMN XML 解析失败：{exception.Message}"));
        }

        var errors = issues.Where(issue => issue.IsError).ToArray();
        var warnings = issues.Where(issue => !issue.IsError).ToArray();

        // 草稿语义：校验结果如实返回，但不阻止保存半成品；发布/部署时再强制拦截。
        var revision = await store
            .SaveAsync(id, request, requestContext.UserId, "save", ct)
            .ConfigureAwait(false);

        return Results.Ok(new SaveBpmnModelResponse(id, revision, errors, warnings));
    }

    /// <summary>
    /// 局部更新模型：只覆盖请求里给出的字段，未给出的字段保持原值。
    /// <para>
    /// 设计器保存只提交 name / processKey / bpmnXml，PUT 的全量语义会把
    /// description、svg 清空；PATCH 修掉这类「保存一次丢字段」的副作用。
    /// </para>
    /// </summary>
    private static async Task<IResult> PatchModelAsync(
        string id,
        PatchBpmnModelRequest request,
        HttpRequest httpRequest,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (existing is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        var conflict = await CheckRevisionAsync(httpRequest, id, store, ct).ConfigureAwait(false);

        if (conflict is not null)
        {
            return conflict;
        }

        if (request.Name is null && request.ProcessKey is null && request.Description is null
            && request.BpmnXml is null && request.Svg is null)
        {
            return Results.BadRequest(new ErrorResponse("至少提供一个要更新的字段。"));
        }

        var merged = new SaveBpmnModelRequest
        {
            Name = request.Name ?? existing.Name,
            ProcessKey = request.ProcessKey ?? existing.ProcessKey,
            Description = request.Description ?? existing.Description,
            BpmnXml = request.BpmnXml ?? existing.BpmnXml,
            Svg = request.Svg ?? existing.Svg,
        };

        if (string.IsNullOrWhiteSpace(merged.BpmnXml))
        {
            return Results.BadRequest(new ErrorResponse("bpmnXml 不能为空。"));
        }

        // 只改了 key 才需要查重；未提交 key 时 merged 沿用原值，不会与别的模型冲突
        if (request.ProcessKey is not null
            && await EnsureProcessKeyAvailableAsync(merged.ProcessKey, id, store, ct).ConfigureAwait(false)
                is { } patchKeyConflict)
        {
            return patchKeyConflict;
        }

        IReadOnlyList<ValidationError> issues = Array.Empty<ValidationError>();

        // 只改元数据时跳过 XML 重校验，避免无谓解析
        if (request.BpmnXml is not null)
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(merged.BpmnXml));
                issues = await engine.ValidateAsync(stream, ct).ConfigureAwait(false);
            }
            catch (BpmnXmlParseException exception)
            {
                return Results.BadRequest(new ErrorResponse($"BPMN XML 解析失败：{exception.Message}"));
            }
        }

        var revision = await store
            .SaveAsync(id, merged, requestContext.UserId, "save", ct)
            .ConfigureAwait(false);

        return Results.Ok(new SaveBpmnModelResponse(
            id,
            revision,
            issues.Where(issue => issue.IsError).ToArray(),
            issues.Where(issue => !issue.IsError).ToArray()));
    }

    /// <summary>新建模型（id 由服务端生成）：与保存同口径校验，返回初始版本与问题清单。</summary>
    private static async Task<IResult> CreateModelAsync(
        SaveBpmnModelRequest request,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        AdminActionAuditor auditor,
        CancellationToken ct) =>
        await CreateModelCoreAsync(request, null, store, engine, requestContext, auditor, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// 导入 BPMN XML：从 XML 推导 process key / 名称后落一个初始版本。
    /// <para>请求体为 <c>text/xml</c>；<c>name</c> 可选，缺省用流程名或 process id。</para>
    /// </summary>
    private static async Task<IResult> ImportModelAsync(
        HttpRequest request,
        string? name,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var xml = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(xml))
        {
            return Results.BadRequest(new ErrorResponse("请求体必须为 BPMN 2.0 XML 文本。"));
        }

        if (!TryReadProcess(xml, out var processKey, out var processName, out var parseError))
        {
            return Results.BadRequest(new ErrorResponse(parseError));
        }

        var model = new SaveBpmnModelRequest
        {
            Name = string.IsNullOrWhiteSpace(name) ? processName ?? processKey : name!.Trim(),
            ProcessKey = processKey,
            BpmnXml = xml,
        };

        return await CreateModelCoreAsync(
                model,
                $"import:{processKey}",
                store,
                engine,
                requestContext,
                auditor,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>基于已部署定义反建模型：把线上版本的 XML 拉回设计器，便于查看 / 修改后重新发布。</summary>
    private static async Task<IResult> CreateModelFromDefinitionAsync(
        string processDefinitionId,
        string? name,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        // 受限身份只在本租户内查找；查不到按 404（不泄露其它租户的定义是否存在）
        if (requestContext.IsTenantRestricted)
        {
            var definitions = await engine
                .FindProcessDefinitionsAsync(
                    new ProcessDefinitionQuery(TenantId: RequestIdentity.Tenant(null, requestContext)), ct)
                .ConfigureAwait(false);

            if (!definitions.Any(definition =>
                    string.Equals(definition.Id, processDefinitionId, StringComparison.Ordinal)))
            {
                return Results.NotFound(new ErrorResponse($"流程定义 '{processDefinitionId}' 不存在。"));
            }
        }

        var xml = await engine.GetProcessDefinitionXmlAsync(processDefinitionId, ct).ConfigureAwait(false);

        if (xml is null)
        {
            return Results.NotFound(new ErrorResponse($"流程定义 '{processDefinitionId}' 不存在。"));
        }

        if (!TryReadProcess(xml, out var processKey, out var processName, out var parseError))
        {
            return Results.BadRequest(new ErrorResponse(parseError));
        }

        var model = new SaveBpmnModelRequest
        {
            Name = string.IsNullOrWhiteSpace(name) ? processName ?? processKey : name!.Trim(),
            ProcessKey = processKey,
            BpmnXml = xml,
        };

        return await CreateModelCoreAsync(
                model,
                $"definition:{processDefinitionId}",
                store,
                engine,
                requestContext,
                auditor,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 复制模型：副本从版本 1 开始，<b>流程 key 自动改成 <c>{原 key}_copy</c></b>。
    /// <para>
    /// 同一租户下一个 key 只能对应一个模型，直接沿用原 key 会与唯一约束冲突；
    /// 副本必须换 key，且 XML 里的 <c>process/@id</c> 要同步改写，否则部署时用的还是旧 key。
    /// </para>
    /// </summary>
    private static async Task<IResult> DuplicateModelAsync(
        string id,
        DuplicateBpmnModelRequest? request,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (existing is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        var name = string.IsNullOrWhiteSpace(request?.Name)
            ? $"{existing.Name}（副本）"
            : request!.Name.Trim();

        var processKey = await NextAvailableProcessKeyAsync(existing.ProcessKey, store, ct)
            .ConfigureAwait(false);

        var model = new SaveBpmnModelRequest
        {
            Name = name,
            ProcessKey = processKey,
            Description = existing.Description,
            BpmnXml = RenameProcess(existing.BpmnXml, processKey, name),
            Svg = existing.Svg,
        };

        return await CreateModelCoreAsync(
                model,
                $"duplicate:{id}",
                store,
                engine,
                requestContext,
                auditor,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>新建 / 导入 / 复制 / 反建的公共落库逻辑（校验 + 审计 + 初始版本）。</summary>
    private static async Task<IResult> CreateModelCoreAsync(
        SaveBpmnModelRequest request,
        string? source,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new ErrorResponse("name 不能为空。"));
        }

        if (string.IsNullOrWhiteSpace(request.ProcessKey))
        {
            return Results.BadRequest(new ErrorResponse("processKey 不能为空。"));
        }

        if (string.IsNullOrWhiteSpace(request.BpmnXml))
        {
            return Results.BadRequest(new ErrorResponse("bpmnXml 不能为空。"));
        }

        // 同一 key 只允许一个模型：导入 / 复制 / 反建都可能撞上已有 key
        if (await EnsureProcessKeyAvailableAsync(request.ProcessKey, null, store, ct).ConfigureAwait(false)
            is { } keyConflict)
        {
            return keyConflict;
        }

        IReadOnlyList<ValidationError> issues;

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.BpmnXml));
            issues = await engine.ValidateAsync(stream, ct).ConfigureAwait(false);
        }
        catch (BpmnXmlParseException exception)
        {
            return Results.BadRequest(new ErrorResponse($"BPMN XML 解析失败：{exception.Message}"));
        }

        var created = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ModelCreate,
            AdminAuditTargets.Model,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { name = request.Name, request.ProcessKey, source },
            successDetail: detail => new { detail.Id, detail.ProcessKey, detail.Name, source },
            operation: async token => await store.CreateAsync(request, requestContext.UserId, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return Results.Created(
            $"/api/admin/models/{created.Id}",
            new SaveBpmnModelResponse(
                created.Id,
                1,
                issues.Where(issue => issue.IsError).ToArray(),
                issues.Where(issue => !issue.IsError).ToArray()));
    }

    /// <summary>
    /// <c>process_key</c> 唯一性预检（同租户内）：被别的模型占用时返回 409。
    /// <para>
    /// 数据库上有唯一索引兜底；这里提前判断是为了给出「被哪个模型占用」这种可操作信息
    /// （并发写入撞上唯一索引时仍然是 409，但由数据库报错决定）。
    /// </para>
    /// </summary>
    private static async Task<IResult?> EnsureProcessKeyAvailableAsync(
        string processKey,
        string? excludeId,
        BpmnModelStore store,
        CancellationToken ct)
    {
        var owner = await store
            .FindIdByProcessKeyAsync(processKey, excludeId, ct)
            .ConfigureAwait(false);

        return owner is null
            ? null
            : Results.Conflict(new ErrorResponse(
                $"流程 key '{processKey}' 已被模型 '{owner}' 占用：同一租户下 key 必须唯一，请改用其它 key。"));
    }

    /// <summary>
    /// 副本的流程 key：<c>{base}_copy</c>，已被占用则递增 <c>_copy2</c> / <c>_copy3</c>…
    /// （process_key 列宽 50，先截断基名给后缀留位置）。
    /// </summary>
    private static async Task<string> NextAvailableProcessKeyAsync(
        string baseKey,
        BpmnModelStore store,
        CancellationToken ct)
    {
        var stem = baseKey.Length > 40 ? baseKey[..40] : baseKey;

        for (var attempt = 1; attempt <= 50; attempt++)
        {
            var candidate = attempt == 1 ? $"{stem}_copy" : $"{stem}_copy{attempt}";

            if (await store.FindIdByProcessKeyAsync(candidate, null, ct).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }

        return $"{stem}_{Guid.NewGuid():N}"[..Math.Min(50, stem.Length + 9)];
    }

    /// <summary>
    /// 改写 XML 里 process 的 id / name（复制模型时同步 key）；解析失败时退回原文，
    /// 由保存 / 部署阶段的校验兜底报错。
    /// </summary>
    private static string RenameProcess(string xml, string processKey, string name)
    {
        try
        {
            var converter = new BpmnXmlConverter();

            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var bpmnModel = converter.ConvertToBpmnModel(input);
            var process = bpmnModel.Processes.FirstOrDefault();

            if (process is null)
            {
                return xml;
            }

            process.Id = processKey;
            process.Name = name;

            using var output = new MemoryStream();
            converter.ConvertToXml(bpmnModel, output);

            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception exception) when (exception is BpmnXmlParseException or System.Xml.XmlException)
        {
            return xml;
        }
    }

    /// <summary>从 XML 读 process id / name（导入、反建共用）；失败给出可操作错误。</summary>
    private static bool TryReadProcess(string xml, out string processKey, out string? processName, out string error)
    {
        processKey = string.Empty;
        processName = null;
        error = string.Empty;

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var bpmnModel = new BpmnXmlConverter().ConvertToBpmnModel(stream);
            var process = bpmnModel.Processes.FirstOrDefault();

            if (process is null || string.IsNullOrWhiteSpace(process.Id))
            {
                error = "XML 中没有可用的 process（缺少 id）。";
                return false;
            }

            processKey = process.Id;
            processName = process.Name;
            return true;
        }
        catch (Exception exception) when (exception is BpmnXmlParseException or System.Xml.XmlException)
        {
            error = $"BPMN XML 解析失败：{exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// 发布前预检：对模型当前 XML 跑与发布相同的解析 + 结构 / 表达式校验，返回结构化问题清单。
    /// <para>只读操作，不落库、不改版本；解析失败按阻断级问题返回（而不是 400）。</para>
    /// </summary>
    private static async Task<IResult> ValidateModelAsync(
        string id,
        BpmnModelStore store,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var model = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(model.BpmnXml));
            var issues = await engine.ValidateAsync(stream, ct).ConfigureAwait(false);

            return Results.Ok(new ModelValidationResponse(
                issues.Where(issue => issue.IsError).ToArray(),
                issues.Where(issue => !issue.IsError).ToArray()));
        }
        catch (BpmnXmlParseException exception)
        {
            return Results.Ok(new ModelValidationResponse(
                new[]
                {
                    new ValidationError("xml-parse", null, null, exception.Message, ValidationSeverity.Error),
                },
                Array.Empty<ValidationError>()));
        }
    }

    /// <summary>
    /// 删除模型（连同版本历史；已部署的定义不受影响）。删除写审计 model.delete。
    /// 模型不存在时 404 且不留审计（与发布一致：「删不存在的模型」不算一次管理动作）。
    /// </summary>
    private static async Task<IResult> DeleteModelAsync(
        string id,
        RequestContext requestContext,
        BpmnModelStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (await store.GetAsync(id, ct).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        var removed = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ModelDelete,
            AdminAuditTargets.Model,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { modelId = id },
            successDetail: _ => new { modelId = id, removed = true },
            operation: async token => await store.DeleteAsync(id, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return removed
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
    }

    /// <summary>
    /// 版本历史（分页，不含 XML/SVG 正文，按版本号倒序）。
    /// <para>默认 20 条、上限 200；<c>envelope=true</c> 返回分页信封，否则裸数组 + <c>X-Total-Count</c>。</para>
    /// </summary>
    private static async Task<IResult> ListModelHistoryAsync(
        string id,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        BpmnModelStore store,
        HttpResponse response,
        CancellationToken ct)
    {
        var first = Math.Max(0, firstResult ?? 0);
        var size = Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize);

        var page = await store.PageHistoryAsync(id, first, size, ct).ConfigureAwait(false);

        return PagedResults.Create(page.Items, page.Total, first, size, response, envelope == true);
    }

    private static async Task<IResult> GetModelRevisionAsync(
        string id,
        string revisionId,
        BpmnModelStore store,
        CancellationToken ct) =>
        await store.GetRevisionAsync(id, revisionId, ct).ConfigureAwait(false) is { } revision
            ? Results.Ok(revision)
            : Results.NotFound(new ErrorResponse($"模型 '{id}' 的版本 '{revisionId}' 不存在。"));

    private static async Task<IResult> RollbackModelAsync(
        string id,
        string revisionId,
        HttpRequest httpRequest,
        BpmnModelStore store,
        RequestContext requestContext,
        CancellationToken ct)
    {
        var conflict = await CheckRevisionAsync(httpRequest, id, store, ct).ConfigureAwait(false);

        if (conflict is not null)
        {
            return conflict;
        }

        return await store.RollbackAsync(id, revisionId, requestContext.UserId, ct).ConfigureAwait(false) is { } model
            ? Results.Ok(model)
            : Results.NotFound(new ErrorResponse($"模型 '{id}' 的版本 '{revisionId}' 不存在。"));
    }

    /// <summary>
    /// 解析 <c>If-Match: "12"</c>（也接受 <c>12</c> / 弱校验 <c>W/"12"</c>）。
    /// 未提供时返回 false —— 兼容旧客户端，不做并发校验。
    /// </summary>
    private static bool TryGetExpectedRevision(HttpRequest request, out int expectedRevision)
    {
        expectedRevision = 0;

        var value = request.Headers.IfMatch.ToString();

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim();

        if (token.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            token = token[2..].Trim();
        }

        token = token.Trim('"');

        return int.TryParse(
                token,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out expectedRevision)
            && expectedRevision > 0;
    }

    /// <summary>版本不匹配时返回 409；未带 <c>If-Match</c> 或版本一致返回 null。</summary>
    private static async Task<IResult?> CheckRevisionAsync(
        HttpRequest request,
        string id,
        BpmnModelStore store,
        CancellationToken ct)
    {
        if (!TryGetExpectedRevision(request, out var expected))
        {
            return null;
        }

        var current = await store.GetLatestRevisionAsync(id, ct).ConfigureAwait(false);

        if (current is null || current == expected)
        {
            return null;
        }

        return Results.Conflict(new RevisionConflictResponse(
            $"模型已被其他人修改（服务端版本 {current}，本次提交基于版本 {expected}），请刷新后重试。",
            current.Value));
    }

    /// <summary>
    /// 清理模型历史：只保留最近 <c>keepLatest</c> 个版本快照（默认 10，最新版本永远保留）。
    /// <para>清理本身也是管理动作（<c>model.history_prune</c>），成功 / 失败都写审计。</para>
    /// </summary>
    private static async Task<IResult> PruneModelHistoryAsync(
        string id,
        int? keepLatest,
        RequestContext requestContext,
        BpmnModelStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var keep = Math.Clamp(keepLatest ?? 10, 0, 1000);

        if (await store.GetAsync(id, ct).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        var removed = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ModelHistoryPrune,
            AdminAuditTargets.Model,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { modelId = id, keepLatest = keep },
            successDetail: removed => new { modelId = id, keepLatest = keep, removed },
            operation: async token => await store.PruneHistoryAsync(id, keep, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return Results.Ok(new ModelHistoryPruneResponse(removed, id, keep));
    }

    /// <summary>
    /// 发布模型：用当前 XML 走引擎部署（解析 + 校验 + 版本递增，与设计器部署同一套口径）。
    /// <para>
    /// 阻断级校验问题返回 400（<c>ProcessValidationException</c> → 400 + errors 明细），
    /// 提示级问题随响应 warnings 返回。请求体可省略；带 <c>svg</c> 时（管理端部署前用
    /// bpmn-js 渲染的流程图图片），把当前 XML 连同图片写入一条 <c>publish</c> 版本快照 ——
    /// 版本历史里即可查看「部署当时」的流程图。
    /// </para>
    /// <para>发布属高危管理动作，写审计 model.publish；模型不存在时 404 且不留审计。</para>
    /// </summary>
    private static async Task<IResult> PublishModelAsync(
        string id,
        HttpRequest httpRequest,
        RequestContext requestContext,
        BpmnModelStore store,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // 请求体可选：有 JSON body 才解析（无 body 的旧调用方 / 测试照常工作）。
        // 不用「可空 body 参数」绑定 —— 无 Content-Type 的 POST 会被推断为缺失 body 而拿不到正确响应。
        PublishModelRequest? request = null;

        if (httpRequest.ContentLength is > 0)
        {
            request = await httpRequest.ReadFromJsonAsync<PublishModelRequest>(ct).ConfigureAwait(false);
        }

        var model = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        var conflict = await CheckRevisionAsync(httpRequest, id, store, ct).ConfigureAwait(false);

        if (conflict is not null)
        {
            return conflict;
        }

        // 没有未发布的修改时不重复部署：重复点击「发布」不应该每次都产生新定义版本
        if (request?.SkipIfUnchanged == true
            && await TryBuildUnchangedPublishAsync(model, store, engine, requestContext, ct)
                .ConfigureAwait(false) is { } unchanged)
        {
            await auditor.RecordAsync(
                requestContext,
                AdminAuditActions.ModelPublish,
                AdminAuditTargets.Model,
                ResolveTenant(null, requestContext) ?? "all",
                detail: new
                {
                    modelId = id,
                    processKey = model.ProcessKey,
                    skipped = true,
                    unchanged.ProcessDefinitionId,
                    unchanged.Version,
                },
                ct: ct).ConfigureAwait(false);

            return Results.Ok(unchanged);
        }

        ProcessDefinition definition = null!;
        IReadOnlyList<NetBpm.Bpmn.Validation.ValidationError> deployWarnings =
            Array.Empty<NetBpm.Bpmn.Validation.ValidationError>();

        definition = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ModelPublish,
            AdminAuditTargets.Model,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { modelId = id, processKey = model.ProcessKey },
            successDetail: deployed => new
            {
                modelId = id,
                processKey = model.ProcessKey,
                definitionId = deployed.Id,
                version = deployed.Version,
                deploymentId = deployed.DeploymentId,
            },
            operation: async token =>
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(model.BpmnXml));

                var result = await engine
                    .DeployWithValidationAsync(
                        stream,
                        $"{model.Id}.bpmn20.xml",
                        $"模型发布：{model.Name}",
                        ct: token)
                    .ConfigureAwait(false);

                deployWarnings = result.Warnings;

                // 部署成功后总是落一条 publish 版本快照：
                // 「是否已发布 / 发布历史」依赖它（只有看图的旧逻辑会漏记没有 SVG 的发布）。
                // 没有新 SVG 时沿用主表现有预览，避免把已有图清空；快照失败不影响部署结果本身。
                await store
                    .SaveAsync(
                        id,
                        new SaveBpmnModelRequest
                        {
                            Name = model.Name,
                            ProcessKey = model.ProcessKey,
                            Description = model.Description,
                            BpmnXml = model.BpmnXml,
                            Svg = string.IsNullOrWhiteSpace(request?.Svg) ? model.Svg : request.Svg,
                        },
                        requestContext.UserId,
                        "publish",
                        token)
                    .ConfigureAwait(false);

                return result.ProcessDefinition;
            },
            ct).ConfigureAwait(false);

        // 发布后自检：委托（服务任务 / 各类监听器）能否解析 —— 解析不到时随响应回给调用方，
        // 避免「发布成功、实例跑到该节点才发现没实现」。
        var precheck = await DeploymentDelegatePrecheck.VerifyAsync(
                httpContext.RequestServices,
                engine,
                definition!.Id,
                loggerFactory.CreateLogger(nameof(AdminEndpoints)),
                ct)
            .ConfigureAwait(false);

        // 提示级校验 + 委托自检合并返回；全空时给 null（契约里「无提示」的语义）
        var warnings = (deployWarnings.Concat(precheck ?? Array.Empty<NetBpm.Bpmn.Validation.ValidationError>()))
            .ToArray();

        return Results.Ok(new DeployResponse(
            definition.Id,
            definition.Key,
            definition.Version,
            definition.DeploymentId,
            definition.ResourceName,
            $"模型发布：{model.Name}",
            Warnings: warnings.Length == 0 ? null : warnings));
    }

    /// <summary>
    /// 发布历史：版本历史里的 <c>publish</c> 快照 + 就近匹配到的部署定义。
    /// <para>
    /// 快照与部署记录没有外键，这里按「同 processKey + 部署时间最接近」匹配，
    /// 只有唯一命中才回填 definitionId / version（并发发布时宁可返回未关联，也不猜）。
    /// </para>
    /// </summary>
    private static async Task<IResult> ListModelPublicationsAsync(
        string id,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        CancellationToken ct)
    {
        var model = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound(new ErrorResponse($"流程模型 '{id}' 不存在。"));
        }

        var history = await store.ListHistoryAsync(id, ct).ConfigureAwait(false);
        var publications = history
            .Where(revision => string.Equals(revision.Action, "publish", StringComparison.OrdinalIgnoreCase))
            .OrderBy(revision => revision.CreatedAt)
            .ToArray();

        if (publications.Length == 0)
        {
            return Results.Ok(Array.Empty<ModelPublicationResponse>());
        }

        var definitions = await engine
            .FindProcessDefinitionsAsync(
                new ProcessDefinitionQuery(Key: model.ProcessKey, TenantId: ResolveTenant(null, requestContext)),
                ct)
            .ConfigureAwait(false);

        var unmatched = definitions
            .Where(definition => definition.DeployTime is not null)
            .OrderBy(definition => definition.DeployTime)
            .ToList();

        var results = new List<ModelPublicationResponse>(publications.Length);

        foreach (var publication in publications)
        {
            var candidates = unmatched
                .Where(definition =>
                    Math.Abs((definition.DeployTime!.Value - publication.CreatedAt).TotalSeconds)
                    <= PublicationMatchWindowSeconds)
                .ToArray();

            if (candidates.Length == 1)
            {
                var matched = candidates[0];
                unmatched.Remove(matched);

                results.Add(new ModelPublicationResponse(
                    publication.Revision,
                    publication.CreatedAt,
                    publication.Operator,
                    matched.Id,
                    matched.Version,
                    true));
            }
            else
            {
                results.Add(new ModelPublicationResponse(
                    publication.Revision,
                    publication.CreatedAt,
                    publication.Operator,
                    null,
                    null,
                    false));
            }
        }

        return Results.Ok(results.OrderByDescending(item => item.Revision).ToArray());
    }

    /// <summary>发布快照与部署时间的匹配窗口（秒）；发布快照在部署返回后立刻写入，通常 &lt; 1s。</summary>
    private const double PublicationMatchWindowSeconds = 10;

    /// <summary>批量补齐列表页的发布状态：一次读版本水位、一次读同租户定义。</summary>
    private static async Task EnrichPublishStatesAsync(
        IReadOnlyList<BpmnModelSummary> models,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        CancellationToken ct)
    {
        if (models.Count == 0)
        {
            return;
        }

        var stamps = await store
            .GetRevisionStampsAsync(models.Select(model => model.Id).ToArray(), ct)
            .ConfigureAwait(false);

        var definitions = await engine
            .FindProcessDefinitionsAsync(
                new ProcessDefinitionQuery(TenantId: ResolveTenant(null, requestContext)),
                ct)
            .ConfigureAwait(false);

        var latestByKey = new Dictionary<string, DeployedProcessDefinition>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            if (!latestByKey.TryGetValue(definition.Key, out var current) || definition.Version > current.Version)
            {
                latestByKey[definition.Key] = definition;
            }
        }

        foreach (var model in models)
        {
            stamps.TryGetValue(model.Id, out var stamp);
            latestByKey.TryGetValue(model.ProcessKey, out var definition);
            model.Publish = BuildPublishState(stamp, definition);
        }
    }

    /// <summary>单个模型的发布状态（详情页使用，只查该 key 的定义）。</summary>
    private static async Task<BpmnModelPublishState> ResolvePublishStateAsync(
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        string modelId,
        string processKey,
        CancellationToken ct)
    {
        var stamps = await store.GetRevisionStampsAsync(new[] { modelId }, ct).ConfigureAwait(false);
        var definitions = await engine
            .FindProcessDefinitionsAsync(
                new ProcessDefinitionQuery(Key: processKey, TenantId: ResolveTenant(null, requestContext)),
                ct)
            .ConfigureAwait(false);

        stamps.TryGetValue(modelId, out var stamp);

        return BuildPublishState(stamp, definitions.OrderByDescending(definition => definition.Version).FirstOrDefault());
    }

    /// <summary>
    /// 发布幂等：模型没有未发布的修改时，直接回放线上最新定义。
    /// <para>
    /// 判据用版本水位（<c>publishedRevision &gt;= latestRevision</c>），不比对 XML 文本 ——
    /// 排版差异不该被当成「内容变了」而多造一个定义版本；确实要重新部署可不传该开关。
    /// </para>
    /// </summary>
    private static async Task<DeployResponse?> TryBuildUnchangedPublishAsync(
        BpmnModelDetail model,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        CancellationToken ct)
    {
        var stamps = await store.GetRevisionStampsAsync(new[] { model.Id }, ct).ConfigureAwait(false);

        if (!stamps.TryGetValue(model.Id, out var stamp)
            || stamp.PublishedRevision is not { } published
            || stamp.LatestRevision > published)
        {
            return null;
        }

        // 部署用的是 XML 里的 process id，模型名下的 processKey 只是登记值：
        // 两者不一致时按 XML 查（老数据 / 导入模型可能存在这种漂移），否则查不到线上定义会白白重发一版
        var definitionKey = TryReadProcess(model.BpmnXml, out var xmlKey, out _, out _)
            && !string.IsNullOrWhiteSpace(xmlKey)
                ? xmlKey
                : model.ProcessKey;

        var definitions = await engine
            .FindProcessDefinitionsAsync(
                new ProcessDefinitionQuery(Key: definitionKey, TenantId: ResolveTenant(null, requestContext)),
                ct)
            .ConfigureAwait(false);

        var latest = definitions.OrderByDescending(definition => definition.Version).FirstOrDefault();

        return latest is null
            ? null
            : new DeployResponse(
                latest.Id,
                latest.Key,
                latest.Version,
                latest.DeploymentId,
                latest.ResourceName ?? $"{latest.Key}.bpmn20.xml",
                $"模型发布：{model.Name}（内容未变化，跳过部署）",
                Warnings: null,
                Skipped: true);
    }

    private static BpmnModelPublishState BuildPublishState(
        BpmnModelStore.BpmnModelRevisionStamp? stamp,
        DeployedProcessDefinition? latestDefinition)
    {
        var state = new BpmnModelPublishState
        {
            CurrentRevision = stamp?.LatestRevision,
            PublishedRevision = stamp?.PublishedRevision,
            // 只在有发布记录时判断：历史数据（早期没有 publish 快照）不做无依据的「未发布」提示
            HasUnpublishedChanges = stamp?.PublishedRevision is { } publishedRevision
                && stamp.LatestRevision > publishedRevision,
        };

        if (latestDefinition is not null)
        {
            state.LatestProcessDefinitionId = latestDefinition.Id;
            state.LatestPublishedVersion = latestDefinition.Version;
            state.LatestPublishedAt = latestDefinition.DeployTime;
        }

        return state;
    }

    private static async Task<IReadOnlyList<ProcessType>> ListProcessTypesAsync(
        ProcessTypeStore store,
        CancellationToken ct) => await store.ListAsync(ct).ConfigureAwait(false);

    private static async Task<IResult> GetProcessTypeAsync(
        string id,
        ProcessTypeStore store,
        CancellationToken ct) =>
        await store.GetAsync(id, ct).ConfigureAwait(false) is { } item
            ? Results.Ok(item)
            : Results.NotFound(new ErrorResponse($"流程类型 '{id}' 不存在。"));

    private static async Task<IResult> CreateProcessTypeAsync(
        SaveProcessTypeRequest request,
        ProcessTypeStore store,
        RequestContext requestContext,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new ErrorResponse("name 不能为空。"));
        }

        var created = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ProcessTypeCreate,
            AdminAuditTargets.ProcessType,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { name = request.Name },
            successDetail: processType => new { id = processType.Id, name = processType.Name },
            operation: async token => await store.CreateAsync(request, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return Results.Created($"/api/admin/process-types/{created.Id}", created);
    }

    private static async Task<IResult> UpdateProcessTypeAsync(
        string id,
        SaveProcessTypeRequest request,
        ProcessTypeStore store,
        RequestContext requestContext,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new ErrorResponse("name 不能为空。"));
        }

        if (await store.GetAsync(id, ct).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new ErrorResponse($"流程类型 '{id}' 不存在。"));
        }

        var updated = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ProcessTypeUpdate,
            AdminAuditTargets.ProcessType,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { id, name = request.Name },
            successDetail: changed => new { id, name = request.Name, changed },
            operation: async token => await store.UpdateAsync(id, request, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return updated
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse($"流程类型 '{id}' 不存在。"));
    }

    private static async Task<IResult> DeleteProcessTypeAsync(
        string id,
        ProcessTypeStore store,
        RequestContext requestContext,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (existing is null)
        {
            return Results.NotFound(new ErrorResponse($"流程类型 '{id}' 不存在。"));
        }

        var removed = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.ProcessTypeDelete,
            AdminAuditTargets.ProcessType,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { id, name = existing.Name },
            successDetail: deleted => new { id, name = existing.Name, deleted },
            operation: async token => await store.DeleteAsync(id, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return removed
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse($"流程类型 '{id}' 不存在。"));
    }
}

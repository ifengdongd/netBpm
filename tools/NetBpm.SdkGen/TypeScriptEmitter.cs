using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NetBpm.SdkGen;

/// <summary>
/// TypeScript 产物：<c>models.ts</c>（模型）+ <c>client.ts</c>（客户端）+ 工程骨架。
/// </summary>
/// <remarks>
/// <para>
/// <c>client.ts</c> 分两段：一段是<b>固定的运行时</b>（拼 URL、挂身份头、把非 2xx 转成异常、返序列化），
/// 一段是<b>按操作生成</b>的资源类方法。运行时不做成「每个方法各写一遍」——那会让 23 个方法
/// 各有一套错误处理，修一处漏 22 处；它也不做成「另一个包」，因为 SDK 就一层 transport，
/// 拆包只会让 `npm i` 之后还要再 `npm i`。
/// </para>
/// </remarks>
public sealed class TypeScriptEmitter(ContractDocument contract)
{
    private const string PackageName = "@netbpm/platform-sdk";
    private const string PackageVersion = "1.0.0";

    /// <summary>生成 <c>sdk/typescript</c> 下的全部文件。</summary>
    public IEnumerable<GeneratedFile> Emit()
    {
        yield return new GeneratedFile("sdk/typescript/src/models.ts", Models());
        yield return new GeneratedFile("sdk/typescript/src/client.ts", Client());
        yield return new GeneratedFile("sdk/typescript/src/index.ts", Index());
        yield return new GeneratedFile("sdk/typescript/package.json", PackageJson());
        yield return new GeneratedFile("sdk/typescript/tsconfig.json", TsConfig());
    }

    // ─────────────────────────── models.ts ───────────────────────────

    private string Models()
    {
        var builder = new StringBuilder();

        builder.Append(Banner(
            "NetBpm 平台业务面（`/workflow/v1`）的数据模型。",
            "模型来自 OpenAPI 快照里「被业务面操作可达」的那部分 schema；`/api/**` 与 `/admin/v1` 的类型不在其中。"));

        foreach (var name in contract.ReachableSchemaNames)
        {
            var schema = contract.Schema(name);

            if (schema is not JsonObject map)
            {
                continue;
            }

            var values = Schemas.EnumValues(map);

            builder.Append(values.Count > 0 && Schemas.Types(map).Count == 0
                ? Enum(name, values)
                : Record(name, map));
        }

        return builder.ToString();
    }

    private static string Enum(string name, IReadOnlyList<string> values)
    {
        var builder = new StringBuilder();

        builder.AppendLine(CultureInfo.InvariantCulture, $"// ── {name} ──");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"export type {name} = {TypeMapper.Union(values)};");
        builder.AppendLine();

        var constant = OperationNaming.Camel(name) + "Values";

        builder.AppendLine(CultureInfo.InvariantCulture, $"export const {constant}: readonly {name}[] = [");

        foreach (var value in values)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  \"{value}\",");
        }

        builder.AppendLine("];");
        builder.AppendLine();
        builder.AppendLine("/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */");

        builder.AppendLine(CultureInfo.InvariantCulture, $$"""
        export function parse{{name}}(value: string | null | undefined): {{name}} | undefined {
          return value != null && ({{constant}} as readonly string[]).includes(value)
            ? (value as {{name}})
            : undefined;
        }

        """);

        return builder.ToString();
    }

    private static string Record(string name, JsonObject map)
    {
        var required = TypeMapper.RequiredNames(map);
        var properties = map["properties"] as JsonObject;

        var builder = new StringBuilder();

        builder.AppendLine(CultureInfo.InvariantCulture, $"// ── {name} ──");
        builder.AppendLine();

        if (properties is null || properties.Count == 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"export type {name} = Record<string, unknown>;");
            builder.AppendLine();

            return builder.ToString();
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"export interface {name} {{");

        foreach (var (property, schema) in properties)
        {
            var optional = required.Contains(property) ? string.Empty : "?";

            builder.AppendLine(CultureInfo.InvariantCulture, $"  {TypeMapper.PropertyName(property)}{optional}: {TypeMapper.TypeScript(schema)};");
        }

        builder.AppendLine("}");
        builder.AppendLine();

        return builder.ToString();
    }

    // ─────────────────────────── client.ts ───────────────────────────

    private string Client()
    {
        var builder = new StringBuilder();

        var banner = Banner(
            "NetBpm 平台业务面（`/workflow/v1`）的 TypeScript 客户端。",
            "资源分组与 .NET SDK 一一对应（instances / processes / tasks），方法名相同，便于跨栈对照。");

        var body = new StringBuilder();

        body.Append(Runtime);

        foreach (var group in Groups())
        {
            body.Append(ResourceClass(group.Key, group.ToArray()));
        }

        body.Append(RootClient());

        // 只引正文真正用到的模型：全引会得到一个「一半是死代码」的 import 块，
        // 漏引则会直接编译不过——所以按正文命中算，不靠人记。
        return banner + ModelImports(body.ToString()) + body;
    }

    /// <summary>按正文里出现的类型名生成 <c>import type</c>（顺序稳定：按名称序）。</summary>
    private string ModelImports(string body)
    {
        var used = contract.ReachableSchemaNames
            .Where(name => System.Text.RegularExpressions.Regex.IsMatch(
                body,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b"))
            .ToArray();

        if (used.Length == 0)
        {
            return string.Empty;
        }

        // 一行塞 25 个类型名没法 review；超过 3 个就一行一个
        return used.Length <= 3
            ? $"import type {{ {string.Join(", ", used)} }} from \"./models.js\";\n\n"
            : "import type {\n"
                + string.Concat(used.Select(name => $"  {name},\n"))
                + "} from \"./models.js\";\n\n";
    }

    private string Index() =>
        Banner("导出全部模型与客户端。", "按需 `import { NetBpmClient } from \"@netbpm/platform-sdk\"`。")
        + "export * from \"./models.js\";\n"
        + "export * from \"./client.js\";\n";

    /// <summary>按资源分组（instances / processes / tasks），组内按快照顺序。</summary>
    private IEnumerable<IGrouping<string, ContractOperation>> Groups() =>
        contract.Operations
            .GroupBy(operation => OperationNaming.Resolve(operation).Resource)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

    private static string ResourceClass(string resource, ContractOperation[] operations)
    {
        var typeName = OperationNaming.Resolve(operations[0]).ResourceType;

        var queryTypes = new StringBuilder();
        var members = new StringBuilder();

        foreach (var operation in operations)
        {
            var name = OperationNaming.Resolve(operation);

            if (operation.QueryParameters.Count > 0)
            {
                queryTypes.Append(QueryInterface(typeName, name.Method, operation));
            }

            members.Append(Method(typeName, name.Method, operation));
        }

        var builder = new StringBuilder();

        builder.AppendLine(CultureInfo.InvariantCulture, $"// ────────────────────── {resource} ──────────────────────");
        builder.AppendLine(CultureInfo.InvariantCulture, $"export class {OperationNaming.Pascal(resource)}Client {{");
        builder.AppendLine("  /** @internal 由 {@link NetBpmClient} 构造；直接 new 会绕开共享的 transport。 */");
        builder.AppendLine("  constructor(private readonly _t: HttpTransport) {}");
        builder.AppendLine();

        // 成员之间靠 Method() 自己带的空行分隔；这里只裁掉末尾多余的那一行
        builder.Append(members.ToString().TrimEnd('\r', '\n'));
        builder.AppendLine();
        builder.AppendLine("}");
        builder.AppendLine();

        // 查询类型放前面：先声明再使用，读起来顺（TS 其实不要求顺序）
        return queryTypes + builder.ToString();
    }

    private static string QueryInterface(string typeName, string method, ContractOperation operation)
    {
        var builder = new StringBuilder();

        builder.AppendLine(CultureInfo.InvariantCulture, $"/** `{operation.Method} {operation.Path}` 的查询参数。 */");
        builder.AppendLine(CultureInfo.InvariantCulture, $"export interface {QueryName(typeName, method)} extends QueryShape {{");

        foreach (var parameter in operation.QueryParameters)
        {
            var optional = parameter.Required ? string.Empty : "?";

            builder.AppendLine(CultureInfo.InvariantCulture, $"  {parameter.Name}{optional}: {QueryType(parameter.Schema)};");
        }

        builder.AppendLine("}");
        builder.AppendLine();

        return builder.ToString();
    }

    private static string QueryName(string typeName, string method) =>
        typeName + OperationNaming.Pascal(method) + "Query";

    /// <summary>查询串里的类型：时间接受 <c>string | Date</c>（由 transport 统一格式化）。</summary>
    private static string QueryType(JsonNode? schema)
    {
        var types = Schemas.Types(schema);

        if (types.Contains("array"))
        {
            return $"readonly {QueryType(schema?["items"])}[]";
        }

        if (types.Contains("boolean"))
        {
            return "boolean";
        }

        if (types.Contains("integer") || types.Contains("number"))
        {
            return "number";
        }

        return Schemas.Format(schema) == "date-time" ? "string | Date" : "string";
    }

    private static string Method(string typeName, string method, ContractOperation operation)
    {
        var parameters = new List<string>();

        foreach (var pathParameter in operation.PathParameters)
        {
            parameters.Add($"{pathParameter.Name}: string");
        }

        if (operation.QueryParameters.Count > 0)
        {
            parameters.Add($"query: {QueryName(typeName, method)} = {{}}");
        }

        if (operation.RequestSchema is not null)
        {
            var bodyType = TypeMapper.TypeScript(operation.RequestSchema);

            parameters.Add(operation.RequestRequired ? $"body: {bodyType}" : $"body?: {bodyType}");
        }

        var isPost = string.Equals(operation.Method, "POST", StringComparison.Ordinal);

        if (isPost)
        {
            parameters.Add("options?: RequestOptions");
        }

        var returnType = operation.ReturnsText
            ? "Promise<string>"
            : operation.SuccessSchema is null
                ? "Promise<void>"
                : $"Promise<{TypeMapper.TypeScript(operation.SuccessSchema)}>";

        var builder = new StringBuilder();

        builder.AppendLine(CultureInfo.InvariantCulture, $"  /** `{operation.Method} {operation.Path}` */");

        if (parameters.Count == 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {method}(): {returnType} {{");
        }
        else if (parameters.Count == 1)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {method}({parameters[0]}): {returnType} {{");
        }
        else
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {method}(");

            foreach (var parameter in parameters)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"    {parameter},");
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"  ): {returnType} {{");
        }

        builder.Append(Call(operation, isPost));

        builder.AppendLine("  }");
        builder.AppendLine();

        if (operation.IsCursorPage)
        {
            builder.Append(Iterate(typeName, method, operation));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string Call(ContractOperation operation, bool isPost)
    {
        var path = operation.Path.TrimStart('/');
        var hasPathParameters = operation.PathParameters.Count > 0;

        var pathExpression = hasPathParameters
            ? $"fill(\"{path}\", {{ {string.Join(", ", operation.PathParameters.Select(parameter => parameter.Name))} }})"
            : $"\"{path}\"";

        var arguments = new List<string> { pathExpression };

        if (operation.QueryParameters.Count > 0)
        {
            arguments.Add("query");
        }

        if (operation.RequestSchema is not null)
        {
            arguments.Add("body");
        }

        if (isPost)
        {
            arguments.Add("options");
        }

        var target = operation.ReturnsText
            ? "getText"
            : operation.Method switch
            {
                "POST" => "post",
                "PUT" => "put",
                "DELETE" => "delete",
                _ => "get",
            };

        var generic = operation.ReturnsText || operation.SuccessSchema is null
            ? string.Empty
            : $"<{TypeMapper.TypeScript(operation.SuccessSchema)}>";

        // 带 fill() 的路径较长：拆成多行，避免单行 120+ 字符后每行都变成噪声
        if (hasPathParameters)
        {
            var builder = new StringBuilder();

            builder.AppendLine(CultureInfo.InvariantCulture, $"    return this._t.{target}{generic}(");

            foreach (var argument in arguments)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"      {argument},");
            }

            builder.AppendLine("    );");

            return builder.ToString();
        }

        return $"    return this._t.{target}{generic}({string.Join(", ", arguments)});\n";
    }

    /// <summary>分页列表的懒遍历：调用方不必手写游标循环。</summary>
    private static string Iterate(string typeName, string method, ContractOperation operation)
    {
        var itemType = operation.PageItemTypeName ?? "unknown";
        var queryName = QueryName(typeName, method);

        return $$"""
          /** 逐页翻完（自动跟 `nextCursor`），适合「全量同步」这类场景。 */
          async *iterate(query: {{queryName}} = {}): AsyncGenerator<{{itemType}}, void, undefined> {
            let cursor = query.cursor;

            for (;;) {
              const page = await this.{{method}}({ ...query, cursor });

              for (const item of page.items) {
                yield item;
              }

              if (!page.hasMore || !page.nextCursor) {
                return;
              }

              cursor = page.nextCursor;
            }
          }

        """;
    }

    private string RootClient()
    {
        var builder = new StringBuilder();

        builder.AppendLine("/** SDK 入口：一个实例持有共享的 transport 与三个资源客户端。 */");
        builder.AppendLine("export class NetBpmClient {");
        builder.AppendLine("  readonly transport: HttpTransport;");

        foreach (var group in Groups())
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  readonly {group.Key}: {OperationNaming.Pascal(group.Key)}Client;");
        }

        builder.AppendLine();
        builder.AppendLine("  constructor(options: NetBpmClientOptions = {}) {");
        builder.AppendLine("    this.transport = new HttpTransport(options);");

        foreach (var group in Groups())
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"    this.{group.Key} = new {OperationNaming.Pascal(group.Key)}Client(this.transport);");
        }

        builder.AppendLine("  }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string PackageJson() => $$"""
    {
      "name": "{{PackageName}}",
      "version": "{{PackageVersion}}",
      "private": true,
      "type": "module",
      "main": "./src/index.ts",
      "types": "./src/index.ts",
      "scripts": {
        "typecheck": "tsc -p tsconfig.json"
      },
      "devDependencies": {
        "typescript": "^5.9.0"
      }
    }

    """;

    private static string TsConfig() => """
    {
      "compilerOptions": {
        "target": "ES2022",
        "lib": ["ES2022", "DOM"],
        "module": "ESNext",
        "moduleResolution": "Bundler",
        "strict": true,
        "noUncheckedIndexedAccess": true,
        "noImplicitOverride": true,
        "noEmit": true,
        "skipLibCheck": true,
        "forceConsistentCasingInFileNames": true
      },
      "include": ["src/**/*.ts"]
    }

    """;

    /// <summary>生成文件开头的「请勿手改」横幅。</summary>
    private string Banner(string summary, string detail)
    {
        var builder = new StringBuilder();

        builder.AppendLine("/**");
        builder.AppendLine(CultureInfo.InvariantCulture, $" * {summary}");
        builder.AppendLine(" *");
        builder.AppendLine(CultureInfo.InvariantCulture, $" * {detail}");
        builder.AppendLine(" *");
        builder.AppendLine(CultureInfo.InvariantCulture, $" * 本文件由 `tools/NetBpm.SdkGen` 从 `contracts/openapi/workflow-v1.json`（OpenAPI "
            + $"{contract.OpenApiVersion}）生成，**请勿手改**：改了会在 SdkGenerationTests 里红。");
        builder.AppendLine(" * 重新生成：`dotnet run --project tools/NetBpm.SdkGen`");
        builder.AppendLine(" */");
        builder.AppendLine();

        return builder.ToString();
    }

    // ─────────────────────────── 固定运行时 ───────────────────────────

    /// <summary>
    /// 运行时（transport / 错误 / 头 / 查询串），与具体端点无关，因此整段固定。
    /// </summary>
    private const string Runtime = """
// ────────────────────── 请求与错误 ──────────────────────

/** SDK 配置。 */
export interface NetBpmClientOptions {
  /** 服务根地址，默认 `http://localhost:5080/`。可带虚拟目录（`https://host/netbpm/`），末尾斜杠会自动补。 */
  baseUrl?: string;
  /** 客户端凭据（`X-Api-Key`）。 */
  apiKey?: string;
  /** 用户令牌（`Authorization: Bearer`）。传函数则每个请求调用一次，便于续期。 */
  accessToken?: string | (() => string | Promise<string>);
  /** 代客调用：审批结论记在这个人身上，读接口的可见性也收窄到他的视角（需 `workflow:task:submit:onbehalf`）。 */
  subjectId?: string;
  tenantId?: string;
  /** 单请求超时（毫秒），默认 100 秒。 */
  timeoutMs?: number;
  /** 自定义 `fetch`（测试桩、代理，或 Node 18 之前自行注入）。 */
  fetch?: typeof globalThis.fetch;
  /** 额外请求头。 */
  headers?: Record<string, string>;
}

/** 单个请求的可选项。 */
export interface RequestOptions {
  /** 幂等键：同键重放不会重复开单 / 重复提交（写操作专用）。 */
  idempotencyKey?: string;
  /** 取消信号；不传则按 `timeoutMs` 自动超时。 */
  signal?: AbortSignal;
}

/** 查询串允许的取值（`Date` 由 transport 转成 ISO 8601）。 */
export type QueryValue = string | number | boolean | Date | null | undefined;

/** 查询参数容器：生成的 `*Query` 接口都 extends 它，transport 才收得下任意查询形状。 */
export type QueryShape = Record<string, QueryValue>;

/**
 * 平台返回的错误（RFC 9457 `application/problem+json`）。
 *
 * 非 2xx 一律抛它——包括网关返回的 HTML（此时 `problem` 为空，`body` 保留原文），
 * 以及 2xx 却不是合法 JSON 的情况（此时 `problem` 为空、`body` 说明原因）。
 */
export class NetBpmApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: PlatformProblem | undefined,
    readonly body: string,
    readonly path: string,
  ) {
    super(problem ? `${status} ${problem.code}：${problem.title}` : `${status}（响应体不是问题详情）`);
    this.name = "NetBpmApiError";
  }

  /** 稳定的错误码（如 `task.not_found`）；网关错误时为空。 */
  get code(): string | undefined {
    return this.problem?.code ?? undefined;
  }

  /** 排查用的链路号：报障时把它给对方，能在日志里直接定位。 */
  get traceId(): string | undefined {
    return this.problem?.traceId ?? undefined;
  }

  /** 字段级校验明细（422 时通常有值）。 */
  get errors() {
    return this.problem?.errors ?? undefined;
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }

  get isConflict(): boolean {
    return this.status === 409;
  }

  get isInvalidRequest(): boolean {
    return this.status === 400 || this.status === 422;
  }

  get isAuthorizationFailure(): boolean {
    return this.status === 401 || this.status === 403;
  }

  /** 值得重试的失败（限流 / 网关 / 服务不可用）。 */
  get isTransient(): boolean {
    return this.status === 429 || this.status === 502 || this.status === 503 || this.status === 504;
  }
}

const DEFAULT_BASE_URL = "http://localhost:5080/";
const DEFAULT_TIMEOUT_MS = 100_000;
const BODY_LIMIT = 4096;

/** 把路径模板里的 `{name}` 换成转义后的实参。 */
export function fill(template: string, parameters: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = parameters[name];

    if (value === undefined) {
      throw new Error(`缺少路径参数 ${name}（模板 ${template}）`);
    }

    return encodeURIComponent(value);
  });
}

/**
 * 一层薄 transport：所有资源客户端共用，因此「头怎么挂、错怎么抛」只有一份实现。
 *
 * 路径一律写成相对形式（`workflow/v1/processes`，不带前导斜杠），
 * 这样 `baseUrl` 可以带虚拟目录；前导斜杠会把虚拟目录吃掉。
 */
export class HttpTransport {
  private readonly baseUrl: string;
  private readonly fetchImpl: typeof globalThis.fetch;
  private readonly options: NetBpmClientOptions;

  constructor(options: NetBpmClientOptions = {}) {
    this.options = options;
    this.baseUrl = normalizeBaseUrl(options.baseUrl ?? DEFAULT_BASE_URL);

    const impl = options.fetch ?? globalThis.fetch;

    if (impl === undefined) {
      throw new Error("找不到 fetch()：浏览器与 Node 18+ 自带；其他环境请通过 options.fetch 注入。");
    }

    this.fetchImpl = impl;
  }

  async get<T>(path: string, query?: QueryShape, options?: RequestOptions): Promise<T> {
    const response = await this.execute("GET", path, query, undefined, options);

    return readJson<T>(response, path);
  }

  async getText(path: string, query?: QueryShape, options?: RequestOptions): Promise<string> {
    const response = await this.execute("GET", path, query, undefined, options);

    return response.text();
  }

  async post<T>(path: string, body: unknown, options?: RequestOptions): Promise<T> {
    const response = await this.execute("POST", path, undefined, body, options);

    return readJson<T>(response, path);
  }

  async put<T>(path: string, body: unknown, options?: RequestOptions): Promise<T> {
    const response = await this.execute("PUT", path, undefined, body, options);

    return readJson<T>(response, path);
  }

  async delete(path: string, options?: RequestOptions): Promise<void> {
    await this.execute("DELETE", path, undefined, undefined, options);
  }

  private async execute(
    method: string,
    path: string,
    query: QueryShape | undefined,
    body: unknown,
    options: RequestOptions | undefined,
  ): Promise<Response> {
    const init: RequestInit = {
      method,
      headers: await this.buildHeaders(body !== undefined, options),
      signal: options?.signal ?? AbortSignal.timeout(this.options.timeoutMs ?? DEFAULT_TIMEOUT_MS),
    };

    if (body !== undefined) {
      init.body = JSON.stringify(body);
    }

    const response = await this.fetchImpl(this.buildUrl(path, query), init);

    if (!response.ok) {
      throw await toApiError(response, path);
    }

    return response;
  }

  private buildUrl(path: string, query?: QueryShape): string {
    const url = new URL(path, this.baseUrl);

    for (const [name, value] of Object.entries(query ?? {})) {
      if (value === undefined || value === null || value === "") {
        continue;
      }

      url.searchParams.append(name, value instanceof Date ? value.toISOString() : String(value));
    }

    return url.toString();
  }

  private async buildHeaders(hasBody: boolean, options?: RequestOptions): Promise<Record<string, string>> {
    const headers: Record<string, string> = { Accept: "application/json", ...this.options.headers };

    if (hasBody) {
      headers["Content-Type"] = "application/json";
    }

    const token =
      typeof this.options.accessToken === "function" ? await this.options.accessToken() : this.options.accessToken;

    if (token) {
      headers["Authorization"] = `Bearer ${token}`;
    }

    if (this.options.apiKey) {
      headers["X-Api-Key"] = this.options.apiKey;
    }

    if (this.options.subjectId) {
      headers["X-Subject-Id"] = this.options.subjectId;
    }

    if (this.options.tenantId) {
      headers["X-Tenant-Id"] = this.options.tenantId;
    }

    if (options?.idempotencyKey) {
      headers["Idempotency-Key"] = options.idempotencyKey;
    }

    return headers;
  }
}

function normalizeBaseUrl(value: string): string {
  return value.endsWith("/") ? value : `${value}/`;
}

function truncate(text: string, limit: number = BODY_LIMIT): string {
  return text.length <= limit ? text : `${text.slice(0, limit)}…`;
}

async function readJson<T>(response: Response, path: string): Promise<T> {
  const text = await response.text();

  // 2xx 但响应体为空只可能是服务端实现问题：报出来，而不是静默返回 undefined
  // （后者会让调用方在很远的地方才吃到 TypeError，排查成本天差地别）
  if (text.trim().length === 0) {
    throw new NetBpmApiError(response.status, undefined, "（2xx 但响应体为空）", path);
  }

  try {
    return JSON.parse(text) as T;
  } catch (error) {
    throw new NetBpmApiError(response.status, undefined, `（响应体不是合法 JSON：${String(error)}）`, path);
  }
}

async function toApiError(response: Response, path: string): Promise<NetBpmApiError> {
  let body = "";

  try {
    body = await response.text();
  } catch {
    body = "";
  }

  let problem: PlatformProblem | undefined;

  if (body.trim().length > 0) {
    try {
      problem = JSON.parse(body) as PlatformProblem;
    } catch {
      // 网关 502 的 HTML、反向代理的纯文本都走这里：问题体解析不出就保留原文
      problem = undefined;
    }
  }

  return new NetBpmApiError(response.status, problem, truncate(body), path);
}

""";
}

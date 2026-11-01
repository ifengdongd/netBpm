/**
 * NetBpm 平台业务面（`/workflow/v1`）的 TypeScript 客户端。
 *
 * 资源分组与 .NET SDK 一一对应（instances / processes / tasks），方法名相同，便于跨栈对照。
 *
 * 本文件由 `tools/NetBpm.SdkGen` 从 `contracts/openapi/workflow-v1.json`（OpenAPI 3.1.1）生成，**请勿手改**：改了会在 SdkGenerationTests 里红。
 * 重新生成：`dotnet run --project tools/NetBpm.SdkGen`
 */

import type {
  AttachmentRef,
  ClaimTaskRequest,
  CursorPageOfInstanceSummary,
  CursorPageOfProcessDescriptor,
  CursorPageOfTaskSummary,
  FormSnapshot,
  HistoryView,
  InstanceParameterView,
  InstanceSummary,
  InstanceView,
  ParameterSchema,
  ParameterSnapshot,
  PlatformProblem,
  ProcessDefinitionInfo,
  ProcessDescriptor,
  ProcessFormSchema,
  ProcessVersionInfo,
  StartInstanceRequest,
  StartInstanceResult,
  SubmitTaskRequest,
  SubmitTaskResult,
  TaskSummary,
  TaskView,
  TerminateInstanceRequest,
  TransferTaskRequest,
} from "./models.js";

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
/** `GET /workflow/v1/instances` 的查询参数。 */
export interface InstanceListQuery extends QueryShape {
  processKey?: string;
  businessKey?: string;
  status?: string;
  startedBy?: string;
  involvedUser?: string;
  startedFrom?: string | Date;
  startedTo?: string | Date;
  cursor?: string;
  limit?: number;
  direction?: string;
}

/** `GET /workflow/v1/instances/{instanceId}/history` 的查询参数。 */
export interface InstanceGetHistoryQuery extends QueryShape {
  afterSequence?: number;
  limit?: number;
}

/** `GET /workflow/v1/instances/{instanceId}/parameters/history` 的查询参数。 */
export interface InstanceGetParameterHistoryQuery extends QueryShape {
  limit?: number;
}

// ────────────────────── instances ──────────────────────
export class InstancesClient {
  /** @internal 由 {@link NetBpmClient} 构造；直接 new 会绕开共享的 transport。 */
  constructor(private readonly _t: HttpTransport) {}

  /** `GET /workflow/v1/instances` */
  list(query: InstanceListQuery = {}): Promise<CursorPageOfInstanceSummary> {
    return this._t.get<CursorPageOfInstanceSummary>("workflow/v1/instances", query);
  }

  /** 逐页翻完（自动跟 `nextCursor`），适合「全量同步」这类场景。 */
  async *iterate(query: InstanceListQuery = {}): AsyncGenerator<InstanceSummary, void, undefined> {
    let cursor = query.cursor;

    for (;;) {
      const page = await this.list({ ...query, cursor });

      for (const item of page.items) {
        yield item;
      }

      if (!page.hasMore || !page.nextCursor) {
        return;
      }

      cursor = page.nextCursor;
    }
  }

  /** `POST /workflow/v1/instances` */
  start(
    body: StartInstanceRequest,
    options?: RequestOptions,
  ): Promise<StartInstanceResult> {
    return this._t.post<StartInstanceResult>("workflow/v1/instances", body, options);
  }

  /** `GET /workflow/v1/instances/{instanceId}` */
  get(instanceId: string): Promise<InstanceView> {
    return this._t.get<InstanceView>(
      fill("workflow/v1/instances/{instanceId}", { instanceId }),
    );
  }

  /** `GET /workflow/v1/instances/{instanceId}/diagram.svg` */
  getDiagram(instanceId: string): Promise<string> {
    return this._t.getText(
      fill("workflow/v1/instances/{instanceId}/diagram.svg", { instanceId }),
    );
  }

  /** `GET /workflow/v1/instances/{instanceId}/history` */
  getHistory(
    instanceId: string,
    query: InstanceGetHistoryQuery = {},
  ): Promise<HistoryView> {
    return this._t.get<HistoryView>(
      fill("workflow/v1/instances/{instanceId}/history", { instanceId }),
      query,
    );
  }

  /** `GET /workflow/v1/instances/{instanceId}/parameters` */
  getParameters(instanceId: string): Promise<InstanceParameterView> {
    return this._t.get<InstanceParameterView>(
      fill("workflow/v1/instances/{instanceId}/parameters", { instanceId }),
    );
  }

  /** `GET /workflow/v1/instances/{instanceId}/parameters/history` */
  getParameterHistory(
    instanceId: string,
    query: InstanceGetParameterHistoryQuery = {},
  ): Promise<ParameterSnapshot[]> {
    return this._t.get<ParameterSnapshot[]>(
      fill("workflow/v1/instances/{instanceId}/parameters/history", { instanceId }),
      query,
    );
  }

  /** `POST /workflow/v1/instances/{instanceId}/terminate` */
  terminate(
    instanceId: string,
    body: TerminateInstanceRequest,
    options?: RequestOptions,
  ): Promise<InstanceView> {
    return this._t.post<InstanceView>(
      fill("workflow/v1/instances/{instanceId}/terminate", { instanceId }),
      body,
      options,
    );
  }
}

/** `GET /workflow/v1/processes` 的查询参数。 */
export interface ProcessListQuery extends QueryShape {
  keyword?: string;
  category?: string;
  status?: string;
  cursor?: string;
  limit?: number;
}

/** `GET /workflow/v1/processes/{processKey}/definition` 的查询参数。 */
export interface ProcessGetDefinitionQuery extends QueryShape {
  includeXml?: boolean;
}

/** `GET /workflow/v1/processes/{processKey}/diagram.svg` 的查询参数。 */
export interface ProcessGetDiagramQuery extends QueryShape {
  version?: number;
}

/** `GET /workflow/v1/processes/{processKey}/form/schema` 的查询参数。 */
export interface ProcessGetFormSchemaQuery extends QueryShape {
  version?: number;
}

// ────────────────────── processes ──────────────────────
export class ProcessesClient {
  /** @internal 由 {@link NetBpmClient} 构造；直接 new 会绕开共享的 transport。 */
  constructor(private readonly _t: HttpTransport) {}

  /** `GET /workflow/v1/processes` */
  list(query: ProcessListQuery = {}): Promise<CursorPageOfProcessDescriptor> {
    return this._t.get<CursorPageOfProcessDescriptor>("workflow/v1/processes", query);
  }

  /** 逐页翻完（自动跟 `nextCursor`），适合「全量同步」这类场景。 */
  async *iterate(query: ProcessListQuery = {}): AsyncGenerator<ProcessDescriptor, void, undefined> {
    let cursor = query.cursor;

    for (;;) {
      const page = await this.list({ ...query, cursor });

      for (const item of page.items) {
        yield item;
      }

      if (!page.hasMore || !page.nextCursor) {
        return;
      }

      cursor = page.nextCursor;
    }
  }

  /** `GET /workflow/v1/processes/{processKey}` */
  get(processKey: string): Promise<ProcessDescriptor> {
    return this._t.get<ProcessDescriptor>(
      fill("workflow/v1/processes/{processKey}", { processKey }),
    );
  }

  /** `GET /workflow/v1/processes/{processKey}/definition` */
  getDefinition(
    processKey: string,
    query: ProcessGetDefinitionQuery = {},
  ): Promise<ProcessDefinitionInfo> {
    return this._t.get<ProcessDefinitionInfo>(
      fill("workflow/v1/processes/{processKey}/definition", { processKey }),
      query,
    );
  }

  /** `GET /workflow/v1/processes/{processKey}/diagram.svg` */
  getDiagram(
    processKey: string,
    query: ProcessGetDiagramQuery = {},
  ): Promise<string> {
    return this._t.getText(
      fill("workflow/v1/processes/{processKey}/diagram.svg", { processKey }),
      query,
    );
  }

  /** `GET /workflow/v1/processes/{processKey}/form/schema` */
  getFormSchema(
    processKey: string,
    query: ProcessGetFormSchemaQuery = {},
  ): Promise<ProcessFormSchema> {
    return this._t.get<ProcessFormSchema>(
      fill("workflow/v1/processes/{processKey}/form/schema", { processKey }),
      query,
    );
  }

  /** `GET /workflow/v1/processes/{processKey}/parameters/schema` */
  getParameterSchema(processKey: string): Promise<ParameterSchema> {
    return this._t.get<ParameterSchema>(
      fill("workflow/v1/processes/{processKey}/parameters/schema", { processKey }),
    );
  }

  /** `GET /workflow/v1/processes/{processKey}/versions` */
  listVersions(processKey: string): Promise<ProcessVersionInfo[]> {
    return this._t.get<ProcessVersionInfo[]>(
      fill("workflow/v1/processes/{processKey}/versions", { processKey }),
    );
  }
}

/** `GET /workflow/v1/tasks` 的查询参数。 */
export interface TaskListQuery extends QueryShape {
  scope?: string;
  processKey?: string;
  activityId?: string;
  keyword?: string;
  overdueOnly?: boolean;
  cursor?: string;
  limit?: number;
}

// ────────────────────── tasks ──────────────────────
export class TasksClient {
  /** @internal 由 {@link NetBpmClient} 构造；直接 new 会绕开共享的 transport。 */
  constructor(private readonly _t: HttpTransport) {}

  /** `GET /workflow/v1/tasks` */
  list(query: TaskListQuery = {}): Promise<CursorPageOfTaskSummary> {
    return this._t.get<CursorPageOfTaskSummary>("workflow/v1/tasks", query);
  }

  /** 逐页翻完（自动跟 `nextCursor`），适合「全量同步」这类场景。 */
  async *iterate(query: TaskListQuery = {}): AsyncGenerator<TaskSummary, void, undefined> {
    let cursor = query.cursor;

    for (;;) {
      const page = await this.list({ ...query, cursor });

      for (const item of page.items) {
        yield item;
      }

      if (!page.hasMore || !page.nextCursor) {
        return;
      }

      cursor = page.nextCursor;
    }
  }

  /** `GET /workflow/v1/tasks/{taskId}` */
  get(taskId: string): Promise<TaskView> {
    return this._t.get<TaskView>(
      fill("workflow/v1/tasks/{taskId}", { taskId }),
    );
  }

  /** `GET /workflow/v1/tasks/{taskId}/attachments` */
  getAttachments(taskId: string): Promise<AttachmentRef[]> {
    return this._t.get<AttachmentRef[]>(
      fill("workflow/v1/tasks/{taskId}/attachments", { taskId }),
    );
  }

  /** `POST /workflow/v1/tasks/{taskId}/claim` */
  claim(
    taskId: string,
    body?: ClaimTaskRequest | null,
    options?: RequestOptions,
  ): Promise<TaskView> {
    return this._t.post<TaskView>(
      fill("workflow/v1/tasks/{taskId}/claim", { taskId }),
      body,
      options,
    );
  }

  /** `GET /workflow/v1/tasks/{taskId}/form` */
  getForm(taskId: string): Promise<FormSnapshot> {
    return this._t.get<FormSnapshot>(
      fill("workflow/v1/tasks/{taskId}/form", { taskId }),
    );
  }

  /** `POST /workflow/v1/tasks/{taskId}/submit` */
  submit(
    taskId: string,
    body: SubmitTaskRequest,
    options?: RequestOptions,
  ): Promise<SubmitTaskResult> {
    return this._t.post<SubmitTaskResult>(
      fill("workflow/v1/tasks/{taskId}/submit", { taskId }),
      body,
      options,
    );
  }

  /** `POST /workflow/v1/tasks/{taskId}/transfer` */
  transfer(
    taskId: string,
    body: TransferTaskRequest,
    options?: RequestOptions,
  ): Promise<TaskView> {
    return this._t.post<TaskView>(
      fill("workflow/v1/tasks/{taskId}/transfer", { taskId }),
      body,
      options,
    );
  }

  /** `POST /workflow/v1/tasks/{taskId}/unclaim` */
  unclaim(
    taskId: string,
    options?: RequestOptions,
  ): Promise<TaskView> {
    return this._t.post<TaskView>(
      fill("workflow/v1/tasks/{taskId}/unclaim", { taskId }),
      options,
    );
  }
}

/** SDK 入口：一个实例持有共享的 transport 与三个资源客户端。 */
export class NetBpmClient {
  readonly transport: HttpTransport;
  readonly instances: InstancesClient;
  readonly processes: ProcessesClient;
  readonly tasks: TasksClient;

  constructor(options: NetBpmClientOptions = {}) {
    this.transport = new HttpTransport(options);
    this.instances = new InstancesClient(this.transport);
    this.processes = new ProcessesClient(this.transport);
    this.tasks = new TasksClient(this.transport);
  }
}

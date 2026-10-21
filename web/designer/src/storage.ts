/**
 * 流程模型列表的持久化（服务端优先，localStorage 兜底）。
 *
 * 管理端后端补齐了「流程图列表」的存取接口（见 src/NetBpm.Admin 的 /api/admin/models），
 * 因此设计器改为优先走服务端：
 *   GET    /api/admin/models          模型列表（摘要，不含 XML）
 *   GET    /api/admin/models/{id}     模型详情（含 BPMN XML）
 *   PUT    /api/admin/models/{id}     保存（upsert）
 *   DELETE /api/admin/models/{id}     删除
 *
 * 独立 `npm run dev`（Vite 代理到 NetBpm.Rest，只有 deploy / 回显接口、没有 /api/admin/models）
 * 时，首次探测到 404 即自动回退到 localStorage，保证原「草稿 + 部署 + 回显」工作流不受影响。
 *
 * parseProcessMeta / upsertDiagram 是纯函数，可在 Node 单测里直接验证（见 test/designer.test.ts）。
 */

import {
  credentialHeaders,
  describeHttpFailure,
  MODEL_READ_SCOPE,
  MODEL_WRITE_SCOPE,
} from './auth';

export interface DiagramRecord {
  /** 模型唯一 id（服务端 bpmn_model.id；localStorage 模式 = 旧 modelId，用于 ?modelId= 直接打开）。 */
  modelId: string;
  /** BPMN process 的 id（服务端 processKey），列表内的显示键。 */
  processId: string;
  /** 流程名称（取 process 的 name，没有则退回 id）。 */
  name: string;
  /** 完整 BPMN XML；列表摘要场景可能为空，编辑 / 部署前需按 modelId 再取详情。 */
  xml: string;
  /** 最近保存时间（ISO 字符串）。 */
  updatedAt: string;
  /**
   * 已部署版本号（localStorage 模式）；服务端模式恒为 0，部署版本见「流程部署列表」。
   */
  version: number;
  /**
   * 服务端模型的最新版本号（乐观并发用：保存时作为 `If-Match`）。
   * localStorage 模式没有这个概念，缺省 undefined；0 表示还没有任何快照。
   */
  revision?: number;
}

const STORAGE_KEY = 'netbpm.designer.diagrams.v1';

type StorageLike = Pick<Storage, 'getItem' | 'setItem'>;

// ─────────────────────────── 服务端 DTO（最小契约）───────────────────────────

interface BpmnModelSummary {
  id: string;
  name: string;
  processKey: string;
  modifyTime: string;
  /** 最新版本号（0 = 尚无快照），保存时作为 If-Match 的期望值。 */
  revision?: number;
}

interface BpmnModelDetail {
  id: string;
  name: string;
  processKey: string;
  bpmnXml: string;
  createTime: string;
  modifyTime: string | null;
  /** 最新版本号（0 = 尚无快照），保存时作为 If-Match 的期望值。 */
  revision?: number;
}

interface SaveBpmnModelRequest {
  name: string;
  processKey: string;
  bpmnXml: string;
}

// ─────────────────────────── 模式探测 ───────────────────────────

/**
 * 后端是否提供 /api/admin/models。探测一次后缓存：
 * 独立开发（NetBpm.Rest 宿主无该路由）返回 404 → localStorage 模式。
 */
let backendMode: 'server' | 'local' | null = null;

async function resolveMode(): Promise<'server' | 'local'> {
  if (backendMode !== null) {
    return backendMode;
  }

  try {
    const response = await fetch('/api/admin/models', { method: 'GET', headers: credentialHeaders() });

    // 403 = 后端存在但当前凭据没有管理权限：必须留在 server 模式，
    // 让列表/保存把 403 明确报出来；静默回退到 localStorage 会让人误以为「数据丢了」。
    backendMode = response.ok || response.status === 401 || response.status === 403 ? 'server' : 'local';
  } catch {
    backendMode = 'local';
  }

  return backendMode;
}

// ─────────────────────────── 纯函数（供 Node 单测）───────────────────────────

/**
 * 从 BPMN XML 里解析 process 的 id 与 name。
 *
 * 用正则而不是 DOMParser：单测跑在 Node 里没有 DOM；且这里只需要最外层
 * <process …> 标签上的两个属性，正则足够（XML 由本设计器/后端生成，格式可控）。
 */
export function parseProcessMeta(xml: string): { processId: string; name: string } {
  const tag = /<(\w+:)?process\b[^>]*>/i.exec(xml)?.[0] ?? '';

  const attr = (name: string): string | undefined => {
    const match = new RegExp(`\\b${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`).exec(tag);

    return match ? (match[1] ?? match[2]) : undefined;
  };

  const processId = attr('id') ?? '';
  const name = attr('name');

  return {
    processId,
    name: name !== undefined && name.length > 0 ? name : processId,
  };
}

/** 按 processId 覆盖更新；不存在则插入到最前（列表按最近编辑排序）。不改变传入数组。 */
export function upsertDiagram(records: DiagramRecord[], record: DiagramRecord): DiagramRecord[] {
  const next = records.filter((existing) => existing.processId !== record.processId);

  next.unshift(record);

  return next;
}

// ─────────────────────────── 工具 ───────────────────────────

/** 生成设计器侧的 modelId（UUID，兜底用时间戳+随机数）。 */
function newModelId(): string {
  const cryptoObj = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;

  if (cryptoObj?.randomUUID !== undefined) {
    return cryptoObj.randomUUID();
  }

  return `m_${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
}

function defaultStorage(): StorageLike | undefined {
  return typeof localStorage === 'undefined' ? undefined : localStorage;
}

function summaryToRecord(summary: BpmnModelSummary): DiagramRecord {
  return {
    modelId: summary.id,
    processId: summary.processKey,
    name: summary.name,
    xml: '',
    updatedAt: summary.modifyTime,
    version: 0,
    revision: summary.revision ?? 0,
  };
}

function detailToRecord(detail: BpmnModelDetail): DiagramRecord {
  return {
    modelId: detail.id,
    processId: detail.processKey,
    name: detail.name,
    xml: detail.bpmnXml,
    updatedAt: detail.modifyTime ?? detail.createTime,
    version: 0,
    revision: detail.revision ?? 0,
  };
}

/** 把非 2xx 且非 404 的响应归因成可读错误（403 补上所需管理 scope）。 */
function ensureOk(response: Response, action: string, requiredScope: string = MODEL_READ_SCOPE): void {
  if (response.ok || response.status === 404) {
    return;
  }

  throw new Error(
    `${action}失败：${describeHttpFailure(
      response.status,
      `HTTP ${response.status} ${response.statusText}`,
      requiredScope,
    )}`,
  );
}

/**
 * 保存失败的明细：后端在 XML 解析失败时返回 `{ message }`，
 * 校验失败（草稿不会发生，但保留兼容）可能返回 `{ errors: [...] }`。
 * 只取前几条，避免把整页 JSON 塞进状态栏。
 */
async function describeSaveFailure(response: Response): Promise<string> {
  const text = await response.text();

  try {
    const body = JSON.parse(text) as {
      message?: string;
      errors?: { message?: string; elementId?: string }[];
    };

    // 409：If-Match 与服务端版本不一致（别人先改过），消息里已带服务端版本与处理建议
    if (response.status === 409) {
      return `保存冲突：${body.message ?? '模型已被其他人修改，请刷新后重试。'}`;
    }

    const details = (body.errors ?? [])
      .slice(0, 5)
      .map((issue) => (issue.elementId ? `${issue.elementId}: ${issue.message ?? ''}` : issue.message ?? ''))
      .filter((detail) => detail.length > 0);

    if (details.length > 0) {
      return `保存被拒绝：\n${details.join('\n')}`;
    }

    if (body.message !== undefined && body.message.length > 0) {
      return `保存被拒绝：${describeHttpFailure(response.status, body.message, MODEL_WRITE_SCOPE)}`;
    }
  } catch {
    // 非 JSON 响应（代理错误页等），退回到状态码
  }

  return `保存失败：${describeHttpFailure(
    response.status,
    `HTTP ${response.status} ${response.statusText}`,
    MODEL_WRITE_SCOPE,
  )}`;
}

/**
 * 读取保存响应里的新版本号（`SaveBpmnModelResponse.revision`），
 * 后续保存用它做 `If-Match`；响应体异常时返回 undefined（退化为不做并发校验）。
 */
async function readSavedRevision(response: Response): Promise<number | undefined> {
  try {
    const body = (await response.json()) as { revision?: number };

    return typeof body.revision === 'number' && body.revision > 0 ? body.revision : undefined;
  } catch {
    return undefined;
  }
}

// ─────────────────────────── 对外 API（异步）───────────────────────────

/** 模型列表（不含 XML 正文，按最近修改倒序）。 */
export async function readDiagrams(): Promise<DiagramRecord[]> {
  if ((await resolveMode()) === 'server') {
    const response = await fetch('/api/admin/models', { headers: credentialHeaders() });

    ensureOk(response, '加载模型列表');

    const summaries = (await response.json()) as BpmnModelSummary[];

    return summaries.map(summaryToRecord);
  }

  return readLocal();
}

/** 按 modelId 取模型（含 XML）；不存在返回 undefined。 */
export async function findByModelId(modelId: string): Promise<DiagramRecord | undefined> {
  if ((await resolveMode()) === 'server') {
    const response = await fetch(`/api/admin/models/${encodeURIComponent(modelId)}`, {
      headers: credentialHeaders(),
    });

    if (response.status === 404) {
      return undefined;
    }

    ensureOk(response, '加载模型');

    return detailToRecord((await response.json()) as BpmnModelDetail);
  }

  return readLocal().find((record) => record.modelId === modelId);
}

/**
 * 保存（新增或更新）一条流程模型并返回保存后的记录。
 * 服务端模式：`modelId` 缺省生成新 id（新建），否则 PATCH 到该 id（更新）。
 *
 * `revision` 是打开模型时拿到的服务端版本号：带上后以 `If-Match` 提交，
 * 期间被别人改过时后端返回 409，避免「后保存的人静默覆盖前者」。
 */
export async function saveDiagramXml(
  xml: string,
  modelId?: string,
  revision?: number,
): Promise<DiagramRecord> {
  const meta = parseProcessMeta(xml);
  const processId = meta.processId.length > 0 ? meta.processId : `process_${Date.now().toString(36)}`;
  const name = meta.name.length > 0 ? meta.name : '未命名流程';

  if ((await resolveMode()) === 'server') {
    const id = modelId ?? newModelId();
    const payload: SaveBpmnModelRequest = { name, processKey: processId, bpmnXml: xml };

    // 后端只认 If-Match；没有已知版本（新建 / 首次保存）时不带，保持旧行为。
    const ifMatch: Record<string, string> =
      revision !== undefined && revision > 0 ? { 'If-Match': `"${revision}"` } : {};

    const send = (method: 'PATCH' | 'PUT') =>
      fetch(`/api/admin/models/${encodeURIComponent(id)}`, {
        method,
        headers: { 'Content-Type': 'application/json', ...ifMatch, ...credentialHeaders() },
        body: JSON.stringify(payload),
      });

    // 已有模型用 PATCH：设计器拿不到描述 / SVG，PUT 的全量语义会把它们清空。
    // 新建（前端生成 id）仍走 PUT upsert；模型已被删除或旧服务端不认识 PATCH 时回退 PUT。
    let response = modelId === undefined ? await send('PUT') : await send('PATCH');

    if (modelId !== undefined && (response.status === 404 || response.status === 405)) {
      response = await send('PUT');
    }

    if (!response.ok) {
      throw new Error(await describeSaveFailure(response));
    }

    return {
      modelId: id,
      processId,
      name,
      xml,
      updatedAt: new Date().toISOString(),
      version: 0,
      revision: (await readSavedRevision(response)) ?? revision,
    };
  }

  return saveLocal(xml, modelId);
}

/** 按 modelId 删除模型。 */
export async function deleteDiagram(modelId: string): Promise<void> {
  if ((await resolveMode()) === 'server') {
    const response = await fetch(`/api/admin/models/${encodeURIComponent(modelId)}`, {
      method: 'DELETE',
      headers: credentialHeaders(),
    });

    ensureOk(response, '删除', MODEL_WRITE_SCOPE);

    return;
  }

  writeLocal(readLocal().filter((record) => record.modelId !== modelId));
}

/** 部署成功后用后端返回的版本号覆盖更新草稿的 version（服务端模式为 no-op）。 */
export async function setDiagramVersion(modelId: string, version: number): Promise<void> {
  if ((await resolveMode()) === 'local') {
    const records = readLocal();
    const index = records.findIndex((record) => record.modelId === modelId);

    if (index >= 0) {
      records[index] = { ...records[index], version };
      writeLocal(records);
    }
  }
}

// ─────────────────────────── localStorage 兜底 ───────────────────────────

function readLocal(): DiagramRecord[] {
  const storage = defaultStorage();

  if (storage === undefined) {
    return [];
  }

  try {
    const raw = storage.getItem(STORAGE_KEY);

    return raw === null ? [] : (JSON.parse(raw) as DiagramRecord[]);
  } catch {
    // 数据损坏时按空列表处理，不让设计器打不开
    return [];
  }
}

function writeLocal(records: DiagramRecord[]): void {
  defaultStorage()?.setItem(STORAGE_KEY, JSON.stringify(records));
}

/** localStorage 模式保存：`modelId` 缺省按 processId 覆盖更新（保留原 modelId）。 */
function saveLocal(xml: string, modelId?: string): DiagramRecord {
  const meta = parseProcessMeta(xml);
  const processId = meta.processId.length > 0 ? meta.processId : `process_${Date.now().toString(36)}`;
  const name = meta.name.length > 0 ? meta.name : '未命名流程';

  const records = readLocal();
  const existing = modelId !== undefined
    ? records.find((record) => record.modelId === modelId)
    : records.find((record) => record.processId === processId);

  const record: DiagramRecord = {
    modelId: existing?.modelId ?? modelId ?? newModelId(),
    processId,
    name,
    xml,
    updatedAt: new Date().toISOString(),
    version: existing?.version ?? 0,
  };

  writeLocal(upsertDiagram(records, record));

  return record;
}

/**
 * 流程图列表的本地持久化（流程记录）。
 *
 * 后端契约（§6.3）只有部署与回显两个接口，没有"流程图列表"的存取接口，
 * 因此草稿列表先存 localStorage；将来后端补上列表接口后，
 * 只需替换本文件里的 readDiagrams / writeDiagrams 两个函数。
 *
 * parseProcessMeta / upsertDiagram 是纯函数，可在 Node 单测里直接验证（见 test/designer.test.ts）。
 */

export interface DiagramRecord {
  /** 设计器侧唯一 id（UUID），对应 Activiti 的 modelId；用于 ?modelId= 直接打开。 */
  modelId: string;
  /** BPMN process 的 id，列表内的唯一键（同 id 保存即覆盖更新）。 */
  processId: string;
  /** 流程名称（取 process 的 name，没有则退回 id）。 */
  name: string;
  /** 完整 BPMN XML。 */
  xml: string;
  /** 最近保存时间（ISO 字符串）。 */
  updatedAt: string;
  /**
   * 已部署版本号；每次「部署」成功由后端返回的版本号覆盖更新。
   * 0 表示尚未部署过。
   */
  version: number;
}

const STORAGE_KEY = 'netbpm.designer.diagrams.v1';

type StorageLike = Pick<Storage, 'getItem' | 'setItem'>;

function defaultStorage(): StorageLike | undefined {
  return typeof localStorage === 'undefined' ? undefined : localStorage;
}

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

/** 生成设计器侧的 modelId（UUID，兜底用时间戳+随机数）。 */
function newModelId(): string {
  const cryptoObj = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;

  if (cryptoObj?.randomUUID !== undefined) {
    return cryptoObj.randomUUID();
  }

  return `m_${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
}

/** 保存（新增或更新）一条流程记录并写入存储，返回保存后的记录。 */
export function saveDiagramXml(xml: string, storage: StorageLike | undefined = defaultStorage()): DiagramRecord {
  const meta = parseProcessMeta(xml);
  const processId = meta.processId.length > 0 ? meta.processId : `process_${Date.now().toString(36)}`;
  const name = meta.name.length > 0 ? meta.name : '未命名流程';

  // 同 processId 覆盖更新时，保留原 modelId 与 version，使 ?modelId= 链接在反复保存后依然有效
  const existing = readDiagrams(storage).find((record) => record.processId === processId);

  const record: DiagramRecord = {
    modelId: existing?.modelId ?? newModelId(),
    processId,
    name,
    xml,
    updatedAt: new Date().toISOString(),
    version: existing?.version ?? 0,
  };

  writeDiagrams(upsertDiagram(readDiagrams(storage), record), storage);

  return record;
}

/** 按 modelId 查找草稿（用于 ?modelId= 直接打开）。 */
export function findByModelId(modelId: string, storage: StorageLike | undefined = defaultStorage()): DiagramRecord | undefined {
  return readDiagrams(storage).find((record) => record.modelId === modelId);
}

export function readDiagrams(storage: StorageLike | undefined = defaultStorage()): DiagramRecord[] {
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

export function writeDiagrams(records: DiagramRecord[], storage: StorageLike | undefined = defaultStorage()): void {
  storage?.setItem(STORAGE_KEY, JSON.stringify(records));
}

export function deleteDiagram(processId: string, storage: StorageLike | undefined = defaultStorage()): void {
  writeDiagrams(
    readDiagrams(storage).filter((record) => record.processId !== processId),
    storage,
  );
}

/** 部署成功后用后端返回的版本号覆盖更新草稿的 version（不改动 XML/名称）。 */
export function setDiagramVersion(
  processId: string,
  version: number,
  storage: StorageLike | undefined = defaultStorage(),
): void {
  const records = readDiagrams(storage);
  const index = records.findIndex((record) => record.processId === processId);

  if (index < 0) {
    return;
  }

  records[index] = { ...records[index], version };
  writeDiagrams(records, storage);
}

/**
 * NetBpm 流程设计器入口（开发计划 §6）。
 *
 * 与后端的契约（§6.3 + 部署列表）：
 *   POST /api/designer/deploy                提交 BPMN XML → 后端解析 + 校验 + 落库
 *   GET  /api/designer/definition/{id}/xml   回显 BPMN XML
 *   GET  /api/designer/definitions           已部署流程定义列表（部署列表数据源）
 * 设计器不直接操作数据库；流程图草稿列表存 localStorage（见 storage.ts）。
 *
 * 页面结构（参考流程管理布局）：左侧「流程管理」菜单（流程模型列表 / 流程部署列表），
 * 右侧主区三个视图：模型列表、部署列表、编辑器（新增/编辑共用）。
 *
 * P8 覆盖的 §6.2 四项：①activiti-moddle.json 描述符 ②调色板裁剪 ③属性面板（白名单）
 * ④即时校验（本地镜像后端规则，部署前拦截）。
 */
import Modeler from 'bpmn-js/lib/Modeler';
import { BpmnPropertiesPanelModule } from 'bpmn-js-properties-panel';
import {
  credentialHeaders,
  describeHttpFailure,
  MODEL_READ_SCOPE,
  MODEL_WRITE_SCOPE,
} from './auth';
import activitiModdle from './activiti-moddle.json';
import NetBpmPaletteProvider from './palette';
import NetBpmPropertiesProvider from './designer/propertiesProvider';
import { renderIssues } from './issues';
import { hasBlockingError, summarize, validateDiagram, type DraftIssue } from './validation';
import {
  deleteDiagram,
  findByModelId,
  parseProcessMeta,
  readDiagrams,
  saveDiagramXml,
  setDiagramVersion,
  type DiagramRecord,
} from './storage';
import type {
  CanvasService,
  EditorActionsService,
  ElementRegistryService,
  SelectionService,
} from './designer/types';

import 'bpmn-js/dist/assets/diagram-js.css';
import 'bpmn-js/dist/assets/bpmn-font/css/bpmn.css';
// ⚠️ 路径是 @bpmn-io/properties-panel：bpmn-js-properties-panel v5 的 dist 里已经**没有** assets/
import '@bpmn-io/properties-panel/assets/properties-panel.css';

/** 新建流程模板：只含一个空开始事件，命名空间必须与 Activiti 一致（§13）。 */
function emptyDiagram(processId: string): string {
  return `<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                  xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                  xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                  xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                  xmlns:activiti="http://activiti.org/bpmn"
                  id="Definitions_1"
                  targetNamespace="http://netbpm/bpmn">
  <bpmn:process id="${processId}" name="新建流程" isExecutable="true">
    <bpmn:startEvent id="StartEvent_1" />
  </bpmn:process>
  <bpmndi:BPMNDiagram id="BPMNDiagram_1">
    <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="${processId}">
      <bpmndi:BPMNShape id="StartEvent_1_di" bpmnElement="StartEvent_1">
        <dc:Bounds x="160" y="120" width="36" height="36" />
      </bpmndi:BPMNShape>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</bpmn:definitions>`;
}

/** 新流程的 processId：时间戳 + 随机后缀，避免列表里同名覆盖。 */
function randomProcessId(): string {
  return `process_${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
}

// ─────────────────────────── 缺失图形信息（BPMN DI）的兜底 ───────────────────────────

const BpmnNs = 'http://www.omg.org/spec/BPMN/20100524/MODEL';
const BpmnDiNs = 'http://www.omg.org/spec/BPMN/20100524/DI';
const DcNs = 'http://www.omg.org/spec/DD/20100524/DC';
const DiNs = 'http://www.omg.org/spec/DD/20100524/DI';

/** 需要画出来的流节点（网关 / 事件 / 任务；子流程等也按默认尺寸兜底）。 */
const FlowNodeTags = new Set([
  'startEvent',
  'endEvent',
  'intermediateThrowEvent',
  'intermediateCatchEvent',
  'boundaryEvent',
  'task',
  'userTask',
  'serviceTask',
  'scriptTask',
  'manualTask',
  'receiveTask',
  'sendTask',
  'businessRuleTask',
  'callActivity',
  'subProcess',
  'exclusiveGateway',
  'inclusiveGateway',
  'parallelGateway',
  'eventBasedGateway',
  'complexGateway',
]);

/** 各类节点的默认尺寸（与 bpmn-js 调色板默认值一致，避免保存后图形突变）。 */
function defaultSize(tag: string): { width: number; height: number } {
  if (tag.endsWith('Event')) {
    return { width: 36, height: 36 };
  }

  if (tag.endsWith('Gateway')) {
    return { width: 50, height: 50 };
  }

  return { width: 100, height: 80 };
}

/**
 * 只有语义、没有图形（<c>bpmndi:BPMNDiagram</c>）的 BPMN —— 例如外部工具生成或测试用的 XML ——
 * bpmn-js 会以 <c>no diagram to display</c> 拒绝载入，编辑器直接打不开。
 * 这里补一份最小 DI（按文档顺序横向排布 + 连线取两端中心），让用户能进编辑器调整并保存，
 * 保存后完整图形信息即写入模型；返回 <c>null</c> 表示无需补（已有 DI）或补不了（解析失败）。
 */
function withGeneratedDiagramInterchange(xml: string): string | null {
  if (/<(\w+:)?BPMNDiagram\b/.test(xml)) {
    return null;
  }

  const document_ = new DOMParser().parseFromString(xml, 'application/xml');

  if (document_.getElementsByTagName('parsererror').length > 0) {
    return null;
  }

  const definitions = document_.documentElement;
  const process =
    document_.getElementsByTagNameNS(BpmnNs, 'process')[0] ??
    document_.getElementsByTagName('process')[0];

  if (!process || !definitions) {
    return null;
  }

  const processId = process.getAttribute('id') ?? '';

  if (processId.length === 0) {
    return null;
  }

  const nodes: Element[] = [];
  const flows: Element[] = [];

  for (const child of Array.from(process.children)) {
    const tag = child.localName ?? child.nodeName;

    if (tag === 'sequenceFlow') {
      flows.push(child);
    } else if (FlowNodeTags.has(tag)) {
      nodes.push(child);
    }
  }

  if (nodes.length === 0) {
    return null;
  }

  // 命名空间声明：缺什么补什么，不影响已有前缀
  const ensureNs = (prefix: string, uri: string) => {
    if (!definitions.hasAttribute(`xmlns:${prefix}`)) {
      definitions.setAttributeNS('http://www.w3.org/2000/xmlns/', `xmlns:${prefix}`, uri);
    }
  };

  ensureNs('bpmndi', BpmnDiNs);
  ensureNs('dc', DcNs);
  ensureNs('di', DiNs);

  const create = (ns: string, name: string) => document_.createElementNS(ns, `bpmndi:${name}`);

  const diagram = create(BpmnDiNs, 'BPMNDiagram');
  diagram.setAttribute('id', 'BPMNDiagram_generated');

  const plane = create(BpmnDiNs, 'BPMNPlane');
  plane.setAttribute('id', 'BPMNPlane_generated');
  plane.setAttribute('bpmnElement', processId);

  const boxes = new Map<string, { x: number; y: number; width: number; height: number }>();
  let x = 160;

  for (const node of nodes) {
    const id = node.getAttribute('id') ?? '';

    if (id.length === 0) {
      continue;
    }

    const size = defaultSize(node.localName ?? node.nodeName);
    const box = { x, y: 120, width: size.width, height: size.height };
    boxes.set(id, box);

    const shape = create(BpmnDiNs, 'BPMNShape');
    shape.setAttribute('id', `${id}_di`);
    shape.setAttribute('bpmnElement', id);

    const bounds = document_.createElementNS(DcNs, 'dc:Bounds');
    bounds.setAttribute('x', String(box.x));
    bounds.setAttribute('y', String(box.y));
    bounds.setAttribute('width', String(box.width));
    bounds.setAttribute('height', String(box.height));
    shape.appendChild(bounds);

    plane.appendChild(shape);

    x += size.width + 90;
  }

  for (const flow of flows) {
    const id = flow.getAttribute('id') ?? '';
    const source = flow.getAttribute('sourceRef') ?? '';
    const target = flow.getAttribute('targetRef') ?? '';

    if (id.length === 0 || !boxes.has(source) || !boxes.has(target)) {
      continue;
    }

    const from = boxes.get(source)!;
    const to = boxes.get(target)!;

    const edge = create(BpmnDiNs, 'BPMNEdge');
    edge.setAttribute('id', `${id}_di`);
    edge.setAttribute('bpmnElement', id);

    const start = document_.createElementNS(DiNs, 'di:waypoint');
    start.setAttribute('x', String(from.x + from.width));
    start.setAttribute('y', String(from.y + from.height / 2));

    const end = document_.createElementNS(DiNs, 'di:waypoint');
    end.setAttribute('x', String(to.x));
    end.setAttribute('y', String(to.y + to.height / 2));

    edge.append(start, end);
    plane.appendChild(edge);
  }

  diagram.appendChild(plane);
  definitions.appendChild(diagram);

  return new XMLSerializer().serializeToString(document_);
}

// ─────────────────────────── DOM 引用 ───────────────────────────

const modelListView = document.querySelector<HTMLElement>('#model-list-view');
const deployListView = document.querySelector<HTMLElement>('#deploy-list-view');
const editorView = document.querySelector<HTMLElement>('#editor-view');
const navModels = document.querySelector<HTMLAnchorElement>('#nav-models');
const navDeployments = document.querySelector<HTMLAnchorElement>('#nav-deployments');
const listStatusElement = document.querySelector<HTMLSpanElement>('#list-status');
const listEmptyElement = document.querySelector<HTMLElement>('#list-empty');
const diagramRowsElement = document.querySelector<HTMLTableSectionElement>('#diagram-rows');
const deployRowsElement = document.querySelector<HTMLTableSectionElement>('#deploy-rows');
const deployStatusElement = document.querySelector<HTMLSpanElement>('#deploy-status');
const deployEmptyElement = document.querySelector<HTMLElement>('#deploy-empty');
const editorTitleElement = document.querySelector<HTMLElement>('#editor-title');
const statusElement = document.querySelector<HTMLSpanElement>('#status');
const issuesElement = document.querySelector<HTMLElement>('#issues');
const fileInputList = document.querySelector<HTMLInputElement>('#file-input-list');
const fileInputEditor = document.querySelector<HTMLInputElement>('#file-input-editor');

/**
 * 嵌入模式（?embed=1）：被管理控制台 iframe 内嵌时隐藏左侧「流程管理」菜单
 * （模型列表 / 部署列表）与编辑器内的「返回列表」——控制台有自己的导航与返回按钮，
 * 两套入口并存会让人误以为又跳回了另一个管理页。
 */
const embedMode = new URLSearchParams(window.location.search).get('embed') === '1';

if (embedMode) {
  document.querySelector('.sidebar')?.remove();
  document.querySelector('#btn-back')?.remove();
}

// ─────────────────────────── 模型器（首次进入编辑器时才创建）───────────────────────────

interface ModelerServices {
  modeler: Modeler;
  registry: ElementRegistryService;
  selection: SelectionService;
  canvas: CanvasService;
  editorActions: EditorActionsService;
}

let services: ModelerServices | null = null;

/** 当前编辑中的模型 id（服务端模式的 upsert 键；localStorage 模式也用它保持链接稳定）。 */
let currentModelId: string | null = null;

/**
 * 当前模型在服务端的最新版本号（打开 / 上次保存时拿到的），保存时作为 `If-Match`。
 * 期间若被别人改过，后端回 409 而不是让后保存的人静默覆盖。
 */
let currentRevision: number | undefined;

/**
 * 画布是否有未保存的修改。
 * 关闭/刷新页面与「返回列表」都会拦截 —— 过去两者都是直接跳走，误点即丢图。
 */
let editorDirty = false;

function ensureModeler(): ModelerServices {
  if (services !== null) {
    return services;
  }

  const modeler = new Modeler({
    container: '#canvas',
    propertiesPanel: {
      parent: '#properties',
    },
    additionalModules: [
      // 属性面板宿主（只装宿主，**不装**官方默认属性组 —— 属性组由 NetBpmPropertiesProvider 全量接管）
      BpmnPropertiesPanelModule,
      {
        // ⚠️ diagram-js 的 DI 是惰性实例化：injector.init() 只实例化各模块 __init__
        // 列表里的服务。没有 __init__，这两个 provider 永远不会被构造（构造函数里的
        // registerProvider 就不会执行），面板会一直空白。
        __init__: ['paletteProvider', 'netBpmPropertiesProvider'],
        // 整体替换默认调色板：只保留子集元素 + 连线工具（§6.2 第 2 项）
        paletteProvider: ['type', NetBpmPaletteProvider],
        // 属性面板（§6.2 第 3 项）
        netBpmPropertiesProvider: ['type', NetBpmPropertiesProvider],
      },
    ],
    moddleExtensions: {
      // 没有这个描述符，bpmn-js 往返会丢掉 activiti:* 扩展属性（§6.2 第 1 项 / 风险 R5）
      activiti: activitiModdle,
    },
  });

  services = {
    modeler,
    registry: modeler.get<ElementRegistryService>('elementRegistry'),
    selection: modeler.get<SelectionService>('selection'),
    canvas: modeler.get<CanvasService>('canvas'),
    editorActions: modeler.get<EditorActionsService>('editorActions'),
  };

  // 导入完成与每次编辑后重跑校验（commandStack.changed 覆盖属性面板的写入）
  modeler.on('import.done', () => {
    editorDirty = false;
    refreshValidation();
  });
  modeler.on('commandStack.changed', () => {
    editorDirty = true;
    refreshValidation();
  });

  return services;
}

// ─────────────────────────── 视图切换 ───────────────────────────

type MenuView = 'models' | 'deployments';

function showView(view: MenuView | 'editor'): void {
  if (modelListView !== null) {
    modelListView.hidden = view !== 'models';
  }

  if (deployListView !== null) {
    deployListView.hidden = view !== 'deployments';
  }

  if (editorView !== null) {
    editorView.hidden = view !== 'editor';
  }

  // 左侧菜单高亮：编辑器从模型列表进入，保持「流程模型列表」高亮
  navModels?.classList.toggle('active', view === 'models' || view === 'editor');
  navDeployments?.classList.toggle('active', view === 'deployments');

  // 画布在 hidden 容器里初始化时尺寸为 0，显示后需要重算
  if (view === 'editor') {
    ensureModeler().canvas.resized();
  }
}

// ─────────────────────────── 列表视图 ───────────────────────────

function setListStatus(message: string, isError = false): void {
  if (listStatusElement !== null) {
    listStatusElement.textContent = message;
    listStatusElement.style.color = isError ? '#c0392b' : '#555';
  }
}

function formatTime(iso: string): string {
  const date = new Date(iso);

  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString('zh-CN', { hour12: false });
}

async function renderList(): Promise<void> {
  if (diagramRowsElement === null) {
    return;
  }

  const records = await readDiagrams();
  diagramRowsElement.replaceChildren();

  if (listEmptyElement !== null) {
    listEmptyElement.hidden = records.length > 0;
  }

  for (const record of records) {
    const row = document.createElement('tr');

    const nameCell = document.createElement('td');
    nameCell.textContent = record.name;

    const idCell = document.createElement('td');
    idCell.textContent = record.processId;
    idCell.className = 'cell-id';

    const timeCell = document.createElement('td');
    timeCell.textContent = formatTime(record.updatedAt);

    const versionCell = document.createElement('td');
    versionCell.textContent = record.version > 0 ? String(record.version) : '—';

    const actionCell = document.createElement('td');

    const editButton = document.createElement('button');
    editButton.type = 'button';
    editButton.textContent = '编辑';
    editButton.addEventListener('click', () => void openModel(record.modelId));

    const exportButton = document.createElement('button');
    exportButton.type = 'button';
    exportButton.textContent = '导出 XML';
    exportButton.addEventListener('click', () => void exportModel(record.modelId, record.processId));

    const deleteButton = document.createElement('button');
    deleteButton.type = 'button';
    deleteButton.textContent = '删除';
    deleteButton.className = 'danger';
    deleteButton.addEventListener('click', () => {
      void (async () => {
        if (window.confirm(`确定删除流程图「${record.name}」（${record.processId}）吗？`)) {
          await deleteDiagram(record.modelId);
          await renderList();
          setListStatus(`已删除：${record.name}`);
        }
      })();
    });

    const deployButton = document.createElement('button');
    deployButton.type = 'button';
    deployButton.textContent = '部署';
    deployButton.addEventListener('click', () => void deployModel(record.modelId, deployButton));

    actionCell.append(editButton, exportButton, deployButton, deleteButton);
    row.append(nameCell, idCell, timeCell, versionCell, actionCell);
    diagramRowsElement.append(row);
  }
}

// ─────────────────────────── 部署列表视图 ───────────────────────────

function setDeployStatus(message: string, isError = false): void {
  if (deployStatusElement !== null) {
    deployStatusElement.textContent = message;
    deployStatusElement.style.color = isError ? '#c0392b' : '#555';
  }
}

/** GET /api/designer/definitions 的行结构（见 Contracts.cs 的 ProcessDefinitionResponse）。 */
interface DeployedDefinition {
  processDefinitionId: string;
  key: string;
  name: string | null;
  version: number;
  deploymentId: string;
  tenantId: string;
  deployTime: string | null;
}

async function renderDeployList(): Promise<void> {
  if (deployRowsElement === null) {
    return;
  }

  setDeployStatus('正在加载…');

  let definitions: DeployedDefinition[] = [];

  try {
    const response = await fetch('/api/designer/definitions', { headers: credentialHeaders() });

    // 先读原始文本：后端异常时可能返回空/非 JSON 响应体，
    // 直接 response.json() 会抛「Unexpected end of JSON input」且丢失状态码信息。
    const text = await response.text();

    if (!response.ok) {
      let detail = response.statusText;
      try {
        const parsed = JSON.parse(text) as { message?: string };
        if (parsed.message) {
          detail = parsed.message;
        }
      } catch {
        // 非 JSON 错误体：保留 statusText
      }
      // 通用 Internal Server Error 通常意味着后端未启动（Vite 代理失败）
      // 或后端异常未返回 JSON 错误体，给出可操作的排查方向。
      const hint =
        detail === 'Internal Server Error' || detail === ''
          ? '（无法从后端获取错误详情：请确认 NetBpm.Rest 已在 http://localhost:5080 启动，且数据库可连接）'
          : '';
      setDeployStatus(
        `加载失败：${describeHttpFailure(response.status, detail, MODEL_READ_SCOPE)}${hint}`,
        true,
      );
      return;
    }

    // 空响应体（如 204）一律当作空列表，避免 JSON 解析异常
    definitions = text.length > 0 ? (JSON.parse(text) as DeployedDefinition[]) : [];
  } catch (error) {
    setDeployStatus(`加载失败：${(error as Error).message}`, true);
    return;
  }

  deployRowsElement.replaceChildren();

  if (deployEmptyElement !== null) {
    deployEmptyElement.hidden = definitions.length > 0;
  }

  for (const definition of definitions) {
    const row = document.createElement('tr');

    const nameCell = document.createElement('td');
    nameCell.textContent = definition.name ?? definition.key;

    const keyCell = document.createElement('td');
    keyCell.textContent = definition.key;
    keyCell.className = 'cell-id';

    const timeCell = document.createElement('td');
    timeCell.textContent = definition.deployTime !== null ? formatTime(definition.deployTime) : '—';

    const versionCell = document.createElement('td');
    versionCell.textContent = String(definition.version);

    row.append(nameCell, keyCell, timeCell, versionCell);
    deployRowsElement.append(row);
  }

  setDeployStatus(`共 ${definitions.length} 条部署记录`);
}

// ─────────────────────────── 编辑器视图 ───────────────────────────

function setStatus(message: string, isError = false): void {
  if (statusElement !== null) {
    statusElement.textContent = message;
    statusElement.style.color = isError ? '#c0392b' : '#555';
  }
}

/** 重跑即时校验并刷新问题清单（导入完成、每次编辑后都会调用）。 */
function refreshValidation(): DraftIssue[] {
  if (services === null) {
    return [];
  }

  const issues = validateDiagram(services.registry);

  // 把问题直接标到画布上：长流程只看底部清单很难定位，标红/标黄后才能"一眼看到错在哪"
  applyIssueMarkers(services, issues);

  if (issuesElement !== null) {
    renderIssues(issuesElement, issues, focusElement);
  }

  setStatus(summarize(issues), hasBlockingError(issues));

  return issues;
}

/** 清掉上一次的校验标记，再按「错误优先」给每个元素挂一个标记。 */
function applyIssueMarkers(current: ModelerServices, issues: DraftIssue[]): void {
  for (const element of current.registry.getAll()) {
    current.canvas.removeMarker(element.id, 'netbpm-error');
    current.canvas.removeMarker(element.id, 'netbpm-warning');
  }

  const markerByElement = new Map<string, string>();

  for (const issue of issues) {
    if (issue.elementId === undefined || current.registry.get(issue.elementId) === undefined) {
      continue;
    }

    if (issue.severity === 'error') {
      markerByElement.set(issue.elementId, 'netbpm-error');
    } else if (!markerByElement.has(issue.elementId)) {
      markerByElement.set(issue.elementId, 'netbpm-warning');
    }
  }

  for (const [elementId, marker] of markerByElement) {
    current.canvas.addMarker(elementId, marker);
  }
}

function focusElement(elementId: string): void {
  if (services === null) {
    return;
  }

  const element = services.registry.get(elementId);

  if (!element) {
    return;
  }

  services.selection.select(element);
  services.canvas.scrollToElement(element);
}

/**
 * 载入流程图。
 * <para>没有图形信息（<c>bpmndi:BPMNDiagram</c>）的 XML 会被 bpmn-js 拒载（no diagram to display）——
 * 先补一份最小 DI 再试，让「只有语义」的模型也能进编辑器调整并保存。</para>
 */
async function loadDiagram(xml: string, source: string): Promise<void> {
  try {
    await importDiagram(xml, source);
  } catch (error) {
    const patched = withGeneratedDiagramInterchange(xml);

    if (patched === null) {
      console.error('[netbpm] 载入失败堆栈：', error);
      setStatus(`载入失败：${describeImportFailure(error)}`, true);
      return;
    }

    try {
      await importDiagram(patched, source, '已自动生成布局（原 XML 缺少图形信息），保存后写入完整图形信息。');
    } catch (retryError) {
      console.error('[netbpm] 载入失败堆栈：', retryError);
      setStatus(`载入失败：${describeImportFailure(retryError)}`, true);
    }
  }
}

/** 真正导入并刷新标题 / 校验清单。 */
async function importDiagram(xml: string, source: string, notice?: string): Promise<void> {
  await ensureModeler().modeler.importXML(xml);

  const meta = parseProcessMeta(xml);

  if (editorTitleElement !== null) {
    editorTitleElement.textContent = meta.name.length > 0 ? meta.name : '未命名流程';
  }

  setStatus(notice ?? `已载入：${source}`);
  refreshValidation();
}

/** 把 bpmn-js 的英文导入错误翻译成可操作的中文提示。 */
function describeImportFailure(error: unknown): string {
  const message = (error as Error)?.message ?? String(error);

  if (/no diagram to display/i.test(message)) {
    return '该 XML 没有图形信息（bpmndi:BPMNDiagram），无法渲染；请改用含图形的流程图，或在编辑器里手动布局后保存。';
  }

  if (/no process/i.test(message)) {
    return '该 XML 里找不到 process 节点，不是有效的 BPMN 流程图。';
  }

  return message;
}

async function exportXml(): Promise<string> {
  const { modeler } = ensureModeler();
  const { xml } = await modeler.saveXML({ format: true });

  return xml ?? '';
}

function downloadXml(xml: string, filename: string): void {
  const blob = new Blob([xml], { type: 'application/xml' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');

  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

/** 把当前打开的流程图同步到 URL（?modelId=），使刷新/重新加载仍能回到同一流程图。 */
function syncModelIdToUrl(modelId: string): void {
  const url = new URL(window.location.href);
  url.searchParams.set('modelId', modelId);
  url.searchParams.delete('processDefinitionId');
  url.searchParams.delete('action');
  window.history.replaceState(null, '', url);
}

/** 清空 URL 里的 modelId（返回列表时调用）。 */
function clearModelIdUrl(): void {
  const url = new URL(window.location.href);
  url.search = '';
  window.history.replaceState(null, '', url);
}

/** 按 modelId 打开模型进入编辑器（先取详情，兼容列表摘要无 XML 的情况）。 */
async function openModel(modelId: string): Promise<void> {
  const record = await findByModelId(modelId);

  if (record === undefined) {
    setListStatus(`模型已失效：${modelId}`, true);
    await renderList();
    return;
  }

  openRecord(record);
}

/** 按 modelId 导出模型 XML（列表摘要无 XML，需先取详情）。 */
async function exportModel(modelId: string, fallbackName: string): Promise<void> {
  const record = await findByModelId(modelId);

  if (record === undefined) {
    setListStatus(`模型已失效：${modelId}`, true);
    return;
  }

  downloadXml(record.xml, `${record.processId.length > 0 ? record.processId : fallbackName}.bpmn20.xml`);
}

/** 列表操作列的「部署」：先取模型详情（XML），再走本地校验 + 提交后端。 */
async function deployModel(modelId: string, button: HTMLButtonElement): Promise<void> {
  const record = await findByModelId(modelId);

  if (record === undefined) {
    setListStatus(`模型已失效：${modelId}`, true);
    return;
  }

  await deployRecord(record, button);
}

/** 新建流程图：先落一份模型（生成 modelId），再进入编辑器。 */
async function openNewEditor(): Promise<void> {
  const record = await saveDiagramXml(emptyDiagram(randomProcessId()));
  openRecord(record);
}

/** 打开已有模型进入编辑器，并把 modelId 写进 URL。 */
function openRecord(record: DiagramRecord): void {
  currentModelId = record.modelId;
  currentRevision = record.revision;
  syncModelIdToUrl(record.modelId);
  showView('editor');
  void loadDiagram(record.xml, record.name);
}

async function save(): Promise<void> {
  try {
    const xml = await exportXml();
    const meta = parseProcessMeta(xml);

    if (meta.processId.length === 0) {
      setStatus('保存失败：XML 里找不到 process 节点。', true);
      return;
    }

    const record = await saveDiagramXml(xml, currentModelId ?? undefined, currentRevision);

    currentModelId = record.modelId;
    currentRevision = record.revision;

    if (editorTitleElement !== null) {
      editorTitleElement.textContent = record.name;
    }

    editorDirty = false;
    setStatus(`已保存：${record.name}（${record.processId}）`);
  } catch (error) {
    setStatus(`保存失败：${(error as Error).message}`, true);
  }
}

/** 部署端点响应体（见 src/NetBpm.Rest/Contracts.cs 的 DeployResponse）。 */
interface DeployPayload {
  processDefinitionId?: string;
  version?: number;
  message?: string;
  warnings?: { rule?: string; message?: string }[] | null;
}

/** 部署结果：提示级问题（不阻断部署，但实例跑到对应节点可能失败）单独带出来。 */
interface DeployOutcome {
  ok: boolean;
  message: string;
  version: number;
  warnings: string[];
}

/** 生成幂等键（crypto.randomUUID 优先，非安全上下文退化为时间戳 + 随机串）。 */
function newIdempotencyKey(): string {
  const cryptoObj = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;

  return cryptoObj?.randomUUID?.()
    ?? `designer-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/**
 * 把一段 BPMN XML 提交到后端部署（§6.3 的 POST /api/designer/deploy）。
 * 后端做权威校验，这里只负责上报结果。
 */
async function deployXml(xml: string): Promise<DeployOutcome> {
  try {
    const response = await fetch('/api/designer/deploy', {
      method: 'POST',
      // 幂等键：网络重试 / 代理重发时后端只部署一次，不会多出一个定义版本
      headers: {
        'Content-Type': 'text/xml',
        'Idempotency-Key': newIdempotencyKey(),
        ...credentialHeaders(),
      },
      body: xml,
    });

    const payload = (await response.json()) as DeployPayload;

    if (!response.ok) {
      return {
        ok: false,
        message: `部署被拒绝：${describeHttpFailure(
          response.status,
          payload.message ?? response.statusText,
          MODEL_WRITE_SCOPE,
        )}`,
        version: 0,
        warnings: [],
      };
    }

    const warnings = (payload.warnings ?? [])
      .map((warning) => (warning.message ?? '').trim())
      .filter((message) => message.length > 0);

    return {
      ok: true,
      message: `部署成功：${payload.processDefinitionId ?? ''}（v${payload.version ?? 0}）`,
      version: Number(payload.version) || 0,
      warnings,
    };
  } catch (error) {
    return { ok: false, message: `部署失败：${(error as Error).message}`, version: 0, warnings: [] };
  }
}

/**
 * 部署提示的一行摘要（状态栏只有一行）：完整明细写控制台，清单里给出条数与第一条。
 * 提示级问题不阻断部署，但「部署成功、实例跑到该节点才失败」正是要避免的事，必须露出来。
 */
function describeDeployWarnings(warnings: string[]): string {
  if (warnings.length === 0) {
    return '';
  }

  const first = warnings[0].length > 120 ? `${warnings[0].slice(0, 120)}…` : warnings[0];

  return `；${warnings.length} 条提示：${first}`;
}

/** 把 XML 临时导入（隐藏的）模型器，复用同一套本地校验规则跑一遍。 */
async function validateXmlString(xml: string): Promise<DraftIssue[]> {
  await ensureModeler().modeler.importXML(xml);

  return validateDiagram(ensureModeler().registry);
}

/**
 * 列表操作列的「部署」：先本地校验拦截，再提交后端。
 * 校验/部署状态都写到列表状态栏，避免进入编辑器。
 */
async function deployRecord(record: DiagramRecord, button: HTMLButtonElement): Promise<void> {
  button.disabled = true;

  try {
    setListStatus('正在校验…');

    const issues = await validateXmlString(record.xml);

    if (hasBlockingError(issues)) {
      setListStatus(`部署已阻止：${summarize(issues)}`, true);
      return;
    }

    setListStatus('正在部署…');

    const result = await deployXml(record.xml);

    if (result.ok && result.version > 0) {
      // 部署成功：用后端返回的版本号更新草稿，列表里版本列自动递增
      await setDiagramVersion(record.modelId, result.version);
      await renderList();
    }

    if (result.warnings.length > 0) {
      // 明细可能较长，状态栏放不下：完整内容写控制台，工具栏只留摘要
      console.warn(`[netbpm] 部署提示（${record.name}）：`, result.warnings);
    }

    setListStatus(`${result.message}${describeDeployWarnings(result.warnings)}`, !result.ok);
  } finally {
    button.disabled = false;
  }
}

/** 从 URL ?processDefinitionId=xxx 回显已部署的定义（编辑器视图）。 */
async function loadDefinitionFromQuery(): Promise<boolean> {
  const params = new URLSearchParams(window.location.search);
  const definitionId = params.get('processDefinitionId');

  if (definitionId === null) {
    return false;
  }

  showView('editor');

  const response = await fetch(`/api/designer/definition/${encodeURIComponent(definitionId)}/xml`, {
    headers: credentialHeaders(),
  });

  if (!response.ok) {
    setStatus(
      `回显失败：${describeHttpFailure(response.status, response.statusText, MODEL_READ_SCOPE)}`,
      true,
    );
    return true;
  }

  await loadDiagram(await response.text(), definitionId);
  return true;
}

// ─────────────────────────── 画布/设置栏拖拽分隔条 ───────────────────────────

const PROPERTIES_WIDTH_KEY = 'netbpm.designer.propertiesWidth';
const PROPERTIES_DEFAULT_WIDTH = 320;
const PROPERTIES_MIN_WIDTH = 280;

const panelResizer = document.querySelector<HTMLDivElement>('#panel-resizer');
const propertiesPanel = document.querySelector<HTMLElement>('#properties');

function applyPropertiesWidth(width: number): void {
  if (propertiesPanel !== null) {
    propertiesPanel.style.width = `${Math.round(width)}px`;
  }

  // 容器尺寸变了，bpmn-js 画布需要重算视口
  services?.canvas.resized();
}

function initPanelResizer(): void {
  if (panelResizer === null || propertiesPanel === null) {
    return;
  }

  const saved = Number(localStorage.getItem(PROPERTIES_WIDTH_KEY));

  if (Number.isFinite(saved) && saved >= PROPERTIES_MIN_WIDTH) {
    applyPropertiesWidth(saved);
  }

  let dragging = false;
  let startX = 0;
  let startWidth = 0;

  panelResizer.addEventListener('pointerdown', (event) => {
    dragging = true;
    startX = event.clientX;
    startWidth = propertiesPanel.getBoundingClientRect().width;
    panelResizer.setPointerCapture(event.pointerId);
    document.body.classList.add('resizing');
  });

  panelResizer.addEventListener('pointermove', (event) => {
    if (!dragging) {
      return;
    }

    const width = Math.min(
      Math.max(startWidth + (startX - event.clientX), PROPERTIES_MIN_WIDTH),
      window.innerWidth * 0.7,
    );

    applyPropertiesWidth(width);
  });

  const stopDragging = (): void => {
    if (!dragging) {
      return;
    }

    dragging = false;
    document.body.classList.remove('resizing');
    localStorage.setItem(PROPERTIES_WIDTH_KEY, propertiesPanel.getBoundingClientRect().width.toString());
  };

  panelResizer.addEventListener('pointerup', stopDragging);
  panelResizer.addEventListener('pointercancel', stopDragging);

  // 双击恢复默认宽度
  panelResizer.addEventListener('dblclick', () => {
    localStorage.removeItem(PROPERTIES_WIDTH_KEY);
    applyPropertiesWidth(PROPERTIES_DEFAULT_WIDTH);
  });
}

// ─────────────────────────── 事件绑定 ───────────────────────────

// 列表：新增 / 导入
document.querySelector('#btn-new')?.addEventListener('click', () => void openNewEditor());

document.querySelector('#btn-import')?.addEventListener('click', () => fileInputList?.click());

fileInputList?.addEventListener('change', () => {
  const file = fileInputList.files?.[0];

  if (!file) {
    return;
  }

  void file.text().then(async (xml) => {
    if (parseProcessMeta(xml).processId.length === 0) {
      setListStatus(`导入失败：${file.name} 不是有效的 BPMN 流程图（找不到 process 节点）。`, true);
      return;
    }

    const record = await saveDiagramXml(xml);
    await renderList();
    setListStatus(`已导入：${record.name}（${record.processId}）`);
  }).finally(() => {
    fileInputList.value = '';
  });
});

// 编辑器：返回（有未保存修改时先确认，避免误点丢图）
document.querySelector('#btn-back')?.addEventListener('click', () => {
  if (editorDirty && !window.confirm('当前流程图有未保存的修改，确定返回列表吗？')) {
    return;
  }

  editorDirty = false;
  currentModelId = null;
  currentRevision = undefined;
  clearModelIdUrl();
  void renderList();
  showView('models');
});

// 关闭/刷新页面：浏览器原生「离开确认」（提示文案由浏览器决定，不能自定义）
window.addEventListener('beforeunload', (event) => {
  if (!editorDirty) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
});

fileInputEditor?.addEventListener('change', () => {
  const file = fileInputEditor.files?.[0];

  if (!file) {
    return;
  }

  void file
    .text()
    .then((xml) => loadDiagram(xml, file.name))
    .finally(() => {
      fileInputEditor.value = '';
    });
});

// ─────────────────────────── 图标工具栏 ───────────────────────────

/** 图标按钮统一绑定：services 未就绪时（画布尚未初始化）点击不生效。 */
function bindIcon(id: string, handler: (services: ModelerServices) => void): void {
  document.querySelector(`#${id}`)?.addEventListener('click', () => {
    if (services === null) {
      return;
    }

    handler(services);
  });
}

bindIcon('it-save', () => void save());

bindIcon('it-import', () => fileInputEditor?.click());

bindIcon('it-export', () => {
  void exportXml().then((xml) => {
    const meta = parseProcessMeta(xml);

    downloadXml(xml, `${meta.processId.length > 0 ? meta.processId : 'process'}.bpmn20.xml`);
    setStatus('已导出 XML');
  });
});

bindIcon('it-cut', ({ editorActions }) => editorActions.trigger('cut'));
bindIcon('it-copy', ({ editorActions }) => editorActions.trigger('copy'));
bindIcon('it-paste', ({ editorActions }) => editorActions.trigger('paste'));
bindIcon('it-delete', ({ editorActions }) => editorActions.trigger('removeSelection'));
bindIcon('it-undo', ({ editorActions }) => editorActions.trigger('undo'));
bindIcon('it-redo', ({ editorActions }) => editorActions.trigger('redo'));
bindIcon('it-zoom-in', ({ editorActions }) => editorActions.trigger('stepZoom', { value: 1 }));
bindIcon('it-zoom-out', ({ editorActions }) => editorActions.trigger('stepZoom', { value: -1 }));
bindIcon('it-zoom-fit', ({ canvas }) => canvas.zoom('fit-viewport', 'auto'));

// 左侧菜单：模型列表 / 部署列表
navModels?.addEventListener('click', (event) => {
  event.preventDefault();
  void renderList();
  showView('models');
});

navDeployments?.addEventListener('click', (event) => {
  event.preventDefault();
  void renderDeployList();
  showView('deployments');
});

// 部署列表：刷新
document.querySelector('#btn-refresh-deployments')?.addEventListener('click', () => void renderDeployList());

// ─────────────────────────── 启动 ───────────────────────────

async function boot(): Promise<void> {
  // 1) ?modelId= 直接打开草稿（刷新/分享链接都能回到同一流程图，对齐 Activiti 的 modeler.html?modelId=）
  const params = new URLSearchParams(window.location.search);
  const modelId = params.get('modelId');

  if (modelId !== null) {
    const record = await findByModelId(modelId);

    if (record !== undefined) {
      openRecord(record);
      return;
    }

    // modelId 失效（模型已删除）→ 回退到模型列表
    clearModelIdUrl();
  }

  // 1.5) ?action=new 直接新建流程模型（管理端「新建」入口，避免先跳列表再点一次新增）
  if (params.get('action') === 'new') {
    await openNewEditor();
    return;
  }

  // 2) ?processDefinitionId= 回显已部署定义
  const echoed = await loadDefinitionFromQuery();
  if (echoed) {
    return;
  }

  // 3) 默认显示流程模型列表
  await renderList();
  showView('models');
}

void boot();

initPanelResizer();

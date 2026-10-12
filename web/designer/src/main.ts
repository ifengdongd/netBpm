/**
 * NetBpm 流程设计器入口（开发计划 §6）。
 *
 * 与后端的契约只有两个（§6.3）：
 *   POST /api/designer/deploy                提交 BPMN XML → 后端解析 + 校验 + 落库
 *   GET  /api/designer/definition/{id}/xml   回显 BPMN XML
 * 设计器不直接操作数据库；流程图草稿列表存 localStorage（见 storage.ts）。
 *
 * 页面结构：默认显示「流程图列表」，从列表进入「编辑器」（新增/编辑共用）。
 *
 * P8 覆盖的 §6.2 四项：①activiti-moddle.json 描述符 ②调色板裁剪 ③属性面板（白名单）
 * ④即时校验（本地镜像后端规则，部署前拦截）。
 */
import Modeler from 'bpmn-js/lib/Modeler';
import { BpmnPropertiesPanelModule } from 'bpmn-js-properties-panel';
import activitiModdle from './activiti-moddle.json';
import NetBpmPaletteProvider from './palette';
import NetBpmPropertiesProvider from './designer/propertiesProvider';
import { renderIssues } from './issues';
import { hasBlockingError, summarize, validateDiagram, type DraftIssue } from './validation';
import {
  deleteDiagram,
  parseProcessMeta,
  readDiagrams,
  saveDiagramXml,
  type DiagramRecord,
} from './storage';
import type {
  CanvasService,
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

// ─────────────────────────── DOM 引用 ───────────────────────────

const listView = document.querySelector<HTMLElement>('#list-view');
const editorView = document.querySelector<HTMLElement>('#editor-view');
const listStatusElement = document.querySelector<HTMLSpanElement>('#list-status');
const listEmptyElement = document.querySelector<HTMLElement>('#list-empty');
const diagramRowsElement = document.querySelector<HTMLTableSectionElement>('#diagram-rows');
const editorTitleElement = document.querySelector<HTMLElement>('#editor-title');
const statusElement = document.querySelector<HTMLSpanElement>('#status');
const issuesElement = document.querySelector<HTMLElement>('#issues');
const deployButton = document.querySelector<HTMLButtonElement>('#btn-deploy');
const fileInputList = document.querySelector<HTMLInputElement>('#file-input-list');
const fileInputEditor = document.querySelector<HTMLInputElement>('#file-input-editor');

// ─────────────────────────── 模型器（首次进入编辑器时才创建）───────────────────────────

interface ModelerServices {
  modeler: Modeler;
  registry: ElementRegistryService;
  selection: SelectionService;
  canvas: CanvasService;
}

let services: ModelerServices | null = null;

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
      // 整体替换默认调色板：只保留子集元素 + 连线工具（§6.2 第 2 项）
      { paletteProvider: ['type', NetBpmPaletteProvider] },
      // 属性面板（§6.2 第 3 项）
      { netBpmPropertiesProvider: ['type', NetBpmPropertiesProvider] },
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
  };

  // 导入完成与每次编辑后重跑校验（commandStack.changed 覆盖属性面板的写入）
  modeler.on('import.done', () => refreshValidation());
  modeler.on('commandStack.changed', () => refreshValidation());

  return services;
}

// ─────────────────────────── 视图切换 ───────────────────────────

function showView(view: 'list' | 'editor'): void {
  if (listView !== null) {
    listView.hidden = view !== 'list';
  }

  if (editorView !== null) {
    editorView.hidden = view !== 'editor';
  }

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

function renderList(): void {
  if (diagramRowsElement === null) {
    return;
  }

  const records = readDiagrams();
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

    const actionCell = document.createElement('td');

    const editButton = document.createElement('button');
    editButton.type = 'button';
    editButton.textContent = '编辑';
    editButton.addEventListener('click', () => openRecord(record));

    const exportButton = document.createElement('button');
    exportButton.type = 'button';
    exportButton.textContent = '导出 XML';
    exportButton.addEventListener('click', () => downloadXml(record.xml, `${record.processId}.bpmn20.xml`));

    const deleteButton = document.createElement('button');
    deleteButton.type = 'button';
    deleteButton.textContent = '删除';
    deleteButton.className = 'danger';
    deleteButton.addEventListener('click', () => {
      if (window.confirm(`确定删除流程图「${record.name}」（${record.processId}）吗？`)) {
        deleteDiagram(record.processId);
        renderList();
        setListStatus(`已删除：${record.name}`);
      }
    });

    actionCell.append(editButton, exportButton, deleteButton);
    row.append(nameCell, idCell, timeCell, actionCell);
    diagramRowsElement.append(row);
  }
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

  if (issuesElement !== null) {
    renderIssues(issuesElement, issues, focusElement);
  }

  const blocked = hasBlockingError(issues);

  if (deployButton !== null) {
    deployButton.disabled = blocked;
    deployButton.title = blocked ? '存在校验错误，先修复再部署' : '';
  }

  setStatus(summarize(issues), blocked);

  return issues;
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

async function loadDiagram(xml: string, source: string): Promise<void> {
  try {
    await ensureModeler().modeler.importXML(xml);

    const meta = parseProcessMeta(xml);

    if (editorTitleElement !== null) {
      editorTitleElement.textContent = meta.name.length > 0 ? meta.name : '未命名流程';
    }

    setStatus(`已载入：${source}`);
    refreshValidation();
  } catch (error) {
    setStatus(`载入失败：${(error as Error).message}`, true);
  }
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

/** 进入编辑器：新建（传 emptyDiagram(randomProcessId())）或编辑已有记录。 */
function openEditor(xml: string): void {
  showView('editor');
  void loadDiagram(xml, '编辑器');
}

function openRecord(record: DiagramRecord): void {
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

    const record = saveDiagramXml(xml);

    if (editorTitleElement !== null) {
      editorTitleElement.textContent = record.name;
    }

    setStatus(`已保存：${record.name}（${record.processId}）`);
  } catch (error) {
    setStatus(`保存失败：${(error as Error).message}`, true);
  }
}

async function deploy(): Promise<void> {
  // 部署前再校验一次：即使按钮被脚本触发也不放行
  const issues = refreshValidation();

  if (hasBlockingError(issues)) {
    setStatus('部署已阻止：请先修复校验错误（见下方清单）。', true);
    issuesElement?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    return;
  }

  setStatus('正在部署…');

  try {
    const xml = await exportXml();
    const response = await fetch('/api/designer/deploy', {
      method: 'POST',
      headers: { 'Content-Type': 'text/xml' },
      body: xml,
    });

    const payload = await response.json();

    if (!response.ok) {
      setStatus(`部署被拒绝：${payload.message ?? response.statusText}`, true);
      return;
    }

    setStatus(`部署成功：${payload.processDefinitionId}（v${payload.version}）`);
  } catch (error) {
    setStatus(`部署失败：${(error as Error).message}`, true);
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

  const response = await fetch(`/api/designer/definition/${encodeURIComponent(definitionId)}/xml`);

  if (!response.ok) {
    setStatus(`回显失败：${response.statusText}`, true);
    return true;
  }

  await loadDiagram(await response.text(), definitionId);
  return true;
}

// ─────────────────────────── 事件绑定 ───────────────────────────

// 列表：新增 / 导入
document.querySelector('#btn-new')?.addEventListener('click', () => openEditor(emptyDiagram(randomProcessId())));

document.querySelector('#btn-import')?.addEventListener('click', () => fileInputList?.click());

fileInputList?.addEventListener('change', () => {
  const file = fileInputList.files?.[0];

  if (!file) {
    return;
  }

  void file.text().then((xml) => {
    if (parseProcessMeta(xml).processId.length === 0) {
      setListStatus(`导入失败：${file.name} 不是有效的 BPMN 流程图（找不到 process 节点）。`, true);
      return;
    }

    const record = saveDiagramXml(xml);
    renderList();
    setListStatus(`已导入：${record.name}（${record.processId}）`);
  }).finally(() => {
    fileInputList.value = '';
  });
});

// 编辑器：返回 / 保存 / 导入 / 导出 / 部署
document.querySelector('#btn-back')?.addEventListener('click', () => {
  renderList();
  showView('list');
});

document.querySelector('#btn-save')?.addEventListener('click', () => void save());

document.querySelector('#btn-import-editor')?.addEventListener('click', () => fileInputEditor?.click());

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

document.querySelector('#btn-export')?.addEventListener('click', () => {
  void exportXml().then((xml) => {
    const meta = parseProcessMeta(xml);

    downloadXml(xml, `${meta.processId.length > 0 ? meta.processId : 'process'}.bpmn20.xml`);
    setStatus('已导出 XML');
  });
});

deployButton?.addEventListener('click', () => void deploy());

// ─────────────────────────── 启动 ───────────────────────────

void loadDefinitionFromQuery().then((loaded) => {
  if (loaded) {
    return;
  }

  // 默认显示流程图列表，不再直接进入画布
  renderList();
  showView('list');
});

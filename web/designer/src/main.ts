/**
 * NetBpm 流程设计器入口（开发计划 §6）。
 *
 * 与后端的契约只有两个（§6.3）：
 *   POST /api/designer/deploy                提交 BPMN XML → 后端解析 + 校验 + 落库
 *   GET  /api/designer/definition/{id}/xml   回显 BPMN XML
 * 设计器不直接操作数据库。
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
const EMPTY_DIAGRAM = `<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                  xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                  xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                  xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                  xmlns:activiti="http://activiti.org/bpmn"
                  id="Definitions_1"
                  targetNamespace="http://netbpm/bpmn">
  <bpmn:process id="process_1" name="新建流程" isExecutable="true">
    <bpmn:startEvent id="StartEvent_1" />
  </bpmn:process>
  <bpmndi:BPMNDiagram id="BPMNDiagram_1">
    <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="process_1">
      <bpmndi:BPMNShape id="StartEvent_1_di" bpmnElement="StartEvent_1">
        <dc:Bounds x="160" y="120" width="36" height="36" />
      </bpmndi:BPMNShape>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</bpmn:definitions>`;

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

const registry = modeler.get<ElementRegistryService>('elementRegistry');
const selection = modeler.get<SelectionService>('selection');
const canvas = modeler.get<CanvasService>('canvas');

const statusElement = document.querySelector<HTMLSpanElement>('#status');
const fileInput = document.querySelector<HTMLInputElement>('#file-input');
const issuesElement = document.querySelector<HTMLElement>('#issues');
const deployButton = document.querySelector<HTMLButtonElement>('#btn-deploy');

function setStatus(message: string, isError = false): void {
  if (!statusElement) {
    return;
  }

  statusElement.textContent = message;
  statusElement.style.color = isError ? '#c0392b' : '#555';
}

/** 重跑即时校验并刷新问题清单（导入完成、每次编辑后都会调用）。 */
function refreshValidation(): DraftIssue[] {
  const issues = validateDiagram(registry);

  if (issuesElement) {
    renderIssues(issuesElement, issues, focusElement);
  }

  const blocked = hasBlockingError(issues);

  if (deployButton) {
    deployButton.disabled = blocked;
    deployButton.title = blocked ? '存在校验错误，先修复再部署' : '';
  }

  setStatus(summarize(issues), blocked);

  return issues;
}

function focusElement(elementId: string): void {
  const element = registry.get(elementId);

  if (!element) {
    return;
  }

  selection.select(element);
  canvas.scrollToElement(element);
}

async function loadDiagram(xml: string, source: string): Promise<void> {
  try {
    await modeler.importXML(xml);
    setStatus(`已载入：${source}`);
    refreshValidation();
  } catch (error) {
    setStatus(`载入失败：${(error as Error).message}`, true);
  }
}

async function exportXml(): Promise<string> {
  const { xml } = await modeler.saveXML({ format: true });
  return xml ?? '';
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

async function loadDefinitionFromQuery(): Promise<boolean> {
  const params = new URLSearchParams(window.location.search);
  const definitionId = params.get('processDefinitionId');

  if (!definitionId) {
    return false;
  }

  const response = await fetch(`/api/designer/definition/${encodeURIComponent(definitionId)}/xml`);

  if (!response.ok) {
    setStatus(`回显失败：${response.statusText}`, true);
    return true;
  }

  await loadDiagram(await response.text(), definitionId);
  return true;
}

// ─────────────────────────── 事件绑定 ───────────────────────────

// 导入完成与每次编辑后重跑校验（commandStack.changed 覆盖属性面板的写入）
modeler.on('import.done', () => refreshValidation());
modeler.on('commandStack.changed', () => refreshValidation());

document.querySelector('#btn-new')?.addEventListener('click', () => {
  void loadDiagram(EMPTY_DIAGRAM, '新建流程');
});

document.querySelector('#btn-import')?.addEventListener('click', () => fileInput?.click());

document.querySelector('#btn-export')?.addEventListener('click', () => {
  void exportXml().then((xml) => {
    const blob = new Blob([xml], { type: 'application/xml' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'process.bpmn20.xml';
    anchor.click();
    URL.revokeObjectURL(url);
    setStatus('已导出 XML');
  });
});

deployButton?.addEventListener('click', () => void deploy());

fileInput?.addEventListener('change', () => {
  const file = fileInput.files?.[0];

  if (!file) {
    return;
  }

  void file
    .text()
    .then((xml) => loadDiagram(xml, file.name))
    .finally(() => {
      fileInput.value = '';
    });
});

void loadDefinitionFromQuery().then((loaded) => {
  if (!loaded) {
    void loadDiagram(EMPTY_DIAGRAM, '新建流程');
  }
});

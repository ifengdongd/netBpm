/**
 * 调色板裁剪（开发计划 §6.2 第 2 项）。
 *
 * 只保留子集元素 + 连线工具，移除子流程 / 边界事件 / 中间事件 / 泳道 / 数据对象等全部按钮。
 * 做法：整个替换 bpmn-js 默认的 paletteProvider（而不是叠加一个 provider）——
 * Palette 是按 key 合并各 provider 的条目的，无法"删除"默认条目。
 */

type PaletteEntry = {
  group: string;
  className: string;
  title: string;
  action: Record<string, unknown>;
};

const MODULE_GROUP = 'model';
const TOOL_GROUP = 'tools';

export default class NetBpmPaletteProvider {
  static $inject = [
    'palette',
    'create',
    'elementFactory',
    'spaceTool',
    'lassoTool',
    'handTool',
    'globalConnect',
    'translate',
  ];

  private readonly create: any;
  private readonly elementFactory: any;
  private readonly spaceTool: any;
  private readonly lassoTool: any;
  private readonly handTool: any;
  private readonly globalConnect: any;
  private readonly translate: (text: string) => string;

  constructor(
    palette: any,
    create: any,
    elementFactory: any,
    spaceTool: any,
    lassoTool: any,
    handTool: any,
    globalConnect: any,
    translate: (text: string) => string,
  ) {
    this.create = create;
    this.elementFactory = elementFactory;
    this.spaceTool = spaceTool;
    this.lassoTool = lassoTool;
    this.handTool = handTool;
    this.globalConnect = globalConnect;
    this.translate = translate;

    palette.registerProvider(this);
  }

  getPaletteEntries(): Record<string, PaletteEntry> {
    const t = this.translate;

    return {
      // ── 事件 ──────────────────────────────────────────────
      'create.start-event': this.createShape('bpmn:StartEvent', 'bpmn-icon-start-event-none', '开始事件'),
      'create.end-event': this.createShape('bpmn:EndEvent', 'bpmn-icon-end-event-none', '结束事件'),

      // ── 任务 ──────────────────────────────────────────────
      'create.user-task': this.createShape('bpmn:UserTask', 'bpmn-icon-user-task', '用户任务'),
      'create.service-task': this.createShape('bpmn:ServiceTask', 'bpmn-icon-service-task', '服务任务'),
      'create.manual-task': this.createShape('bpmn:ManualTask', 'bpmn-icon-manual-task', '手工任务'),
      'create.script-task': this.createShape('bpmn:ScriptTask', 'bpmn-icon-script-task', '脚本任务'),

      // ── 网关 ──────────────────────────────────────────────
      'create.exclusive-gateway': this.createShape('bpmn:ExclusiveGateway', 'bpmn-icon-gateway-xor', '排他网关'),
      'create.parallel-gateway': this.createShape('bpmn:ParallelGateway', 'bpmn-icon-gateway-parallel', '并行网关'),

      // ── 连线 ──────────────────────────────────────────────
      'global-connect-tool': {
        group: TOOL_GROUP,
        className: 'bpmn-icon-connection-multi',
        title: t('连线'),
        action: {
          click: (event: Event) => this.globalConnect.start(event),
          dragstart: (event: Event) => this.globalConnect.start(event),
        },
      },

      // ── 画布工具（保留默认行为的等价实现）──────────────────
      'tool-hand': {
        group: TOOL_GROUP,
        className: 'bpmn-icon-hand-tool',
        title: t('抓手工具'),
        action: {
          click: (event: Event) => this.handTool.activateHand(event),
          dragstart: (event: Event) => this.handTool.activateHand(event),
        },
      },
      'tool-lasso': {
        group: TOOL_GROUP,
        className: 'bpmn-icon-lasso-tool',
        title: t('套索选择'),
        action: {
          click: (event: Event) => this.lassoTool.activateSelection(event),
          dragstart: (event: Event) => this.lassoTool.activateSelection(event),
        },
      },
      'tool-space': {
        group: TOOL_GROUP,
        className: 'bpmn-icon-space-tool',
        title: t('空间工具'),
        action: {
          click: (event: Event) => this.spaceTool.activateSelection(event),
          dragstart: (event: Event) => this.spaceTool.activateSelection(event),
        },
      },
    };
  }

  /**
   * 创建一个"拖拽 / 点击即新建元素"的调色板条目。
   * 使用 elementFactory.createShape + create.start，这是 bpmn-js 8–18 稳定的公开用法。
   */
  private createShape(type: string, className: string, label: string): PaletteEntry {
    const start = (event: Event) => {
      const shape = this.elementFactory.createShape({ type });
      this.create.start(event, shape);
    };

    return {
      group: MODULE_GROUP,
      className,
      title: this.translate(label),
      action: { click: start, dragstart: start },
    };
  }
}

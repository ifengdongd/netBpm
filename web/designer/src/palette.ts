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
    'selection',
    'modeling',
    'moddle',
    'elementRegistry',
    'translate',
  ];

  private readonly create: any;
  private readonly elementFactory: any;
  private readonly spaceTool: any;
  private readonly lassoTool: any;
  private readonly handTool: any;
  private readonly globalConnect: any;
  private readonly selection: any;
  private readonly modeling: any;
  private readonly moddle: any;
  private readonly elementRegistry: any;
  private readonly translate: (text: string) => string;

  constructor(
    palette: any,
    create: any,
    elementFactory: any,
    spaceTool: any,
    lassoTool: any,
    handTool: any,
    globalConnect: any,
    selection: any,
    modeling: any,
    moddle: any,
    elementRegistry: any,
    translate: (text: string) => string,
  ) {
    this.create = create;
    this.elementFactory = elementFactory;
    this.spaceTool = spaceTool;
    this.lassoTool = lassoTool;
    this.handTool = handTool;
    this.globalConnect = globalConnect;
    this.selection = selection;
    this.modeling = modeling;
    this.moddle = moddle;
    this.elementRegistry = elementRegistry;
    this.translate = translate;

    palette.registerProvider(this);
  }

  getPaletteEntries(): Record<string, PaletteEntry> {
    const t = this.translate;

    return {
      // ── 事件 ──────────────────────────────────────────────
      'create.start-event': this.createShape('bpmn:StartEvent', 'bpmn-icon-start-event-none', '开始事件'),
      'create.end-event': this.createShape('bpmn:EndEvent', 'bpmn-icon-end-event-none', '结束事件'),
      'create.boundary-timer': {
        group: MODULE_GROUP,
        className: 'bpmn-icon-intermediate-event-catch-timer',
        title: t('边界定时器（挂在选中的用户任务上）'),
        action: { click: (event: Event) => this.createBoundaryTimer(event) },
      },

      // ── 任务 ──────────────────────────────────────────────
      'create.user-task': this.createShape('bpmn:UserTask', 'bpmn-icon-user-task', '用户任务'),
      'create.service-task': this.createShape('bpmn:ServiceTask', 'bpmn-icon-service-task', '服务任务'),
      'create.manual-task': this.createShape('bpmn:ManualTask', 'bpmn-icon-manual-task', '手工任务'),
      'create.script-task': this.createShape('bpmn:ScriptTask', 'bpmn-icon-script-task', '脚本任务'),

      // ── 网关 ──────────────────────────────────────────────
      // ⚠️ 不提供事件网关：它的后续必须是中间捕获事件（定时/消息/信号），本子集没有对应行为，
      // 画出来必然被部署期拒绝（event-gateway-unsupported）。导出历史模型里若已存在，
      // 属性面板仍会显示只读提示，不影响打开与另存。
      'create.exclusive-gateway': this.createShape('bpmn:ExclusiveGateway', 'bpmn-icon-gateway-xor', '单一网关'),
      'create.parallel-gateway': this.createShape('bpmn:ParallelGateway', 'bpmn-icon-gateway-parallel', '并行网关'),
      'create.inclusive-gateway': this.createShape('bpmn:InclusiveGateway', 'bpmn-icon-gateway-or', '包含网关'),

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
   * 新建「边界定时器」并直接挂到用户任务上。
   *
   * 边界事件不能像普通节点一样落在空白画布上（没有宿主就是不合法模型），所以这里
   * 优先挂到当前选中的用户任务；没选中但图里只有一个用户任务时自动用它；
   * 其它情况给出提示，让用户先选任务。
   */
  private createBoundaryTimer(event: Event): void {
    void event;

    const isUserTask = (element: any) => element?.businessObject?.$type === 'bpmn:UserTask';
    let host = this.selection.get().find(isUserTask);

    if (host === undefined) {
      const userTasks = this.elementRegistry.getAll().filter(isUserTask);

      if (userTasks.length === 1) {
        host = userTasks[0];
      }
    }

    if (host === undefined) {
      this.notify('请先选中一个用户任务，再点击「边界定时器」。');
      return;
    }

    const shape = this.modeling.createShape(
      { type: 'bpmn:BoundaryEvent' },
      { x: host.x + host.width - 18, y: host.y + host.height - 18 },
      host,
    );

    this.modeling.updateProperties(shape, {
      attachedToRef: host.businessObject,
      cancelActivity: true,
      eventDefinitions: [
        this.moddle.create('bpmn:TimerEventDefinition', {
          timeDuration: this.moddle.create('bpmn:FormalExpression', { body: 'PT24H' }),
        }),
      ],
    });

    this.selection.select(shape);
    this.notify('已添加边界定时器（默认 PT24H），可在右侧属性面板修改时长或改为时间点。');
  }

  private notify(message: string): void {
    if (typeof window !== 'undefined' && typeof window.alert === 'function') {
      window.alert(message);
    }
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

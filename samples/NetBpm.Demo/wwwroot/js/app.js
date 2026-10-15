// NetBpm.Demo 各页面共用的工具：请求封装、URL 参数、格式化、轻提示、导航。
// 设计原则：前端不写死任何流程与审批选项 —— 流程清单来自 /api/processes，
// 审批选项来自待办/实例视图里的表单定义。

// 流程 key → 该流程 HTTP 端点前缀（与后端 /api/{prefix} 一致）
const PROCESS_API = {
  order_process: '/api/order',
  leave_process: '/api/leave',
};

// ─────────────────────────── 基础工具 ───────────────────────────

const $ = (sel, el = document) => el.querySelector(sel);
const $$ = (sel, el = document) => [...el.querySelectorAll(sel)];

// 读取 location.search 里的参数
function qs(name) {
  return new URLSearchParams(location.search).get(name);
}

// HTML 转义（所有动态文本进 DOM 前都过一遍）
function esc(value) {
  if (value === null || value === undefined) return '';
  return String(value)
    .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;').replaceAll("'", '&#39;');
}

// 时间格式化（UTC → 本地展示）
function fmtTime(value) {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}

// 占位符渲染：把模板里的 {businessId} / {taskId} / {processInstanceId} 换成实际值
function fillTemplate(template, values = {}) {
  return String(template ?? '').replace(/\{(\w+)\}/g, (m, key) => (key in values ? values[key] : m));
}

// 状态徽标：Running 蓝 / Approved 绿 / Refused 红
function statusPill(status) {
  const map = {
    Running: ['run', '审批中'],
    Approved: ['ok', '审批通过'],
    Refused: ['no', '审批拒绝'],
  };
  const [cls, label] = map[status] ?? ['run', status ?? '—'];
  return `<span class="pill ${cls}">${esc(label)}</span>`;
}

// ─────────────────────────── 请求封装 ───────────────────────────

// fetch 封装：非 2xx 时按 {message, errors} 形状抛错，页面 catch 后 toast 出来
async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
    body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
  });

  if (response.status === 204) return null;

  const payload = await response.json().catch(() => null);

  if (!response.ok) {
    const detail = payload?.message ?? `请求失败（HTTP ${response.status}）`;
    const errors = Array.isArray(payload?.errors) && payload.errors.length
      ? `\n${payload.errors.join('\n')}` : '';
    throw new Error(detail + errors);
  }

  return payload;
}

// ─────────────────────────── 轻提示 ───────────────────────────

function toast(message, kind = 'err') {
  let el = $('#toast');
  if (!el) {
    el = document.createElement('div');
    el.id = 'toast';
    el.className = 'toast';
    document.body.appendChild(el);
  }
  el.textContent = message;
  el.className = `toast ${kind}`;
  el.style.display = 'block';
  clearTimeout(el._timer);
  el._timer = setTimeout(() => { el.style.display = 'none'; }, kind === 'ok' ? 2500 : 6000);
}

// ─────────────────────────── 流程清单 ───────────────────────────

// 拉流程信息枚举（GET /api/processes）；页面顶部与下拉都用它
async function loadProcesses() {
  const list = await api('/api/processes');
  return Array.isArray(list) ? list : [];
}

// 按流程 key 查一条
async function loadProcess(name) {
  return api(`/api/processes/${encodeURIComponent(name)}`);
}

// ─────────────────────────── 页面骨架 ───────────────────────────

// 注入顶部导航（active 为当前页的 data-page 名）
function renderNav(active) {
  const links = [
    ['index', '首页 · 流程发起', '/index.html'],
    ['tasks', '待办任务列表', '/tasks.html'],
    ['approve', '流程审批', null],          // 审批页要带任务参数，导航里不可直接点
    ['bill', '业务单查看', null],
    ['deployments', '流程部署', '/deployments.html'],
    ['transfer', '待办任务转移', '/transfer.html'],
    ['jump', '流程跳转', '/jump.html'],
  ];
  const nav = document.createElement('nav');
  nav.className = 'top';
  nav.innerHTML =
    '<span class="brand">NetBpm 审批中心</span>' +
    links.map(([page, label, href]) =>
      href
        ? `<a href="${href}" class="${page === active ? 'active' : ''}">${esc(label)}</a>`
        : `<a href="javascript:void(0)" class="${page === active ? 'active' : ''}" style="opacity:.55">${esc(label)}</a>`)
      .join('');
  document.body.prepend(nav);
}

// 组织架构员工下拉（待办转移页选新审批人）
async function loadEmployees() {
  return api('/api/org/employees');
}

// 请假审批 Demo 端到端验证：把 BPMN 里写的流转规则逐条跑一遍。
//
// 跑法（先在项目根目录把 Demo 起起来）：
//   dotnet run --project samples/NetBpm.Demo.LeaveApproval --urls http://localhost:5090
//   node samples/NetBpm.Demo.LeaveApproval/tests/e2e-leave.mjs
//   # 换端口：BASE=http://localhost:5090 node ...
//
// 🔁 可反复执行：每轮用一段<b>新的业务单号</b>（见下面 RUN），断言只认本轮的号，
//    所以上一轮留下的行不会干扰 —— 不用先清库 / 删文件。
//
// ⚠️ 唯一的例外：Leave:Store = Json <b>且</b>引擎是内存宿主时，流程实例 id 每次启动都从 1 重新开始，
//    而 JSON 是持久化的 —— 同 id 的旧审批记录会混进来。那种组合下跑之前请先删掉
//    samples/NetBpm.Demo.LeaveApproval/data/leave-business.json
//    （默认的 appsettings.Development.json 是 MySQL 宿主，不受影响）。
//
// 🔑 断言分两类，别混：
//   · <b>透过 HTTP 接口</b>的断言（[0]~[10]）—— 与业务数据落在哪儿无关，两种宿主都要通过；
//   · <b>落库核对</b>（[11]）—— 直接看落点：JSON 宿主看文件区块，MySQL 宿主看四张业务表。
//     表单值快照这类「宿主能给就给」的字段差异，也只在 [11] 里按宿主分支断言。
//
// 约定：下面所有 view / task 都是**已解包**的对象（不是 {status, body} 响应壳），
// 需要看 HTTP 状态码的地方才用 api() 裸调用 —— 免得两层结构来回搞混。
const BASE = process.env.BASE ?? 'http://localhost:5090';

/**
 * 本轮的业务单号基数（毫秒时间戳取模后再放大，纯粹为了好读）。
 *
 * ⚠️ 单号必须是<b>正整数</b>：业务表里 instances.business_id / leave_bill.business_id 都是 bigint，
 * 早先 'E2E-A' 那种带前缀的字符串落到真表就写不进去（见 LeaveBusinessKey）。
 * 用「每轮一段新号」而不是固定号，是为了让脚本可以连着跑 —— 断言只认本轮的号，
 * 上一轮的行留在库里也不会干扰。
 */
const RUN = ((Date.now() % 10000000) * 1000) + 1000;

/** 本轮第 <paramref name="offset"/> 张单子的单号。 */
const biz = (offset) => String(RUN + offset);

const BIZ = {
  probe: biz(0),
  A: biz(1), B: biz(2), C: biz(3), D: biz(4),
  E: biz(5), F: biz(6), G: biz(7), H: biz(8),
  E2: biz(9), P: biz(10), P2: biz(11), P3: biz(12), I: biz(13),
  L: biz(14), L2: biz(15),
};


let passed = 0;
const failures = [];

function check(name, actual, expected) {
  const ok = JSON.stringify(actual) === JSON.stringify(expected);
  if (ok) {
    passed += 1;
    console.log(`  ✓ ${name}`);
  } else {
    failures.push(name);
    console.log(`  ✗ ${name}\n      实际: ${JSON.stringify(actual)}\n      期望: ${JSON.stringify(expected)}`);
  }
}

async function api(method, path, body) {
  const response = await fetch(`${BASE}${path}`, {
    method,
    headers: body === undefined ? {} : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  const text = await response.text();
  let payload = null;
  if (text) {
    try { payload = JSON.parse(text); } catch { payload = text; }
  }

  return { status: response.status, body: payload };
}

const post = (p, b) => api('POST', p, b);
const get = (p) => api('GET', p);

/** 从实例视图里取指定定义节点的待办；没有则返回 null。 */
function taskOf(view, definitionKey) {
  return (view.tasks ?? []).find((t) => t.definitionKey === definitionKey) ?? null;
}

/** 当前待办的节点 id 列表（用于断言「现在停在哪个节点」）。 */
function stops(view) {
  return (view.tasks ?? []).map((t) => t.definitionKey).sort();
}

/** 取某待办上某个表单字段。 */
function fieldOf(view, definitionKey, fieldId) {
  return taskOf(view, definitionKey)?.form.find((f) => f.id === fieldId) ?? null;
}

/** 提交待办（成功时返回实例视图，非 200 直接抛 —— 需要断言失败码的地方用 submitRaw）。 */
async function submit(view, definitionKey, values, paraDict, options) {
  const response = await submitRaw(view, definitionKey, values, paraDict, options);

  if (response.status !== 200) {
    throw new Error(
      `提交 '${definitionKey}' 失败：HTTP ${response.status} ${JSON.stringify(response.body)}`);
  }

  return response.body;
}

/**
 * 提交待办，返回原始响应（用于校验 400 场景）。
 * paraDict 是「有值就按 key 更新」的参数更新；options.operator 是办理人（记进审批历史）。
 */
async function submitRaw(view, definitionKey, values, paraDict, options) {
  const task = taskOf(view, definitionKey);

  if (!task) {
    throw new Error(
      `实例 ${view.processInstanceId} 没有待办节点 '${definitionKey}'，当前停在：${stops(view).join(', ') || '(无)'}`);
  }

  const body = { values };
  if (paraDict) body.paraDict = paraDict;
  if (options?.operator) body.operator = options.operator;

  return post(`/api/leave/tasks/${task.taskId}/submit`, body);
}

/** 发起请假单，返回实例视图。部门默认给 D-SALES，便于断言 BaseModel 的字段真的落进了变量。 */
async function start(applicant, businessKey, options = {}) {
  const body = {
    applicant,
    businessKey,
    businessDeptId: options.businessDeptId ?? 'D-SALES',
    applyDeptId: options.applyDeptId ?? 'D-SALES',
  };
  if (options.paraDict) body.paraDict = options.paraDict;

  const response = await post('/api/leave/instances', body);

  if (response.status !== 200) {
    throw new Error(`发起失败：HTTP ${response.status} ${JSON.stringify(response.body)}`);
  }

  return response.body;
}

/** 参数表按 key 排序后转成普通对象，便于 JSON 比较（后端返回的字典顺序不保证）。 */
function paramsOf(view) {
  return Object.fromEntries(Object.entries(view.parameters ?? {}).sort(([a], [b]) => a.localeCompare(b)));
}

/** 发起 -> 提交申请（days/reason）-> 返回「组长审批」待办所在的实例视图。 */
async function startAndApply(applicant, days, reason, businessKey) {
  return submit(await start(applicant, businessKey), 'task_apply', { days, reason });
}

async function run() {
  console.log(`\n目标服务：${BASE}`);
  console.log(`本轮业务单号：${RUN} ~ ${RUN + 13}\n`);

  // ───────────────────────── 0. 流程定义与表单契约 ─────────────────────────

  console.log('[0] 流程定义 / 表单契约（审批选项必须来自 formProperty，不在前端写死）');

  const definition = await get('/api/leave/process-definition');
  check('GET /process-definition 返回 200', definition.status, 200);
  check('流程定义 id 形如 leave_process:1:N',
    /^leave_process:\d+:\d+$/.test(definition.body.processDefinitionId ?? ''), true);

  const xml = definition.body.xml ?? '';
  check('XML 含 leave.ApproveComplete 委托', xml.includes('leave.ApproveComplete'), true);
  check('XML 含 leave.RefuseComplete 委托', xml.includes('leave.RefuseComplete'), true);
  check('approvalResult 绑定变量 result', /id="approvalResult"[^>]*variable="result"/.test(xml), true);
  check('comment 绑定变量 comment', /id="comment"[^>]*variable="comment"/.test(xml), true);
  check('assignee 用 ${applyUser}（而不是 Demo 内部变量名）', /\$\{applyUser\}/.test(xml), true);
  check('审批通过服务任务名 = 审批通过', /serviceTask id="task_approve_complete" name="审批通过"/.test(xml), true);
  check('拒绝结束服务任务名 = 拒绝结束', /serviceTask id="task_refuse" name="拒绝结束"/.test(xml), true);

  const probe = await start('probe-a', BIZ.probe);
  const applyForm = taskOf(probe, 'task_apply')?.form;
  check('「提交请假申请」字段 = days(long,必填) / reason(string,选填)',
    applyForm?.map((f) => [f.id, f.type, f.variable, f.required]),
    [['days', 'long', 'days', true], ['reason', 'string', 'reason', false]]);

  // ── BaseModel：发起时写入的 5 个字段（result / comment 要等审批时才产生）──
  check('发起即写入 BaseModel 的 5 个变量',
    Object.keys(probe.variables).sort(),
    ['businessId', 'businessDeptId', 'applyUser', 'applyTime', 'applyDeptId'].sort());
  check('applyUser = 申请人', probe.variables.applyUser, 'probe-a');
  check('businessId = 传入的业务键', probe.variables.businessId, BIZ.probe);
  check('businessDeptId = D-SALES', probe.variables.businessDeptId, 'D-SALES');
  check('applyDeptId = D-SALES', probe.variables.applyDeptId, 'D-SALES');
  check('applyTime 是 ISO 时间串', /^\d{4}-\d{2}-\d{2}T/.test(String(probe.variables.applyTime)), true);
  check('「提交请假申请」的 assignee 由 ${applyUser} 解析', taskOf(probe, 'task_apply')?.assignee, 'probe-a');
  check('视图 base：先补齐的字段已可见，result/comment 仍为空',
    [probe.base.applyUser, probe.base.businessId, probe.base.applyDeptId, probe.base.result, probe.base.resultName, probe.base.comment],
    ['probe-a', BIZ.probe, 'D-SALES', null, null, null]);

  // ───────────────────────── 1. ≤3 天：组长通过即审批通过 ─────────────────────────

  console.log('\n[1] days=2（≤3 天）+ 组长通过 ⇒ 直接审批通过');

  let view = await startAndApply('alice', 2, '年假两天', BIZ.A);

  check('提交申请后停在「组长审批」', stops(view), ['task_leader_approve']);
  check('此时尚未产生审批结论', view.result, null);

  const approvalField = fieldOf(view, 'task_leader_approve', 'approvalResult');
  check('approvalResult 的选项 = 1通过 / 2拒绝 / 3驳回修改',
    approvalField?.options.map((o) => [o.id, o.name]),
    [['1', '通过'], ['2', '拒绝'], ['3', '驳回修改']]);
  check('approvalResult 的 variable = result', approvalField?.variable, 'result');
  check('approvalResult 为必填 enum', [approvalField?.type, approvalField?.required], ['enum', true]);
  check('组长审批表单 = approvalResult + comment（天数仍只由申请人填）',
    taskOf(view, 'task_leader_approve')?.form.map((f) => f.id), ['approvalResult', 'comment']);

  const idA = view.processInstanceId;

  view = await submit(view, 'task_leader_approve', { approvalResult: '1', comment: '同意，注意交接' });
  check('组长通过后实例结束', view.running, false);
  check('结束后无待办', stops(view), []);
  check('审批结论 Outcome = Approved', view.result?.outcome, 'Approved');
  check('结论由「审批通过」服务任务产生', view.result?.activityId, 'task_approve_complete');
  check('结论节点名 = 审批通过', view.result?.activityName, '审批通过');
  check('结论回写 days = 2', view.result?.days, 2);
  check('结论回写事由', view.result?.reason, '年假两天');

  // ── 结论里的 BaseModel ──
  check('结论回写申请人', view.result?.base.applyUser, 'alice');
  check('结论回写业务 Id', view.result?.base.businessId, BIZ.A);
  check('结论回写业务部门', view.result?.base.businessDeptId, 'D-SALES');
  check('结论回写申请部门', view.result?.base.applyDeptId, 'D-SALES');
  check('结论回写申请时间', /^\d{4}-\d{2}-\d{2}T/.test(String(view.result?.base.applyTime)), true);
  check('结论的审批结果 = 1 通过（含后端下发的中文名）',
    [view.result?.base.result, view.result?.base.resultName], [1, '通过']);
  check('结论的审批说明 = 组长填的那句话', view.result?.base.comment, '同意，注意交接');

  // 「≤3 天」的判定必须落在 days 上：同一路径下 days=4 必然走向经理
  // （见 [2]），这里只补一条「≤3 天没有经过经理审批」的正面证据
  const historicA = await get(`/api/leave/instances/${idA}`);
  check('结束后实例仍可查（业务数据兜住）', historicA.status, 200);
  check('结束后 Running = false', historicA.body.running, false);

  // ───────────────────────── 2. >3 天：经理通过后才通过 ─────────────────────────

  console.log('\n[2] days=5（>3 天）+ 组长通过 + 经理通过 ⇒ 审批通过');

  view = await startAndApply('alice', 5, '长途休假', BIZ.B);
  const idB = view.processInstanceId;

  view = await submit(view, 'task_leader_approve', { approvalResult: '1' });
  check('组长通过后流转到「经理审批」', stops(view), ['task_manager_approve']);
  check('经理审批前实例仍在运行', view.running, true);
  check('经理审批前无审批结论', view.result, null);
  check('经理审批前 days 已传递', view.variables.days, 5);

  const managerField = fieldOf(view, 'task_manager_approve', 'approvalResult');
  check('经理审批同样使用 approvalResult 选项',
    managerField?.options.map((o) => [o.id, o.name]),
    [['1', '通过'], ['2', '拒绝'], ['3', '驳回修改']]);
  check('经理审批候选组 = managers', taskOf(view, 'task_manager_approve')?.candidateGroups, ['managers']);

  view = await submit(view, 'task_manager_approve', { approvalResult: '1' });
  check('经理通过后实例结束', view.running, false);
  check('经理通过后 Outcome = Approved', view.result?.outcome, 'Approved');
  check('经理通过后由「审批通过」服务任务落结论', view.result?.activityId, 'task_approve_complete');
  check('B 与 A 是两个不同实例', idB === idA, false);

  // ───────────────────────── 3. 组长拒绝 ⇒ 拒绝结束 ─────────────────────────

  console.log('\n[3] 组长拒绝（result=2）⇒ 拒绝结束');

  view = await startAndApply('alice', 5, '想让领导批', BIZ.C);

  view = await submit(view, 'task_leader_approve', { approvalResult: '2' });
  check('拒绝后实例结束', view.running, false);
  check('拒绝后无待办', stops(view), []);
  check('拒绝后 Outcome = Refused', view.result?.outcome, 'Refused');
  check('拒绝结论由「拒绝结束」服务任务产生', view.result?.activityId, 'task_refuse');
  check('拒绝结论节点名 = 拒绝结束', view.result?.activityName, '拒绝结束');
  check('拒绝时 days 仍完整回写', view.result?.days, 5);
  check('拒绝时事由仍完整回写', view.result?.reason, '想让领导批');

  // ───────────────────────── 3b. 经理拒绝 ⇒ 同样走「拒绝结束」─────────────────────────

  console.log('\n[3b] days=5 + 组长通过 + 经理拒绝 ⇒ 拒绝结束（两条拒绝线共用同一服务任务）');

  view = await startAndApply('alice', 6, '经理说不', BIZ.G);
  view = await submit(view, 'task_leader_approve', { approvalResult: '1' });
  view = await submit(view, 'task_manager_approve', { approvalResult: '2' });
  check('经理拒绝后实例结束', view.running, false);
  check('经理拒绝后 Outcome = Refused', view.result?.outcome, 'Refused');
  check('经理拒绝也落在「拒绝结束」', view.result?.activityId, 'task_refuse');

  // ───────────────────────── 4. 驳回修改 ⇒ 申请人修改 ⇒ 重提 ⇒ 通过 ─────────────────────────

  console.log('\n[4] 组长驳回修改（result=3）⇒ 申请人修改 ⇒ 重提 ⇒ 组长通过 ⇒ 通过');

  view = await startAndApply('alice', 9, '写得不清', BIZ.D);

  view = await submit(view, 'task_leader_approve', { approvalResult: '3' });
  check('驳回修改后流转到「申请人修改」', stops(view), ['task_apply_modify']);
  check('驳回修改后实例仍在运行', view.running, true);
  check('驳回修改尚未产生审批结论', view.result, null);
  check('驳回修改后 days 没丢（仍为 9）', view.variables.days, 9);
  check('「申请人修改」的表单与申请一致（days/reason）',
    taskOf(view, 'task_apply_modify')?.form.map((f) => f.id).sort(), ['days', 'reason']);
  check('「申请人修改」指派回发起人', taskOf(view, 'task_apply_modify')?.assignee, 'alice');

  view = await submit(view, 'task_apply_modify', { days: 2, reason: '改成年假两天' });
  check('修改重提后回到「组长审批」', stops(view), ['task_leader_approve']);
  check('重提后 days 已更新为 2', view.variables.days, 2);
  check('重提后事由已更新', view.variables.reason, '改成年假两天');

  view = await submit(view, 'task_leader_approve', { approvalResult: '1' });
  check('二次通过（days=2）直接结束', view.running, false);
  check('二次通过 Outcome = Approved', view.result?.outcome, 'Approved');
  check('二次通过结论用修改后的天数', view.result?.days, 2);
  check('二次通过结论用修改后的事由', view.result?.reason, '改成年假两天');

  // ───────────────────────── 4b. 经理驳回修改 ⇒ 申请人修改 ─────────────────────────

  console.log('\n[4b] days=7 + 组长通过 + 经理驳回修改 ⇒ 回到「申请人修改」');

  view = await startAndApply('alice', 7, '经理让改', BIZ.H);
  view = await submit(view, 'task_leader_approve', { approvalResult: '1' });
  view = await submit(view, 'task_manager_approve', { approvalResult: '3' });
  check('经理驳回修改后回到「申请人修改」', stops(view), ['task_apply_modify']);
  check('经理驳回修改后仍在运行', view.running, true);
  check('经理驳回修改后无结论', view.result, null);

  view = await submit(view, 'task_apply_modify', { days: 3, reason: '压缩到三天' });
  check('改完仍回「组长审批」（与经理无关）', stops(view), ['task_leader_approve']);
  view = await submit(view, 'task_leader_approve', { approvalResult: '1' });
  check('days=3（≤3）直接通过，不再回经理', view.result?.activityId, 'task_approve_complete');
  check('days=3 结论天数 = 3', view.result?.days, 3);

  // ───────────────────────── 5. 表单校验（严格模式）─────────────────────────

  console.log('\n[5] 表单校验：非法枚举 / 缺必填 / 未声明字段都应被拒且不推进流程');

  view = await startAndApply('alice', 1, '校验用例', BIZ.E);
  const idE = view.processInstanceId;

  let bad = await submitRaw(view, 'task_leader_approve', { approvalResult: '9' });
  check('非法枚举值 -> 400', bad.status, 400);
  check('非法枚举列出可选项', /不是合法选项/.test((bad.body.message ?? '') + (bad.body.errors ?? []).join(' ')), true);

  bad = await submitRaw(view, 'task_leader_approve', {});
  check('缺必填 -> 400', bad.status, 400);
  check('缺必填点名字段', (bad.body.errors ?? []).some((e) => e.includes('approvalResult')), true);

  bad = await submitRaw(view, 'task_leader_approve', { approvalResult: '1', hacker: 'x' });
  check('未声明字段 -> 400', bad.status, 400);
  check('未声明字段被点出', (bad.body.errors ?? []).some((e) => e.includes('hacker')), true);

  bad = await post('/api/leave/instances', { applicant: '' });
  check('applicant 为空 -> 400', bad.status, 400);

  // 业务单号在业务表里是 bigint —— 带前缀的字符串必须被挡在入口，而不是等到写库才炸
  bad = await post('/api/leave/instances', { applicant: 'alice', businessKey: 'LV-2026-0001' });
  check('业务 Id 非数字 -> 400', bad.status, 400);
  check('业务 Id 提示要求正整数', /正整数/.test(bad.body.message ?? ''), true);

  // 留空则由 Demo 发号（真实系统里这是业务单号生成器的活，见 LeaveBusinessKey）
  const generated = await start('alice', '');
  check('业务 Id 留空 ⇒ 自动发一个正整数',
    /^\d+$/.test(String(generated.variables.businessId)), true);

  bad = await get('/api/leave/tasks');
  check('待办查询缺条件 -> 400', bad.status, 400);

  bad = await get('/api/leave/instances/not-a-real-instance');
  check('不存在的实例 -> 404', bad.status, 404);

  const afterBad = await get(`/api/leave/instances/${idE}`);
  check('三次失败提交后流程纹丝未动', stops(afterBad.body), ['task_leader_approve']);
  check('三次失败提交后无结论', afterBad.body.result, null);

  // 申请环节同样严格：days 必须是整数
  bad = await submitRaw(await start('alice', BIZ.E2), 'task_apply', { days: '两天' });
  check('days 非整数 -> 400', bad.status, 400);
  check('days 类型错误被点出', (bad.body.errors ?? []).some((e) => e.includes('days')), true);

  // ───────────────────────── 6. 待办中心（候选人 / 候选组）─────────────────────────

  console.log('\n[6] 待办中心：候选人 → 候选组 → 认领后按 assignee 查');

  view = await startAndApply('alice', 4, '待办查询', BIZ.F);
  const idF = view.processInstanceId;

  const leaderTasks = await get('/api/leave/tasks?candidateUser=leader1');
  check('leader1 能查到组长待办', leaderTasks.status, 200);
  check('leader1 的待办里有本实例',
    leaderTasks.body.some((t) => t.processInstanceId === idF && t.definitionKey === 'task_leader_approve'), true);
  check('组长待办候选组 = leaders',
    leaderTasks.body.find((t) => t.processInstanceId === idF)?.candidateGroups, ['leaders']);

  const managerBefore = await get('/api/leave/tasks?candidateUser=manager1');
  check('经理此刻查不到本实例（days=4 还没流转到经理）',
    managerBefore.body.some((t) => t.processInstanceId === idF), false);

  const done = await submit(view, 'task_leader_approve', { approvalResult: '1' });
  check('组长通过后流转到经理', stops(done), ['task_manager_approve']);

  const managerAfter = await get('/api/leave/tasks?candidateUser=manager1');
  check('经理现在能查到待办',
    managerAfter.body.some((t) => t.processInstanceId === idF && t.definitionKey === 'task_manager_approve'), true);

  const leaderAfter = await get('/api/leave/tasks?candidateUser=leader1');
  check('组长已办结，待办里不再有本实例',
    leaderAfter.body.some((t) => t.processInstanceId === idF), false);

  const managerView = await get(`/api/leave/instances/${idF}`);
  check('经理审批的 formKey = leave-approve', taskOf(managerView.body, 'task_manager_approve')?.formKey, 'leave-approve');

  // ───────────────────────── 7. 业务数据（透过接口核对）─────────────────────────

  console.log('\n[7] 业务数据：请假单登记 + 审批结论（与落点无关，两种宿主都该如此）');

  const list = await get('/api/leave/instances');
  check('GET /instances 返回 200', list.status, 200);

  const tracked = [BIZ.A, BIZ.B, BIZ.C, BIZ.D, BIZ.E, BIZ.F, BIZ.G, BIZ.H];
  check('8 张请假单都已登记（E 未结束也在册）',
    [...new Set(list.body.map((i) => i.businessKey).filter((key) => tracked.includes(key)))].sort(),
    [...tracked].sort());

  const summaryOf = (key) => list.body.find((i) => i.businessKey === key);

  check('列表 A 状态 = Approved', summaryOf(BIZ.A)?.status, 'Approved');
  check('列表 C 状态 = Refused', summaryOf(BIZ.C)?.status, 'Refused');
  check('列表 E 状态 = Running', summaryOf(BIZ.E)?.status, 'Running');

  // ⚠️ 列表里的天数 / 事由来自流程参数表 —— 它们是「提交申请」时才写进去的，
  //    所以未结束的 E 也应该已经有值（不是发起那一刻的快照）
  check('列表 A 的天数 / 事由 = 申请时填的值',
    [summaryOf(BIZ.A)?.days, summaryOf(BIZ.A)?.reason], [2, '年假两天']);
  check('列表 E（未结束）的天数 / 事由也已补全',
    [summaryOf(BIZ.E)?.days, summaryOf(BIZ.E)?.reason], [1, '校验用例']);

  // 每张单子的详情：审批结论 + BaseModel 都从业务数据还原
  const details = new Map();

  for (const key of tracked) {
    details.set(key, (await get(`/api/leave/instances/${summaryOf(key).processInstanceId}`)).body);
  }

  const resultOf = (key) => details.get(key)?.result;

  check('A(≤3天,组长通过) => Approved', resultOf(BIZ.A)?.outcome, 'Approved');
  check('B(>3天,经理通过) => Approved', resultOf(BIZ.B)?.outcome, 'Approved');
  check('C(组长拒绝) => Refused', resultOf(BIZ.C)?.outcome, 'Refused');
  check('D(组长驳回后改小再通过) => Approved', resultOf(BIZ.D)?.outcome, 'Approved');
  check('G(经理拒绝) => Refused', resultOf(BIZ.G)?.outcome, 'Refused');
  check('H(经理驳回后改小再通过) => Approved', resultOf(BIZ.H)?.outcome, 'Approved');
  check('E(校验用例) 未结束 => 无结论', resultOf(BIZ.E), null);
  check('F(待办查询) 未结束 => 无结论', resultOf(BIZ.F), null);

  check('D 的事由是修改后的值', resultOf(BIZ.D)?.reason, '改成年假两天');
  check('D 的天数是修改后的值', resultOf(BIZ.D)?.days, 2);
  check('H 的天数是修改后的值', resultOf(BIZ.H)?.days, 3);
  check('H 的事由是修改后的值', resultOf(BIZ.H)?.reason, '压缩到三天');

  // 「驳回修改」不是终态：不该在审批结论里留下任何痕迹
  const results = [...details.values()].map((d) => d.result).filter(Boolean);
  check('结论 Outcome 取值域 = {Approved, Refused}',
    [...new Set(results.map((r) => r.outcome))].sort(), ['Approved', 'Refused']);
  check('已产生 6 条结论（A/B/C/D/G/H 各一条，驳回修改不产生结论）', results.length, 6);

  // 结论的业务 Id 必须跟着请假单走（服务任务跑在子执行上，拿不到流程实例那一列的 BusinessKey，
  // 业务上下文改从 Demo 自己的登记里取 —— 见 LeaveDelegates）
  check('每条结论的业务 Id 都与请假单登记一致',
    [...details.entries()].every(([key, detail]) => !detail.result || detail.result.base.businessId === key),
    true);
  check('每条结论都带申请人', results.every((r) => r.base.applyUser === 'alice'), true);

  // ── BaseModel：业务数据里的 7 个字段 ──
  check('每张请假单都带齐发起时的字段',
    [...details.values()].every((d) => d.base.applyUser === 'alice'
      && d.base.applyDeptId === 'D-SALES'
      && d.base.businessDeptId === 'D-SALES'
      && /^\d{4}-\d{2}-\d{2}T/.test(String(d.base.applyTime))),
    true);
  check('A 的结论带审批结果 1 与审批说明',
    [resultOf(BIZ.A)?.base.result, resultOf(BIZ.A)?.base.comment],
    [1, '同意，注意交接']);
  check('C 的结论带审批结果 2，且没填说明',
    [resultOf(BIZ.C)?.base.result, resultOf(BIZ.C)?.base.comment],
    [2, null]);

  // ───────────────────────── 8. ParaDict ⇒ 流程参数表 ─────────────────────────

  console.log('\n[8] ParaDict：发起落表 / 表单字段入表 / 审批按 key 更新 / 展开为变量');

  // 8.1 发起时带参数
  view = await start('alice', BIZ.P, { paraDict: { city: 'SH', level: 3 } });
  const idP = view.processInstanceId;

  check('发起后参数表 = city + level', paramsOf(view), { city: 'SH', level: '3' });
  check('参数同时展开为流程变量（数字按原文收成字符串）',
    [view.variables.city, view.variables.level], ['SH', '3']);

  // 8.2 表单字段本身也是流程参数，提交时一并入表
  view = await submit(view, 'task_apply', { days: 4, reason: '参数用例' });
  check('提交申请后 days / reason 进了参数表',
    [view.parameters.days, view.parameters.reason], ['4', '参数用例']);
  check('原有参数没被这次提交冲掉',
    [view.parameters.city, view.parameters.level], ['SH', '3']);

  // 8.3 审批时更新参数：有值就按 key 更新，没提到的键保持原样
  view = await submit(view, 'task_leader_approve', { approvalResult: '1' }, { city: 'BJ', backupUser: 'bob' });
  check('按 key 更新：city 被改、backupUser 新增、level 原样',
    [view.parameters.city, view.parameters.backupUser, view.parameters.level], ['BJ', 'bob', '3']);
  check('参数更新同样写进流程变量',
    [view.variables.city, view.variables.backupUser], ['BJ', 'bob']);
  check('审批元数据不进参数表（result / comment 不在其中）',
    ['result', 'comment'].filter((name) => name in (view.parameters ?? {})), []);
  check('参数不影响流转（days=4 仍走到经理审批）', stops(view), ['task_manager_approve']);

  // 8.4 参数名与 BaseModel 字段冲突要被拒
  bad = await post('/api/leave/instances', {
    applicant: 'alice', businessKey: BIZ.P2, paraDict: { applyUser: 'x' },
  });
  check('发起时参数名撞 BaseModel -> 400', bad.status, 400);
  check('冲突提示点名参数名', /applyUser/.test(bad.body.message ?? ''), true);

  const fresh = await start('alice', BIZ.P3);
  bad = await submitRaw(fresh, 'task_apply', { days: 1 }, { result: '9' });
  check('提交时参数名撞 BaseModel -> 400', bad.status, 400);
  check('被拒后流程纹丝未动',
    stops((await get(`/api/leave/instances/${fresh.processInstanceId}`)).body), ['task_apply']);

  // 8.5 参数「落成什么样」是宿主说了算的（JSON 按行展开 / MySQL 落成业务单的列 + ext_params），
  //     所以这一条放到 [11] 里按落点分支断言 —— 这里只留一句提醒，免得以为漏测了。
  check('参数接口与落点无关（落点细节见 [11]）',
    [view.parameters.city, view.parameters.backupUser, view.parameters.level], ['BJ', 'bob', '3']);
  check('idP 供 [11] 核对落点', typeof idP === 'string' && idP.length > 0, true);

  // ───────────────────────── 9. 审批历史记录 ─────────────────────────

  console.log('\n[9] 审批历史：环节序列 + 审批记录（谁在什么时候批了什么）');

  // 9.1 已结束实例：A 是 ≤3 天、组长一级通过
  const historyA = await get(`/api/leave/instances/${idA}/history`);
  check('GET /instances/{id}/history 返回 200', historyA.status, 200);

  const hA = historyA.body;
  check('历史里的业务键 = 请假单的业务 Id', hA.businessKey, BIZ.A);
  check('已结束实例：running=false，带结束时间与历时',
    [hA.running, typeof hA.endedAt === 'string', hA.durationInMilliseconds > 0], [false, true, true]);

  // ⚠️ 环节序列有两种来源：引擎历史（ACT_HI_ACTINST，含网关 / 服务任务 / 事件）
  //    与「业务审批记录 + 当前待办」合成（只有人工环节）。所以断言环节序列时
  //    一律先筛出用户任务 —— 这是两种宿主都成立的口径。
  const userSteps = (history) => history.steps.filter((s) => s.activityType === 'userTask');

  check('环节序列 = 申请 → 组长审批（≤3 天不经经理）',
    userSteps(hA).map((s) => s.activityId), ['task_apply', 'task_leader_approve']);
  check('环节带名字与办理人', userSteps(hA).map((s) => [s.activityName, s.assignee]),
    [['提交请假申请', 'alice'], ['组长审批', null]]);
  check('审批环节带结论（名字取自 BPMN 的选项名）',
    [userSteps(hA)[1].decision, userSteps(hA)[1].decisionName, userSteps(hA)[1].comment],
    ['1', '通过', '同意，注意交接']);
  check('申请环节没有审批结论', [userSteps(hA)[0].decision, userSteps(hA)[0].decisionName], [null, null]);
  check('每个用户环节都有进出时间',
    userSteps(hA).every((s) => typeof s.startedAt === 'string' && typeof s.endedAt === 'string'), true);
  check('流程已结束 ⇒ 没有进行中的环节', userSteps(hA).filter((s) => s.isActive).length, 0);

  check('审批记录 = 申请 + 组长审批两条', hA.approvals.length, 2);
  check('审批记录按提交先后（最旧的在前）',
    hA.approvals[0].submittedAt <= hA.approvals[1].submittedAt, true);
  check('申请那条记下了「流转到组长审批」',
    hA.approvals[0].nextSteps.map((s) => s.activityId), ['task_leader_approve']);
  check('审批那条记下了选项与说明',
    [hA.approvals[1].decision, hA.approvals[1].decisionName, hA.approvals[1].values.comment],
    ['1', '通过', '同意，注意交接']);
  check('让流程结束的那条带结论、且没有下一环节',
    [hA.approvals[1].outcome, hA.approvals[1].nextSteps.length], ['Approved', 0]);
  check('历史里的审批结论与实例视图一致',
    [hA.result.outcome, hA.result.parameters.days, hA.result.activityName], ['Approved', '2', '审批通过']);

  // 9.2 已结束实例：B 走了两级审批（>3 天）
  const hB = (await get(`/api/leave/instances/${idB}/history`)).body;
  check('>3 天：环节序列含经理审批',
    userSteps(hB).map((s) => s.activityId), ['task_apply', 'task_leader_approve', 'task_manager_approve']);
  check('>3 天：两级审批都带结论', userSteps(hB).slice(1).map((s) => s.decisionName), ['通过', '通过']);
  check('>3 天：审批记录三条', hB.approvals.length, 3);

  // 9.3 进行中的实例：最后一个环节没有结束时间
  const hE = (await get(`/api/leave/instances/${idE}/history`)).body;
  check('进行中实例：running=true 且无结束时间', [hE.running, hE.endedAt], [true, null]);
  check('进行中实例：环节序列 = 申请 → 组长审批（停在这里）',
    userSteps(hE).map((s) => s.activityId), ['task_apply', 'task_leader_approve']);
  check('进行中实例：末环节 isActive = true',
    [userSteps(hE).at(-1).isActive, userSteps(hE).at(-1).endedAt], [true, null]);

  // 9.4 办理人：按候选组审批的任务本身没有 assignee，靠提交时的 operator 记住是谁批的
  let viewI = await startAndApply('alice', 2, '办理人记录', BIZ.I);
  viewI = await submit(viewI, 'task_leader_approve', { approvalResult: '1' }, undefined, { operator: 'leader1' });

  const hI = (await get(`/api/leave/instances/${viewI.processInstanceId}/history`)).body;
  check('提交时带 operator ⇒ 审批记录与环节都记住办理人',
    [hI.approvals[1].assignee, userSteps(hI)[1].assignee], ['leader1', 'leader1']);
  check('带办理人不影响业务数据落盘', hI.result.outcome, 'Approved');

  // 9.5 不存在的实例
  const missingHistory = await get('/api/leave/instances/not-a-real-instance/history');
  check('不存在的实例查历史 -> 404', missingHistory.status, 404);

  // ───────────────────────── 10. 演示页接线 ─────────────────────────

  console.log('\n[10] 演示页：审批历史面板确实接在应用层接口上');

  // 接口做好了但页面没接，是这类封装改造最容易漏的一环；这里从页面源码上兜一下。
  const page = await get('/');
  check('GET / 返回演示页', page.status, 200);
  check('页面有审批历史面板', page.body.includes('id="historyPanel"'), true);
  check('页面拉的正是 /instances/{id}/history',
    page.body.includes("+ '/history'"), true);
  check('页面消费后端下发的环节来源与下一步',
    [page.body.includes('stepsSource'), page.body.includes('nextSteps')], [true, true]);
  check('打开实例、发起、提交三处都会刷新历史',
    (page.body.match(/await loadHistory\(/g) ?? []).length, 3);

  // ───────────────────────── 11. 落库核对（按落点分支）─────────────────────────

  console.log('\n[11] 落库核对：直接看业务数据落在了哪儿');

  const inspector = await get('/api/leave/business');
  check('GET /business 返回 200', inspector.status, 200);

  const location = inspector.body.location ?? '';
  check('落点自述存在', location.length > 0, true);
  console.log(`     （当前落点：${location}）`);

  if (inspector.body.store === 'MySql') {
    // ── MySQL 宿主：四张业务表直接 dump 出来，断言的就是库里的行 ──
    const tables = ['instances', 'instance_current_task', 'deal_history', 'leave_bill'];
    check('dump 了四张业务表', tables.filter((name) => Array.isArray(inspector.body[name])), tables);

    // instances：实例登记（发起时建行，每推进一步刷 current_step / assign_emp）
    // 「本轮的号」= BIZ 里所有场景单 + 留空自动发的那一张；用号段过滤，
    // 这样连着跑也不会捡到上一轮的行（库里留着历史数据是正常的）
    const ours = new Set([...Object.values(BIZ), generated.variables.businessId]);
    const rows = inspector.body.instances.filter((row) => ours.has(String(row.businessId)));

    check('instances：本轮的 8 张请假单各一行',
      rows.filter((row) => tracked.includes(String(row.businessId))).length, 8);
    check('instances：申请人与部门都落进了表里',
      rows.every((row) => row.applyDeptId === 'D-SALES'
        // [0] 的探针单是 probe-a 发起的，其余都是 alice
        && row.applyEmpId === (String(row.businessId) === BIZ.probe ? 'probe-a' : 'alice')), true);

    const rowOf = (key) => rows.find((row) => String(row.businessId) === key);
    check('instances：business_id 是数字（bigint 列）', /^\d+$/.test(String(rowOf(BIZ.A)?.businessId)), true);
    check('instances：已结束的单子 status_id = 已完成',
      [rowOf(BIZ.A)?.statusId, rowOf(BIZ.C)?.statusId], ['已完成', '已完成']);
    check('instances：未结束的单子 status_id = 审批中', rowOf(BIZ.E)?.statusId, '审批中');
    check('instances：describe 是「申请人 · 请假 N 天 · 事由」',
      rowOf(BIZ.A)?.describe, 'alice · 请假 2 天 · 年假两天');
    check('instances：current_step 记下结束在哪个环节', rowOf(BIZ.A)?.currentStep, '审批通过');
    check('instances：已完成 ⇒ assign_emp 清空', rowOf(BIZ.A)?.assignEmp, null);
    check('instances：审批中 ⇒ assign_emp = 解析后的审批人（候选组已展开）',
      rowOf(BIZ.E)?.assignEmp, 'leader1');
    check('instances：workflow_instance_id 就是引擎实例 id',
      rowOf(BIZ.A)?.workflowInstanceId, idA);

    // deal_history：每次提交追加一条（驳回后重提不覆盖，历史全留住）
    const deals = inspector.body.deal_history
      .filter((row) => ours.has(String(row.businessId)));

    const dealsOf = (key) => deals
      .filter((row) => String(row.businessId) === key)
      // 快照按 deal_history_id 倒序（方便一眼看到最新一条），
      // 但断言要说的是「流水先后」—— 这里翻回升序
      .sort((a, b) => a.dealHistoryId - b.dealHistoryId);

    check('deal_history：A（申请 + 组长审批）两条', dealsOf(BIZ.A).length, 2);
    check('deal_history：B（申请 + 组长 + 经理）三条', dealsOf(BIZ.B).length, 3);
    check('deal_history：D 驳回后重提 → 追加而非覆盖',
      dealsOf(BIZ.D).map((row) => row.taskNode),
      ['提交请假申请', '组长审批', '申请人修改', '组长审批']);
    check('deal_history：记下审批结果与说明',
      [dealsOf(BIZ.A)[1].dealResult, dealsOf(BIZ.A)[1].dealComment], ['1', '同意，注意交接']);
    check('deal_history：申请环节还没有审批结果', dealsOf(BIZ.A)[0].dealResult, null);
    check('deal_history：记下办理人（operator 优先）', dealsOf(BIZ.I)[1].dealEmpId, 'leader1');
    check('deal_history：记下环节配置 form_key', dealsOf(BIZ.B)[2].formKey, 'leave-approve');

    // instance_current_task：流程每前进一步整体替换，实例结束就清空
    const appliesOf = new Map(rows.map((row) => [String(row.businessId), row.applyId]));
    const currentOf = (key) => inspector.body.instance_current_task
      .filter((row) => row.applyId === appliesOf.get(key));

    check('instance_current_task：已结束的单子没有待办', currentOf(BIZ.A).length, 0);
    check('instance_current_task：E 停在组长审批',
      currentOf(BIZ.E).map((row) => row.taskNode), ['组长审批']);
    check('instance_current_task：assign_emp_id 是解析后的审批人',
      currentOf(BIZ.E)[0]?.assignEmpId, 'leader1');
    check('instance_current_task：task_owner 留着流程定义里的候选组',
      currentOf(BIZ.E)[0]?.taskOwner, 'leaders');
    check('instance_current_task：task_id 能反查回引擎待办',
      currentOf(BIZ.E)[0]?.taskId,
      taskOf((await get(`/api/leave/instances/${idE}`)).body, 'task_leader_approve')?.taskId);
    check('instance_current_task：form_key 与实例一起落', currentOf(BIZ.F)[0]?.formKey, 'leave-approve');

    // leave_bill：days / reason 是真列，其余参数进 ext_params
    const bills = inspector.body.leave_bill.filter((row) => ours.has(String(row.businessId)));
    const billOf = (key) => bills.find((row) => String(row.businessId) === key);

    check('leave_bill：days / reason 落成真列',
      [billOf(BIZ.A)?.days, billOf(BIZ.A)?.reason], [2, '年假两天']);
    check('leave_bill：days 是数字（smallint 列，不是字符串）', typeof billOf(BIZ.A)?.days, 'number');
    check('leave_bill：驳回改小后的值已同步回来',
      [billOf(BIZ.D)?.days, billOf(BIZ.D)?.reason], [2, '改成年假两天']);
    check('leave_bill：扩展参数进 ext_params（JSON 列）',
      Object.fromEntries(Object.entries(JSON.parse(billOf(BIZ.P)?.extParams ?? '{}')).sort()),
      { backupUser: 'bob', city: 'BJ', level: '3' });
    check('leave_bill：审批元数据没被当成业务参数写进 ext_params',
      ['result', 'comment'].filter((name) => name in JSON.parse(billOf(BIZ.A)?.extParams ?? '{}')), []);

    // ── 宿主差异：MySQL 业务表没有的那两列 ──
    check('MySQL 宿主：deal_history 没有任务 id 列 ⇒ 审批记录的 taskId 是空串',
      hA.approvals.map((a) => a.taskId), ['', '']);
    check('MySQL 宿主：即便如此，环节序列照样认领得回审批记录（按环节名 + 先后顺序）',
      [userSteps(hA)[1].decision, userSteps(hA)[1].comment], ['1', '同意，注意交接']);
    check('MySQL 宿主：deal_history 没有表单快照列 ⇒ values 只还原得出 result / comment',
      hA.approvals[0].values, {});
    check('MySQL 宿主：环节序列来自引擎历史（ACT_HI_ACTINST）', hA.stepsSource, 'EngineHistory');
  } else {
    // ── JSON 宿主：五个区块 ──
    const blocks = ['instances', 'results', 'parameters', 'approvals', 'currentTasks'];
    check('落点是 JSON 文件', /^JSON 文件 · /.test(location), true);
    check('五个区块都在', blocks.filter((name) => Array.isArray(inspector.body[name])), blocks);

    const rows = inspector.body.instances.filter((i) => tracked.includes(i.base.businessId));
    check('8 张请假单都已登记', rows.length, 8);

    const paramRows = inspector.body.parameters.filter((p) => p.processInstanceId === idP);
    check('参数表按 实例×参数名 展开成行',
      paramRows.map((p) => [p.name, p.value]).sort(([a], [b]) => a.localeCompare(b)),
      [['backupUser', 'bob'], ['city', 'BJ'], ['days', '4'], ['level', '3'], ['reason', '参数用例']]);
    check('每行都带更新时间',
      paramRows.every((p) => /^\d{4}-\d{2}-\d{2}T/.test(String(p.updatedAt))), true);

    const currentRows = inspector.body.currentTasks.filter((t) => t.processInstanceId === idE);
    check('当前待办区块记下了 E 的组长审批', currentRows.map((t) => t.activityName), ['组长审批']);
    check('当前待办的审批人是解析后的（候选组已展开）', currentRows[0]?.approvers, ['leader1']);

    // ── 宿主差异：JSON 宿主想存什么就存什么 ──
    check('JSON 宿主：审批记录存下了完整表单值快照',
      hA.approvals[0].values, { days: '2', reason: '年假两天' });
    check('JSON 宿主：环节序列由业务审批记录合成（内存宿主没有 ACT_HI_*）',
      hA.stepsSource, 'BusinessApprovals');
  }

    console.log('\n[12] 监听器执行：执行监听器（start/take/end）+ 事件监听器（TASK_COMPLETED）');

  // 挂载点（leave-process.bpmn20.xml）：task_apply 的 start/end、flow_start_apply 的 take、
  // 流程级 activiti:eventListener（events=TASK_COMPLETED）。
  // 走一遍「≤3 天直接通过」：
  //   · 监听器已覆盖全部 4 个用户任务（start/end）与 15 条顺序流（take）：
  //     走一遍「≤3 天直接通过」跨 6 条连线、2 个用户任务 ⇒ start/end 各 +2、take +6；
  //   · 未订阅的事件（如 TASK_CREATED）不通知 ⇒ +0。计数来自 /api/leave/listener-stats。
  const statsBefore = (await get('/api/leave/listener-stats')).body;
  let viewL = await startAndApply('alice', 2, '监听器验证', BIZ.L);
  viewL = await submit(viewL, 'task_leader_approve', { approvalResult: '1', comment: '监听器验证' });
  const statsAfter = (await get('/api/leave/listener-stats')).body;

  const deltaOf = (key) => (statsAfter[key] ?? 0) - (statsBefore[key] ?? 0);
  check('执行监听器 start +2（task_apply / 组长审批进入）', deltaOf('executionStart'), 2);
  check('执行监听器 take +6（路径跨 6 条顺序流）', deltaOf('executionTake'), 6);
  check('执行监听器 end +2（task_apply / 组长审批离开）', deltaOf('executionEnd'), 2);
  check('事件监听器 TASK_COMPLETED +2（申请 + 组长各提交一次）', deltaOf('taskCompleted'), 2);
  check('未订阅的事件不通知（TASK_CREATED 不在 events 清单）', deltaOf('taskCreated'), 0);
  check('实例正常结束（监听器没有干扰流转）', viewL.running, false);
// ───────────────────────── 汇总 ─────────────────────────

  console.log(`\n${'─'.repeat(64)}`);
  if (failures.length === 0) {
    console.log(`全部通过：${passed} 项断言 ✅`);
  } else {
    console.log(`通过 ${passed} 项，失败 ${failures.length} 项 ❌`);
    for (const name of failures) {
      console.log(`  · ${name}`);
    }
  }
  console.log(`${'─'.repeat(64)}\n`);

  return failures.length === 0 ? 0 : 1;
}

run()
  .then((code) => globalThis.process.exit(code))
  .catch((error) => {
    console.error('\n测试脚本自身异常：', error);
    globalThis.process.exit(2);
  });

// ============================================================================
//  订单审批 Demo · 端到端验证脚本（Node >= 18，零依赖）
//
//  先起宿主再跑：
//    dotnet run --project samples/NetBpm.Demo.OrderApproval --urls http://localhost:5290
//    node samples/NetBpm.Demo.OrderApproval/tests/e2e-order.mjs
//
//  覆盖四条路径：
//    A. 提交申请 → 订单状态 = 1（审批中）
//    B. 经理审批 result=1 → order.ApproveComplete → 订单状态 = 100（审批通过）
//    C. 经理审批 result=3（驳回修改）→ 申请人修改重提 → 再批通过 → 100
//    D. 经理审批 result=2 → order.RefuseComplete → 订单状态 = -1（审批拒绝）
//
//  可以反复跑：每轮取新的订单号，断言只认本轮的号。
// ============================================================================

const BASE_URL = process.env.BASE_URL ?? 'http://localhost:5290';

let passed = 0;
const failures = [];

function check(label, condition, detail) {
  if (condition) {
    passed += 1;
    console.log(`  ok  ${label}`);
  } else {
    failures.push(label);
    console.error(`FAIL  ${label}${detail === undefined ? '' : ` —— ${JSON.stringify(detail)}`}`);
  }
}

async function api(path, options) {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
    body: options?.body === undefined ? undefined : JSON.stringify(options.body),
  });

  const text = await response.text();
  const json = text ? JSON.parse(text) : null;

  return { status: response.status, json };
}

// 订单视图上的待办 id：提交申请 / 每次提交后返回的 OrderBillView 带当前环节与首个待办
function expectTask(billView, activityName) {
  if (billView?.currentStep !== activityName || !billView?.taskId) {
    throw new Error(`订单 ${billView?.bill?.businessId} 没有停在「${activityName}」：${JSON.stringify(billView)}`);
  }
  return billView.taskId;
}

async function waitForHealth() {
  for (let attempt = 0; attempt < 30; attempt += 1) {
    try {
      const response = await fetch(`${BASE_URL}/health`);
      if (response.ok) return;
    } catch {
      // 宿主还没起来，继续等
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`宿主 ${BASE_URL} 没有就绪（先 dotnet run）。`);
}

async function main() {
  await waitForHealth();
  console.log(`\n订单审批 e2e —— ${BASE_URL}\n`);

  // ───────────────────────── 路径 A + B：提交申请 → 通过 ─────────────────────────
  console.log('[A/B] 提交申请 → 经理审批通过');

  const created = await api('/api/order/bills', {
    method: 'POST',
    body: {
      applicant: 'alice',
      productName: '测试服务器 R760',
      amount: 128000,
      remark: 'e2e 自动化下单',
    },
  });

  check('提交申请返回 201', created.status === 201, created);
  const approvedBill = created.json.bill;
  check('提交申请后订单状态 = 1（审批中）', approvedBill?.status === 1, approvedBill);
  check('订单带流程实例 id', typeof approvedBill?.processInstanceId === 'string' && approvedBill.processInstanceId.length > 0, approvedBill);
  check('首个环节 = 经理审批', created.json.currentStep === '经理审批', created.json);

  const approved = await api(`/api/order/tasks/${created.json.taskId}/submit`, {
    method: 'POST',
    body: { values: { approvalResult: '1', comment: '预算充足，同意' }, operator: 'manager1' },
  });
  check('审批提交成功', approved.status === 200, approved);

  const afterApprove = await api(`/api/order/bills/${approvedBill.businessId}`);
  check('审批通过后订单状态 = 100', afterApprove.json?.bill?.status === 100, afterApprove.json);
  check('状态名 = 审批通过', afterApprove.json?.bill?.statusName === '审批通过', afterApprove.json);
  check('审批意见已回写', afterApprove.json?.bill?.approveComment === '预算充足，同意', afterApprove.json);
  check('流程已结束（Running=false）', afterApprove.json?.running === false, afterApprove.json);
  check('审批结论 = Approved', afterApprove.json?.outcome === 'Approved', afterApprove.json);

  // ───────────────────────── 路径 C：驳回修改 → 重提 → 通过 ─────────────────────────
  console.log('\n[C] 驳回修改 → 申请人重提 → 通过');

  const billC = (await api('/api/order/bills', {
    method: 'POST',
    body: { applicant: 'bob', productName: '办公笔记本 ×20', amount: 160000 },
  })).json;

  await api(`/api/order/tasks/${expectTask(billC, '经理审批')}/submit`, {
    method: 'POST',
    body: { values: { approvalResult: '3', comment: '金额超了，改小一点' }, operator: 'manager1' },
  });

  const afterRefund = await api(`/api/order/bills/${billC.bill.businessId}`);
  check('驳回修改后订单仍是审批中（1）', afterRefund.json?.bill?.status === 1, afterRefund.json);
  check('当前环节回到申请人修改', afterRefund.json?.currentStep === '申请人修改', afterRefund.json);

  await api(`/api/order/tasks/${expectTask(afterRefund.json, '申请人修改')}/submit`, {
    method: 'POST',
    body: { values: { amount: 99000, remark: '已砍到 20 台 → 12 台' } },
  });

  const afterResubmit = await api(`/api/order/bills/${billC.bill.businessId}`);
  check('重提后回到经理审批', afterResubmit.json?.currentStep === '经理审批', afterResubmit.json);
  check('重提后订单仍是审批中（1）', afterResubmit.json?.bill?.status === 1, afterResubmit.json);

  await api(`/api/order/tasks/${expectTask(afterResubmit.json, '经理审批')}/submit`, {
    method: 'POST',
    body: { values: { approvalResult: '1', comment: '改完没问题了' }, operator: 'manager1' },
  });

  const afterApproveC = await api(`/api/order/bills/${billC.bill.businessId}`);
  check('重提审批后订单状态 = 100', afterApproveC.json?.bill?.status === 100, afterApproveC.json);

  // ───────────────────────── 路径 D：拒绝 ─────────────────────────
  console.log('\n[D] 经理审批拒绝');

  const billD = (await api('/api/order/bills', {
    method: 'POST',
    body: { applicant: 'alice', productName: '游戏显卡 H100', amount: 260000 },
  })).json;

  await api(`/api/order/tasks/${expectTask(billD, '经理审批')}/submit`, {
    method: 'POST',
    body: { values: { approvalResult: '2', comment: '与生产无关，拒绝' }, operator: 'manager1' },
  });

  const afterRefuse = await api(`/api/order/bills/${billD.bill.businessId}`);
  check('拒绝后订单状态 = -1', afterRefuse.json?.bill?.status === -1, afterRefuse.json);
  check('状态名 = 审批拒绝', afterRefuse.json?.bill?.statusName === '审批拒绝', afterRefuse.json);
  check('审批意见已回写', afterRefuse.json?.bill?.approveComment === '与生产无关，拒绝', afterRefuse.json);
  check('审批结论 = Refused', afterRefuse.json?.outcome === 'Refused', afterRefuse.json);

  // ───────────────────────── 审批历史（按订单反查） ─────────────────────────
  console.log('\n[历史] 按订单号反查审批历史');

  const history = await api(`/api/order/bills/${billD.bill.businessId}/history`);
  check('历史可查（200）', history.status === 200, history.status);
  check('历史记录了拒绝审批', JSON.stringify(history.json?.approvals ?? []).includes('与生产无关，拒绝'), history.json?.approvals);

  // ───────────────────────── 汇总 ─────────────────────────
  console.log(`\n通过 ${passed} 项断言${failures.length > 0 ? `，失败 ${failures.length} 项：\n  - ${failures.join('\n  - ')}` : '，全部通过 ✅'}\n`);

  if (failures.length > 0) {
    process.exitCode = 1;
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});

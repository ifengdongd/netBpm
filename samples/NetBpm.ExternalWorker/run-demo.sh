#!/usr/bin/env bash
#
# S4 端到端演示：一条含 serviceTask(activiti:topic) 的流程，在两个独立进程之间跑完。
#
#   platform ── (HTTP) ── worker
#     引擎这边没有开票代码          worker 这边有
#
# 不用 docker 也能跑：只要本机有 MySQL 和 dotnet。
#
#   ./samples/NetBpm.ExternalWorker/run-demo.sh
#
# 环境变量：
#   NETBPM_MYSQL_CONNECTION  流程库连接串（默认本机 root/空密码 的 netbpm 库）
#   NETBPM_PORT              platform 监听端口（默认 5199）
#   NETBPM_PY                解析 JSON 用的 python（默认 python）
#
set -uo pipefail

PY="${NETBPM_PY:-python}"
PORT="${NETBPM_PORT:-5199}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONN="${NETBPM_MYSQL_CONNECTION:-Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=;Allow User Variables=true;}"
BASE="http://127.0.0.1:${PORT}"
BPMN="${REPO_ROOT}/samples/NetBpm.ExternalWorker/processes/external-task-demo.bpmn20.xml"

PLATFORM_PID=""
WORKER_PID=""

cleanup() {
  [ -n "${WORKER_PID}" ] && kill "${WORKER_PID}" 2>/dev/null
  [ -n "${PLATFORM_PID}" ] && kill "${PLATFORM_PID}" 2>/dev/null
  wait 2>/dev/null
  echo
  echo "演示结束，两个进程已停止。"
}
trap cleanup EXIT

case "${1:-}" in
  -h|--help)
    sed -n '2,22p' "$0" | sed 's/^# \?//'
    exit 0
    ;;
esac

step() { printf '\n=== %s ===\n' "$1"; }

step "① 启动 platform（独立部署宿主）"
cd "${REPO_ROOT}/src/NetBpm.Platform.Service"
PORT="${PORT}" \
NetBpm__ConnectionString="${CONN}" \
NetBpm__Security__RequireCredential=false \
NetBpm__AutoMigrate=true \
NetBpm__EnableHistory=true \
  dotnet run --no-launch-profile > /tmp/netbpm-demo-platform.log 2>&1 &
PLATFORM_PID=$!

printf '等待探活'
for _ in $(seq 1 60); do
  if curl -fs "${BASE}/health" > /dev/null 2>&1; then break; fi
  printf '.'; sleep 1
done
echo
curl -fs "${BASE}/health"; echo
curl -fs "${BASE}/health/ready"; echo

step "② 部署演示流程（两个节点都是外部任务）"
if [ ! -f "${BPMN}" ]; then echo "找不到流程定义：${BPMN}" >&2; exit 1; fi
curl -fs -X POST "${BASE}/api/designer/deploy" \
  -H "Content-Type: text/xml" \
  --data-binary "@${BPMN}" | head -c 400; echo

step "③ 启动 worker（客户系统侧进程）"
cd "${REPO_ROOT}/samples/NetBpm.ExternalWorker"
NetBpm__BaseUrl="${BASE}" \
NetBpm__Topics="invoice,notify" \
NetBpm__PollIntervalSeconds=2 \
  dotnet run --no-launch-profile > /tmp/netbpm-demo-worker.log 2>&1 &
WORKER_PID=$!
sleep 3

step "④ 发起实例：amount=1000"
START_BODY='{"processDefinitionKey":"externalTaskDemo","businessKey":"DEMO-OK-1","variables":{"amount":1000}}'
INSTANCE_JSON="$(curl -fs -X POST "${BASE}/api/runtime/process-instances" \
  -H "Content-Type: application/json" -d "${START_BODY}")"
echo "${INSTANCE_JSON}"
# ⚠️ 响应体的字段名是 processInstanceId 不是 id。取错字段会得到空串，
# 后面 ?processInstanceId= 就退化成「查全部」，断言会拿到别人家的任务 —— 假通过。
INSTANCE_ID="$("${PY}" -c "import json,sys;d=json.load(sys.stdin);print(d.get('processInstanceId') or d.get('id') or '')" <<<"${INSTANCE_JSON}")"
[ -n "${INSTANCE_ID}" ] || { echo "无法从响应里取到实例 id：${INSTANCE_JSON}" >&2; exit 1; }

step "⑤ 等待 worker 拉走两个服务节点"
printf '等待'
for _ in $(seq 1 30); do
  TASKS="$(curl -fs "${BASE}/api/runtime/tasks?processInstanceId=${INSTANCE_ID}" 2>/dev/null || echo '[]')"
  COUNT="$("${PY}" -c "import json,sys;print(len(json.load(sys.stdin)))" <<<"${TASKS}" 2>/dev/null || echo 0)"
  [ "${COUNT}" -ge 1 ] && break
  printf '.'; sleep 1
done
echo
echo "当前待办：${TASKS}"

if [ "${COUNT:-0}" -lt 1 ]; then
  echo "❌ worker 没有把流程推过服务节点。查看 worker 日志：" >&2
  tail -20 /tmp/netbpm-demo-worker.log >&2
  exit 1
fi
echo "✅ 两个外部任务节点已被 worker 处理，流程停在人工复核。"

step "⑥ 演示失败 / 重试 / 死信（simulateFailure=true）"
FAIL_JSON="$(curl -fs -X POST "${BASE}/api/runtime/process-instances" \
  -H "Content-Type: application/json" \
  -d '{"processDefinitionKey":"externalTaskDemo","businessKey":"DEMO-FAIL-1","variables":{"amount":1000,"simulateFailure":true}}')"
FAIL_ID="$("${PY}" -c "import json,sys;d=json.load(sys.stdin);print(d.get('processInstanceId') or d.get('id') or '')" <<<"${FAIL_JSON}")"
echo "失败演示实例：${FAIL_ID}"

export FAIL_ID
printf '等待重试耗尽'
for _ in $(seq 1 40); do
  DEAD="$(curl -fs "${BASE}/api/runtime/external-tasks?status=DEAD_LETTER" 2>/dev/null || echo '[]')"
  # ⚠️ 必须按本次实例 id 过滤：这个接口返回的是全表死信，
  # 直接用第一条会拿到上一轮演示留下的行 —— 断言会「通过但没在验证自己那一条」。
  if "${PY}" -c "
import json,sys,os
items=json.load(sys.stdin)
fail=os.environ.get('FAIL_ID','')
sys.exit(0 if any(t.get('processInstanceId')==fail and t.get('status')=='DEAD_LETTER' for t in items) else 1)
" <<<"${DEAD}" 2>/dev/null; then
    break
  fi
  printf '.'; sleep 1
done
echo
DEAD_ID="$("${PY}" -c "
import json,sys,os
items=json.load(sys.stdin)
fail=os.environ.get('FAIL_ID','')
print(next((t['id'] for t in items if t.get('status')=='DEAD_LETTER' and t.get('processInstanceId')==fail), ''))
" <<<"${DEAD}" 2>/dev/null || echo '')"

if [ -z "${DEAD_ID}" ]; then
  echo "⚠️ 还没进死信（也许退避时间较长）。当前：${DEAD}"
else
  echo "死信任务：${DEAD_ID}"
  # 失败必须让流程停在开票节点：这里若查到复核待办，说明「失败也把流程推过去了」
  STUCK="$(curl -fs "${BASE}/api/runtime/tasks?processInstanceId=${FAIL_ID}" 2>/dev/null || echo '[]')"
  echo "失败实例 ${FAIL_ID} 的当前待办：${STUCK}"
  if "${PY}" -c "import json,sys;sys.exit(1 if json.load(sys.stdin) else 0)" <<<"${STUCK}" 2>/dev/null; then
    echo "✅ 流程停在原地（没有待办被推进出来）—— 失败不会跳过节点。"
  else
    echo "❌ 失败的流程竟然推进了 —— 这是外部任务最严重的错。"
  fi
  echo "→ 管理员重投："
  curl -fs -o /dev/null -w 'HTTP %{http_code}\n' -X POST \
    "${BASE}/api/runtime/external-tasks/${DEAD_ID}/retry" \
    -H "Content-Type: application/json" -d '{}'
fi

step "日志摘录"
echo "── worker ──"
grep -E "领到任务|完成|业务失败" /tmp/netbpm-demo-worker.log | tail -8
echo "── platform ──"
grep -E "回执失败|告警|No worker|external_task" /tmp/netbpm-demo-platform.log | tail -6

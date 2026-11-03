#!/usr/bin/env bash
#
# 从「独立发布目录」启动 NetBpm.ServerTaskWorker。
#
# 为什么需要这个脚本：
#   直接 dotnet run 时，可执行文件跑在 samples/NetBpm.ServerTaskWorker/bin/Debug/net10.0/
#   下面。IDE 里点「重新生成」时，MSBuild 要覆盖同一个可执行文件 —— 而文件正被
#   运行中的进程锁着，于是报 MSB3021 / MSB3027「文件正由另一进程使用」。
#   编译失败，错误列表里全是复制文件的红叉，看不出真实原因。
#
#   这里的做法：先 publish 到 artifacts/（已被 .gitignore 忽略），再从那里启动。
#   运行时与 IDE 的编译输出彻底分离，两者不再抢同一个文件。
#
# 用法：
#   ./samples/NetBpm.ServerTaskWorker/run-worker.sh
#
# 密钥来源（优先级从高到低）：
#   1) 已在当前 shell export 过 NETBPM_CLIENT_KEY
#   2) 用户机密：dotnet user-secrets set "Worker:ClientKey" "nbc.xxx.yyy" --project samples/NetBpm.ServerTaskWorker
#   3) 都没有 —— 降级启动：业务接口可用，但不领任务（见 README）
#
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="${REPO_ROOT}/samples/NetBpm.ServerTaskWorker/NetBpm.ServerTaskWorker.csproj"
PUBLISH_DIR="${REPO_ROOT}/artifacts/server-task-worker"

export NETBPM_BASE_URL="${NETBPM_BASE_URL:-http://localhost:5081}"
export WORKER_TOPICS="${WORKER_TOPICS:-approveComplete,refuseComplete}"
export PORT="${PORT:-3007}"

echo "[1/2] 发布到 ${PUBLISH_DIR}"
if ! dotnet publish "${PROJECT}" -c Debug -o "${PUBLISH_DIR}" --nologo; then
  echo "[启动失败] 发布未成功。" >&2
  exit 1
fi

echo "[2/2] 启动 worker"
echo "        引擎=${NETBPM_BASE_URL}  主题=${WORKER_TOPICS}  端口=${PORT}"
if [ -n "${NETBPM_CLIENT_KEY:-}" ]; then
  echo "        密钥=已配置（NETBPM_CLIENT_KEY）"
else
  echo "        密钥=未配置 —— 业务接口可用，但 worker 不会领任务"
fi

# 从发布目录启动：工作目录跟着走，单据文件落在发布目录下的 data/
cd "${PUBLISH_DIR}"
exec "${PUBLISH_DIR}/NetBpm.ServerTaskWorker.exe"

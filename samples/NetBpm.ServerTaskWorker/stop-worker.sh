#!/usr/bin/env bash
#
# 停止正在运行的 NetBpm.ServerTaskWorker。
#
# 为什么需要这个脚本：
#   worker 跑起来后会锁住自己的可执行文件，IDE 的「重新生成」就会报
#   MSB3021 / MSB3027「文件正由另一进程使用」。
#   光看错误列表认不出是哪条进程锁的 —— 这个脚本按端口找出来并停掉。
#
# 用法：
#   ./samples/NetBpm.ServerTaskWorker/stop-worker.sh [端口]
#   默认端口 3007。
#
set -uo pipefail

PORT="${1:-3007}"

echo "查找监听 ${PORT} 的进程…"

PIDS="$(netstat -ano 2>/dev/null | grep LISTENING | grep -E "[:.]${PORT}[[:space:]]" | awk '{print $NF}' | sort -u)"

if [ -z "${PIDS}" ]; then
  echo "端口 ${PORT} 上没有进程在监听，无需停止。"
  exit 0
fi

for pid in ${PIDS}; do
  echo "停止 PID ${pid} ..."
  kill "${pid}" 2>/dev/null || taskkill //PID "${pid}" //F >/dev/null 2>&1 || true
done

# 等一下端口真正释放，否则紧接着重新生成仍可能撞上文件锁
sleep 2

echo "已停止。端口 ${PORT} 现在可以重新生成并启动。"

#!/usr/bin/env bash
#
# 从「独立发布目录」启动 NetBpm 管理控制台。
#
# 为什么需要这个脚本：
#   直接 dotnet run 时，可执行文件跑在 src/NetBpm.Admin/bin/Debug/net10.0/ 下面。
#   IDE 里点「重新生成」时，MSBuild 要覆盖同一个可执行文件 —— 而文件正被运行中的
#   进程锁着，于是报 MSB3021 / MSB3027「文件正由另一进程使用」。
#   编译失败，错误列表里全是复制文件的红叉，看不出真实原因。
#
#   这里的做法：先 publish 到 artifacts/（已被 .gitignore 忽略），再从那里启动。
#   运行时与 IDE 的编译输出彻底分离，两者不再抢同一个文件。
#
# 用法：
#   ./src/NetBpm.Admin/run-admin.sh [端口]
#   默认端口 5081（外部任务 worker 依赖这个端口）。
#
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="${REPO_ROOT}/src/NetBpm.Admin/NetBpm.Admin.csproj"
PUBLISH_DIR="${REPO_ROOT}/artifacts/admin-console"

PORT="${1:-5081}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="http://localhost:${PORT}"

echo "[1/2] 发布到 ${PUBLISH_DIR}"
if ! dotnet publish "${PROJECT}" -c Debug -o "${PUBLISH_DIR}" --nologo; then
  echo "[启动失败] 发布未成功。" >&2
  exit 1
fi

echo "[2/2] 启动控制台"
echo "        地址=http://localhost:${PORT}  环境=${ASPNETCORE_ENVIRONMENT}"

# 从发布目录启动：工作目录跟着走（相对路径的配置、wwwroot 都在发布目录里）
cd "${PUBLISH_DIR}"
exec "${PUBLISH_DIR}/NetBpm.Admin"

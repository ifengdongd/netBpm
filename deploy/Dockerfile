# syntax=docker/dockerfile:1
#
# NetBpm 独立部署镜像（S4）。
#
# 两份服务共用这一个 Dockerfile，用 --build-arg PROJECT= 选择要构建的宿主：
#   docker build -f deploy/Dockerfile --build-arg PROJECT=src/NetBpm.Platform.Service/NetBpm.Platform.Service.csproj -t netbpm/platform .
#   docker build -f deploy/Dockerfile --build-arg PROJECT=samples/NetBpm.ExternalWorker/NetBpm.ExternalWorker.csproj    -t netbpm/worker   .
#
# worker 与平台共用同一个基础层，但它们是【两个进程 / 两个容器】：
# 外部任务的意义就在于 worker 由客户在自己那边建设和发布。

ARG DOTNET_VERSION=10.0
ARG PROJECT=src/NetBpm.Platform.Service/NetBpm.Platform.Service.csproj

# ─────────────────────────── 构建阶段 ───────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# 先只拿工程与包管理文件还原：源码没变时这一层命中缓存，CI 上省下的时间最可观。
# ⚠️ 用中央包管理（CPM）时必须带上 Directory.Packages.props，否则 NU1008。
COPY global.json NuGet.config Directory.Build.props Directory.Packages.props BannedSymbols.txt ./
COPY src/ src/
COPY samples/ samples/

RUN dotnet restore "${PROJECT}"

COPY . .
RUN dotnet publish "${PROJECT}" \
      -c Release \
      -o /app/publish \
      --no-restore \
      -p:UseAppHost=false

# ─────────────────────────── 运行阶段 ───────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS runtime
WORKDIR /app

# tini 之外的必要性不大，但需要 sh / TCP；aspnet alpine 镜像自带 busybox，够 healthcheck 用
COPY --from=build /app/publish ./

# 入口：exec form 不支持变量，而 shell form 会让 sh 变成 PID 1（SIGTERM 转发不到 dotnet，
# 容器就无法优雅退出）。因此用一个两行的 entrypoint 脚本：exec 保证信号直达应用进程。
ARG APP_DLL=NetBpm.Platform.Service.dll
ENV APP_DLL=${APP_DLL}

RUN printf '#!/bin/sh\nexec dotnet "$APP_DLL"\n' > /entrypoint.sh \
    && chmod +x /entrypoint.sh

# 非 root 运行：镜像里预置了 app 用户（uid 1654）。
# 用 root 跑 Web 服务意味着任何 RCE 都直接拿到容器内的全部权限 —— 这不是「多一层保险」，是及格线。
USER app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

# healthcheck 刻意打 /health 而不是 /health/ready：
# ready 会去连数据库，数据库一抖动编排就会反复重启一个本来健康的实例。
# 「进程还在但库连不上」该由告警系统管，不该让编排把它杀掉。
HEALTHCHECK --interval=15s --timeout=3s --start-period=25s --retries=3 \
  CMD ["wget", "-qO-", "http://127.0.0.1:8080/health"]

ENTRYPOINT ["/entrypoint.sh"]

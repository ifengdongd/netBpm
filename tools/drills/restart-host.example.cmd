@echo off
rem 崩溃恢复演练用的「拉起宿主」示例：脚本 -StartCommand 指向本文件即可。
rem 生产里这一步由 systemd / K8s 完成；本地演练用它让整条命令自包含。

set "NetBpm__ConnectionString=Server=127.0.0.1;Database=netbpm;User ID=root;Password=;Allow User Variables=true;"
set "NetBpm__EnableOutbox=true"
set "NetBpm__UseInMemoryExecutionTree=false"

cd /d E:\DataCode\NetBpm
dotnet run --project src/NetBpm.Rest --no-build -- --urls http://127.0.0.1:5199

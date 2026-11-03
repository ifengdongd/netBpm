#Requires -Version 5.1
<#
.SYNOPSIS
    停止正在运行的 NetBpm.ServerTaskWorker。

.DESCRIPTION
    为什么需要这个脚本：
    worker 跑起来后会锁住自己的 exe，IDE 的「重新生成」就会报
    MSB3021 / MSB3027「文件正由另一进程使用」。
    光看错误列表认不出是哪条进程锁的 —— 这个脚本按端口找出来并停掉。

.PARAMETER Port
    worker 监听端口，默认 3007。

.EXAMPLE
    .\samples\NetBpm.ServerTaskWorker\stop-worker.ps1

.EXAMPLE
    .\samples\NetBpm.ServerTaskWorker\stop-worker.ps1 -Port 3017
#>
[CmdletBinding()]
param(
    [int] $Port = 3007
)

$ErrorActionPreference = 'Stop'

Write-Host "查找监听 $Port 的进程…"

# 只认「真正在监听」的连接。ESTABLISHED / TIME_WAIT 里也可能带端口号，
# 误杀会连累别的进程。
$connections = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue

if (-not $connections) {
    Write-Host "端口 $Port 上没有进程在监听，无需停止。"
    exit 0
}

$pids = $connections | Select-Object -ExpandProperty OwningProcess -Unique

foreach ($processId in $pids) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue

    if (-not $process) {
        continue
    }

    Write-Host "停止 PID $processId ($($process.ProcessName)) …"

    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
}

# 等一下端口真正释放，否则紧接着重新生成仍可能撞上文件锁
Start-Sleep -Seconds 2

$stillThere = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue

if ($stillThere) {
    Write-Host "端口 $Port 仍被占用，可能有别的进程。请手工确认。" -ForegroundColor Yellow
    exit 1
}

Write-Host "已停止。端口 $Port 现在可以重新生成并启动。"

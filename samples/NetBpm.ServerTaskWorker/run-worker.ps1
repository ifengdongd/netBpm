#Requires -Version 5.1
<#
.SYNOPSIS
    从「独立发布目录」启动 NetBpm.ServerTaskWorker。

.DESCRIPTION
    为什么需要这个脚本：
    直接 dotnet run 时，可执行文件跑在 samples\NetBpm.ServerTaskWorker\bin\Debug\net10.0\
    下面。IDE 里点「重新生成」时，MSBuild 要覆盖同一个 exe —— 而文件正被运行中的进程
    锁着，于是报 MSB3021 / MSB3027「文件正由另一进程使用」。
    编译失败，错误列表里全是复制文件的红叉，看不出真实原因。

    这里的做法：先 publish 到 artifacts\（已被 .gitignore 忽略），再从那里启动。
    运行时与 IDE 的编译输出彻底分离，两者不再抢同一个文件。

.PARAMETER ClientKey
    客户端密钥（nbc.{clientId}.{secret} 整串）。省略时依次尝试：
    环境变量 NETBPM_CLIENT_KEY → 用户机密 Worker:ClientKey → 都没有则降级启动。

.EXAMPLE
    .\samples\NetBpm.ServerTaskWorker\run-worker.ps1

.EXAMPLE
    .\samples\NetBpm.ServerTaskWorker\run-worker.ps1 -ClientKey 'nbc.cl_xxx.yyy'
#>
[CmdletBinding()]
param(
    [string] $ClientKey,
    [string] $BaseUrl = 'http://localhost:5081',
    [string] $Topics = 'approveComplete,refuseComplete',
    [int]    $Port = 3007,
    [string] $PublishDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project  = Join-Path $repoRoot 'samples\NetBpm.ServerTaskWorker\NetBpm.ServerTaskWorker.csproj'

if (-not $PublishDir) {
    $PublishDir = Join-Path $repoRoot 'artifacts\server-task-worker'
}

# 密钥来源优先级：显式参数 > 环境变量 > 用户机密 > 降级
if (-not $ClientKey) { $ClientKey = $env:NETBPM_CLIENT_KEY }

if (-not $ClientKey) {
    $secrets = & dotnet user-secrets list --project $project 2>$null
    $line = $secrets | Where-Object { $_ -like 'Worker:ClientKey = *' } | Select-Object -First 1

    if ($line) {
        $ClientKey = ($line -split '=', 2)[1].Trim()
    }
}

Write-Host '[1/2] 发布到' $PublishDir

& dotnet publish $project -c Debug -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host '[启动失败] 发布未成功。' -ForegroundColor Red
    exit 1
}

Write-Host '[2/2] 启动 worker'
Write-Host ('       引擎=' + $BaseUrl + '  主题=' + $Topics + '  端口=' + $Port)

if ($ClientKey) {
    Write-Host '       密钥=已配置'
} else {
    Write-Host '       密钥=未配置 —— 业务接口可用，但 worker 不会领任务' -ForegroundColor Yellow
}

# 环境变量：与原 Node 版同名，运维脚本不用改
$env:NETBPM_BASE_URL = $BaseUrl
$env:WORKER_TOPICS   = $Topics
$env:PORT            = "$Port"

if ($ClientKey) {
    $env:NETBPM_CLIENT_KEY = $ClientKey
}

# 从发布目录启动：工作目录跟着走，单据文件落在发布目录下的 data\
Push-Location $PublishDir

try {
    & (Join-Path $PublishDir 'NetBpm.ServerTaskWorker.exe')
}
finally {
    Pop-Location
}

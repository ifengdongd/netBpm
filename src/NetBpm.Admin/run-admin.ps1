#Requires -Version 5.1
<#
.SYNOPSIS
    从「独立发布目录」启动 NetBpm 管理控制台。

.DESCRIPTION
    为什么需要这个脚本：
    直接 dotnet run（或在 IDE 里点运行）时，可执行文件跑在 src\NetBpm.Admin\bin\Debug\net10.0\
    下面。IDE 里点「重新生成」时，MSBuild 要覆盖同一个 exe —— 而文件正被运行中的进程
    锁着，于是报 MSB3021 / MSB3027「文件正由另一进程使用」。
    编译失败，错误列表里全是复制文件的红叉，看不出真实原因。

    这里的做法：先 publish 到 artifacts\（已被 .gitignore 忽略），再从那里启动。
    运行时与 IDE 的编译输出彻底分离，两者不再抢同一个文件。
    控制台常驻时也能随时重新生成。

    配套：stop-admin.ps1 按端口停掉运行中的控制台。

.PARAMETER Port
    控制台监听端口，默认 5081（外部任务 worker 依赖这个端口）。

.PARAMETER Environment
    ASPNETCORE_ENVIRONMENT，默认 Development（开发配置里写死了 MySQL 连接串与 dev-only JWT 密钥）。

.PARAMETER PublishDir
    发布目录，默认 <仓库>\artifacts\admin-console。

.EXAMPLE
    .\src\NetBpm.Admin\run-admin.ps1

.EXAMPLE
    .\src\NetBpm.Admin\run-admin.ps1 -Port 5081
#>
[CmdletBinding()]
param(
    [int]    $Port = 5081,
    [string] $Environment = 'Development',
    [string] $PublishDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project  = Join-Path $repoRoot 'src\NetBpm.Admin\NetBpm.Admin.csproj'

if (-not $PublishDir) {
    $PublishDir = Join-Path $repoRoot 'artifacts\admin-console'
}

Write-Host '[1/2] 发布到' $PublishDir

& dotnet publish $project -c Debug -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host '[启动失败] 发布未成功。' -ForegroundColor Red
    exit 1
}

Write-Host '[2/2] 启动控制台'
Write-Host ('       地址=http://localhost:' + $Port + '  环境=' + $Environment)

$env:ASPNETCORE_ENVIRONMENT = $Environment
$env:ASPNETCORE_URLS       = "http://localhost:$Port"

# 从发布目录启动：工作目录跟着走（相对路径的配置、wwwroot 都在发布目录里）
Push-Location $PublishDir

try {
    & (Join-Path $PublishDir 'NetBpm.Admin.exe')
}
finally {
    Pop-Location
}

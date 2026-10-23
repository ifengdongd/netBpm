# ============================================================================
#  NetBpm 管理控制台打包：前端构建（designer + admin）→ dotnet publish → 发行 zip
#  用法：powershell -File tools\pack-admin.ps1 [-Configuration Release] [-SkipFrontend]
#  产物：artifacts\netbpm-admin-<ver>\       发布目录（app\ 为 publish 输出，含 wwwroot）
#        artifacts\netbpm-admin-<ver>.zip    可部署的完整管理控制台（需 .NET 10 运行时）
#  说明：与 tools\pack.ps1（引擎库打包）互补；两者版本号需保持一致。
# ============================================================================
param(
    [string]$Configuration = "Release",
    [switch]$SkipFrontend
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 与 tools\pack.ps1 的 $version 保持一致
$version = "0.1.3"

$adminProject = Join-Path $root "src\NetBpm.Admin\NetBpm.Admin.csproj"
$distDir = Join-Path $root "artifacts\netbpm-admin-$version"
$appDir = Join-Path $distDir "app"

# ① 前端构建（designer → wwwroot/designer，admin → wwwroot）
if (-not $SkipFrontend) {
    $npm = Get-Command npm -ErrorAction SilentlyContinue

    if ($null -eq $npm) {
        Write-Warning "未检测到 npm，跳过前端构建；将直接使用仓库内已有的 src\NetBpm.Admin\wwwroot 产物。"
        Write-Warning "如需重新构建前端，请安装 Node.js 后去掉 -SkipFrontend 再运行。"
    }
    else {
        $webRoot = Join-Path $root "web"

        foreach ($name in @("designer", "admin")) {
            $projDir = Join-Path $webRoot $name

            Write-Host "== ① 前端构建（$name）==" -ForegroundColor Cyan
            Push-Location $projDir
            try {
                if (-not (Test-Path (Join-Path $projDir "node_modules"))) {
                    Write-Host "npm install"
                    npm install
                    if ($LASTEXITCODE -ne 0) { throw "$name npm install 失败" }
                }

                Write-Host "npm run build"
                npm run build
                if ($LASTEXITCODE -ne 0) { throw "$name npm run build 失败" }
            }
            finally {
                Pop-Location
            }
        }
    }
}

# ② dotnet publish（Web SDK 会把 wwwroot\ 作为静态 Web 资产一并带入 publish 输出）
Write-Host "== ② dotnet publish（$Configuration）==" -ForegroundColor Cyan
if (Test-Path $distDir) { Remove-Item -Recurse -Force $distDir }
New-Item -ItemType Directory -Force $distDir | Out-Null

dotnet publish $adminProject -c $Configuration -o $appDir --nologo
if ($LASTEXITCODE -ne 0) { throw "publish NetBpm.Admin 失败" }

# ③ 部署说明（自含，不依赖仓库）
$readme = @"
NetBpm 管理控制台 · 发行包（$version）

一、内容
  app\  NetBpm.Admin 宿主（net10.0 framework-dependent 发布），含：
        - wwwroot\          管理控制台前端（React + Ant Design）
        - wwwroot\designer\ 流程设计器（bpmn-js）
        - appsettings*.json 配置（DatabaseType=MySql；ConnectionString 为空，需自行填写）

二、运行
  1. 安装 .NET 10 运行时（dotnet --list-runtimes 确认）。
  2. 修改 app\appsettings.json：
       - DatabaseType: MySql / SqlServer
       - ConnectionString 指向你的库（默认空字符串，启动前必须填写）
       - AutoMigrate: true 会自动建库建表（含 bpmn_model / workflow_type / ACT_* 等；默认 false）
     （鉴权可选：NetBpm:Security:ApiKey 或 NetBpm:Security:Jwt:SigningKey 任一非空即开启）
  3. 启动：dotnet app\NetBpm.Admin.dll --urls http://0.0.0.0:5081
     访问：http://localhost:5081 （管理控制台；开启鉴权时首次请求会自动跳登录页）

三、说明
  - 本包已内置前端构建产物，运行无需 Node/npm。
  - 管理控制台需要真实数据库（内存模式不支持模型 / 流程类型存储）。
  - 引擎库（NuGet / DLL / SQL 脚本）请用 tools\pack.ps1 单独打包。
"@
Set-Content -Path (Join-Path $distDir "README.txt") -Value $readme -Encoding UTF8

# ④ 压缩发行包
Write-Host "== ③ 压缩发行包 ==" -ForegroundColor Cyan
$zip = Join-Path $root "artifacts\netbpm-admin-$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $zip -Force

Write-Host ""
Write-Host "完成：$zip" -ForegroundColor Green
Get-ChildItem $appDir | Select-Object Name | Format-Table

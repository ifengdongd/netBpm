# ============================================================================
#  NetBpm 引擎打包：NuGet 包（9 个引擎库）+ 发行 zip（DLL / nupkg / SQL 脚本）
#  用法：powershell -File tools\pack.ps1 [-Configuration Release]
#  产物：artifacts\nuget\*.nupkg        本地 NuGet 源可直接引用
#        artifacts\netbpm-<ver>.zip     DLL + nupkg + 两方言 SQL 脚本
# ============================================================================
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "0.1.0"
$nugetDir = Join-Path $root "artifacts\nuget"
$distDir = Join-Path $root "artifacts\netbpm-$version"

# 引擎库（依赖序）：NetBpm.Rest 是独立宿主，不参与打包
$projects = @(
    "NetBpm.El",
    "NetBpm.Bpmn.Model",
    "NetBpm.Bpmn.Validation",
    "NetBpm.Bpmn.Converter",
    "NetBpm.Identity",
    "NetBpm.Engine",
    "NetBpm.Persistence",
    "NetBpm.Runtime",
    "NetBpm.Workflow"
)

Write-Host "== ① dotnet pack（$Configuration）==" -ForegroundColor Cyan
foreach ($name in $projects) {
    $project = Join-Path $root "src\$name\$name.csproj"
    Write-Host "pack $name"
    dotnet pack $project -c $Configuration -o $nugetDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "pack $name 失败" }
}

Write-Host "== ② 汇总发行目录 ==" -ForegroundColor Cyan
if (Test-Path $distDir) { Remove-Item -Recurse -Force $distDir }
New-Item -ItemType Directory -Force (Join-Path $distDir "db\mysql") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $distDir "db\sqlserver") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $distDir "nuget") | Out-Null

Copy-Item (Join-Path $nugetDir "*.nupkg") (Join-Path $distDir "nuget")
Copy-Item (Join-Path $root "db\mysql\*.sql") (Join-Path $distDir "db\mysql")
Copy-Item (Join-Path $root "db\sqlserver\*.sql") (Join-Path $distDir "db\sqlserver")

# 打包内说明（自含，不依赖仓库）
$distReadme = @"
NetBpm 流程引擎 · 发行包（$version）

一、内容
  nuget\   9 个引擎 NuGet 包（NetBpm.Workflow 为接入入口，其余为依赖链）
  db\      两方言建表脚本（引擎基础表 + 流程实例业务表 + 流程类型表 + Demo 业务表）

二、其它 .NET 项目接入步骤
  1. 建库并执行 db 脚本（必跑 0001 → 0002 → 0003 → 0008；其余按需）：
     mysql  : mysql -u<user> -p <db> < db\mysql\0001_create_schema.sql （逐个执行）
     sqlserver: 逐个执行 db\sqlserver\000x_*.sql（wf schema）
  2. 引用 NuGet 包（任选其一）：
     - 本地源：dotnet nuget add source "<本目录绝对路径>\nuget" -n netbpm
     - 直接引用：把 nuget\*.nupkg 拷到项目内，在 csproj 加
       <ItemGroup><PackageReference Include="NetBpm.Workflow" Version="$version" /></ItemGroup>
       并在项目文件或 nuget.config 里声明本地源。
  3. 组合根注册（程序包管理器控制台或 Program.cs）：
       builder.Services.AddNetBpm(builder.Configuration);        // 引擎（连接串见 appsettings）
       builder.Services.AddNetBpmWorkflow<MySqlWorkflowDataStore>(); // 业务数据端口（自实现或用样版）
     启动即部署：WorkflowProcessBootstrapper.DeployAsync(...) 把 BPMN 灌进引擎。
  4. 流程设计器（画流程导出 BPMN 放进 processes/ 即可被部署）：
     http://localhost:5273/?modelId=<模型 id>（源码 web/designer，npm run dev 启动）

三、SQL 脚本分层
  0001/0002 引擎基础表；0003 流程实例业务表（workflow_type_id 存流程类型 id）；
  0008 流程类型表；0004/0005/0006/0007 为 Demo 业务表，按需取舍。
"@
Set-Content -Path (Join-Path $distDir "README.txt") -Value $distReadme -Encoding UTF8

Write-Host "== ③ 压缩发行包 ==" -ForegroundColor Cyan
$zip = Join-Path $root "artifacts\netbpm-$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $zip -Force

Write-Host ""
Write-Host "完成：$zip" -ForegroundColor Green
Get-ChildItem $nugetDir -Filter *.nupkg | Select-Object Name, Length | Format-Table

<#
.SYNOPSIS
    把当前仓库镜像到 GitHub，让 `.github/workflows/ci.yml` 真正跑起来。

.DESCRIPTION
    为什么需要这一步：仓库主远端是 Gitee，而流水线定义只有 GitHub Actions
    （`.github/workflows/ci.yml`，三个 Job：frontend / build-and-unit-tests /
    contract-governance / integration-tests）。Gitee 上没有 `.workflow/`，
    也就没有任何流水线会执行 —— CI 不是「绿」或「红」，是「从来没跑过」。

    脚本只做"安全镜像"：
      * 不 force、不用 `--mirror`（那会顺手删掉 GitHub 侧多出来的分支）；
      * remote 已存在且地址不同 → 直接报错退出，不擅自改指向；
      * 可重复执行（幂等）。

.PARAMETER RepositoryUrl
    目标仓库地址，必须是 GitHub（https 或 ssh 均可）。

.PARAMETER RemoteName
    远端名，默认 `github`。

.EXAMPLE
    ./tools/ci/mirror-to-github.ps1 -RepositoryUrl https://github.com/<user>/net-bpmn.git

.NOTES
    前置条件（脚本不代劳，因为需要仓库属主的账号）：
      1. GitHub 上已建好**空仓库**（不要勾选 README / .gitignore / license）；
      2. 本机有可用凭据：`git config --global credential.helper manager`（HTTPS），
         或把公钥加到 GitHub 账号（SSH）。
    推送成功后：仓库 Settings → Actions → General 确认 workflow 已启用；
    首次 push 会自动触发 ci.yml。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryUrl,

    [string]$RemoteName = 'github'
)

$ErrorActionPreference = 'Stop'

if ($RepositoryUrl -match 'gitee\.com') {
    throw "目标是 Gitee 地址（$RepositoryUrl）。本脚本只用于镜像到 GitHub；主远端请继续用 origin。"
}

$isHttps = $RepositoryUrl -match '^https://github\.com/[\w.-]+/[\w.-]+?(\.git)?/?$'
$isSsh = $RepositoryUrl -match '^git@github\.com:[\w.-]+/[\w.-]+?(\.git)?$'

if (-not ($isHttps -or $isSsh)) {
    throw "看起来不像 GitHub 仓库地址：$RepositoryUrl（期望 https://github.com/<owner>/<repo>.git 或 git@github.com:<owner>/<repo>.git）。"
}

# remote 幂等：已存在则只核对地址，绝不擅自改指向
$existing = (& git remote get-url $RemoteName 2>$null)
$existingExit = $LASTEXITCODE

if ($existingExit -ne 0) {
    & git remote add $RemoteName $RepositoryUrl
    if ($LASTEXITCODE -ne 0) { throw "添加远端 $RemoteName 失败。" }
    Write-Host "[mirror] 已添加远端 $RemoteName -> $RepositoryUrl"
}
elseif ($existing.Trim() -ne $RepositoryUrl.Trim()) {
    throw "远端 $RemoteName 已指向 $existing（与本次目标 $RepositoryUrl 不同）。请先手动确认并调整，脚本不会覆盖。"
}
else {
    Write-Host "[mirror] 远端 $RemoteName 已存在且指向正确，继续推送。"
}

# 推全部分支与标签（不带 --force：镜像的目的是"让 CI 跑"，不是强推覆盖别人的历史）
& git push $RemoteName --all
if ($LASTEXITCODE -ne 0) { throw "推送分支失败（检查凭据 / 仓库是否存在 / 是否有写权限）。" }

& git push $RemoteName --tags
if ($LASTEXITCODE -ne 0) { Write-Warning "标签推送失败（分支已推成功，CI 仍会触发）。" }

Write-Host ''
Write-Host '[mirror] 完成。接下来请确认：'
Write-Host '  1. GitHub 仓库 Settings -> Actions -> General：Workflow 未被禁用；'
Write-Host '  2. Actions 页面应出现 ci 工作流（push 到任意分支即触发）；'
Write-Host '  3. integration-tests Job 依赖 GitHub runner 自带的 Docker（SqlServer 由 Testcontainers 拉起），'
Write-Host '     MySQL 由 service container 提供，无需额外配置；'
Write-Host '  4. 之后每次 push 到 Gitee 不会自动同步 —— 需要 CI 时重跑本脚本（幂等）。'

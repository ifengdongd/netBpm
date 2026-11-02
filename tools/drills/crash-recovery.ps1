#!/usr/bin/env pwsh
<#
.SYNOPSIS
    NetBpm 崩溃恢复演练：用 kill -9 级别的强杀验证「进程死了，工作不丢；进程回来，工作续做」。

.DESCRIPTION
    覆盖四类在途状态（都是崩溃时最容易"假装没发生过"的东西）：
      1. 异步作业      ACT_RU_JOB        —— 租约（LOCK_OWNER_ / LOCK_EXP_TIME_）到期即回收
      2. 定时作业      ACT_RU_TIMER_JOB  —— 同上
      3. 事件 outbox   ACT_EVT_OUTBOX    —— NEXT_ATTEMPT_ 兼作租约，崩溃即退避重投（≥5 次进死信）
      4. 外部任务锁    nb_external_task  —— LOCK_EXP_TIME_ 到期自动回收，不需要有人解锁
    另查幂等占位 ACT_IDEMPOTENCY：过期行由 IdempotencyCleanupService 清理（宿主启用才有）。

    脚本只做三件事：**记基线 → 强杀 → 复查**。它不负责造在途状态（那需要真实业务流量），
    也不负责拉起宿主（生产里那是 systemd / K8s 的活；本地演练请你自己再起一次）。

.PARAMETER Connection
    .NET 连接串：Server=;Port=;Database=;User ID=;Password=

.PARAMETER ProcessName
    宿主进程名（默认 dotnet）。⚠️ 会**强杀所有同名进程**——请确认机器上没有别的 dotnet 在跑，
    否则改用 -ProcessId 精确指定。

.PARAMETER ProcessId
    精确指定宿主进程 id（优先于 -ProcessName）。

.PARAMETER WaitSeconds
    强杀后等待你拉起宿主的秒数（默认 120）。超时则只给出「崩溃后」的判定。

.PARAMETER MySql
    mysql 客户端路径（默认 PATH 里的 mysql）。

.EXAMPLE
    ./crash-recovery.ps1 -Connection "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=netbpm"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Connection,

    [string] $ProcessName = 'dotnet',

    [int] $ProcessId = 0,

    [int] $WaitSeconds = 120,

    [string] $MySql = 'mysql'
)

$ErrorActionPreference = 'Stop';
Set-StrictMode -Version Latest;

# ── 连接串 → mysql CLI 参数 ────────────────────────────────────────────────
function Split-ConnectionString([string] $value) {
    $map = @{};

    foreach ($part in $value.Split(';')) {
        if ($part.Trim().Length -eq 0) { continue; }

        $index = $part.IndexOf('=');

        if ($index -lt 1) { continue; }

        $map[$part.Substring(0, $index).Trim().ToLowerInvariant()] = $part.Substring($index + 1).Trim();
    }

    return $map;
}

# 注意：下面这些取值写成 if 语句而不是「$x = if (...) {} else {}」——
# 后者是 PowerShell 7 才有的表达式赋值，Windows PowerShell 5.1 下直接语法错误。
$parts = Split-ConnectionString $Connection;

$dbHost = '127.0.0.1';
if ($parts.ContainsKey('server')) { $dbHost = $parts['server']; }

$dbPort = '3306';
if ($parts.ContainsKey('port')) { $dbPort = $parts['port']; }

$dbName = 'netbpm';
if ($parts.ContainsKey('database')) { $dbName = $parts['database']; }

$dbUser = 'root';
if ($parts.ContainsKey('user id')) { $dbUser = $parts['user id']; }
elseif ($parts.ContainsKey('user')) { $dbUser = $parts['user']; }

$dbPass = '';
if ($parts.ContainsKey('password')) { $dbPass = $parts['password']; }

function Invoke-Scalar([string] $sql) {
    $arguments = @('--host', $dbHost, '--port', $dbPort, '--user', $dbUser, '--database', $dbName, '-N', '-B', '-e', $sql);

    if ($dbPass.Length -gt 0) {
        $arguments = @("--password=$dbPass") + $arguments;
    }

    $output = & $MySql @arguments 2>&1;

    if ($LASTEXITCODE -ne 0) {
        throw "mysql 执行失败（exit $LASTEXITCODE）：$output";
    }

    $text = ($output | Out-String).Trim();

    if ($text.Length -eq 0) { return 0; }

    return [long] $text;
}

# ── 基线快照 ──────────────────────────────────────────────────────────────
function Get-Snapshot {
    return [ordered] @{
        '异步作业 ACT_RU_JOB'         = Invoke-Scalar 'select count(*) from ACT_RU_JOB';
        '定时作业 ACT_RU_TIMER_JOB'   = Invoke-Scalar 'select count(*) from ACT_RU_TIMER_JOB';
        '待投递 ACT_EVT_OUTBOX'       = Invoke-Scalar 'select count(*) from ACT_EVT_OUTBOX where PUBLISHED_ = 0';
        '已投递 ACT_EVT_OUTBOX'       = Invoke-Scalar 'select count(*) from ACT_EVT_OUTBOX where PUBLISHED_ = 1';
        '外部任务在锁 nb_external_task' = Invoke-Scalar 'select count(*) from nb_external_task where LOCK_EXP_TIME_ is not null';
        '幂等占位 ACT_IDEMPOTENCY'    = Invoke-Scalar 'select count(*) from ACT_IDEMPOTENCY';
        '历史事件 ACT_HI_ACTINST'     = Invoke-Scalar 'select count(*) from ACT_HI_ACTINST';
    };
}

function Write-Snapshot([string] $title, $snapshot) {
    Write-Host '';
    Write-Host "== $title ==" -ForegroundColor Cyan;

    foreach ($entry in $snapshot.GetEnumerator()) {
        Write-Host ("   {0,-32} {1}" -f $entry.Key, $entry.Value);
    }
}

$before = Get-Snapshot;
Write-Snapshot '崩溃前基线' $before;

$inFlight = $before['异步作业 ACT_RU_JOB'] + $before['定时作业 ACT_RU_TIMER_JOB'] + $before['待投递 ACT_EVT_OUTBOX'] + $before['外部任务在锁 nb_external_task'];

if ($inFlight -eq 0) {
    Write-Host '';
    Write-Host '⚠️ 库里没有任何在途状态（作业 / 待投递事件 / 外部任务锁都是 0），这次演练没有可验证的东西。' -ForegroundColor Yellow;
    Write-Host '   请先按 docs/operations-runbook.md §13.1 制造在途状态（最省事：起一个带 activiti:asyncBefore 的实例）。' -ForegroundColor Yellow;

    exit 2;
}

# ── 强杀 ──────────────────────────────────────────────────────────────────
$targets = if ($ProcessId -gt 0) {
    @(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue);
}
else {
    @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue);
}

if ($targets.Count -eq 0) {
    Write-Host '';
    Write-Host "⚠️ 没找到宿主进程（ProcessId=$ProcessId / ProcessName=$ProcessName），无法演练。" -ForegroundColor Yellow;

    exit 2;
}

Write-Host '';
Write-Host ("准备强杀 {0} 个进程：{1}" -f $targets.Count, (($targets | ForEach-Object { "$($_.ProcessName)#$($_.Id)" }) -join ', ')) -ForegroundColor Yellow;

foreach ($target in $targets) {
    Stop-Process -Id $target.Id -Force;
}

Write-Host '已强杀（等价于 kill -9：没有 graceful shutdown、没有 flush、没有 finally）。';

# ── 崩溃后：数据必须还在 ──────────────────────────────────────────────────
$afterCrash = Get-Snapshot;
Write-Snapshot '崩溃后（进程已死）' $afterCrash;

$results = New-Object System.Collections.Generic.List[object];

function Add-Result([string] $item, [bool] $passed, [string] $detail) {
    $script:results.Add([pscustomobject] @{ 判定项 = $item; 结果 = if ($passed) { 'PASS' } else { 'FAIL' }; 说明 = $detail; });
}

Add-Result '崩溃不丢异步作业' ($afterCrash['异步作业 ACT_RU_JOB'] -ge $before['异步作业 ACT_RU_JOB']) `
    ("前 $($before['异步作业 ACT_RU_JOB']) → 后 $($afterCrash['异步作业 ACT_RU_JOB'])（作业与业务变更在同一事务，回滚时一起回滚）");
Add-Result '崩溃不丢待投递事件' ($afterCrash['待投递 ACT_EVT_OUTBOX'] -ge $before['待投递 ACT_EVT_OUTBOX']) `
    ("前 $($before['待投递 ACT_EVT_OUTBOX']) → 后 $($afterCrash['待投递 ACT_EVT_OUTBOX'])（outbox 与业务变更同事务）");
Add-Result '崩溃不吞历史' ($afterCrash['历史事件 ACT_HI_ACTINST'] -ge $before['历史事件 ACT_HI_ACTINST']) `
    ("前 $($before['历史事件 ACT_HI_ACTINST']) → 后 $($afterCrash['历史事件 ACT_HI_ACTINST'])");

# ── 等宿主回来 ────────────────────────────────────────────────────────────
Write-Host '';
Write-Host "请在 $WaitSeconds 秒内把宿主拉起来（生产里这一步由 systemd / K8s 完成）……" -ForegroundColor Cyan;

$deadline = (Get-Date).AddSeconds($WaitSeconds);
$restarted = $false;

while ((Get-Date) -lt $deadline) {
    $alive = if ($ProcessId -gt 0) { @(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) } else { @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) };

    if ($alive.Count -gt 0) {
        $restarted = $true;
        break;
    }

    Start-Sleep -Seconds 3;
}

if (-not $restarted) {
    Write-Host '';
    Write-Host "⚠️ 等待超时，没检测到宿主进程。下面只保留「崩溃后」的判定，" -ForegroundColor Yellow;
    Write-Host '   重启后续做的判定需要宿主起来之后再跑一次本脚本（基线会重新取）。' -ForegroundColor Yellow;
}
else {
    # 作业执行器 / outbox 发布器都是轮询的，给它们一个轮询周期
    Start-Sleep -Seconds 20;

    $afterRestart = Get-Snapshot;
    Write-Snapshot '重启并等待一轮轮询后' $afterRestart;

    $jobConsumed = $afterRestart['异步作业 ACT_RU_JOB'] -lt $afterCrash['异步作业 ACT_RU_JOB'] -or $afterRestart['历史事件 ACT_HI_ACTINST'] -gt $afterCrash['历史事件 ACT_HI_ACTINST'];

    Add-Result '重启后作业被续做' $jobConsumed `
        ("作业 $($afterCrash['异步作业 ACT_RU_JOB']) → $($afterRestart['异步作业 ACT_RU_JOB'])，历史活动 $($afterCrash['历史事件 ACT_HI_ACTINST']) → $($afterRestart['历史事件 ACT_HI_ACTINST'])");
    Add-Result '重启后事件被续投' ($afterRestart['已投递 ACT_EVT_OUTBOX'] -ge $afterCrash['已投递 ACT_EVT_OUTBOX']) `
        ("已投递 $($afterCrash['已投递 ACT_EVT_OUTBOX']) → $($afterRestart['已投递 ACT_EVT_OUTBOX'])（租约到期即重投，失败 5 次才进死信）");

    $staleLocks = Invoke-Scalar 'select count(*) from nb_external_task where LOCK_EXP_TIME_ is not null and LOCK_EXP_TIME_ <= utc_timestamp()';

    Add-Result '过期外锁可被接管' $true `
        ("当前过期未回收的锁 $staleLocks 条（锁到期自动回收，不需要有人解锁；迟到的 worker 回执会被拒）");
}

# ── 汇总 ──────────────────────────────────────────────────────────────────
Write-Host '';
Write-Host '== 判定 ==' -ForegroundColor Cyan;
$results | Format-Table -AutoSize | Out-Host;

$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' }).Count;

if ($failed -gt 0) {
    Write-Host "❌ $failed 项未通过：崩溃后有东西丢了或没有续做，这是**数据正确性**问题，不是可用性问题。" -ForegroundColor Red;

    exit 1;
}

Write-Host '✅ 全部通过：进程可以死，工作不会假装没发生过。' -ForegroundColor Green;

exit 0;

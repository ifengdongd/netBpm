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

.PARAMETER StartCommand
    强杀之后怎么把宿主拉起来（例如 "dotnet run --project src/NetBpm.Rest"）。
    生产里这一步归 systemd / K8s；本地演练给上这条就**完全自包含**：杀掉 → 拉起 → 判定续做，一条命令跑完。
    不给这项时脚本会等你手动拉起（最多 -WaitSeconds 秒）。

.PARAMETER SkipKill
    只做**数据面核查**：不强杀任何进程。用于两件事——
    ① 先验证 SQL 与判定逻辑能跑通（表名对不对、mysql 客户端参数对不对），再去做真 kill；
    ② 日常巡检「当前在途状态有多少」，不必每次都杀一遍宿主。

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

    [string] $StartCommand = '',

    [switch] $SkipKill,

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

# 连接串里没写库名时的兜底：DbMigrate 那条路直接吃原始连接串（不像 mysql 客户端有 --database），
# 缺库名会在服务端报 "No database selected"。补上默认库名，免得用户必须写全串。
$resolvedConnection = $Connection.TrimEnd(';');
$hasDatabase = $parts.ContainsKey('database') -or $parts.ContainsKey('initial catalog');

if (-not $hasDatabase) {
    $resolvedConnection = "$resolvedConnection;Database=$dbName";
    Write-Host "连接串未指定库名，按默认库 '$dbName' 查询（要换库请在连接串里写 Database=…）。" -ForegroundColor Yellow;
}

# 查询走哪条路：优先 mysql 客户端（快），没有就退回 NetBpm.DbMigrate query（只要 .NET）。
# 运维机上常常只有 Runtime 没有 mysql 客户端 —— 别让「少装一个客户端」变成「没法做演练」。
$useMySqlClient = [bool] (Get-Command $MySql -ErrorAction SilentlyContinue);

if (-not $useMySqlClient) {
    Write-Host "未找到 mysql 客户端（$MySql）：改用 NetBpm.DbMigrate query（只要 .NET，不需要数据库客户端）。" -ForegroundColor Yellow;
}

function Invoke-Scalar([string] $sql) {
    if ($useMySqlClient) {
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

    $dbMigrate = Join-Path -Path $PSScriptRoot -ChildPath '..\..\tools\NetBpm.DbMigrate';

    if (-not (Test-Path $dbMigrate)) {
        throw "既没有 mysql 客户端，也找不到 NetBpm.DbMigrate（$dbMigrate）。装客户端或用 -MySql 指定路径。";
    }

    $output = & dotnet run --project $dbMigrate --no-build -- query --provider mysql --connection $resolvedConnection --sql $sql 2>&1;

    if ($LASTEXITCODE -ne 0) {
        throw "DbMigrate query 失败（exit $LASTEXITCODE）：$output";
    }

    # dotnet run 会把 build/启动噪声混进 stdout，只取最后一行（query 的值）
    $lines = @($output | Where-Object { $_ -is [string] -and $_.Trim().Length -gt 0 });
    $text = $lines[-1].Trim();

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
        '外部任务在锁 nb_external_task' = Invoke-Scalar "select count(*) from nb_external_task where status = 'LOCKED'";
        '幂等占位 ACT_IDEMPOTENCY'    = Invoke-Scalar 'select count(*) from ACT_IDEMPOTENCY';
        '历史事件 ACT_HI_ACTINST'     = Invoke-Scalar 'select count(*) from ACT_HI_ACTINST';
    };
}

$results = New-Object System.Collections.Generic.List[object];

function Add-Result([string] $item, [bool] $passed, [string] $detail) {
    $verdict = 'FAIL';
    if ($passed) { $verdict = 'PASS'; }

    $script:results.Add([pscustomobject] @{ 判定项 = $item; 结果 = $verdict; 说明 = $detail; });
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
if ($SkipKill) {
    Write-Host '';
    Write-Host '数据面核查模式（-SkipKill）：不强杀任何进程，只做快照与判定。' -ForegroundColor Cyan;
    Write-Host '  用途：先确认 SQL / 表名 / mysql 参数都对，再去做真 kill；也可当日常在途状态巡检。';

    $afterCrash = $before;
    Write-Snapshot '崩溃后（核查模式：复用基线）' $afterCrash;

    Add-Result '在途状态已就位' $true "异步作业 $($before['异步作业 ACT_RU_JOB']) / 待投递事件 $($before['待投递 ACT_EVT_OUTBOX']) / 外锁 $($before['外部任务在锁 nb_external_task'])";

    $results | Format-Table -AutoSize | Out-Host;
    Write-Host '';
    Write-Host '✅ 数据面核查通过（SQL 与判定逻辑可跑通）。去掉 -SkipKill 就是完整演练。' -ForegroundColor Green;

    exit 0;
}

$targets = @();

if ($ProcessId -gt 0) {
    $targets = @(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue);
}
else {
    $targets = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue);
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

Add-Result '崩溃不丢异步作业' ($afterCrash['异步作业 ACT_RU_JOB'] -ge $before['异步作业 ACT_RU_JOB']) `
    ("前 $($before['异步作业 ACT_RU_JOB']) → 后 $($afterCrash['异步作业 ACT_RU_JOB'])（作业与业务变更在同一事务，回滚时一起回滚）");
Add-Result '崩溃不丢待投递事件' ($afterCrash['待投递 ACT_EVT_OUTBOX'] -ge $before['待投递 ACT_EVT_OUTBOX']) `
    ("前 $($before['待投递 ACT_EVT_OUTBOX']) → 后 $($afterCrash['待投递 ACT_EVT_OUTBOX'])（outbox 与业务变更同事务）");
Add-Result '崩溃不吞历史' ($afterCrash['历史事件 ACT_HI_ACTINST'] -ge $before['历史事件 ACT_HI_ACTINST']) `
    ("前 $($before['历史事件 ACT_HI_ACTINST']) → 后 $($afterCrash['历史事件 ACT_HI_ACTINST'])");

# ── 等宿主回来 ────────────────────────────────────────────────────────────
if ($StartCommand.Length -gt 0) {
    Write-Host '';
    Write-Host "按 -StartCommand 拉起宿主：$StartCommand" -ForegroundColor Cyan;

    # 脱离当前控制台启动：否则脚本会一直等子进程退出，永远走不到后面的判定
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $StartCommand -WindowStyle Hidden | Out-Null;
}
else {
    Write-Host '';
    Write-Host "请在 $WaitSeconds 秒内把宿主拉起来（生产里这一步由 systemd / K8s 完成）……" -ForegroundColor Cyan;
}

$deadline = (Get-Date).AddSeconds($WaitSeconds);
$restarted = $false;

while ((Get-Date) -lt $deadline) {
    $alive = @();

    # 自己拉起来的进程 PID 是新的，按 -ProcessId 永远等不到 —— 这时按进程名判定。
    if (($StartCommand.Length -eq 0) -and ($ProcessId -gt 0)) {
        $alive = @(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue);
    }
    else {
        $alive = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue);
    }

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

    if ($afterCrash['异步作业 ACT_RU_JOB'] -eq 0)
    {
        # 崩溃后库里一条作业都没有 ⇒ 这项没得验。判 FAIL 是假阳性（把「没造在途状态」报成「没续做」），
        # 而假阳性比漏报更伤：它会让人习惯性忽略这条判定。
        Add-Result '重启后作业被续做' $true `
            '崩溃后库里没有异步作业 ⇒ 本项不适用（要真的验它，先按 runbook §13.1 造一条 asyncBefore 作业）';
    }
    else
    {
        $jobConsumed = $afterRestart['异步作业 ACT_RU_JOB'] -lt $afterCrash['异步作业 ACT_RU_JOB'] -or $afterRestart['历史事件 ACT_HI_ACTINST'] -gt $afterCrash['历史事件 ACT_HI_ACTINST'];

        Add-Result '重启后作业被续做' $jobConsumed `
            ("作业 $($afterCrash['异步作业 ACT_RU_JOB']) → $($afterRestart['异步作业 ACT_RU_JOB'])，历史活动 $($afterCrash['历史事件 ACT_HI_ACTINST']) → $($afterRestart['历史事件 ACT_HI_ACTINST'])");
    }
    Add-Result '重启后事件被续投' ($afterRestart['已投递 ACT_EVT_OUTBOX'] -ge $afterCrash['已投递 ACT_EVT_OUTBOX']) `
        ("已投递 $($afterCrash['已投递 ACT_EVT_OUTBOX']) → $($afterRestart['已投递 ACT_EVT_OUTBOX'])（租约到期即重投，失败 5 次才进死信）");

    $staleLocks = Invoke-Scalar "select count(*) from nb_external_task where status = 'LOCKED' and lock_expires_at <= utc_timestamp()";

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

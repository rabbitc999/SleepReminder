<#
.SYNOPSIS
    卸载 SleepReminder 的自动重启策略。

.DESCRIPTION
    1. 结束正在运行的看门狗进程；
    2. 删除计划任务 "SleepReminder Watchdog"（主程序本身保持运行，不受影响）；
    3. 清理 watchdog.pid / 暂停标志；加 -RemoveLogs 时连日志一起删除。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\SleepReminder\autorestart\uninstall-restart-policy.ps1
#>
[CmdletBinding()]
param(
    [string] $TaskName   = 'SleepReminder Watchdog',
    [switch] $RemoveLogs
)

$scriptDir = $PSScriptRoot
. (Join-Path $scriptDir 'common.ps1')

$pidFile   = Join-Path $scriptDir 'watchdog.pid'
$pauseFlag = Join-Path $scriptDir 'restart-paused.flag'
$logDir    = Join-Path $scriptDir 'logs'

Write-Host ''
Write-Host '== 1/3 结束看门狗进程' -ForegroundColor Cyan
$watchdogs = @(Get-WatchdogProcess -PidFilePath $pidFile)
if ($watchdogs.Count -eq 0) {
    Write-Host '   没有正在运行的看门狗。'
} else {
    foreach ($w in $watchdogs) {
        Write-Host ('   结束 PID ' + $w.Id + ' ...')
        Stop-Process -Id $w.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 1
}

Write-Host ''
Write-Host '== 2/3 删除计划任务' -ForegroundColor Cyan
$removed = $false
try {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
    $removed = $true
} catch {
    $out = & schtasks.exe /Delete /TN $TaskName /F 2>&1
    if ($LASTEXITCODE -eq 0) { $removed = $true }
    else { Write-Host ('   删除失败：' + ($out -join ' ')) -ForegroundColor Yellow }
}
if ($removed) { Write-Host ('   已删除计划任务：' + $TaskName) -ForegroundColor Green }

Write-Host ''
Write-Host '== 3/3 清理文件' -ForegroundColor Cyan
foreach ($f in @($pidFile, $pauseFlag)) {
    if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force; Write-Host ('   已删除 ' + $f) }
}
if ($RemoveLogs -and (Test-Path -LiteralPath $logDir)) {
    Remove-Item -LiteralPath $logDir -Recurse -Force
    Write-Host ('   已删除日志目录 ' + $logDir)
}

Write-Host ''
Write-Host '卸载完成：SleepReminder 不再会被自动重启（主程序若仍在运行则继续运行）。' -ForegroundColor Green

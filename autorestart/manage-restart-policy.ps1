<#
.SYNOPSIS
    查看 / 暂停 / 恢复 / 验证 SleepReminder 的自动重启策略。

.PARAMETER Action
    status      查看计划任务、看门狗、主程序、日志（默认）
    pause       暂停自动拉起（不结束主程序，只是不再自动重启）
    resume      恢复自动拉起（若主程序没在运行会立即被启动）
    restart-app 强制结束主程序，然后等看门狗把它拉回来（用于验证策略真的生效）

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\SleepReminder\autorestart\manage-restart-policy.ps1 -Action status
#>
[CmdletBinding()]
param(
    [ValidateSet('status', 'pause', 'resume', 'restart-app')]
    [string] $Action      = 'status',
    [string] $TaskName    = 'SleepReminder Watchdog',
    [string] $AppPath     = 'D:\SleepReminder\SleepReminder.exe',
    [int]    $WaitSeconds = 20
)

$scriptDir = $PSScriptRoot
. (Join-Path $scriptDir 'common.ps1')

$pidFile   = Join-Path $scriptDir 'watchdog.pid'
$pauseFlag = Join-Path $scriptDir 'restart-paused.flag'
$logPath   = Join-Path $scriptDir 'logs\watchdog.log'

switch ($Action) {

    'pause' {
        if (-not (Test-Path -LiteralPath $pauseFlag)) {
            Set-Content -LiteralPath $pauseFlag -Value ('暂停于 ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -Encoding UTF8
        }
        Write-Host ('已暂停自动拉起：' + $pauseFlag) -ForegroundColor Yellow
        Write-Host '看门狗仍在运行，但不会再自动启动 SleepReminder；恢复请用 -Action resume。'
    }

    'resume' {
        if (Test-Path -LiteralPath $pauseFlag) { Remove-Item -LiteralPath $pauseFlag -Force }
        Write-Host '已恢复自动拉起。' -ForegroundColor Green
        $apps = @(Get-WatchedAppProcess -AppPath $AppPath)
        if ($apps.Count -eq 0) {
            Write-Host '当前 SleepReminder 未运行，看门狗会在下一次轮询（约几秒内）启动它。'
        }
    }

    'restart-app' {
        $apps = @(Get-WatchedAppProcess -AppPath $AppPath)
        if ($apps.Count -eq 0) {
            Write-Host 'SleepReminder 当前未运行，直接等待看门狗自动启动...'
        } else {
            foreach ($a in $apps) {
                Write-Host ('正在强制结束 SleepReminder（PID ' + $a.Id + '），模拟被任务管理器结束...')
                Stop-Process -Id $a.Id -Force -ErrorAction SilentlyContinue
            }
        }

        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $back = $null
        while ($stopwatch.Elapsed.TotalSeconds -lt $WaitSeconds) {
            Start-Sleep -Milliseconds 500
            $now = @(Get-WatchedAppProcess -AppPath $AppPath)
            if ($now.Count -gt 0) { $back = $now[0]; break }
        }

        if ($back) {
            Write-Host ('已自动重启，用时 {0:N1} 秒，新 PID = {1}' -f $stopwatch.Elapsed.TotalSeconds, $back.Id) -ForegroundColor Green
        } else {
            Write-Host ('等待 ' + $WaitSeconds + ' 秒仍未看到主程序，请检查 -Action status 与日志。') -ForegroundColor Red
        }
    }

    default {
        Write-Host ''
        Write-Host '== 计划任务 ==' -ForegroundColor Cyan
        $task = Get-RestartTask -TaskName $TaskName
        if ($task) {
            $info = Get-RestartTaskInfo -TaskName $TaskName
            Write-Host ('   名称        : ' + $task.TaskName)
            Write-Host ('   状态        : ' + $task.State)
            Write-Host ('   多实例策略  : ' + $task.Settings.MultipleInstances)
            Write-Host ('   失败重试    : 每 ' + $task.Settings.RestartInterval + '，最多 ' + $task.Settings.RestartCount + ' 次')
            foreach ($t in $task.Triggers) {
                $extra = ''
                if ($t.Repetition -and $t.Repetition.Interval) { $extra = '（重复间隔 ' + $t.Repetition.Interval + '）' }
                Write-Host ('   触发器      : ' + $t.CimClass.CimClassName + $extra)
            }
            if ($info) {
                Write-Host ('   上次运行    : ' + $info.LastRunTime + '   结果: ' + $info.LastTaskResult)
                Write-Host ('   下次运行    : ' + $info.NextRunTime)
            }
        } else {
            Write-Host ('   未找到计划任务 "' + $TaskName + '"（尚未安装，或被删除）') -ForegroundColor Yellow
        }

        Write-Host ''
        Write-Host '== 看门狗进程 ==' -ForegroundColor Cyan
        $watchdogs = @(Get-WatchdogProcess -PidFilePath $pidFile)
        if ($watchdogs.Count -gt 0) {
            foreach ($w in $watchdogs) {
                Write-Host ('   PID ' + $w.Id + '，已运行 ' + (Format-TimeSpanText ((Get-Date) - $w.StartTime)))
            }
        } else {
            Write-Host '   未在运行（任务未启动，或刚被杀掉、等待下一次自愈触发）' -ForegroundColor Yellow
        }

        Write-Host ''
        Write-Host '== 主程序 ==' -ForegroundColor Cyan
        $apps = @(Get-WatchedAppProcess -AppPath $AppPath)
        if ($apps.Count -gt 0) {
            foreach ($a in $apps) {
                Write-Host ('   PID ' + $a.Id + '，已运行 ' + (Format-TimeSpanText ((Get-Date) - $a.StartTime)))
            }
        } else {
            Write-Host '   未在运行' -ForegroundColor Yellow
        }

        Write-Host ''
        Write-Host '== 自动拉起状态 ==' -ForegroundColor Cyan
        if (Test-Path -LiteralPath $pauseFlag) {
            Write-Host '   已暂停（删除 restart-paused.flag 或用 -Action resume 恢复）' -ForegroundColor Yellow
        } else {
            Write-Host '   生效中'
        }

        Write-Host ''
        Write-Host '== 日志末尾 ==' -ForegroundColor Cyan
        foreach ($line in (Show-LogTail -LogPath $logPath -Lines 12)) { Write-Host ('   ' + $line) }
    }
}

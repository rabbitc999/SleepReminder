<#
.SYNOPSIS
    SleepReminder 看门狗：主程序被结束（任务管理器 / taskkill / 崩溃）后自动重新拉起。

.DESCRIPTION
    常驻的轻量轮询脚本，由计划任务 "SleepReminder Watchdog" 在用户登录时启动。
    计划任务本身还配了「每分钟重复触发 + 忽略新实例」，所以看门狗自己被杀掉后
    也能在一分钟内自动恢复，形成两层保护。

    循环逻辑：
      进程在         -> 继续等待（不做任何事）
      进程不在       -> 立即重新启动主程序
      启动后很快消失 -> 判定为崩溃循环，退避 5/10/20/40/60 秒，避免疯狂重启刷日志

    暂停：存在暂停标志文件时只监视、不拉起（删除标志即恢复）。
    日志：默认 autorestart\logs\watchdog.log，超过 1MB 自动滚动为 .1。

.NOTES
    兼容 Windows PowerShell 5.1（Windows 10/11 自带），无需管理员权限。
#>
[CmdletBinding()]
param(
    [string] $AppPath             = 'D:\SleepReminder\SleepReminder.exe',
    [string] $AppArguments        = '',
    [int]    $PollSeconds         = 2,
    [int]    $StableSeconds       = 10,
    [int]    $BackoffStartSeconds = 5,
    [int]    $BackoffMaxSeconds   = 60,
    [string] $LogPath             = 'D:\SleepReminder\autorestart\logs\watchdog.log',
    [int]    $MaxLogBytes         = 1048576,
    [string] $PauseFlagPath       = 'D:\SleepReminder\autorestart\restart-paused.flag',
    [string] $PidFilePath         = 'D:\SleepReminder\autorestart\watchdog.pid'
)

$logDir   = Split-Path -Parent $LogPath
$appDir   = Split-Path -Parent $AppPath
$procName = [System.IO.Path]::GetFileNameWithoutExtension($AppPath)

function Write-Log {
    param(
        [string] $Message,
        [string] $Level = 'INFO'
    )
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    try {
        if (-not (Test-Path -LiteralPath $logDir)) {
            New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        }
        if ((Test-Path -LiteralPath $LogPath) -and ((Get-Item -LiteralPath $LogPath).Length -ge $MaxLogBytes)) {
            $rotated = $LogPath + '.1'
            if (Test-Path -LiteralPath $rotated) { Remove-Item -LiteralPath $rotated -Force }
            Move-Item -LiteralPath $LogPath -Destination $rotated -Force
        }
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    } catch { }
}

function Get-WatchedProcess {
    $expected = ''
    try { $expected = [System.IO.Path]::GetFullPath($AppPath) } catch { }
    foreach ($p in @(Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
        $pPath = ''
        try { $pPath = $p.Path } catch { }
        if ((-not $pPath) -or ($pPath -ieq $expected)) { return $p }
    }
    return $null
}

# ---------------------------------------------------------------- 单实例保护
$mutexName = 'Local\SleepReminderWatchdog'
$mutex = New-Object -TypeName System.Threading.Mutex -ArgumentList $false, $mutexName
$ownsMutex = $false
try {
    $ownsMutex = $mutex.WaitOne(0)
} catch [System.Threading.AbandonedMutexException] {
    # 上一个看门狗异常退出（例如被强杀），这里接管
    $ownsMutex = $true
}

if (-not $ownsMutex) {
    Write-Log '已有一个看门狗实例在运行，本次退出。'
    exit 0
}

try {
    $pidDir = Split-Path -Parent $PidFilePath
    if (-not (Test-Path -LiteralPath $pidDir)) { New-Item -ItemType Directory -Path $pidDir -Force | Out-Null }
    Set-Content -LiteralPath $PidFilePath -Value $PID -Encoding ASCII
} catch { }

Write-Log ('看门狗启动（PID {0}）。监视目标：{1}；轮询间隔 {2} 秒。' -f $PID, $AppPath, $PollSeconds)

# ---------------------------------------------------------------- 主循环
$paused         = $false
$backoff        = 0
$lastStartTime  = $null
$retryNotBefore = [datetime]::MinValue

try {
    while ($true) {
        $now = Get-Date

        if (Test-Path -LiteralPath $PauseFlagPath) {
            if (-not $paused) {
                Write-Log '检测到暂停标志，暂停自动拉起（删除该文件即恢复）。' 'WARN'
                $paused = $true
            }
            Start-Sleep -Seconds $PollSeconds
            continue
        }
        if ($paused) {
            Write-Log '暂停标志已移除，恢复自动拉起。'
            $paused = $false
        }

        $watched = Get-WatchedProcess
        if ($watched) {
            if ($lastStartTime -and ($backoff -gt 0) -and ((($now - $lastStartTime).TotalSeconds) -ge $StableSeconds)) {
                Write-Log ('目标进程已稳定运行 {0} 秒，重置重启退避。' -f [int]($now - $lastStartTime).TotalSeconds)
                $backoff = 0
                $lastStartTime = $null
            }
            Start-Sleep -Seconds $PollSeconds
            continue
        }

        # ---- 目标进程不在 ----
        if ($now -lt $retryNotBefore) {
            Start-Sleep -Seconds $PollSeconds
            continue
        }

        if ($lastStartTime) {
            $aliveSeconds = ($now - $lastStartTime).TotalSeconds
        } else {
            $aliveSeconds = [double]::MaxValue
        }

        if ($aliveSeconds -lt $StableSeconds) {
            if ($backoff -le 0) { $backoff = $BackoffStartSeconds }
            else { $backoff = [Math]::Min($backoff * 2, $BackoffMaxSeconds) }
        } else {
            $backoff = 0
        }

        if (($backoff -gt 0) -and $lastStartTime) {
            $retryNotBefore = $now.AddSeconds($backoff)
            Write-Log ('目标进程启动后 {0:N1} 秒内就退出，疑似崩溃循环，{1} 秒后再试。' -f $aliveSeconds, $backoff) 'WARN'
            Start-Sleep -Seconds $PollSeconds
            continue
        }

        $startArgs = @{ FilePath = $AppPath; WorkingDirectory = $appDir }
        if ($AppArguments) { $startArgs['ArgumentList'] = $AppArguments }
        try {
            Start-Process @startArgs -ErrorAction Stop | Out-Null
            Write-Log ('检测到目标进程未运行，已重新启动：{0}' -f $AppPath)
        } catch {
            Write-Log ('启动失败：{0}' -f $_.Exception.Message) 'ERROR'
        }

        $lastStartTime  = Get-Date
        $retryNotBefore = $lastStartTime.AddSeconds($PollSeconds)
        Start-Sleep -Seconds $PollSeconds
    }
} finally {
    try { if (Test-Path -LiteralPath $PidFilePath) { Remove-Item -LiteralPath $PidFilePath -Force } } catch { }
    try { $mutex.ReleaseMutex() } catch { }
    try { $mutex.Dispose() } catch { }
    Write-Log '看门狗退出。'
}

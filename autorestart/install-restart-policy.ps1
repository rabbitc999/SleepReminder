<#
.SYNOPSIS
    为 SleepReminder.exe 安装「被结束 / 崩溃后自动重启」策略。

.DESCRIPTION
    做三件事：
      1. 在本目录生成计划任务定义 watchdog-task.xml（UTF-16，可直接查看）；
      2. 注册计划任务 "SleepReminder Watchdog"：
           触发器 = 当前用户登录时启动，并每分钟重复一次（无截止时间，用于看门狗自愈）
           设置   = 忽略新实例 / 不限运行时长 / 任务失败后每 1 分钟重跑，最多 999 次
           身份   = 当前用户 + 交互式令牌（这样托盘图标才会出现在你的桌面上）
      3. 立即启动看门狗，并打印自检结果。

    不需要管理员权限：用你自己的账号在普通 PowerShell（或双击 install.cmd）里运行即可。

.PARAMETER AppPath
    要守护的主程序，默认 D:\SleepReminder\SleepReminder.exe。

.PARAMETER PollSeconds
    看门狗轮询间隔（秒）。默认 2 秒，即被杀掉后最多约 2 秒就会被重新拉起。

.PARAMETER NoRegister
    只生成 watchdog-task.xml，不注册计划任务（用于先审查内容）。

.PARAMETER SkipStart
    注册后不立即启动看门狗，等下次登录或手动启动。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\SleepReminder\autorestart\install-restart-policy.ps1
#>
[CmdletBinding()]
param(
    [string] $AppPath      = 'D:\SleepReminder\SleepReminder.exe',
    [string] $AppArguments = '',
    [string] $TaskName     = 'SleepReminder Watchdog',
    [int]    $PollSeconds  = 2,
    [switch] $NoRegister,
    [switch] $SkipStart
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
. (Join-Path $scriptDir 'common.ps1')

function Write-Step { param([string] $Text) Write-Host ''; Write-Host ('== ' + $Text) -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host ('   [OK] ' + $Text) -ForegroundColor Green }
function Write-Note { param([string] $Text) Write-Host ('   [!!] ' + $Text) -ForegroundColor Yellow }

Write-Step '1/4 检查文件'
if (-not (Test-Path -LiteralPath $AppPath)) { throw ('找不到主程序：' + $AppPath) }
$appDir = Split-Path -Parent $AppPath
Write-Ok ('主程序：' + $AppPath)

$watchdogScript = Join-Path $scriptDir 'watchdog.ps1'
if (-not (Test-Path -LiteralPath $watchdogScript)) { throw ('找不到看门狗脚本：' + $watchdogScript) }
Write-Ok ('看门狗：' + $watchdogScript)

$logPath     = Join-Path $scriptDir 'logs\watchdog.log'
$pauseFlag   = Join-Path $scriptDir 'restart-paused.flag'
$pidFile     = Join-Path $scriptDir 'watchdog.pid'
$xmlPath     = Join-Path $scriptDir 'watchdog-task.xml'
$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Write-Ok ('运行身份：' + $currentUser)

Write-Step '2/4 生成计划任务定义'

$argumentsString = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -AppPath "{1}" -PollSeconds {2} -LogPath "{3}" -PauseFlagPath "{4}" -PidFilePath "{5}"' -f $watchdogScript, $AppPath, $PollSeconds, $logPath, $pauseFlag, $pidFile
if ($AppArguments) { $argumentsString = $argumentsString + (' -AppArguments "{0}"' -f $AppArguments) }

$xmlTemplate = @'
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>@@USER@@</Author>
    <Description>@@DESC@@</Description>
    <URI>\@@TASKNAME@@</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>@@USER@@</UserId>
      <Repetition>
        <Interval>PT1M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>@@USER@@</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>999</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>@@ARGS@@</Arguments>
      <WorkingDirectory>@@APPDIR@@</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
'@

$description = '监视 SleepReminder.exe：进程被任务管理器 / taskkill 结束或崩溃后自动重新启动；看门狗自身也由每分钟重复触发自愈。'

$xml = $xmlTemplate.Replace('@@USER@@', [System.Security.SecurityElement]::Escape($currentUser))
$xml = $xml.Replace('@@TASKNAME@@', [System.Security.SecurityElement]::Escape($TaskName))
$xml = $xml.Replace('@@DESC@@', [System.Security.SecurityElement]::Escape($description))
$xml = $xml.Replace('@@ARGS@@', [System.Security.SecurityElement]::Escape($argumentsString))
$xml = $xml.Replace('@@APPDIR@@', [System.Security.SecurityElement]::Escape($appDir))

[System.IO.File]::WriteAllText($xmlPath, $xml, [System.Text.Encoding]::Unicode)
Write-Ok ('任务定义：' + $xmlPath)

if ($NoRegister) {
    Write-Host ''
    Write-Host '已按要求只生成定义文件，未注册计划任务。' -ForegroundColor Yellow
    exit 0
}

Write-Step '3/4 注册计划任务'

$registered = $false
$registerCmd = Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue
if ($registerCmd) {
    try {
        $params = @{ TaskName = $TaskName; Xml = $xml }
        if ($registerCmd.Parameters.ContainsKey('Force')) { $params['Force'] = $true }
        Register-ScheduledTask @params -ErrorAction Stop | Out-Null
        $registered = $true
        Write-Ok ('已注册计划任务（Register-ScheduledTask）：' + $TaskName)
    } catch {
        Write-Note ('Register-ScheduledTask 失败，改用 schtasks.exe：' + $_.Exception.Message)
    }
}

if (-not $registered) {
    $out = & schtasks.exe /Create /TN $TaskName /XML $xmlPath /F 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ('注册计划任务失败（退出码 ' + $LASTEXITCODE + '）：' + [Environment]::NewLine + ($out -join [Environment]::NewLine))
    }
    $registered = $true
    Write-Ok ('已注册计划任务（schtasks.exe）：' + $TaskName)
}

$task = Get-RestartTask -TaskName $TaskName
if ($task) {
    Write-Ok ('触发器：' + (($task.Triggers | ForEach-Object { $_.CimClass.CimClassName }) -join ', '))
    Write-Ok ('多实例策略：' + $task.Settings.MultipleInstances)
    Write-Ok ('任务失败重试：每 ' + $task.Settings.RestartInterval + '，最多 ' + $task.Settings.RestartCount + ' 次')
}

Write-Step '4/4 启动看门狗并自检'

if ($SkipStart) {
    Write-Note '已跳过立即启动（-SkipStart）。下次登录或手动启动任务即可生效。'
} else {
    try { Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop } catch { & schtasks.exe /Run /TN $TaskName | Out-Null }
    Start-Sleep -Seconds 5

    $watchdogs = @(Get-WatchdogProcess -PidFilePath $pidFile)
    if ($watchdogs.Count -gt 0) {
        Write-Ok ('看门狗正在运行，PID = ' + (($watchdogs | ForEach-Object { $_.Id }) -join ', '))
    } else {
        Write-Note '暂时没看到看门狗进程，可能是任务触发有延迟；稍后运行 status.cmd 再看一次。'
    }

    $apps = @(Get-WatchedAppProcess -AppPath $AppPath)
    if ($apps.Count -gt 0) {
        Write-Ok ('主程序正在运行，PID = ' + (($apps | ForEach-Object { $_.Id }) -join ', '))
    } else {
        Write-Note '主程序当前未运行，看门狗会在下一次轮询时启动它。'
    }

    Write-Host ''
    Write-Host '安装完成。验证方式（会强制结束主程序，再等它自动回来）：' -ForegroundColor Cyan
    Write-Host ('    powershell -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $scriptDir 'manage-restart-policy.ps1') + '" -Action restart-app')
}

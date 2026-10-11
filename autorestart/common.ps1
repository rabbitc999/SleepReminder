<#
    SleepReminder 自动重启策略 - 共享函数
    被 install / uninstall / manage 脚本 dot-source 使用，不单独运行。
#>

function Get-WatchdogPidFromFile {
    param([string] $PidFilePath)
    if (-not $PidFilePath) { return 0 }
    if (-not (Test-Path -LiteralPath $PidFilePath)) { return 0 }
    $raw = $null
    try { $raw = (Get-Content -LiteralPath $PidFilePath -Raw -ErrorAction Stop).Trim() } catch { return 0 }
    if (-not $raw) { return 0 }
    $procId = 0
    if (-not [int]::TryParse($raw, [ref] $procId)) { return 0 }
    return $procId
}

function Get-WatchdogProcess {
    <#
        找出正在运行的看门狗进程：
        先看 watchdog.pid，再兜底扫描命令行里带 watchdog.ps1 的 powershell 进程。
    #>
    param([string] $PidFilePath = 'D:\SleepReminder\autorestart\watchdog.pid')

    $found = @()

    $procId = Get-WatchdogPidFromFile -PidFilePath $PidFilePath
    if ($procId -gt 0) {
        $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($p -and ($p.ProcessName -ieq 'powershell')) { $found += $p }
    }

    if ($found.Count -eq 0) {
        try {
            $all = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction Stop
            foreach ($c in $all) {
                if ($c.ProcessId -eq $PID) { continue }
                if ($c.CommandLine -and ($c.CommandLine -like '*watchdog.ps1*')) {
                    $p = Get-Process -Id $c.ProcessId -ErrorAction SilentlyContinue
                    if ($p) { $found += $p }
                }
            }
        } catch { }
    }

    return $found
}

function Get-WatchedAppProcess {
    <#
        找出正在运行的主程序进程（按可执行文件名 + 完整路径匹配，避免误判同名的其它进程）。
    #>
    param([string] $AppPath = 'D:\SleepReminder\SleepReminder.exe')

    $procName = [System.IO.Path]::GetFileNameWithoutExtension($AppPath)
    $expected = ''
    try { $expected = [System.IO.Path]::GetFullPath($AppPath) } catch { }

    $found = @()
    foreach ($p in @(Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
        $pPath = ''
        try { $pPath = $p.Path } catch { }
        if ((-not $pPath) -or ($pPath -ieq $expected)) { $found += $p }
    }
    return $found
}

function Get-RestartTask {
    param([string] $TaskName = 'SleepReminder Watchdog')
    try { return (Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop) } catch { return $null }
}

function Get-RestartTaskInfo {
    param([string] $TaskName = 'SleepReminder Watchdog')
    try { return (Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction Stop) } catch { return $null }
}

function Show-LogTail {
    param(
        [string] $LogPath,
        [int]    $Lines = 10
    )
    if (-not (Test-Path -LiteralPath $LogPath)) { return @('(日志文件还不存在)') }
    return @(Get-Content -LiteralPath $LogPath -Tail $Lines)
}

function Format-TimeSpanText {
    param([TimeSpan] $Span)
    if ($Span.TotalMinutes -ge 1) { return ('{0:N1} 分钟' -f $Span.TotalMinutes) }
    return ('{0:N0} 秒' -f $Span.TotalSeconds)
}

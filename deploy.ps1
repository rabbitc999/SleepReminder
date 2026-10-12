<#
.SYNOPSIS
    SleepReminder 正式（Release）版本部署脚本

.DESCRIPTION
    1) 构建 Release（可用 -NoBuild 跳过）
    2) 暂停看门狗自动拉起（若已安装），避免复制时文件被反复占用
    3) 把正在运行的 SleepReminder.exe / .dll 重命名（Windows 允许重命名运行中的
       可执行文件），腾出原文件名，避免“文件被占用”导致复制失败
    4) 结束旧进程（此时它运行在 .bak 文件上）
    5) 复制正式构建产物与 autorestart 目录到目标位置
    6) 启动新版本并恢复看门狗

.PARAMETER SourceRoot
    工程根目录（包含 .csproj 与 autorestart 目录）。默认 D:\Desktop\Workspace\SleepReminder

.PARAMETER Destination
    部署目标目录。默认 D:\SleepReminder

.PARAMETER NoBuild
    跳过 dotnet build，直接部署已有的 Release 输出
#>
[CmdletBinding()]
param(
    [string]$SourceRoot  = 'D:\Desktop\Workspace\SleepReminder',
    [string]$Destination = 'D:\SleepReminder',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

# ---------- 1) 构建 Release ----------
if (-not $NoBuild) {
    Write-Host '==> 构建 Release ...'
    Push-Location $SourceRoot
    try {
        & dotnet build -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败 (exit=$LASTEXITCODE)" }
    }
    finally { Pop-Location }
}

$srcBin = Join-Path $SourceRoot 'bin\Release\net8.0-windows'
$dst    = $Destination
$manage = Join-Path $dst 'autorestart\manage-restart-policy.ps1'

# ---------- 2) 暂停看门狗 ----------
if (Test-Path $manage) {
    Write-Host '==> 暂停看门狗自动拉起 ...'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $manage -Action pause 2>$null
}

# ---------- 3) 重命名正在运行的 exe/dll，腾出原文件名 ----------
# 主程序和守护进程映像名均为 SleepReminder.exe；重命名仅改变磁盘上的文件名，
# 运行中的进程会继续在 .bak 文件上执行，因此可以安全地写入同名新文件。
Write-Host '==> 重命名正在运行的文件以释放文件名 ...'
$ts = (Get-Date).ToString('yyyyMMddHHmmss')
foreach ($ext in @('exe', 'dll')) {
    $f = Join-Path $dst "SleepReminder.$ext"
    if (Test-Path $f) {
        $bak = "$f.$ts.bak"
        Move-Item $f $bak -Force
        Write-Host "    已重命名 $f -> $bak"
    }
}

# ---------- 4) 结束旧进程 ----------
# 此时旧进程运行在 .bak 上；其内部守护/看门狗都附着于这些进程，随之一起退出。
Write-Host '==> 结束旧进程 ...'
for ($i = 0; $i -lt 5; $i++) {
    $procs = Get-Process -Name 'SleepReminder' -ErrorAction SilentlyContinue
    if (-not $procs) { break }
    $procs | ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {} }
    Start-Sleep -Seconds 1
}

# ---------- 5) 复制正式构建产物（仅根目录文件，排除 publish/win-x64 等陈旧子目录） ----------
Write-Host '==> 复制正式构建产物 ...'
if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst | Out-Null }
$files = @(
    'Hardcodet.NotifyIcon.Wpf.dll',
    'SleepReminder.deps.json',
    'SleepReminder.dll',
    'SleepReminder.exe',
    'SleepReminder.pdb',
    'SleepReminder.runtimeconfig.json'
)
foreach ($f in $files) {
    $s = Join-Path $srcBin $f
    if (Test-Path $s) { Copy-Item $s -Destination $dst -Force }
}

# ---------- 6) 复制 autorestart 目录（看门狗 + 计划任务脚本） ----------
Write-Host '==> 复制 autorestart ...'
$srcAuto = Join-Path $SourceRoot 'autorestart'
$dstAuto = Join-Path $dst 'autorestart'
if (-not (Test-Path $dstAuto)) { New-Item -ItemType Directory -Path $dstAuto | Out-Null }
Copy-Item (Join-Path $srcAuto '*') -Destination $dstAuto -Recurse -Force

# ---------- 7) 启动新版本 ----------
Write-Host '==> 启动新版本 ...'
Start-Process -FilePath (Join-Path $dst 'SleepReminder.exe')

# ---------- 8) 恢复看门狗 ----------
if (Test-Path $manage) {
    Write-Host '==> 恢复看门狗自动拉起 ...'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $manage -Action resume 2>$null
}

Write-Host "部署完成：新版本已写入 $dst 并启动。"

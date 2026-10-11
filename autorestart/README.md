# SleepReminder 自动重启策略

目标：**SleepReminder.exe 被结束（任务管理器「结束任务」、`taskkill /f`、进程崩溃）后自动重新启动**，并且保护机制本身也不容易被绕过。

## 快速安装

在**你自己的账号**下打开 PowerShell（普通权限即可，不需要管理员），执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\SleepReminder\autorestart\install-restart-policy.ps1
```

也可以直接双击 `install.cmd`。装完会打印自检结果。

## 验证策略真的生效

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\SleepReminder\autorestart\manage-restart-policy.ps1 -Action restart-app
```

它会强制结束 SleepReminder，然后等看门狗把它拉回来，正常会在 **2～4 秒**内看到「已自动重启，用时 x.x 秒，新 PID = …」。也可以自己在任务管理器里结束 `SleepReminder.exe`，再运行 `status.cmd` 看日志。

## 日常使用

| 操作 | 命令 |
| --- | --- |
| 查看状态 / 日志 | 双击 `status.cmd`，或 `manage-restart-policy.ps1 -Action status` |
| 临时暂停自动拉起 | `manage-restart-policy.ps1 -Action pause` |
| 恢复自动拉起 | `manage-restart-policy.ps1 -Action resume` |
| 只重启主程序（不卸载策略） | `manage-restart-policy.ps1 -Action restart-app` |
| 卸载策略 | 双击 `uninstall.cmd`（加 `-RemoveLogs` 连日志一起删） |

暂停时看门狗仍在运行，只是不再自动启动主程序；如果你确实想手动关掉 SleepReminder 待一会儿，请用 `pause` 而不是卸载。

## 它是怎么工作的

```
用户登录
   |
   +-> 计划任务 "SleepReminder Watchdog"（登录时启动，之后每分钟重复触发，忽略新实例）
          |
          +-> watchdog.ps1 常驻循环，每 2 秒检查一次 SleepReminder.exe
                 +- 进程在   -> 什么都不做
                 +- 进程不在 -> 立即重新拉起（约 2 秒内）
                 +- 刚启动就退出 -> 退避 5/10/20/40/60 秒，避免崩溃循环刷屏
```

两层保护：

1. **看门狗**负责把主程序拉回来。它判断的是「进程是否存在」，所以不管进程是被温和结束还是被 `taskkill /f` 强杀，都会恢复（不像 Windows 服务的「恢复策略」那样依赖退出码）。
2. **计划任务本身**每分钟重复触发一次，且设置为「忽略新实例」：看门狗要是也被杀掉了，一分钟内会被任务重新拉起，之后几秒内主程序也就回来了。

日志：`autorestart\logs\watchdog.log`（超过 1 MB 自动滚动为 `.log.1`），记录每次「检测到进程未运行 / 已重新启动 / 崩溃退避」。

## 为什么不用 Windows 服务 / `sc failure`

SleepReminder 是带托盘的 WPF 桌面程序，必须跑在**登录用户的交互会话**里，托盘图标才可见。Windows 服务跑在 Session 0，看不到桌面，装成服务会出现「进程在跑但托盘/窗口不出现」的问题。计划任务用「当前用户 + 交互式令牌」运行，既能正常显示托盘，又能靠 Task Scheduler 服务给看门狗提供自愈，所以是这类桌面程序最合适的做法。

## 可调参数

安装脚本参数（例如 `install.cmd -PollSeconds 1`）：

* `-AppPath`：主程序路径，默认 `D:\SleepReminder\SleepReminder.exe`
* `-AppArguments`：启动参数，默认空
* `-TaskName`：计划任务名，默认 `SleepReminder Watchdog`
* `-PollSeconds`：轮询间隔秒数，默认 2（越小恢复越快）
* `-NoRegister`：只在 `autorestart` 目录生成 `watchdog-task.xml`，不注册任务
* `-SkipStart`：注册后不立即启动看门狗

计划任务的完整定义会写到 `autorestart\watchdog-task.xml`（UTF-16），可以直接打开检查。

如果 `-AppArguments` 里带引号或特殊字符，命令行转义容易出错：建议先用 `-NoRegister` 生成 XML，手动改里面的 `-AppArguments`，再用 `schtasks /Create /TN "SleepReminder Watchdog" /XML D:\SleepReminder\autorestart\watchdog-task.xml /F` 注册。

## 说明与限制

* 恢复延迟：主程序 **≤ 约 2 秒**；看门狗自身被杀 **≤ 约 1 分钟**（Task Scheduler 重复触发的最小粒度是 1 分钟）。
* 注册计划任务不需要管理员，但需要在你自己的登录会话里运行安装脚本；如果报「访问被拒绝」，请确认没有在受限/沙箱账户下执行。
* 任务在登录时启动，因此主程序也会跟着登录自动运行。
* 如果只想让程序安静一会儿，用 `-Action pause`；如果要彻底撤销，用 `uninstall.cmd`。直接删掉整个 `autorestart` 目录会让任务反复失败（找不到脚本），请用卸载脚本。

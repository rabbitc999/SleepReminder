using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace SleepReminder
{
    public partial class App : Application
    {
        private static Mutex _mutex = new Mutex(true, "SleepReminder_Mutex");
        private static Mutex _daemonMutex; // 守护进程互斥体
        private DispatcherTimer _watchdogTimer;

        public static bool IsDebugMode => Debugger.IsAttached;

        /// <summary>
        /// 测试模式：即调试模式（Debugger.IsAttached）。开启后“今天已延后”的记忆
        /// 只保存在内存中、不写入注册表，既不会污染真实的跨重启记忆，
        /// 也便于单独测试该逻辑（每次启动都是全新状态）。
        /// </summary>
        public static bool IsTestMode => IsDebugMode;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 检查是否以守护进程模式启动
            bool isDaemon = false;
            int mainPid = 0;
            foreach (string arg in e.Args)
            {
                if (arg.Equals("--daemon", StringComparison.OrdinalIgnoreCase))
                {
                    isDaemon = true;
                }
                else if (arg.StartsWith("--pid=", StringComparison.OrdinalIgnoreCase))
                {
                    string pidStr = arg.Substring(6);
                    int.TryParse(pidStr, out mainPid);
                }
            }

            if (isDaemon)
            {
                // 运行守护进程
                RunDaemon(mainPid);
                // 守护进程不显示UI，直接退出
                Shutdown();
                return;
            }

            // 正常启动主程序
            if (!_mutex.WaitOne(TimeSpan.Zero, true))
            {
                // 已有实例，激活现有窗口
                if (Application.Current.MainWindow != null)
                {
                    var win = Application.Current.MainWindow;
                    win.Visibility = Visibility.Visible;
                    win.WindowState = WindowState.Normal;
                    win.Topmost = true;
                    win.Activate();
                    win.Focus();
                    win.Topmost = false;
                }
                Shutdown();
                return;
            }

            base.OnStartup(e);
            this.Exit += (s, args) =>
            {
                _mutex.ReleaseMutex();
                SignalNormalExit();
            };

            // 启动守护进程和监控
            if (!IsDebugMode)
            {
                StartDaemon();
                StartWatchdog();   // ← 新增
            }

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        private void StartDaemon()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                int pid = Process.GetCurrentProcess().Id;
                ProcessStartInfo psi = new ProcessStartInfo(exePath, $"--daemon --pid={pid}");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process daemon = Process.Start(psi);
            }
            catch (Exception ex)
            {
            }
        }

        private void RunDaemon(int mainPid)
        {
            // 创建互斥体，标识守护进程运行
            _daemonMutex = new Mutex(true, "Global\\SleepReminderDaemonMutex");
            // 创建退出事件
            using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\SleepReminderExitEvent"))
            {
                bool shouldExit = false;
                while (!shouldExit)
                {
                    // 检查退出事件
                    if (exitEvent.WaitOne(0))
                    {
                        shouldExit = true;
                        break;
                    }

                    // 检查主进程是否存在
                    try
                    {
                        Process p = Process.GetProcessById(mainPid);
                        if (p.HasExited)
                        {
                            // 再次检查退出事件（可能在检查期间被设置）
                            if (exitEvent.WaitOne(0))
                            {
                                shouldExit = true;
                                break;
                            }
                            else
                            {
                                // 非正常退出，重启主程序
                                RestartMainProcess();
                                shouldExit = true;
                                break;
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        // 进程不存在
                        if (exitEvent.WaitOne(0))
                        {
                            shouldExit = true;
                            break;
                        }
                        else
                        {
                            RestartMainProcess();
                            shouldExit = true;
                            break;
                        }
                    }

                    Thread.Sleep(2000); // 每2秒检查一次
                }
            }
        }

        private void RestartMainProcess()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(exePath);
            }
            catch { }
        }

        public static void SignalNormalExit()
        {
            try
            {
                using (var exitEvent = EventWaitHandle.OpenExisting("Global\\SleepReminderExitEvent"))
                {
                    exitEvent.Set();
                }
            }
            catch { }
        }

        private void StartWatchdog()
        {
            _watchdogTimer = new DispatcherTimer();
            _watchdogTimer.Interval = TimeSpan.FromSeconds(5); // 每5秒检查一次
            _watchdogTimer.Tick += (s, e) => WatchDaemon();
            _watchdogTimer.Start();
        }

        private void WatchDaemon()
        {
            try
            {
                // 尝试打开守护进程的互斥体
                bool exists = Mutex.TryOpenExisting("Global\\SleepReminderDaemonMutex", out _);
                if (!exists)
                {
                    // 守护进程已消失，重新启动
                    StartDaemon();
                }
            }
            catch
            {
                // 如果异常（如权限问题），也尝试重启
                StartDaemon();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _watchdogTimer?.Stop();
            SignalNormalExit();
            base.OnExit(e);
        }
    }
}
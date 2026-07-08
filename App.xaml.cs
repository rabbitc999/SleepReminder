using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace SleepReminder
{
    public partial class App : Application
    {
        private static Mutex _mutex = new Mutex(true, "SleepReminder_Mutex");

        public static bool IsDebugMode => Debugger.IsAttached;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!_mutex.WaitOne(TimeSpan.Zero, true))
            {
                // 已有实例，激活现有窗口
                if (Application.Current.MainWindow != null)
                {
                    var win = Application.Current.MainWindow;
                    win.Visibility = Visibility.Visible;
                    win.WindowState = WindowState.Normal; // 或根据调试模式决定
                    win.Topmost = true; // 临时置顶
                    win.Activate();
                    win.Focus();
                    win.Topmost = false; // 可恢复
                }
                // 退出当前实例
                Shutdown();
                return;
            }

            base.OnStartup(e);
            // 注册退出释放互斥体
            this.Exit += (s, args) => { _mutex.ReleaseMutex(); };
        }
    }
}
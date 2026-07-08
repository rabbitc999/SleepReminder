using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Timer = System.Threading.Timer;

namespace SleepReminder
{
    public partial class MainWindow : Window
    {
        private System.Windows.Threading.DispatcherTimer timer;
        private System.Windows.Threading.DispatcherTimer killTaskMgrTimer;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private DateTime? lastReminderDate = null; // 记录上次提醒的日期
        private bool _hasSnoozedToday = false; // 当天是否已使用延后
        private bool _canAttemptClose = true;

        public MainWindow()
        {
            InitializeComponent();

            // ---------- 根据调试模式设置窗口初始状态 ----------
            if (App.IsDebugMode)
            {
                this.Visibility = Visibility.Hidden;
                this.WindowState = WindowState.Normal;
                this.WindowStyle = WindowStyle.SingleBorderWindow; // 或者默认
                this.Topmost = false;
            }
            else
            {
                this.Visibility = Visibility.Hidden;
                this.WindowState = WindowState.Maximized;
                this.WindowStyle = WindowStyle.None;
                this.Topmost = true;
            }

            InitializeTray();
            SetAutoStart(true);
            SetupTimer();

            this.Closing += MainWindow_Closing;
        }

        // ---------- 托盘 ----------
        private void InitializeTray()
        {
            trayIcon = new NotifyIcon();
            trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
            trayIcon.Text = "睡眠提醒器";
            trayIcon.Visible = true;

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("设置提醒时间", null, OnSetTime);
            trayIcon.ContextMenuStrip = trayMenu;
        }

        private void OnSetTime(object sender, EventArgs e)
        {
            var setWin = new SetTimeWindow();
            if (setWin.ShowDialog() == true)
            {
                // 保存操作已在SetTimeWindow中完成，只需重置提醒状态
                lastReminderDate = null; // 让下次检测重新触发
                SetupTimer();
            }
        }

        // ---------- 定时提醒 ----------
        private void SetupTimer()
        {
            if (timer != null) timer.Stop();
            timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(5);
            timer.Tick += (s, e) => CheckReminder();
            timer.Start();
            CheckReminder();
        }

        private void CheckReminder()
        {
            var now = DateTime.Now;
            (int sh, int sm, int eh, int em) = RegistryHelper.LoadReminderTime();

            DateTime start = new DateTime(now.Year, now.Month, now.Day, sh, sm, 0);
            DateTime end = new DateTime(now.Year, now.Month, now.Day, eh, em, 0);

            // 跨天处理：如果结束时间早于开始时间，视为次日
            bool isCrossDay = end < start;
            if (isCrossDay)
            {
                end = end.AddDays(1);
            }

            bool isInInterval = (now >= start && now <= end);

            // ---------- 自动隐藏：仅生产模式，且窗口可见但不在区间内 ----------
            if (!isInInterval && this.Visibility == Visibility.Visible)
            {
                HideReminder();
                return; // 不再执行后续提醒
            }

            // ---------- 触发提醒 ----------
            if (isInInterval && this.Visibility != Visibility.Visible)
            {
                if (lastReminderDate == null || lastReminderDate.Value.Date != now.Date)
                {
                    _canAttemptClose = true;
                    _hasSnoozedToday = false; // 新的一天重置延后标记
                    ShowReminder();
                    lastReminderDate = now.Date;
                }
            }
        }

        private void HideReminder()
        {
            this.Visibility = Visibility.Hidden;
            // 停止杀任务管理器
            if (killTaskMgrTimer != null)
            {
                killTaskMgrTimer.Stop();
            }
            // 可选：恢复窗口样式等，但不需要，因为下次提醒会重新设置
        }

        private void ShowReminder()
        {
            this.Visibility = Visibility.Visible;
            this.WindowState = WindowState.Maximized;

            if (!App.IsDebugMode)
            {
                this.Topmost = true;
            }

            this.Activate();
            this.Focus();

            // 启动任务管理器绞杀定时器
            StartKillTaskManager();
        }

        // ---------- 任务管理器检测与终止 ----------
        private void StartKillTaskManager()
        {
            if (killTaskMgrTimer == null)
            {
                killTaskMgrTimer = new System.Windows.Threading.DispatcherTimer();
                killTaskMgrTimer.Interval = TimeSpan.FromSeconds(1);
                killTaskMgrTimer.Tick += (s, e) => KillTaskManager();
            }
            killTaskMgrTimer.Start();
        }

        private void KillTaskManager()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("taskmgr");
                foreach (var p in procs)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(500);
                    }
                    catch { /* 可能无权限或进程已退出 */ }
                }
            }
            catch { /* 忽略异常 */ }
        }

        // ---------- 生产模式阻止关闭 ----------
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (this.Visibility == Visibility.Visible)
            {
                e.Cancel = true;
                MessageBox.Show("别想关！快去睡觉！", "强制提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- 开机自启动（确保写入 EXE 路径） ----------
        private void SetAutoStart(bool enable)
        {
            try
            {
                string appName = "SleepReminder";
                string appPath = Assembly.GetEntryAssembly().Location;
                if (Path.GetExtension(appPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    appPath = Path.ChangeExtension(appPath, ".exe");

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enable)
                    {
                        key.SetValue(appName, appPath);
                        // 验证是否写入成功
                        string written = key.GetValue(appName) as string;
                        if (written != appPath)
                            MessageBox.Show($"开机自启动写入失败，预期路径：{appPath}\n实际值：{written ?? "（无）"}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                        else if (App.IsDebugMode && App.IsDebugMode)
                            MessageBox.Show($"开机自启动已设置为：{appPath}", "调试信息", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        key.DeleteValue(appName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"设置开机自启动失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShutdownButton_Click(object sender, RoutedEventArgs e)
        {
            // 确认对话框（置顶显示）
            MessageBoxResult result = MessageBox.Show(
                "确认要立即关机吗？\n所有未保存的工作将丢失！",
                "关机确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    // 执行关机命令（0秒延迟）
                    Process.Start("shutdown", "/s /t 0");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"关机失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // 添加延后按钮事件
        private void SnoozeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hasSnoozedToday)
            {
                MessageBox.Show("今天已经延后过一次了，不能再延后！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 隐藏主窗口（停止杀进程）
            HideReminder();
            _hasSnoozedToday = true;
            int seconds = App.IsDebugMode ? 10 : 600; // 调试模式10秒，生产模式10分钟

            // 创建倒计时窗口（10分钟 = 600秒）
            var snoozeWin = new SnoozeWindow(seconds, () =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    // 检查是否仍处于提醒区间
                    var now = DateTime.Now;
                    (int sh, int sm, int eh, int em) = RegistryHelper.LoadReminderTime();
                    DateTime start = new DateTime(now.Year, now.Month, now.Day, sh, sm, 0);
                    DateTime end = new DateTime(now.Year, now.Month, now.Day, eh, em, 0);
                    if (end < start) end = end.AddDays(1);

                    if (now >= start && now <= end)
                    {
                        ShowReminder(); // 重新显示主窗口（会再次启动杀进程）
                    }
                    else
                    {
                        // 已过区间，保持隐藏，并重置延后标记以便下次
                        _hasSnoozedToday = false;
                    }
                });
            });

            snoozeWin.Show();
        }

        private void CloseReminder_Click(object sender, RoutedEventArgs e)
        {
            // 如果今天已经答错过，直接提示并返回
            if (!_canAttemptClose)
            {
                MessageBox.Show("您已经答错过一次，无法再通过答题关闭窗口！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 创建答题窗口实例
            var answerWin = new AnswerWindow();

            // 以模态对话框方式显示，并获取返回结果
            bool? result = answerWin.ShowDialog();

            // 判断用户是否答对了所有题（包括Boss题）
            if (result == true && answerWin.IsAllCorrect)
            {
                // 全部正确 → 关闭提醒（隐藏霸屏）
                HideReminder();
                // 记录当天已成功关闭，防止再次触发
                lastReminderDate = DateTime.Now.Date;
            }
            else if (result == false && answerWin.HasWrongAnswer)
            {
                // 有题目答错 → 取消本次关闭机会
                _canAttemptClose = false;
                MessageBox.Show("您有题目答错，本次提醒无法再通过答题关闭！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            // 如果用户点了取消（没提交），则不做任何操作
        }
    }
}
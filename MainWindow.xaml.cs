using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace SleepReminder
{
    public partial class MainWindow : Window
    {
        private System.Windows.Threading.DispatcherTimer timer;
        private System.Windows.Threading.DispatcherTimer killTaskMgrTimer;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private bool _hasSnoozedToday = false; // 当天是否已使用延后
        private bool _canAttemptClose = true;
        private bool _isInInterval = false;      // 当前是否处于提醒区间
        private bool _triggeredThisInterval = false; // 当前区间是否已触发过

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
            trayMenu.Items.Add("设置延后时长", null, OnSetSnooze);
            trayMenu.Items.Add("退出", null, OnExitWithQuiz); // 新增
            trayIcon.ContextMenuStrip = trayMenu;
        }

        private void OnExitWithQuiz(object sender, EventArgs e)
        {
            var answerWin = new AnswerWindow();
            bool? result = answerWin.ShowDialog();

            if (result == true && answerWin.IsAllCorrect)
            {
                Application.Current.Shutdown();
            }
            else
            {
                CustomMessageBox.ShowOk("答题失败或取消，无法退出！", "提示", this);
            }
        }

        private void OnSetSnooze(object sender, EventArgs e)
        {
            var win = new SnoozeSettingWindow();
            win.ShowDialog(); // 用户点击确定后已自动保存
        }

        private void OnSetTime(object sender, EventArgs e)
        {
            var setWin = new SetTimeWindow();
            if (setWin.ShowDialog() == true)
            {
                // 保存操作已在SetTimeWindow中完成，只需重置提醒状态
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
            // 检测状态变化
            if (IsInReminderInterval(DateTime.Now))
            {
                if (!_isInInterval) // 刚进入区间
                {
                    _isInInterval = true;
                    _triggeredThisInterval = false; // 重置触发标志
                }

                // 如果尚未触发，则触发提醒
                if (!_triggeredThisInterval)
                {
                    _triggeredThisInterval = true;
                    ShowReminder(); // 显示霸屏
                }
            }
            else // 不在区间内
            {
                if (_isInInterval) // 刚退出区间
                {
                    _isInInterval = false;
                    _triggeredThisInterval = false;
                }

                // 如果窗口可见，则隐藏
                if (this.Visibility == Visibility.Visible)
                    HideReminder();
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
                CustomMessageBox.ShowOk("别想关！快去睡觉！", "强制提醒", this);
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
                            CustomMessageBox.ShowOk($"开机自启动写入失败，预期路径：{appPath}\n实际值：{written ?? "（无）"}", "警告", this);
                        else if (App.IsDebugMode && App.IsDebugMode)
                            CustomMessageBox.ShowOk($"开机自启动已设置为：{appPath}", "调试信息", this);
                    }
                    else
                    {
                        key.DeleteValue(appName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.ShowOk($"设置开机自启动失败：{ex.Message}", "错误", this);
            }
        }

        private void ShutdownButton_Click(object sender, RoutedEventArgs e)
        {
            // 确认对话框（置顶显示）
            bool confirm = CustomMessageBox.ShowConfirm("确认要立即关机吗？\n所有未保存的工作将丢失！", "关机确认", this);

            if (confirm)
            {
                try
                {
                    // 执行关机命令（0秒延迟）
                    Process.Start("shutdown", "/s /t 0");
                }
                catch (Exception ex)
                {
                    CustomMessageBox.ShowOk($"关机失败：{ex.Message}", "错误", this);
                }
            }
        }

        // 添加延后按钮事件
        private void SnoozeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hasSnoozedToday)
            {
                CustomMessageBox.ShowOk("今天已经延后过一次了，不能再延后！", "提示", this);
                return;
            }

            // 隐藏主窗口（停止杀进程）
            HideReminder();
            StartKillTaskManager();
            _hasSnoozedToday = true;

            // 从注册表读取延后分钟数，默认10分钟
            int minutes = RegistryHelper.LoadSnoozeMinutes();
            // 保证至少1分钟，最多120分钟（已在设置窗口限制，但防御性处理）
            if (minutes < 1) minutes = 1;
            if (minutes > 120) minutes = 120;
            int seconds = minutes * 60; // 转换为秒

            if (App.IsDebugMode) seconds = 10;

            // 创建倒计时窗口（10分钟 = 600秒）
            var snoozeWin = new SnoozeWindow(seconds, () =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (IsInReminderInterval(DateTime.Now))
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

        public static bool IsInReminderInterval(DateTime time)
        {
            var (sh, sm, eh, em) = RegistryHelper.LoadReminderTime();
            var start = new TimeSpan(sh, sm, 0);
            var end = new TimeSpan(eh, em, 0);
            if (start <= end)
                return time.TimeOfDay >= start && time.TimeOfDay <= end;
            else
                return time.TimeOfDay >= start || time.TimeOfDay <= end;
        }

        private void CloseReminder_Click(object sender, RoutedEventArgs e)
        {
            // 如果今天已经答错过，直接提示并返回
            if (!_canAttemptClose)
            {
                CustomMessageBox.ShowOk("您已经答错过一次，无法再通过答题关闭窗口！", "提示", this);
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
            }
            else if (result == false && answerWin.HasWrongAnswer)
            {
                // 有题目答错 → 取消本次关闭机会
                _canAttemptClose = false;
                CustomMessageBox.ShowOk("您有题目答错，本次提醒无法再通过答题关闭！", "提示", this);
            }
            // 如果用户点了取消（没提交），则不做任何操作
        }
    }
}
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        private bool _isInInterval = false;      // 当前是否处于提醒区间
        private bool _triggeredThisInterval = false; // 当前区间是否已触发过
        private bool _isSnoozing = false;

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
            ApplyAppearance();

            if (!App.IsDebugMode)
            {
                this.Closing += MainWindow_Closing;
            }
        }

        // ---------- 外观（背景图 / 文字） ----------

        /// <summary>
        /// 按 <see cref="AppearanceSettings"/> 应用提醒页外观：重建条目正文、
        /// 设置大标题与小标题，并按需装载背景图片。图片读取失败时静默退回纯色背景，
        /// 不会因为一张图挂掉整个提醒。
        /// </summary>
        private void ApplyAppearance()
        {
            AppearanceSettings appearance = AppearanceSettings.Current;

            txtWarningTitle.Text = appearance.Title;
            tbTipsHeader.Text = appearance.TipsHeader;
            tbTipsHeader.Visibility = appearance.TipsHeader.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 保留标题（第 0 个子元素），其余条目正文全部重建
            for (int i = spWarningTips.Children.Count - 1; i >= 1; i--)
                spWarningTips.Children.RemoveAt(i);

            foreach (string tip in appearance.Tips)
            {
                spWarningTips.Children.Add(new TextBlock
                {
                    Text = tip,
                    FontSize = 24,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 8, 0, 8),
                    TextAlignment = TextAlignment.Center
                });
            }

            ApplyBackgroundImage(appearance);
        }

        /// <summary>装载并显示自定义背景图片；无图或读取失败时退回纯色背景。</summary>
        /// <param name="appearance">当前外观设置。</param>
        private void ApplyBackgroundImage(AppearanceSettings appearance)
        {
            if (!appearance.HasUsableImage)
            {
                imgWarningBackground.Source = null;
                imgWarningBackground.Visibility = Visibility.Collapsed;
                rectWarningOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // 读完即释放文件句柄，避免锁文件
                bitmap.UriSource = new Uri(appearance.BackgroundImagePath);
                bitmap.EndInit();
                bitmap.Freeze();

                imgWarningBackground.Source = bitmap;
                imgWarningBackground.Visibility = Visibility.Visible;
                rectWarningOverlay.Visibility = Visibility.Visible;
            }
            catch
            {
                imgWarningBackground.Source = null;
                imgWarningBackground.Visibility = Visibility.Collapsed;
                rectWarningOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // ---------- 托盘 ----------
        private void InitializeTray()
        {
            trayIcon = new NotifyIcon();
            trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
            trayIcon.Text = "睡眠提醒器";
            trayIcon.Visible = true;

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("预览", null, OnPreview);
            trayMenu.Items.Add("设置", null, OnSetSettings);
            trayMenu.Items.Add("提醒外观", null, OnSetAppearance);
            trayIcon.ContextMenuStrip = trayMenu;
        }

        /// <summary>
        /// 打开预览窗口：看额度和近期作息（只读窗口，程序不提供界面退出入口）。
        /// </summary>
        private void OnPreview(object sender, EventArgs e)
        {
            var win = new QuotaPreviewWindow();
            win.ShowDialog();
        }

        /// <summary>打开提醒外观设置窗口（背景图片与页面文字，改完立即生效）。</summary>
        private void OnSetAppearance(object sender, EventArgs e)
        {
            var win = new AppearanceSettingsWindow();
            if (win.ShowDialog() == true)
            {
                ApplyAppearance();
            }
        }

        /// <summary>
        /// 打开统一设置窗口：提醒时间、延后时长、额度与周制都在这里改。
        /// 提醒时间与延后时长不受限制，额度组由窗口内部按"每周一次"的锁自行置灰。
        /// </summary>
        private void OnSetSettings(object sender, EventArgs e)
        {
            if (_isSnoozing)
            {
                CustomMessageBox.ShowOk("延后期间不能修改设置！", "提示", this);
                return;
            }

            var win = new SettingsWindow();
            if (win.ShowDialog() == true)
            {
                // 提醒时间可能被改过，重启定时器让新区间立即生效
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

            // 刷新额度摘要：进提醒时让用户先看到本夜还能延后多少
            RefreshQuotaHint();

            // 外观可能刚被改过，进提醒时重新应用一次
            ApplyAppearance();

            this.Activate();
            this.Focus();

            // 启动任务管理器绞杀定时器
            StartKillTaskManager();
        }

        /// <summary>把当前额度摘要刷新到主窗口右上角（取不到就留空）。</summary>
        private void RefreshQuotaHint()
        {
            try
            {
                txtQuotaHint.Text = SleepLedger.DescribeQuota(DateTime.Now);
            }
            catch
            {
                txtQuotaHint.Text = string.Empty;
            }
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

        // 添加延后按钮事件（借贷机制：延后 = 从额度里"借款"）
        private void SnoozeButton_Click(object sender, RoutedEventArgs e)
        {
            DateTime now = DateTime.Now;

            // 1) 先看额度：这一夜还能延后多少分钟
            int available = SleepLedger.GetAvailableMinutes(now);
            if (available <= 0)
            {
                CustomMessageBox.ShowOk(
                    "今晚的延后额度已经用完，不能再延后了。\n\n" +
                    SleepLedger.DescribeQuota(now) + "\n\n" +
                    "额度按“夜”结算：固定额度每夜白送、不可累积；\n" +
                    "附加额度按滚动 7 天回补。关机 / 重启即为一夜的终点。",
                    "额度已用尽", this);
                return;
            }

            // 2) 取本次想延后的分钟数，并夹在剩余额度之内
            int wanted = RegistryHelper.LoadSnoozeMinutes();
            if (wanted < 1) wanted = 1;
            if (wanted > 120) wanted = 120;
            int planned = Math.Min(wanted, available);

            if (planned < wanted)
            {
                bool go = CustomMessageBox.ShowConfirm(
                    $"本次想延后 {wanted} 分钟，但今晚只剩 {planned} 分钟额度。\n\n" +
                    SleepLedger.DescribeQuota(now) + "\n\n" +
                    $"是否按剩余额度延后 {planned} 分钟？",
                    "额度不足", this);
                if (!go)
                    return;
            }

            StartSnooze(planned);
        }

        /// <summary>
        /// “稍后继续”：重新弹出倒计时小窗，**重新开始计时**。
        ///
        /// 它不额外放贷，也不给折扣：能延后的时长仍由
        /// <see cref="SleepLedger.GetAvailableMinutes"/> 决定（本夜已延后的部分早已算进去，
        /// 因为每一次延后都在结束时按实际经过分钟入账），所以重复点它只会一次次消耗本夜额度，
        /// 并不会凭空多出时间。额度见底时与“延后保存数据”一样给出提示。
        /// </summary>
        private void ResumeSnoozeButton_Click(object sender, RoutedEventArgs e)
        {
            int planned = Math.Min(RegistryHelper.LoadSnoozeMinutes(),
                SleepLedger.GetAvailableMinutes(DateTime.Now));

            if (planned <= 0)
            {
                CustomMessageBox.ShowOk(
                    "今晚的延后额度已经用完，不能再延后了。\n\n" +
                    SleepLedger.DescribeQuota(DateTime.Now) + "\n\n" +
                    "额度按“夜”结算：固定额度每夜白送、不可累积；\n" +
                    "附加额度按滚动 7 天回补。关机 / 重启即为一夜的终点。",
                    "额度已用尽", this);
                return;
            }

            StartSnooze(planned);
        }

        /// <summary>
        /// 起一笔延后：记“延后中”锚点、隐藏霸屏、弹出倒计时小窗，并在结束时结清。
        ///
        /// 记账策略：倒计时正常走完 / 用户点“返回提醒页” → 结束时按"实际经过"入账；
        /// 被强杀 / 关机 / 注销 → 由下次启动的启动对账补上。
        /// 两条路都会清掉"延后中"标记，因此同一次延后只会结算一次。
        /// 调试 / 测试模式跳过账本写入，避免污染真实的跨重启状态。
        /// </summary>
        /// <param name="plannedMinutes">本次计划延后的分钟数（调用方已确保在剩余额度以内）。</param>
        private void StartSnooze(int plannedMinutes)
        {
            if (_isSnoozing)
                return;

            bool persist = !App.IsTestMode;
            DateTime snoozeStart = DateTime.Now;
            if (persist)
                SleepLedger.BeginSnooze(snoozeStart);

            // 隐藏主窗口；但继续绞杀任务管理器，防止用户顺手杀掉倒计时窗口
            HideReminder();
            StartKillTaskManager();
            _isSnoozing = true;
            trayIcon.Text = "⏰ 睡眠提醒器 - 延后中";  // 托盘提示变更

            int seconds = plannedMinutes * 60;
            if (App.IsDebugMode) seconds = 10;

            // 倒计时窗口里的两行额度文本都按"本次已流逝秒数"实时算：
            // 固定额度因此会随倒计时逐秒递减，而不是停在进入延后那一刻的快照上。
            Action? heartbeat = persist
                ? (Action)(() => SleepLedger.WriteHeartbeat(DateTime.Now))
                : null;

            var snoozeWin = new SnoozeWindow(seconds,
                elapsed => SleepLedger.DescribeLiveFixedQuota(snoozeStart, elapsed),
                elapsed => SleepLedger.DescribeLiveSummary(snoozeStart, elapsed),
                () =>
            {
                // 用 BeginInvoke 等倒计时窗口真正关闭之后再收尾，
                // 免得重新出现的霸屏被还没关掉的倒计时窗口压在下面。
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // ---------- 延后结束：静默结清这一笔，直接回霸屏 ----------
                    // 账照记（按实际经过分钟入账 + 清"延后中"锚点），只是不再弹结算窗打扰。
                    // 用户点“返回提醒页”提前收起时走的也是这里，因此已跑掉的分钟照扣、不会重复计费。
                    _isSnoozing = false;
                    trayIcon.Text = "睡眠提醒器";       // 恢复托盘提示

                    if (persist)
                    {
                        SleepLedger.SettleSnooze(snoozeStart, DateTime.Now);
                    }

                    if (IsInReminderInterval(DateTime.Now))
                    {
                        ShowReminder(); // 重新显示主窗口（会再次启动杀进程）
                    }
                    // 已过区间则保持隐藏（常见情形：延后跨过零点，这一夜就此结束）
                }));
            }, heartbeat);

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

    }
}

using System;
using System.Windows;
using System.Windows.Threading;

namespace SleepReminder
{
    /// <summary>
    /// 延后倒计时窗口：一个不可关闭的置顶小窗，显示剩余时间与当前额度摘要。
    ///
    /// 除了倒计时，它还承担两项与账本相关的职责：
    /// <list type="bullet">
    ///   <item><description>倒计时结束 → 回调 <c>onComplete</c>，由调用方结清本次延后；</description></item>
    ///   <item><description>周期性回调 <c>onHeartbeat</c>，作为"程序此刻还活着"的证据，
    ///     使进程被强杀后仍能按最后心跳对账计费。</description></item>
    /// </list>
    /// </summary>
    public partial class SnoozeWindow : Window
    {
        /// <summary>写一次心跳之间的间隔（秒），避免每秒都写注册表。</summary>
        private const int HeartbeatIntervalSeconds = 10;

        private int _remainingSeconds;
        private DispatcherTimer _timer;
        private readonly Action _onComplete;
        private readonly Action? _onHeartbeat;
        private int _ticksSinceHeartbeat;

        /// <summary>构造延后倒计时窗口。</summary>
        /// <param name="seconds">倒计时总秒数。</param>
        /// <param name="quotaSummary">显示在倒计时下方的额度摘要文本。</param>
        /// <param name="onComplete">倒计时归零时执行的回调。</param>
        /// <param name="onHeartbeat">周期性心跳回调，可为 null。</param>
        public SnoozeWindow(int seconds, string quotaSummary, Action onComplete, Action? onHeartbeat = null)
        {
            InitializeComponent();
            _remainingSeconds = seconds;
            _onComplete = onComplete;
            _onHeartbeat = onHeartbeat;
            txtQuota.Text = quotaSummary ?? string.Empty;

            // 窗口位置：右上角（距屏幕右边缘20像素，上边缘20像素）
            var screenWidth = SystemParameters.WorkArea.Width;
            this.Left = screenWidth - this.Width - 20;
            this.Top = 20;

            this.Closing += SnoozeWindow_Closing;

            UpdateDisplay();
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _remainingSeconds--;
            UpdateDisplay();

            // 周期性写心跳，作为对账依据
            if (_onHeartbeat != null)
            {
                _ticksSinceHeartbeat++;
                if (_ticksSinceHeartbeat >= HeartbeatIntervalSeconds)
                {
                    _ticksSinceHeartbeat = 0;
                    try
                    {
                        _onHeartbeat();
                    }
                    catch
                    {
                        // 心跳失败不影响倒计时
                    }
                }
            }

            if (_remainingSeconds <= 0)
            {
                _timer.Stop();
                _timer = null;
                _onComplete?.Invoke();
                this.Close(); // 关闭窗口
            }
        }

        private void UpdateDisplay()
        {
            int minutes = _remainingSeconds / 60;
            int seconds = _remainingSeconds % 60;
            txtCountdown.Text = $"{minutes:D2}:{seconds:D2}";
        }

        private void SnoozeWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 阻止用户关闭窗口（Alt+F4、任务栏关闭等）
            if (_remainingSeconds > 0)
            {
                e.Cancel = true;
            }
        }
    }
}

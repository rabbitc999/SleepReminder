using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SleepReminder
{
    /// <summary>
    /// 延后倒计时窗口：一个不可关闭的置顶小窗，显示剩余时间与当前额度。
    ///
    /// 窗口里有两个时间：上面大号的是<b>本次延后的剩余时间</b>；下面那行
    /// <b>固定额度倒计时</b>是本夜固定额度的真实余额——它随本次延后逐秒递减，
    /// 扣完才转到"附加池"，因此看到的不是进入延后那一刻的快照。
    /// 再下面一行是附加池余额与本夜类型（同样按本次已流逝的整分钟实时投影）。
    ///
    /// 除了倒计时，它还承担两项与账本相关的职责：
    /// <list type="bullet">
    ///   <item><description>倒计时结束 → 回调 <c>onComplete</c>，由调用方结清本次延后；</description></item>
    ///   <item><description>周期性回调 <c>onHeartbeat</c>，作为"程序此刻还活着"的证据，
    ///     使进程被强杀后仍能按最后心跳对账计费。</description></item>
    /// </list>
    ///
    /// 本窗口<b>不写账本</b>：界面上的余额都由调用方以"已流逝秒数"为参数实时算出来，
    /// 真正的入账只在倒计时结束时发生一次。
    ///
    /// 窗口<b>可以拖动</b>（没有标题栏，靠自己实现的拖动）：按住窗口任意处拖即可，
    /// 双击回到默认的屏幕右上角。拖动只改变窗口位置，不影响倒计时与计费。
    /// </summary>
    public partial class SnoozeWindow : Window
    {
        /// <summary>写一次心跳之间的间隔（秒），避免每秒都写注册表。</summary>
        private const int HeartbeatIntervalSeconds = 10;

        /// <summary>按住多久算"拖动"（毫秒）；短于此且位移也很小，判为单击（不做事）。</summary>
        private const int DragThresholdMs = 150;

        /// <summary>判定"几乎没有移动"的像素阈值，用于区分单击与拖动。</summary>
        private const double DragThresholdPixels = 4;

        /// <summary>本次延后的总秒数，用于反算已流逝的秒数。</summary>
        private readonly int _totalSeconds;

        private int _remainingSeconds;
        private DispatcherTimer _timer;
        private readonly Func<int, string> _fixedQuotaText;
        private readonly Func<int, string> _summaryText;
        private readonly Action _onComplete;
        private readonly Action? _onHeartbeat;
        private int _ticksSinceHeartbeat;

        // ---------- 拖动状态 ----------
        /// <summary>本次左键按下时的屏幕坐标。</summary>
        private Point _dragStartScreen;

        /// <summary>按下时窗口左上角的屏幕坐标；与上式一起构成"零漂移"式拖动基准。</summary>
        private Point _dragStartWindow;

        /// <summary>按下时刻，用于区分单击与拖动。</summary>
        private DateTime _dragStartTime;

        /// <summary>本次按下之后是否真的拖动过。</summary>
        private bool _dragged;

        /// <summary>构造延后倒计时窗口。</summary>
        /// <param name="seconds">倒计时总秒数。</param>
        /// <param name="fixedQuotaText">按"已流逝秒数"生成固定额度倒计时文本的回调。</param>
        /// <param name="summaryText">按"已流逝秒数"生成附加池与夜型摘要的回调。</param>
        /// <param name="onComplete">倒计时归零时执行的回调。</param>
        /// <param name="onHeartbeat">周期性心跳回调，可为 null。</param>
        public SnoozeWindow(int seconds, Func<int, string> fixedQuotaText, Func<int, string> summaryText,
            Action onComplete, Action? onHeartbeat = null)
        {
            InitializeComponent();
            _totalSeconds = seconds;
            _remainingSeconds = seconds;
            _fixedQuotaText = fixedQuotaText;
            _summaryText = summaryText;
            _onComplete = onComplete;
            _onHeartbeat = onHeartbeat;

            // 初始位置：屏幕右上角（之后用户可以按住窗口拖到任意位置，双击回位）
            ResetPosition();

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

        /// <summary>
        /// 刷新三个显示项：本次延后倒计时、固定额度倒计时、附加池与夜型摘要。
        /// 后两项按"已流逝秒数"实时算出，因此固定额度是真实的余额而不是快照。
        /// </summary>
        private void UpdateDisplay()
        {
            int elapsed = _totalSeconds - _remainingSeconds;

            int minutes = _remainingSeconds / 60;
            int seconds = _remainingSeconds % 60;
            txtCountdown.Text = $"{minutes:D2}:{seconds:D2}";

            txtFixedQuota.Text = SafeText(_fixedQuotaText, elapsed);
            txtQuota.Text = SafeText(_summaryText, elapsed);
        }

        /// <summary>调用额度文本回调；回调抛异常时退回空串，绝不让显示层拖垮倒计时。</summary>
        /// <param name="provider">额度文本回调，可为 null。</param>
        /// <param name="elapsedSeconds">本次延后已经流逝的秒数。</param>
        /// <returns>要显示的文本。</returns>
        private static string SafeText(Func<int, string> provider, int elapsedSeconds)
        {
            try
            {
                return provider?.Invoke(elapsedSeconds) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // ---------- 拖动窗口 ----------
        //
        // 窗口没有标题栏（WindowStyle=None），默认拖不动。这里自己实现：
        // 以"按下时的屏幕坐标 + 当时窗口位置"为基准，移动时把位移加到基准位置上，
        // 这样按住哪一处都不会跳。鼠标移出窗口后仍要继续跟手，所以在按下时捕获鼠标
        // （Mouse.Capture），抬起或离开时释放。

        /// <summary>左键按下：记录拖动基准并捕获鼠标。</summary>
        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _dragStartWindow = new Point(this.Left, this.Top);
            _dragStartTime = DateTime.Now;
            _dragged = false;

            Mouse.Capture((IInputElement)sender);
            e.Handled = true;
        }

        /// <summary>按住移动：按位移移动窗口，并标记"确实拖动过"。</summary>
        private void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            Point current = PointToScreen(e.GetPosition(this));
            double dx = current.X - _dragStartScreen.X;
            double dy = current.Y - _dragStartScreen.Y;

            if (!_dragged &&
                (Math.Abs(dx) > DragThresholdPixels || Math.Abs(dy) > DragThresholdPixels))
                _dragged = true;

            if (!_dragged)
                return;

            this.Left = _dragStartWindow.X + dx;
            this.Top = _dragStartWindow.Y + dy;
        }

        /// <summary>左键抬起：释放鼠标捕获。短按且没怎么动 → 视为双击回位判据。</summary>
        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Mouse.Capture(null);

            bool quickClick = (DateTime.Now - _dragStartTime).TotalMilliseconds < DragThresholdMs;
            if (!_dragged && quickClick && e.ClickCount >= 2)
                ResetPosition();
        }

        /// <summary>鼠标离开窗口：只要没按住就释放捕获，避免吞掉别的窗口的点击。</summary>
        private void Root_MouseLeave(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                Mouse.Capture(null);
        }

        /// <summary>把窗口挪回默认位置（屏幕右上角）。</summary>
        private void ResetPosition()
        {
            this.Left = SystemParameters.WorkArea.Width - this.Width - 20;
            this.Top = 20;
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

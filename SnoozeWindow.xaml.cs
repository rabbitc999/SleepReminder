using System;
using System.Windows;
using System.Windows.Threading;

namespace SleepReminder
{
    public partial class SnoozeWindow : Window
    {
        private int _remainingSeconds;
        private DispatcherTimer _timer;
        private Action _onComplete;

        public SnoozeWindow(int seconds, Action onComplete)
        {
            InitializeComponent();
            _remainingSeconds = seconds;
            _onComplete = onComplete;

            // 窗口位置：右上角（距屏幕右边缘20像素，上边缘20像素）
            var screenWidth = SystemParameters.WorkArea.Width;
            var screenHeight = SystemParameters.WorkArea.Height;
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
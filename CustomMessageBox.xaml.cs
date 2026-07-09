using System;
using System.Windows;
using System.Windows.Controls;

namespace SleepReminder
{
    public partial class CustomMessageBox : Window
    {
        // 按钮点击结果
        public enum ButtonResult
        {
            Left,
            Right,
            None
        }

        private ButtonResult _result = ButtonResult.None;

        private CustomMessageBox(string title, string message,
                                 string leftButtonText = "确定",
                                 string rightButtonText = "取消",
                                 bool showRightButton = true)
        {
            InitializeComponent();

            tbTitle.Text = title;
            tbMessage.Text = message;
            btnLeft.Content = leftButtonText;
            btnRight.Content = rightButtonText;

            // 如果不需要右按钮，则隐藏
            if (!showRightButton)
            {
                btnRight.Visibility = Visibility.Collapsed;
            }

            // 默认焦点在左按钮
            btnLeft.Focus();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn?.Tag?.ToString() == "left")
                _result = ButtonResult.Left;
            else if (btn?.Tag?.ToString() == "right")
                _result = ButtonResult.Right;
            else
                _result = ButtonResult.None;

            this.DialogResult = true;
            Close();
        }

        // ---------- 静态方法：显示消息框 ----------
        public static ButtonResult Show(string message, string title = "提示",
                                        string leftButtonText = "确定",
                                        string rightButtonText = "取消",
                                        bool showRightButton = true,
                                        Window owner = null)
        {
            var msgBox = new CustomMessageBox(title, message, leftButtonText, rightButtonText, showRightButton);
            if (owner != null && owner.IsVisible)
                msgBox.Owner = owner;
            msgBox.ShowDialog();
            return msgBox._result;
        }

        // 简化版：只显示“确定”按钮
        public static void ShowOk(string message, string title = "提示", Window owner = null)
        {
            Show(message, title, "确定", "", false, owner);
        }

        // 带确认/取消的
        public static bool ShowConfirm(string message, string title = "确认", Window owner = null)
        {
            var result = Show(message, title, "确定", "取消", true, owner);
            return result == ButtonResult.Left;
        }
    }
}
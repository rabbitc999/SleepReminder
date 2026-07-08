using System.Windows;

namespace SleepReminder
{
    public partial class SetTimeWindow : Window
    {
        public int StartHour { get; private set; }
        public int StartMinute { get; private set; }
        public int EndHour { get; private set; }
        public int EndMinute { get; private set; }

        public SetTimeWindow()
        {
            InitializeComponent();

            // 初始化下拉框
            for (int i = 0; i < 24; i++)
            {
                cmbStartHour.Items.Add(i.ToString("D2"));
                cmbEndHour.Items.Add(i.ToString("D2"));
            }
            for (int i = 0; i < 60; i++)
            {
                cmbStartMinute.Items.Add(i.ToString("D2"));
                cmbEndMinute.Items.Add(i.ToString("D2"));
            }

            // 从注册表加载当前设置
            var (sh, sm, eh, em) = RegistryHelper.LoadReminderTime();
            cmbStartHour.SelectedIndex = sh;
            cmbStartMinute.SelectedIndex = sm;
            cmbEndHour.SelectedIndex = eh;
            cmbEndMinute.SelectedIndex = em;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            StartHour = cmbStartHour.SelectedIndex;
            StartMinute = cmbStartMinute.SelectedIndex;
            EndHour = cmbEndHour.SelectedIndex;
            EndMinute = cmbEndMinute.SelectedIndex;

            // 保存到注册表
            RegistryHelper.SaveReminderTime(StartHour, StartMinute, EndHour, EndMinute);

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
using System;
using System.Windows;

namespace SleepReminder
{
    public partial class SnoozeSettingWindow : Window
    {
        public int SnoozeMinutes { get; private set; }

        public SnoozeSettingWindow()
        {
            InitializeComponent();

            // 填充下拉框：5~15
            for (int i = 5; i <= 15; i++)
                cmbMinutes.Items.Add(i.ToString());

            // 加载当前保存的值，并选中
            int saved = RegistryHelper.LoadSnoozeMinutes();
            int index = saved - 5; // 因为5对应索引0
            if (index >= 0 && index < cmbMinutes.Items.Count)
                cmbMinutes.SelectedIndex = index;
            else
                cmbMinutes.SelectedIndex = 5; // 默认10（索引5）
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (cmbMinutes.SelectedItem != null && int.TryParse(cmbMinutes.SelectedItem.ToString(), out int minutes))
            {
                SnoozeMinutes = minutes;
                RegistryHelper.SaveSnoozeMinutes(minutes);
                DialogResult = true;
                Close();
            }
            else
            {
                CustomMessageBox.ShowOk("请选择一个有效分钟数", "提示", this);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
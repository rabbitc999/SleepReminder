using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SleepReminder
{
    /// <summary>
    /// 额度设置窗口：编辑作息周制（双休 / 单休 / 大小周）与额度参数
    /// （固定额度、附加池、宽松夜系数、单夜封顶、是否透支）。
    ///
    /// 修改受"每自然周只能改一次"的锁保护（见 <see cref="QuotaSettings.TrySave"/>），
    /// 免得用户当晚发现额度不够就临时把额度调大。锁定状态下"确定"按钮直接置灰，
    /// 并在下方提示下次可改的日期。
    /// </summary>
    public partial class QuotaSettingsWindow : Window
    {
        /// <summary>打开本窗口的时刻，用于周键计算与锁定判断。</summary>
        private readonly DateTime _now;

        /// <summary>构造窗口：装载当前设置并刷新锁定状态。</summary>
        public QuotaSettingsWindow()
        {
            InitializeComponent();
            _now = DateTime.Now;
            LoadIntoForm();
            UpdateLockStatus();
        }

        /// <summary>把当前生效的设置填进表单。</summary>
        private void LoadIntoForm()
        {
            QuotaSettings s = QuotaSettings.Current;

            cmbPattern.SelectedIndex = s.Pattern switch
            {
                WorkPattern.SingleRest => 1,
                WorkPattern.BigSmallWeek => 2,
                _ => 0
            };

            txtFixed.Text = s.FixedMinutesPerNight.ToString(CultureInfo.InvariantCulture);
            txtPool.Text = s.WeeklyPoolMinutes.ToString(CultureInfo.InvariantCulture);
            txtFactor.Text = s.RelaxedNightFactor.ToString("0.##", CultureInfo.InvariantCulture);
            txtCapWork.Text = s.WorknightCapMinutes.ToString(CultureInfo.InvariantCulture);
            txtCapRelax.Text = s.RelaxedNightCapMinutes.ToString(CultureInfo.InvariantCulture);
            chkOverdraft.IsChecked = s.AllowOverdraft;
        }

        /// <summary>刷新"本周还能不能改"的提示与"确定"按钮的可用性。</summary>
        private void UpdateLockStatus()
        {
            bool canModify = QuotaSettings.CanModify(_now);
            tbLock.Text = QuotaSettings.DescribeLock(_now);
            tbLock.Foreground = canModify ? Brushes.LightGreen : Brushes.Orange;
            btnOk.IsEnabled = canModify;
        }

        /// <summary>校验输入、写回设置并落盘；失败时给出原因并保持窗口打开。</summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadInt(txtFixed, "固定额度", 0, 240, out int fixedMinutes))
                return;
            if (!TryReadInt(txtPool, "附加池", 0, 1440, out int pool))
                return;
            if (!TryReadInt(txtCapWork, "紧张夜单夜封顶", 0, 720, out int capWork))
                return;
            if (!TryReadInt(txtCapRelax, "宽松夜单夜封顶", 0, 720, out int capRelax))
                return;

            if (!double.TryParse(txtFactor.Text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double factor) ||
                factor < 0.1 || factor > 1.0)
            {
                CustomMessageBox.ShowOk("宽松夜系数必须是 0.1 ~ 1.0 之间的数字，例如 0.5。", "输入有误", this);
                return;
            }

            // 先把新值写进当前设置实例，再交给 TrySave 做"每周一次"的守门与落盘。
            QuotaSettings cur = QuotaSettings.Current;
            cur.Pattern = cmbPattern.SelectedIndex switch
            {
                1 => WorkPattern.SingleRest,
                2 => WorkPattern.BigSmallWeek,
                _ => WorkPattern.DoubleRest
            };
            cur.FixedMinutesPerNight = fixedMinutes;
            cur.WeeklyPoolMinutes = pool;
            cur.RelaxedNightFactor = factor;
            cur.WorknightCapMinutes = capWork;
            cur.RelaxedNightCapMinutes = capRelax;
            cur.AllowOverdraft = chkOverdraft.IsChecked == true;

            if (!cur.TrySave(_now, out string message))
            {
                // 被"每周一次"的锁挡下：抛弃内存里的改动，回到注册表中的既有值
                QuotaSettings.Reload();
                LoadIntoForm();
                UpdateLockStatus();
                CustomMessageBox.ShowOk(message, "本周不可修改", this);
                return;
            }

            CustomMessageBox.ShowOk(message, "已保存", this);

            // 刚切到大小周且尚未确认过大周基准 → 立刻问一次，省得等下次开机
            if (QuotaSettings.NeedsBigWeekConfirmation())
                StartupPrompts.PromptBigWeekIfNeeded(this, DateTime.Now);

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>从文本框读一个区间内的整数；非法时弹提示并返回 false。</summary>
        /// <param name="box">目标文本框。</param>
        /// <param name="name">字段名，用于提示文案。</param>
        /// <param name="min">允许的最小值。</param>
        /// <param name="max">允许的最大值。</param>
        /// <param name="value">读出的值。</param>
        /// <returns>输入合法返回 true。</returns>
        private bool TryReadInt(TextBox box, string name, int min, int max, out int value)
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) &&
                value >= min && value <= max)
            {
                return true;
            }

            value = 0;
            CustomMessageBox.ShowOk($"{name}必须是 {min} ~ {max} 之间的整数。", "输入有误", this);
            return false;
        }
    }
}

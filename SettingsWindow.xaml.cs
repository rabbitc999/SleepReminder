using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SleepReminder
{
    /// <summary>
    /// 统一设置窗口，把原先分散的三处设置合到一个地方：
    /// <list type="number">
    ///   <item><description><b>提醒时间</b>——可随时修改，不受限制；</description></item>
    ///   <item><description><b>延后时长</b>——可随时修改，不受限制；</description></item>
    ///   <item><description><b>额度与周制</b>——受"每自然周只能改一次"的锁保护
    ///     （见 <see cref="QuotaSettings.TrySave"/>），锁定期间这一组控件会整体置灰。</description></item>
    /// </list>
    ///
    /// 点"确定"时前两组必定生效，第三组仅在未被锁定时生效。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>打开本窗口的时刻，用于周键计算与额度锁判断。</summary>
        private readonly DateTime _now;

        /// <summary>本周是否还允许修改额度设置。</summary>
        private readonly bool _quotaEditable;

        /// <summary>构造窗口：填充下拉框、装载当前设置、按锁定状态置灰额度控件。</summary>
        public SettingsWindow()
        {
            InitializeComponent();
            _now = DateTime.Now;
            _quotaEditable = QuotaSettings.CanModify(_now);

            FillChoiceItems();
            LoadIntoForm();
            ApplyQuotaLock();
        }

        /// <summary>填充时、分与延后分钟数的下拉项。</summary>
        private void FillChoiceItems()
        {
            for (int i = 0; i < 24; i++)
            {
                string text = i.ToString("D2", CultureInfo.InvariantCulture);
                cmbStartHour.Items.Add(text);
                cmbEndHour.Items.Add(text);
            }

            for (int i = 0; i < 60; i++)
            {
                string text = i.ToString("D2", CultureInfo.InvariantCulture);
                cmbStartMinute.Items.Add(text);
                cmbEndMinute.Items.Add(text);
            }

            for (int i = SnoozeMinutesMin; i <= SnoozeMinutesMax; i++)
                cmbSnoozeMinutes.Items.Add(i.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>单次延后分钟数的允许下界。</summary>
        private const int SnoozeMinutesMin = 5;

        /// <summary>单次延后分钟数的允许上界。</summary>
        private const int SnoozeMinutesMax = 15;

        /// <summary>把当前生效的设置填进表单。</summary>
        private void LoadIntoForm()
        {
            var (sh, sm, eh, em) = RegistryHelper.LoadReminderTime();
            cmbStartHour.SelectedIndex = Math.Clamp(sh, 0, 23);
            cmbStartMinute.SelectedIndex = Math.Clamp(sm, 0, 59);
            cmbEndHour.SelectedIndex = Math.Clamp(eh, 0, 23);
            cmbEndMinute.SelectedIndex = Math.Clamp(em, 0, 59);

            int snooze = Math.Clamp(RegistryHelper.LoadSnoozeMinutes(), SnoozeMinutesMin, SnoozeMinutesMax);
            cmbSnoozeMinutes.SelectedIndex = snooze - SnoozeMinutesMin;

            QuotaSettings s = QuotaSettings.Current;
            cmbPattern.SelectedIndex = s.Pattern switch
            {
                WorkPattern.SingleRest => 1,
                WorkPattern.BigSmallWeek => 2,
                WorkPattern.Unemployed => 3,
                _ => 0
            };

            txtFixed.Text = s.FixedMinutesPerNight.ToString(CultureInfo.InvariantCulture);
            txtPoolRelax.Text = s.RelaxedPoolMinutes.ToString(CultureInfo.InvariantCulture);
            txtPoolWork.Text = s.WorknightPoolMinutes.ToString(CultureInfo.InvariantCulture);
            txtFactor.Text = s.RelaxedNightFactor.ToString("0.##", CultureInfo.InvariantCulture);
            txtCapWork.Text = s.WorknightCapMinutes.ToString(CultureInfo.InvariantCulture);
            txtCapRelax.Text = s.RelaxedNightCapMinutes.ToString(CultureInfo.InvariantCulture);
            chkOverdraft.IsChecked = s.AllowOverdraft;
        }

        /// <summary>
        /// 按"本周是否还能改额度"决定第三组的可用性：锁定时整体置灰并说明下次可改时间，
        /// 但提醒时间与延后时长不受影响，仍然可改。
        /// </summary>
        private void ApplyQuotaLock()
        {
            tbLock.Text = _quotaEditable
                ? "额度设置本周还可修改 1 次（改后本自然周内不可再改）。"
                : QuotaSettings.DescribeLock(_now) + "　（本周内提醒时间与延后时长仍可随时修改）";
            tbLock.Foreground = _quotaEditable ? Brushes.LightGreen : Brushes.Orange;

            foreach (Control control in new Control[]
                     {
                         cmbPattern, txtFixed, txtPoolRelax, txtPoolWork, txtFactor,
                         txtCapWork, txtCapRelax, chkOverdraft
                     })
            {
                control.IsEnabled = _quotaEditable;
            }
        }

        /// <summary>
        /// 校验全部输入 → 先落"随时可改"的两组 → 再按锁定状态落额度组。
        /// 任何校验失败都直接返回、不写入任何一项，避免出现"改了一半"的设置。
        /// </summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (cmbStartHour.SelectedIndex < 0 || cmbStartMinute.SelectedIndex < 0 ||
                cmbEndHour.SelectedIndex < 0 || cmbEndMinute.SelectedIndex < 0)
            {
                CustomMessageBox.ShowOk("请选择完整的提醒时间区间。", "输入有误", this);
                return;
            }

            if (cmbSnoozeMinutes.SelectedIndex < 0)
            {
                CustomMessageBox.ShowOk("请选择单次延后的分钟数。", "输入有误", this);
                return;
            }

            // ---- 额度组：先校验，避免时间已存、额度报错这种半成品状态 ----
            int fixedMinutes = 0, poolRelax = 0, poolWork = 0, capWork = 0, capRelax = 0;
            double factor = 0;

            if (_quotaEditable)
            {
                if (!TryReadInt(txtFixed, "固定额度", 0, 240, out fixedMinutes))
                    return;
                if (!TryReadInt(txtPoolRelax, "休息池", 0, 1440, out poolRelax))
                    return;
                if (!TryReadInt(txtPoolWork, "工作池", 0, 1440, out poolWork))
                    return;
                if (!TryReadInt(txtCapWork, "紧张夜单夜封顶", 0, 720, out capWork))
                    return;
                if (!TryReadInt(txtCapRelax, "宽松夜单夜封顶", 0, 720, out capRelax))
                    return;

                if (!double.TryParse(txtFactor.Text.Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out factor) ||
                    factor < 0.1 || factor > 1.0)
                {
                    CustomMessageBox.ShowOk("宽松夜系数必须是 0.1 ~ 1.0 之间的数字，例如 0.5。", "输入有误", this);
                    return;
                }
            }

            // ---- 落盘：提醒时间 + 延后时长（随时可改） ----
            RegistryHelper.SaveReminderTime(
                cmbStartHour.SelectedIndex, cmbStartMinute.SelectedIndex,
                cmbEndHour.SelectedIndex, cmbEndMinute.SelectedIndex);
            RegistryHelper.SaveSnoozeMinutes(cmbSnoozeMinutes.SelectedIndex + SnoozeMinutesMin);

            // ---- 落盘：额度与周制（受每周一次的锁保护） ----
            string quotaMessage = string.Empty;
            if (_quotaEditable)
            {
                QuotaSettings cur = QuotaSettings.Current;
                cur.Pattern = cmbPattern.SelectedIndex switch
                {
                    1 => WorkPattern.SingleRest,
                    2 => WorkPattern.BigSmallWeek,
                    3 => WorkPattern.Unemployed,
                    _ => WorkPattern.DoubleRest
                };
                cur.FixedMinutesPerNight = fixedMinutes;
                cur.RelaxedPoolMinutes = poolRelax;
                cur.WorknightPoolMinutes = poolWork;
                cur.RelaxedNightFactor = factor;
                cur.WorknightCapMinutes = capWork;
                cur.RelaxedNightCapMinutes = capRelax;
                cur.AllowOverdraft = chkOverdraft.IsChecked == true;

                if (!cur.TrySave(_now, out quotaMessage))
                {
                    // 理论上到不了这里（进不来就已被置灰），防御性处理并回退内存改动
                    QuotaSettings.Reload();
                }
            }

            string summary = "提醒时间与延后时长已保存。";
            if (_quotaEditable && quotaMessage.Length > 0)
                summary += "\n\n" + quotaMessage;

            CustomMessageBox.ShowOk(summary, "已保存", this);

            // 刚切到大小周且尚未确认过大周基准 → 立刻问一次，省得等下次开机
            if (_quotaEditable && QuotaSettings.NeedsBigWeekConfirmation())
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

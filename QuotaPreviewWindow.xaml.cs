using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SleepReminder
{
    /// <summary>
    /// 预览窗口：把"额度账本"和"近期作息"摊开给用户看。
    ///
    /// 左栏是额度——今夜归属、夜的类型与系数、本夜已延后、固定额度余额、
    /// 休息池与工作池在各自滚动 7 天窗口内的用量与余额、此刻最多还能延后多少、设置锁状态、违约次数，
    /// 以及近 7 夜的逐夜明细。
    ///
    /// 右栏是作息——从今天起若干天的"上班 / 休息"判定，并标出依据
    /// （法定放假 / 调休 / 按周制作息），方便核对单休、大小周与调休是否正确。
    ///
    /// 窗口是只读的，底部只有一个"刷新"——程序不提供任何界面退出入口。
    /// </summary>
    public partial class QuotaPreviewWindow : Window
    {
        /// <summary>左侧列表展示的最近夜数（含本夜）。</summary>
        private const int RecentNights = 7;

        /// <summary>右侧列表展示的未来天数（含今天）。</summary>
        private const int UpcomingDays = 21;

        private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        private static readonly Brush PrimaryBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        private static readonly Brush RestBrush = new SolidColorBrush(Color.FromRgb(0x7E, 0xD9, 0x57));
        private static readonly Brush WorkBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
        private static readonly Brush TodayBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));

        /// <summary>构造窗口并立刻读取一次数据。</summary>
        public QuotaPreviewWindow()
        {
            InitializeComponent();

            // 只读的展示窗口，允许用户用 Alt+F4 关掉
            Reload();
        }

        /// <summary>重新读取额度与作息数据并刷新界面。</summary>
        private void Reload()
        {
            DateTime now = DateTime.Now;
            DateTime nightDate = SleepLedger.GetNightDate(now);
            QuotaSettings settings = QuotaSettings.Current;

            bool relaxed = SleepLedger.IsRelaxedNight(nightDate);
            int cap = SleepLedger.GetNightCap(nightDate);
            int tonightSeconds = SleepLedger.GetTonightSnoozedSeconds(now);
            int fixedLeftSeconds = Math.Max(0, settings.FixedMinutesPerNight * 60 - tonightSeconds);
            int relaxPoolUsed = SleepLedger.GetPoolUsedSeconds(now, true);
            int relaxPoolLeft = SleepLedger.GetPoolRemainingSeconds(now, true);
            int workPoolUsed = SleepLedger.GetPoolUsedSeconds(now, false);
            int workPoolLeft = SleepLedger.GetPoolRemainingSeconds(now, false);
            int availableSeconds = SleepLedger.GetAvailableSeconds(now);
            // 本夜真正动了所属池的秒数（已含夜系数折算）——与"本夜已延后"不一定相等，
            // 固定额度以内的部分根本不扣池子，两者之差就是让用户误解的来源。
            int tonightPoolSeconds = SleepLedger.GetRecentNights(now, 1).FirstOrDefault()?.PoolSeconds ?? 0;

            var sb = new StringBuilder();
            sb.AppendLine($"今夜归属：{nightDate:yyyy-MM-dd}（{WeekdayName(nightDate.DayOfWeek)}）");
            sb.AppendLine($"夜的类型：{(relaxed ? "宽松夜" : "紧张夜")}（系数 {SleepLedger.GetNightFactor(nightDate):0.#}）");
            sb.AppendLine($"本夜已延后：{SleepLedger.FormatClock(tonightSeconds)} ／ 单夜封顶 {cap} 分钟");
            sb.AppendLine($"　└ 其中动用{SleepLedger.GetPoolName(nightDate)}：{SleepLedger.FormatClock(tonightPoolSeconds)}（固定额度以内的部分不扣池）");
            sb.AppendLine();
            sb.AppendLine($"固定额度：{settings.FixedMinutesPerNight} 分钟 / 夜，本夜剩余 {SleepLedger.FormatClock(fixedLeftSeconds)}");
            sb.AppendLine($"休息池：{settings.RelaxedPoolMinutes} 分钟 / 周，已支取 {SleepLedger.FormatClock(relaxPoolUsed)}，剩余 {SleepLedger.FormatClock(relaxPoolLeft)}");
            sb.AppendLine($"工作池：{settings.WorknightPoolMinutes} 分钟 / 周，已支取 {SleepLedger.FormatClock(workPoolUsed)}，剩余 {SleepLedger.FormatClock(workPoolLeft)}");
            sb.AppendLine($"　　（两池互不挤占，各自滚动 7 天；本夜是{(relaxed ? "宽松夜" : "紧张夜")}，只动{SleepLedger.GetPoolName(nightDate)}）");
            sb.AppendLine();
            sb.AppendLine($"此刻最多还能延后：{SleepLedger.FormatClock(availableSeconds)}");
            sb.AppendLine($"额度设置：{QuotaSettings.DescribeLock(now)}");
            sb.AppendLine($"累计违约：{SleepLedger.GetViolationCount()} 次");

            tbQuota.Text = sb.ToString().TrimEnd();
            tbSubtitle.Text =
                $"数据来自额度账本，生成时刻 {now:yyyy-MM-dd HH:mm:ss}。" +
                "延后按“夜”结算，关机 / 重启即为一夜的终点。";

            BuildNightRows(now);
            BuildDayRows(now);
        }

        /// <summary>构建左栏"近 7 夜明细"表格。</summary>
        /// <param name="now">当前时刻。</param>
        private void BuildNightRows(DateTime now)
        {
            double[] widths = { 78, 46, 50, 62, 62 };

            spNights.Children.Clear();
            spNights.Children.Add(Hint(
                "「延后」= 本夜累计延后时长；「池扣」= 其中真正动用本夜所属池的时长" +
                "（固定额度以内的部分不扣池子）。时长一律按秒记账，显示为 mm:ss。"));
            spNights.Children.Add(BuildRow(new[]
            {
                ("日期", HeaderBrush), ("星期", HeaderBrush), ("夜型", HeaderBrush),
                ("延后", HeaderBrush), ("池扣", HeaderBrush)
            }, widths));

            List<SleepLedger.NightSummary> nights = SleepLedger.GetRecentNights(now, RecentNights);
            if (nights.Count == 0)
            {
                spNights.Children.Add(Hint("最近 7 夜还没有延后记录。"));
                return;
            }

            DateTime nightDate = SleepLedger.GetNightDate(now);
            foreach (SleepLedger.NightSummary n in nights)
            {
                bool isToday = n.NightDate == nightDate;
                Brush dateBrush = isToday ? TodayBrush : PrimaryBrush;
                Brush typeBrush = n.IsRelaxed ? RestBrush : WorkBrush;

                spNights.Children.Add(BuildRow(new[]
                {
                    (n.NightDate.ToString("MM-dd", CultureInfo.InvariantCulture), dateBrush),
                    (WeekdayName(n.NightDate.DayOfWeek), dateBrush),
                    (n.IsRelaxed ? "宽松" : "紧张", typeBrush),
                    (SleepLedger.FormatClock(n.TotalSeconds), MutedBrush),
                    (SleepLedger.FormatClock(n.PoolSeconds), MutedBrush)
                }, widths));
            }
        }

        /// <summary>构建右栏"近期作息"表格。</summary>
        /// <param name="now">当前时刻。</param>
        private void BuildDayRows(DateTime now)
        {
            double[] widths = { 78, 46, 50, 190 };

            spDays.Children.Clear();
            spDays.Children.Add(BuildRow(new[]
            {
                ("日期", HeaderBrush), ("星期", HeaderBrush),
                ("作息", HeaderBrush), ("依据", HeaderBrush)
            }, widths));

            List<WorkdayCalendar.DayInfo> days = WorkdayCalendar.GetUpcomingDays(now, UpcomingDays);
            int restCount = 0;

            foreach (WorkdayCalendar.DayInfo d in days)
            {
                if (!d.IsWorkday)
                    restCount++;

                bool isToday = d.Date == now.Date;
                Brush dateBrush = isToday ? TodayBrush : PrimaryBrush;
                Brush stateBrush = d.IsWorkday ? WorkBrush : RestBrush;

                spDays.Children.Add(BuildRow(new[]
                {
                    (d.Date.ToString("MM-dd", CultureInfo.InvariantCulture), dateBrush),
                    (WeekdayName(d.Date.DayOfWeek), dateBrush),
                    (d.IsWorkday ? "上班" : "休息", stateBrush),
                    (d.Reason, MutedBrush)
                }, widths));
            }

            string hint = $"从今天起 {UpcomingDays} 天内共 {restCount} 天休息。";
            WorkPattern pattern = QuotaSettings.Current.Pattern;
            if (pattern == WorkPattern.Unemployed)
                hint += "待业模式：每天都是放假，因此每一夜都是宽松夜；节假日 / 调休表在此时不参与判定。";
            else if (pattern != WorkPattern.DoubleRest)
                hint += "单休 / 大小周按周制推演；临近假期可在启动提示里改成单位实际作息。";

            tbRestHint.Text = hint;
        }

        /// <summary>按固定列宽拼一行表格。</summary>
        /// <param name="cells">每列的文字与颜色。</param>
        /// <param name="widths">各列宽度（像素）。</param>
        /// <returns>可直接加入面板的行元素。</returns>
        private static UIElement BuildRow((string text, Brush brush)[] cells, double[] widths)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            for (int i = 0; i < widths.Length; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i]) });

            for (int i = 0; i < cells.Length && i < widths.Length; i++)
            {
                var tb = new TextBlock
                {
                    Text = cells[i].text,
                    Foreground = cells[i].brush,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(tb, i);
                grid.Children.Add(tb);
            }

            return grid;
        }

        /// <summary>生成一行提示文案。</summary>
        /// <param name="text">提示内容。</param>
        /// <returns>提示元素。</returns>
        private static UIElement Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = MutedBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        /// <summary>取星期的中文简称。</summary>
        /// <param name="day">星期枚举。</param>
        /// <returns>如"周一"。</returns>
        private static string WeekdayName(DayOfWeek day)
        {
            return day switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => "周日"
            };
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Reload();
        }
    }
}

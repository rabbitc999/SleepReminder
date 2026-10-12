using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SleepReminder
{
    /// <summary>
    /// 假期调休确认窗口。
    ///
    /// 单休、大小周的单位在法定假期上通常"放不满"（例如国庆法定 7 天，公司只放 4 天），
    /// 本窗口把该段假期的每一天列出来让用户勾选实际休息的日子；
    /// 取消勾选的那些天会被记成"要上班"，从而修正额度判定。
    /// </summary>
    public partial class HolidayAdjustWindow : Window
    {
        private readonly List<KeyValuePair<DateTime, CheckBox>> _items = new();

        /// <summary>用户判定为"要上班"的日期（即取消勾选的那些法定假日）。</summary>
        public List<DateTime> WorkDates { get; } = new();

        /// <summary>构造窗口并渲染该段假期的每一天（默认全部勾选 = 按法定全放）。</summary>
        /// <param name="holidayName">假期名称，例如"国庆节"。</param>
        /// <param name="days">该段假期的全部日期，须按时间升序。</param>
        public HolidayAdjustWindow(string holidayName, IReadOnlyList<DateTime> days)
        {
            InitializeComponent();

            if (days.Count > 0)
            {
                tbIntro.Text =
                    $"临近{holidayName}（{days[0]:MM-dd} ~ {days[days.Count - 1]:MM-dd}）。\n" +
                    "单休 / 大小周的单位通常放不满，请勾出你实际休息的日子：";
            }
            else
            {
                tbIntro.Text = "请在下面勾出你实际休息的日子：";
            }

            foreach (DateTime d in days)
            {
                var box = new CheckBox
                {
                    Content = $"{d:MM-dd}（{WeekdayName(d.DayOfWeek)}）",
                    IsChecked = true,
                    Foreground = Brushes.White,
                    FontSize = 15,
                    Margin = new Thickness(0, 6, 0, 6)
                };
                _items.Add(new KeyValuePair<DateTime, CheckBox>(d, box));
                spDays.Children.Add(box);
            }
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

        /// <summary>收集"被取消勾选"的日期。</summary>
        /// <returns>判定为要上班的日期列表。</returns>
        private List<DateTime> CollectUnchecked()
        {
            var list = new List<DateTime>();
            foreach (var item in _items)
            {
                if (item.Value.IsChecked != true)
                    list.Add(item.Key);
            }
            return list;
        }

        private void All_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
                item.Value.IsChecked = true;
        }

        private void None_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
                item.Value.IsChecked = false;
        }

        /// <summary>按法定全放：不产生任何"上班"覆盖。</summary>
        private void Legal_Click(object sender, RoutedEventArgs e)
        {
            WorkDates.Clear();
            DialogResult = true;
            Close();
        }

        /// <summary>确认：把取消勾选的日子作为"要上班"返回。</summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            WorkDates.Clear();
            WorkDates.AddRange(CollectUnchecked());
            DialogResult = true;
            Close();
        }
    }
}

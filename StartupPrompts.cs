using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace SleepReminder
{
    /// <summary>
    /// 启动期的用户确认流程（UI 层）。
    ///
    /// 在主窗口显示后依次执行三件事，各自带"问过就不再问"的幂等保护：
    /// <list type="number">
    ///   <item><description>跨年检查——当年放假安排缺失时生成模板并提示补全；</description></item>
    ///   <item><description>大小周确认——询问"本周是否为大周"，据此推算基准周；</description></item>
    ///   <item><description>假期调休确认——单休 / 大小周临近法定假期时，询问实际休哪几天。</description></item>
    /// </list>
    ///
    /// 本类只负责"问"，判定与落盘分别由 <see cref="WorkdayCalendar"/>、
    /// <see cref="QuotaSettings"/> 完成。
    /// </summary>
    public static class StartupPrompts
    {
        private const string RegKeyPath = @"Software\SleepReminder";
        private const string ValuePromptedYear = "HolidayPromptedYear";
        private const string ValueAdjustedBlock = "HolidayAdjustedBlockStart";
        private const string DateFormat = "yyyy-MM-dd";

        /// <summary>向前观察"即将到来的法定假期"的天数。</summary>
        private const int UpcomingHolidayWindowDays = 10;

        /// <summary>依次执行全部启动期确认。</summary>
        /// <param name="owner">消息框与子窗口的属主，可为 null。</param>
        /// <param name="today">当前时刻。</param>
        public static void RunAll(Window? owner, DateTime today)
        {
            PromptYearlyHolidayIfNeeded(owner, today);
            PromptBigWeekIfNeeded(owner, today);
            PromptHolidayAdjustIfNeeded(owner, today);
        }

        /// <summary>
        /// 检查"当年"放假安排是否已配置；缺失就生成模板并提示用户补全。
        /// 每一年最多提示一次，避免每次开机都打扰。
        /// </summary>
        /// <param name="owner">消息框属主，可为 null。</param>
        /// <param name="today">当前时刻。</param>
        /// <returns>本次给出了提示返回 true。</returns>
        public static bool PromptYearlyHolidayIfNeeded(Window? owner, DateTime today)
        {
            int year = today.Year;
            if (WorkdayCalendar.HasDataForYear(year))
                return false;

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                if (key == null)
                    return false;
                if (key.GetValue(ValuePromptedYear) as string == year.ToString(CultureInfo.InvariantCulture))
                    return false;
                key.SetValue(ValuePromptedYear, year.ToString(CultureInfo.InvariantCulture));
            }

            WorkdayCalendar.EnsureTemplateFor(year);
            string path = WorkdayCalendar.GetOverridePath();

            string message =
                $"{year} 年的放假安排还没有配置。\n" +
                "在补全之前，法定节假日与调休会被当成普通周末，夜间的额度判定会不准确。\n\n" +
                $"已为你生成待填写模板：\n{path}\n\n" +
                "按国务院办公厅的通知填好后重启本程序即可生效。";

            bool open = CustomMessageBox.ShowConfirm(message, $"{year} 年放假安排待配置", owner);
            if (open)
                TryOpenFile(path);

            return true;
        }

        /// <summary>
        /// 若当前是"大小周"且大周基准尚未确认，询问用户"本周是否为大周"。
        /// </summary>
        /// <param name="owner">消息框属主，可为 null。</param>
        /// <param name="today">当前时刻。</param>
        /// <returns>本次弹出了确认框返回 true。</returns>
        public static bool PromptBigWeekIfNeeded(Window? owner, DateTime today)
        {
            if (!QuotaSettings.NeedsBigWeekConfirmation())
                return false;

            DateTime monday = QuotaSettings.GetMonday(today);
            string message =
                $"当前作息设为「大小周」。请确认本周（{monday:MM-dd} 起）属于哪一种：\n\n" +
                "· 选「本周是大周」→ 本周双休（周六、周日都休）\n" +
                "· 选「本周是小周」→ 本周单休（只休周日）\n\n" +
                "系统会据此逐周交替推演，之后如需修改要占用每周一次的设置机会。";

            CustomMessageBox.ButtonResult result = CustomMessageBox.Show(
                message, "确认大小周", "本周是大周", "本周是小周", true, owner);

            // 只明确选"大周"才算大周；其余（含直接关闭）一律按更严格的小周处理
            QuotaSettings.ConfirmBigWeek(today, result == CustomMessageBox.ButtonResult.Left);
            return true;
        }

        /// <summary>
        /// 单休 / 大小周单位临近法定假期时，询问用户"实际休哪几天"——
        /// 这类单位通常放不满法定假期。用户取消勾选的日子会被记成上班。
        /// </summary>
        /// <param name="owner">子窗口属主，可为 null。</param>
        /// <param name="today">当前时刻。</param>
        /// <returns>本次弹出了确认窗口返回 true。</returns>
        public static bool PromptHolidayAdjustIfNeeded(Window? owner, DateTime today)
        {
            // 双休单位一般按法定放即可，无需打扰
            if (QuotaSettings.Current.Pattern == WorkPattern.DoubleRest)
                return false;

            WorkdayCalendar.HolidayBlock? block =
                WorkdayCalendar.FindUpcomingHoliday(today, UpcomingHolidayWindowDays);
            if (block == null || block.Days.Count == 0)
                return false;

            string key = block.Days[0].ToString(DateFormat, CultureInfo.InvariantCulture);
            using (RegistryKey reg = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                if (reg == null)
                    return false;
                if (reg.GetValue(ValueAdjustedBlock) as string == key)
                    return false;
            }

            var win = new HolidayAdjustWindow(block.Name, block.Days);
            if (owner != null && owner.IsVisible)
                win.Owner = owner;

            bool? result = win.ShowDialog();

            // 直接关掉窗口 → 视为尚未处理，下次启动再问
            if (result != true)
                return false;

            using (RegistryKey reg = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                reg?.SetValue(ValueAdjustedBlock, key);
            }

            if (win.WorkDates.Count > 0)
                WorkdayCalendar.AppendCompanyWorkdays(win.WorkDates);

            return true;
        }

        /// <summary>用系统默认程序打开文件；失败时静默忽略。</summary>
        /// <param name="path">要打开的文件路径。</param>
        private static void TryOpenFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // 打不开就让用户自己去路径找，不额外报错
            }
        }
    }
}

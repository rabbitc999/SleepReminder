using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SleepReminder
{
    /// <summary>
    /// 工作日日历：判断某一天是"要上班"还是"休息"。
    ///
    /// 中国的工作日不能只看星期：法定节假日会放假，长假又会"调休"——把前后的周末
    /// 改成上班日（例如 2026-10-10 是周六，但要上班）。此外还有"单休""大小周"这类
    /// 公司作息。因此判定优先级为：
    /// <list type="number">
    ///   <item><description>命中"调休上班"表 → 要上班；</description></item>
    ///   <item><description>命中"法定放假"表 → 休息；</description></item>
    ///   <item><description>否则按 <see cref="QuotaSettings.Pattern"/> 推演周制
    ///     （双休 / 单休 / 大小周）。</description></item>
    /// </list>
    ///
    /// 另有一条压倒性的前提：作息周制若为
    /// <see cref="WorkPattern.Unemployed"/>（待业），则<b>每天都是放假</b>——
    /// 此时连调休 / 单位安排上班表都不作数，也就不必再维护节假日数据。
    ///
    /// 内置数据取自国务院办公厅《关于 2026 年部分节假日安排的通知》
    /// （2025-11-04 发布）。放假安排每年公布一次，因此程序还会在 exe 同目录寻找
    /// <c>holidays.txt</c> 做增量覆盖；当年没有数据时，会在启动时生成模板并提示用户补全。
    /// 文件格式：
    /// <code>
    /// # 注释行以 # 开头
    /// 2026-10-01,R    # R = 休息（法定放假）
    /// 2026-10-10,W    # W = 上班（调休）
    /// </code>
    /// </summary>
    public static class WorkdayCalendar
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<DateTime> RestDays = new HashSet<DateTime>();
        private static readonly HashSet<DateTime> WorkDays = new HashSet<DateTime>();
        private static bool _initialized;

        /// <summary>未经修改时使用的节假日表（2026 年）。</summary>
        private const string EmbeddedTable = @"
# ===== 休息日（R）：法定放假 =====
# 元旦
2026-01-01,R
2026-01-02,R
2026-01-03,R
# 春节
2026-02-15,R
2026-02-16,R
2026-02-17,R
2026-02-18,R
2026-02-19,R
2026-02-20,R
2026-02-21,R
2026-02-22,R
2026-02-23,R
# 清明节
2026-04-04,R
2026-04-05,R
2026-04-06,R
# 劳动节
2026-05-01,R
2026-05-02,R
2026-05-03,R
2026-05-04,R
2026-05-05,R
# 端午节
2026-06-19,R
2026-06-20,R
2026-06-21,R
# 中秋节
2026-09-25,R
2026-09-26,R
2026-09-27,R
# 国庆节
2026-10-01,R
2026-10-02,R
2026-10-03,R
2026-10-04,R
2026-10-05,R
2026-10-06,R
2026-10-07,R
# ===== 调休上班（W）：本是周末，但要上班 =====
2026-01-04,W
2026-02-14,W
2026-02-28,W
2026-05-09,W
2026-09-20,W
2026-10-10,W
";

        /// <summary>外部覆盖文件名（与 exe 同目录）。</summary>
        private const string OverrideFileName = "holidays.txt";

        /// <summary>确保日历已初始化：先装载内置表，再用 exe 同目录的
        /// <c>holidays.txt</c> 做增量覆盖（只增不减，避免误删内置数据）。</summary>
        private static void EnsureInitialized()
        {
            if (_initialized)
                return;

            lock (Gate)
            {
                if (_initialized)
                    return;

                LoadTable(EmbeddedTable, clear: true);

                try
                {
                    string path = GetOverridePath();
                    if (File.Exists(path))
                        LoadTable(File.ReadAllText(path), clear: false);
                }
                catch
                {
                    // 覆盖文件读失败不影响内置表生效
                }

                _initialized = true;
            }
        }

        /// <summary>丢弃已装载的数据，下次判断时重新读取（用户改完文件后可调用）。</summary>
        public static void Reload()
        {
            lock (Gate)
            {
                _initialized = false;
            }
        }

        /// <summary>解析并装载一张节假日表。</summary>
        /// <param name="text">表内容，每行形如 <c>yyyy-MM-dd,R|W</c>。</param>
        /// <param name="clear">为 true 时先清空已有数据。</param>
        private static void LoadTable(string text, bool clear)
        {
            if (clear)
            {
                RestDays.Clear();
                WorkDays.Clear();
            }

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                string[] parts = line.Split(',');
                if (parts.Length != 2)
                    continue;

                if (!DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                    continue;

                switch (parts[1].Trim().ToUpperInvariant())
                {
                    case "R":
                        RestDays.Add(date.Date);
                        break;
                    case "W":
                        WorkDays.Add(date.Date);
                        break;
                }
            }
        }

        // ---------- 判定 ----------

        /// <summary>
        /// 判断某一天是否需要上班：待业模式 &gt; 调休表 &gt; 放假表 &gt; 周制推演。
        /// </summary>
        /// <param name="date">要判断的日期。</param>
        /// <returns>需要上班返回 true，休息返回 false。</returns>
        public static bool IsWorkday(DateTime date)
        {
            EnsureInitialized();
            DateTime d = date.Date;

            // 待业模式：不存在"要上班的日子"，连调休 / 单位安排上班也不作数
            if (QuotaSettings.Current.Pattern == WorkPattern.Unemployed)
                return false;

            if (WorkDays.Contains(d))
                return true;
            if (RestDays.Contains(d))
                return false;

            return IsWorkdayByWeeklyPattern(d);
        }

        /// <summary>
        /// 按用户配置的周制推演某天是否上班（仅在未命中节假日表时使用）。
        /// </summary>
        /// <param name="d">要判断的日期。</param>
        /// <returns>需要上班返回 true。</returns>
        private static bool IsWorkdayByWeeklyPattern(DateTime d)
        {
            switch (QuotaSettings.Current.Pattern)
            {
                case WorkPattern.Unemployed:
                    // 待业：每天都是放假
                    return false;

                case WorkPattern.SingleRest:
                    // 单休：一周只休周日
                    return d.DayOfWeek != DayOfWeek.Sunday;

                case WorkPattern.BigSmallWeek:
                    // 大小周：大周双休、小周单休，逐周交替
                    if (d.DayOfWeek == DayOfWeek.Sunday)
                        return false;
                    if (d.DayOfWeek == DayOfWeek.Saturday)
                        return !QuotaSettings.IsBigWeek(d);
                    return true;

                case WorkPattern.DoubleRest:
                default:
                    // 双休：周六、周日都休
                    return d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday;
            }
        }

        /// <summary>判断某一天是否休息（<see cref="IsWorkday"/> 取反）。</summary>
        /// <param name="date">要判断的日期。</param>
        /// <returns>休息返回 true。</returns>
        public static bool IsRestDay(DateTime date)
        {
            return !IsWorkday(date);
        }

        /// <summary>
        /// 判断某个"夜"是否属于宽松夜——即其次日不用上班。
        /// 周五夜、长假前夜是宽松夜；但调休补班前的周五夜、单休制度下的周五夜不是。
        /// 待业模式下每天都是放假，因此每一夜都是宽松夜。
        /// </summary>
        /// <param name="nightDate">"夜"对应的自然日。</param>
        /// <returns>次日休息返回 true。</returns>
        public static bool IsRelaxedNight(DateTime nightDate)
        {
            return IsRestDay(nightDate.Date.AddDays(1));
        }

        // ---------- 判定结果（带依据，供界面解释） ----------

        /// <summary>某一天的作息判定结果，附带判定依据。</summary>
        public sealed class DayInfo
        {
            /// <summary>被判定的日期。</summary>
            public DateTime Date { get; init; }

            /// <summary>该日是否需要上班。</summary>
            public bool IsWorkday { get; init; }

            /// <summary>判定依据的可读说明，例如"法定放假""按周制作息"。</summary>
            public string Reason { get; init; } = string.Empty;
        }

        /// <summary>
        /// 取某一天的作息判定及依据，便于界面回答"这天凭什么休 / 上班"。
        /// </summary>
        /// <param name="date">要判定的日期。</param>
        /// <returns>带依据的判定结果。</returns>
        public static DayInfo GetDayInfo(DateTime date)
        {
            EnsureInitialized();
            DateTime d = date.Date;

            if (QuotaSettings.Current.Pattern == WorkPattern.Unemployed)
                return new DayInfo { Date = d, IsWorkday = false, Reason = "待业模式（每天放假）" };

            if (WorkDays.Contains(d))
                return new DayInfo { Date = d, IsWorkday = true, Reason = "调休 / 单位安排上班" };
            if (RestDays.Contains(d))
                return new DayInfo { Date = d, IsWorkday = false, Reason = "法定放假" };

            bool work = IsWorkdayByWeeklyPattern(d);
            return new DayInfo { Date = d, IsWorkday = work, Reason = "按周制作息" };
        }

        /// <summary>
        /// 取从今天起连续若干天的作息判定，供"预览"窗口列出来。<b>今天排在最前</b>。
        /// </summary>
        /// <param name="today">起始日期（通常为今天）。</param>
        /// <param name="days">要列出的天数。</param>
        /// <returns>按日期升序的判定结果。</returns>
        public static List<DayInfo> GetUpcomingDays(DateTime today, int days)
        {
            var list = new List<DayInfo>();
            if (days <= 0)
                return list;

            for (int i = 0; i < days; i++)
                list.Add(GetDayInfo(today.Date.AddDays(i)));

            return list;
        }

        // ---------- 跨年维护 ----------

        /// <summary>取外部覆盖文件的完整路径（与 exe 同目录）。</summary>
        /// <returns>文件绝对路径。</returns>
        public static string GetOverridePath()
        {
            return Path.Combine(AppContext.BaseDirectory, OverrideFileName);
        }

        /// <summary>
        /// 判断某一年是否已有放假安排数据。整年既没有"休息"也没有"调休"记录即视为未配置，
        /// 此时判定会退化成"只看周六周日"，节假日与调休都会被算错。
        /// </summary>
        /// <param name="year">要检查的年份。</param>
        /// <returns>已有数据返回 true。</returns>
        public static bool HasDataForYear(int year)
        {
            EnsureInitialized();
            return RestDays.Any(d => d.Year == year) || WorkDays.Any(d => d.Year == year);
        }

        /// <summary>
        /// 若某一年尚未配置，就向覆盖文件追加一段待填写模板，便于照着国务院通知补数据。
        /// 已存在同年的模板块时不会重复追加。
        /// </summary>
        /// <param name="year">目标年份。</param>
        /// <returns>本次是否实际写入了模板。</returns>
        public static bool EnsureTemplateFor(int year)
        {
            if (HasDataForYear(year))
                return false;

            string path = GetOverridePath();
            string marker = $"===== {year} 年放假安排";

            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Contains(marker))
                    return false;

                string block = string.Join(Environment.NewLine, new[]
                {
                    "",
                    $"# ===== {year} 年放假安排（待填写）=====",
                    $"# 请按国务院办公厅《关于 {year} 年部分节假日安排的通知》补全。",
                    "# 格式：每行 \"yyyy-MM-dd,R\" 表示放假，\"yyyy-MM-dd,W\" 表示调休上班。",
                    "# 只填例外即可——平时周六周日默认休息，不必逐条写：",
                    "#   ① 落在工作日的法定假日 → 写 R",
                    "#   ② 被调休成上班的周末   → 写 W",
                    $"# 例：{year}-01-01,R",
                    $"#     {year}-01-09,W",
                    ""
                });

                File.AppendAllText(path, block);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------- 假期段识别与"公司实际作息"覆盖 ----------

        /// <summary>一段连续的法定假期。</summary>
        public sealed class HolidayBlock
        {
            /// <summary>假期名称，例如"国庆节"。</summary>
            public string Name { get; init; } = string.Empty;

            /// <summary>该段假期的全部日期，按升序排列。</summary>
            public List<DateTime> Days { get; init; } = new List<DateTime>();
        }

        /// <summary>
        /// 找出从今天起 <paramref name="withinDays"/> 天内即将开始的一段法定假期。
        /// 切段依据是"连续的法定休息日"，因此普通周末不会被误判成假期。
        /// </summary>
        /// <param name="today">今天。</param>
        /// <param name="withinDays">向前观察的天数。</param>
        /// <returns>找到则返回该段假期；否则返回 null。</returns>
        public static HolidayBlock? FindUpcomingHoliday(DateTime today, int withinDays)
        {
            EnsureInitialized();

            List<DateTime> statutory = RestDays
                .Where(d => d >= today.Date && d <= today.Date.AddDays(withinDays))
                .OrderBy(d => d)
                .ToList();

            if (statutory.Count == 0)
                return null;

            var run = new List<DateTime> { statutory[0] };
            for (int i = 1; i < statutory.Count; i++)
            {
                if (statutory[i] == run[run.Count - 1].AddDays(1))
                {
                    run.Add(statutory[i]);
                }
                else
                {
                    if (run.Count >= 2)
                        return BuildBlock(run);
                    run = new List<DateTime> { statutory[i] };
                }
            }

            return run.Count >= 2 ? BuildBlock(run) : null;
        }

        /// <summary>把一段连续日期包装成假期块，并粗略推断假期名称。</summary>
        /// <param name="days">升序的假期日期。</param>
        /// <returns>假期块。</returns>
        private static HolidayBlock BuildBlock(List<DateTime> days)
        {
            return new HolidayBlock { Name = GuessHolidayName(days[0]), Days = days };
        }

        /// <summary>按月份粗略推断假期名称（每个月的法定假期基本唯一）。</summary>
        /// <param name="firstDay">假期第一天。</param>
        /// <returns>假期名称。</returns>
        private static string GuessHolidayName(DateTime firstDay)
        {
            return firstDay.Month switch
            {
                1 => "元旦",
                2 => "春节",
                4 => "清明节",
                5 => "劳动节",
                6 => "端午节",
                9 => "中秋节",
                10 => "国庆节",
                _ => "法定假期"
            };
        }

        /// <summary>
        /// 把"单位实际要上班"的日期写入覆盖文件（记为 W），并立即重新装载。
        /// 这些日期在判定中会压过法定放假，因此能表达"公司放不满"的情况。
        /// </summary>
        /// <param name="dates">要记为上班的日期。</param>
        /// <returns>写入成功返回 true。</returns>
        public static bool AppendCompanyWorkdays(IEnumerable<DateTime> dates)
        {
            List<DateTime> list = dates.Distinct().OrderBy(d => d).ToList();
            if (list.Count == 0)
                return true;

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"# ===== 单位实际作息（程序于 {DateTime.Today:yyyy-MM-dd} 记录）=====");
                foreach (DateTime d in list)
                    sb.AppendLine($"{d:yyyy-MM-dd},W");

                File.AppendAllText(GetOverridePath(), sb.ToString());
                Reload();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

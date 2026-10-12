using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace SleepReminder
{
    /// <summary>
    /// 每周的作息周制。
    /// </summary>
    public enum WorkPattern
    {
        /// <summary>双休：周六、周日都休息。</summary>
        DoubleRest,

        /// <summary>单休：一周只休周日，周六照常上班。</summary>
        SingleRest,

        /// <summary>大小周：大周双休、小周单休，逐周交替。</summary>
        BigSmallWeek
    }

    /// <summary>
    /// 额度与周制设置。集合了"周制（双休 / 单休 / 大小周）"与"额度参数
    /// （固定额度、附加池、夜系数、单夜封顶、是否透支）"两类可调项。
    ///
    /// 这些参数直接决定惩罚力度，因此受"<b>每个自然周只能修改一次</b>"的锁保护，
    /// 防止出现"当晚额度不够，就临时把额度调大"这种绕开机制的操作。
    /// 设置与账本同放在 <c>HKCU\Software\SleepReminder</c> 下。
    /// </summary>
    public sealed class QuotaSettings
    {
        private const string RegKeyPath = @"Software\SleepReminder";
        private const string ValueSettings = "QuotaSettings";
        private const string ValueSettingsWeek = "LedgerSettingsWeek";
        private const string ValueSettingsCount = "LedgerSettingsChangeCount";
        private const string DateFormat = "yyyy-MM-dd";

        private static QuotaSettings? _current;
        private static readonly object Gate = new object();

        /// <summary>固定额度：每夜白送的分钟数（默认值）。</summary>
        public const int DefaultFixedMinutesPerNight = 10;

        /// <summary>附加池默认容量（分钟 / 滚动 7 天）。</summary>
        public const int DefaultWeeklyPoolMinutes = 60;

        /// <summary>宽松夜默认系数。</summary>
        public const double DefaultRelaxedNightFactor = 0.5;

        /// <summary>紧张夜默认单夜封顶（分钟）。</summary>
        public const int DefaultWorknightCapMinutes = 30;

        /// <summary>宽松夜默认单夜封顶（分钟）。</summary>
        public const int DefaultRelaxedNightCapMinutes = 60;

        /// <summary>作息周制。</summary>
        public WorkPattern Pattern { get; set; } = WorkPattern.DoubleRest;

        /// <summary>
        /// 大小周的大周基准：该日期所在的那一周视为"大周"，之后逐周交替。
        /// <c>null</c> 表示用户尚未确认过，此时 <see cref="IsBigWeek"/> 会保守地按"小周"处理，
        /// 并在程序启动时弹窗询问"本周是否为大周"。
        /// </summary>
        public DateTime? BigWeekAnchorMonday { get; set; }

        /// <summary>固定额度：每夜白送的分钟数。</summary>
        public int FixedMinutesPerNight { get; set; } = DefaultFixedMinutesPerNight;

        /// <summary>附加池容量（分钟 / 滚动 7 天）。</summary>
        public int WeeklyPoolMinutes { get; set; } = DefaultWeeklyPoolMinutes;

        /// <summary>宽松夜的附加消耗系数（越小越宽松）。</summary>
        public double RelaxedNightFactor { get; set; } = DefaultRelaxedNightFactor;

        /// <summary>紧张夜单夜延后封顶（分钟）。</summary>
        public int WorknightCapMinutes { get; set; } = DefaultWorknightCapMinutes;

        /// <summary>宽松夜单夜延后封顶（分钟）。</summary>
        public int RelaxedNightCapMinutes { get; set; } = DefaultRelaxedNightCapMinutes;

        /// <summary>附加池耗尽后是否允许透支。</summary>
        public bool AllowOverdraft { get; set; }

        /// <summary>
        /// 当前生效的设置（首次访问时从注册表装载并缓存）。
        /// 修改设置后调用 <see cref="Reload"/> 使其生效。
        /// </summary>
        public static QuotaSettings Current
        {
            get
            {
                if (_current != null)
                    return _current;

                lock (Gate)
                {
                    _current ??= Load();
                }
                return _current;
            }
        }

        /// <summary>清掉缓存，下次访问 <see cref="Current"/> 时重新读注册表。</summary>
        public static void Reload()
        {
            lock (Gate)
            {
                _current = null;
            }
        }

        // ---------- 周制推演 ----------

        /// <summary>取某个日期所在自然周的周一。</summary>
        /// <param name="date">目标日期。</param>
        /// <returns>该日期所在周的周一。</returns>
        public static DateTime GetMonday(DateTime date)
        {
            int offset = ((int)date.DayOfWeek + 6) % 7; // 周一 = 0
            return date.Date.AddDays(-offset);
        }

        /// <summary>
        /// 判断某个日期所在的那一周是否属于"大周"（双休）。仅大小周模式有意义。
        /// 以 <see cref="BigWeekAnchorMonday"/> 为基准，按周数奇偶交替；
        /// 基准尚未确认时返回 false（按更严格的"小周"处理）。
        /// </summary>
        /// <param name="date">目标日期。</param>
        /// <returns>处于大周返回 true。</returns>
        public static bool IsBigWeek(DateTime date)
        {
            DateTime? anchorValue = Current.BigWeekAnchorMonday;
            if (!anchorValue.HasValue)
                return false;

            DateTime anchor = GetMonday(anchorValue.Value);
            DateTime monday = GetMonday(date);
            int weeks = (int)Math.Round((monday - anchor).TotalDays / 7.0);
            int parity = ((weeks % 2) + 2) % 2;
            return parity == 0;
        }

        /// <summary>取"自然周键"（该周周一的日期字符串），用于每周一次的修改锁。</summary>
        /// <param name="time">目标时刻。</param>
        /// <returns>形如 <c>2026-10-05</c> 的周键。</returns>
        public static string GetWeekKey(DateTime time)
        {
            return GetMonday(time).ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>判断本周是否还允许修改设置。</summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>本周尚未改过返回 true。</returns>
        public static bool CanModify(DateTime now)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                return (key?.GetValue(ValueSettingsWeek) as string) != GetWeekKey(now);
            }
        }

        /// <summary>取历史累计的设置修改次数。</summary>
        /// <returns>累计次数。</returns>
        public static int GetChangeCount()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                return key?.GetValue(ValueSettingsCount) is int c ? c : 0;
            }
        }

        /// <summary>
        /// 尝试保存设置。若本自然周已经改过一次，则拒绝并给出提示文本。
        /// </summary>
        /// <param name="now">当前时刻。</param>
        /// <param name="message">保存结果说明（成功或拒绝原因）。</param>
        /// <returns>保存成功返回 true。</returns>
        public bool TrySave(DateTime now, out string message)
        {
            if (!CanModify(now))
            {
                message = DescribeLock(now);
                return false;
            }

            Normalize();
            Persist();

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                if (key != null)
                {
                    key.SetValue(ValueSettingsWeek, GetWeekKey(now));
                    int total = key.GetValue(ValueSettingsCount) is int c ? c : 0;
                    key.SetValue(ValueSettingsCount, total + 1);
                }
            }

            Reload();
            message = $"设置已保存（本自然周内的这一次修改机会已用尽）\n{Describe()}";
            return true;
        }

        /// <summary>生成设置锁的提示文本，用于说明"本周还能不能改"。</summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>提示文本。</returns>
        public static string DescribeLock(DateTime now)
        {
            if (CanModify(now))
                return "本周还可修改 1 次额度设置（改后本自然周内不可再改）";

            DateTime nextMonday = GetMonday(now).AddDays(7);
            return $"本周修改机会已用尽，下次可改：{nextMonday:MM-dd}（下周一）";
        }

        /// <summary>生成当前设置的可读摘要。</summary>
        /// <returns>多行摘要文本。</returns>
        public string Describe()
        {
            string pattern = Pattern switch
            {
                WorkPattern.SingleRest => "单休（只休周日）",
                WorkPattern.BigSmallWeek => BigWeekAnchorMonday.HasValue
                    ? $"大小周（大周基准 {BigWeekAnchorMonday.Value:yyyy-MM-dd}）"
                    : "大小周（大周基准待确认）",
                _ => "双休"
            };

            return string.Join("\n", new[]
            {
                $"周制：{pattern}",
                $"固定额度：{FixedMinutesPerNight} 分钟 / 夜 ｜ 附加池：{WeeklyPoolMinutes} 分钟 / 周",
                $"宽松夜系数：{RelaxedNightFactor:0.##}（紧张夜恒为 1）",
                $"单夜封顶：紧张夜 {WorknightCapMinutes} 分钟 ｜ 宽松夜 {RelaxedNightCapMinutes} 分钟",
                $"附加池耗尽：{(AllowOverdraft ? "允许透支（转债务）" : "不再放行，只剩固定额度")}"
            });
        }

        // ---------- 大小周确认 ----------

        /// <summary>当前是否为"大小周但大周基准尚未确认"，即需要询问用户。</summary>
        /// <returns>需要询问返回 true。</returns>
        public static bool NeedsBigWeekConfirmation()
        {
            QuotaSettings s = Current;
            return s.Pattern == WorkPattern.BigSmallWeek && !s.BigWeekAnchorMonday.HasValue;
        }

        /// <summary>
        /// 记录"本周是否为大周"，并据此推算大小周的基准周：
        /// 本周是大周则基准取本周一，否则取下周一（下周一必然是下一个大周）。
        ///
        /// 首次确认不占用"每周一次"的设置修改额度；确认之后若还想改，
        /// 必须走 <see cref="TrySave"/>，从而防止"临时把本周改成大周来蹭宽松夜"。
        /// </summary>
        /// <param name="today">当前日期。</param>
        /// <param name="isBigWeek">本周是否为大周（双休）。</param>
        public static void ConfirmBigWeek(DateTime today, bool isBigWeek)
        {
            DateTime monday = GetMonday(today);
            QuotaSettings s = Current;
            s.BigWeekAnchorMonday = isBigWeek ? monday : monday.AddDays(7);
            s.Persist();
            Reload();
        }

        // ---------- 校验与序列化 ----------

        /// <summary>把各字段收敛到合理区间，避免非法配置把机制玩坏。</summary>
        public void Normalize()
        {
            if (BigWeekAnchorMonday.HasValue)
                BigWeekAnchorMonday = GetMonday(BigWeekAnchorMonday.Value);

            FixedMinutesPerNight = Math.Clamp(FixedMinutesPerNight, 0, 240);
            WeeklyPoolMinutes = Math.Clamp(WeeklyPoolMinutes, 0, 1440);
            RelaxedNightFactor = Math.Clamp(RelaxedNightFactor, 0.1, 1.0);
            WorknightCapMinutes = Math.Clamp(WorknightCapMinutes, 0, 720);
            RelaxedNightCapMinutes = Math.Clamp(RelaxedNightCapMinutes, 0, 720);
        }

        /// <summary>从注册表装载设置；不存在或解析失败时返回一份默认设置。</summary>
        /// <returns>装载到的设置实例。</returns>
        private static QuotaSettings Load()
        {
            var s = new QuotaSettings();

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                if (key?.GetValue(ValueSettings) is not string raw || raw.Length == 0)
                    return s;

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string pair in raw.Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    map[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
                }

                if (map.TryGetValue("Pattern", out string? p) &&
                    Enum.TryParse(p, true, out WorkPattern pattern))
                    s.Pattern = pattern;

                if (map.TryGetValue("BigWeekAnchor", out string? a) && a.Length > 0 &&
                    DateTime.TryParseExact(a, DateFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime anchor))
                    s.BigWeekAnchorMonday = anchor;

                s.FixedMinutesPerNight = ReadInt(map, "Fixed", s.FixedMinutesPerNight);
                s.WeeklyPoolMinutes = ReadInt(map, "Pool", s.WeeklyPoolMinutes);
                s.RelaxedNightFactor = ReadDouble(map, "Factor", s.RelaxedNightFactor);
                s.WorknightCapMinutes = ReadInt(map, "CapWork", s.WorknightCapMinutes);
                s.RelaxedNightCapMinutes = ReadInt(map, "CapRelax", s.RelaxedNightCapMinutes);
                s.AllowOverdraft = map.TryGetValue("Overdraft", out string? o) && o == "1";
            }

            s.Normalize();
            return s;
        }

        /// <summary>把当前设置序列化写入注册表。</summary>
        private void Persist()
        {
            string text = string.Join(";", new[]
            {
                "Pattern=" + Pattern,
                "BigWeekAnchor=" + (BigWeekAnchorMonday.HasValue
                    ? BigWeekAnchorMonday.Value.ToString(DateFormat, CultureInfo.InvariantCulture)
                    : string.Empty),
                "Fixed=" + FixedMinutesPerNight.ToString(CultureInfo.InvariantCulture),
                "Pool=" + WeeklyPoolMinutes.ToString(CultureInfo.InvariantCulture),
                "Factor=" + RelaxedNightFactor.ToString("0.###", CultureInfo.InvariantCulture),
                "CapWork=" + WorknightCapMinutes.ToString(CultureInfo.InvariantCulture),
                "CapRelax=" + RelaxedNightCapMinutes.ToString(CultureInfo.InvariantCulture),
                "Overdraft=" + (AllowOverdraft ? "1" : "0")
            });

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(ValueSettings, text);
            }
        }

        /// <summary>从键值表读一个整数，缺失或非法时返回默认值。</summary>
        private static int ReadInt(Dictionary<string, string> map, string name, int fallback)
        {
            return map.TryGetValue(name, out string? v) &&
                   int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed : fallback;
        }

        /// <summary>从键值表读一个浮点数，缺失或非法时返回默认值。</summary>
        private static double ReadDouble(Dictionary<string, string> map, string name, double fallback)
        {
            return map.TryGetValue(name, out string? v) &&
                   double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed : fallback;
        }
    }
}

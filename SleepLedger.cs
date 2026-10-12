using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace SleepReminder
{
    /// <summary>
    /// 睡眠额度账本：把"延后"当成一笔需要计价的借款来管理。
    ///
    /// 额度分两层：
    /// <list type="bullet">
    ///   <item><description>固定额度——每夜白给 <see cref="QuotaSettings.FixedMinutesPerNight"/> 分钟，
    ///     跨夜重置、不可累积；</description></item>
    ///   <item><description>附加额度——一个滚动 7 天、容量 <see cref="QuotaSettings.WeeklyPoolMinutes"/>
    ///     分钟的池子。想超出固定额度继续延后，就从池子里按"夜系数"支取。</description></item>
    /// </list>
    ///
    /// 夜系数按"夜"的类型浮动：<b>宽松夜</b>（次日不用上班，含周五夜、周六夜与长假前夜）
    /// 为 <see cref="QuotaSettings.RelaxedNightFactor"/>；<b>紧张夜</b>（次日要上班，
    /// 含调休补班的周五夜、单休制度下的周五夜）为 1.0。是否为宽松夜由
    /// <see cref="WorkdayCalendar"/> 判定，因此节假日、调休、大小周都会被自动覆盖。
    ///
    /// "夜"的归属以当天提醒开始时刻为界，跨零点仍算同一夜。
    /// 所有状态落在 <c>HKCU\Software\SleepReminder</c>，配合心跳时间戳与系统注册表
    /// <c>ShutdownTime</c> 做启动对账，使"注销 / 重启 / 强杀"都无法把账抹掉。
    /// </summary>
    public static class SleepLedger
    {
        private const string RegKeyPath = @"Software\SleepReminder";
        private const string ValueNightLog = "LedgerNightLog";
        private const string ValueActiveSnooze = "LedgerActiveSnoozeStart";
        private const string ValueHeartbeat = "LedgerHeartbeat";
        private const string ValueViolations = "LedgerViolations";

        private const string DateFormat = "yyyy-MM-dd";
        private const string StampFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>判定时钟回拨时允许的误差分钟数。</summary>
        private const int ClockRollbackToleranceMinutes = 2;

        /// <summary>账本中单"夜"的记录：该夜总延后分钟与其中从附加池支取的分钟。</summary>
        private sealed class NightRecord
        {
            /// <summary>该笔记录归属的"夜"（对应的自然日）。</summary>
            public DateTime NightDate { get; set; }

            /// <summary>该夜累计延后分钟数。</summary>
            public int TotalMinutes { get; set; }

            /// <summary>该夜累计从附加池支取的分钟数（已含夜系数）。</summary>
            public int PoolMinutes { get; set; }
        }

        // ---------- 夜与系数 ----------

        /// <summary>
        /// 按提醒开始时刻归属"夜"：当天时刻不早于提醒开始则算今天，否则算昨天。
        /// 这样 23:30 与次日 00:30 会被归到同一夜，跨零点不会出现"两套账"。
        /// </summary>
        /// <param name="time">要归属的时刻。</param>
        /// <returns>该时刻所属"夜"的自然日。</returns>
        public static DateTime GetNightDate(DateTime time)
        {
            var (sh, sm, _, _) = RegistryHelper.LoadReminderTime();
            var start = new TimeSpan(sh, sm, 0);
            return time.TimeOfDay >= start ? time.Date : time.Date.AddDays(-1);
        }

        /// <summary>
        /// 判断某个"夜"是否属于宽松夜——判据是"次日要不要上班"，
        /// 因此法定节假日、调休补班、大小周都会被自动覆盖。
        /// </summary>
        /// <param name="nightDate">"夜"对应的自然日。</param>
        /// <returns>次日休息返回 true（本周宽松夜）。</returns>
        public static bool IsRelaxedNight(DateTime nightDate)
        {
            return WorkdayCalendar.IsRelaxedNight(nightDate);
        }

        /// <summary>取该夜的附加消耗系数：宽松夜可打折。</summary>
        /// <param name="nightDate">"夜"对应的自然日。</param>
        /// <returns>紧张夜返回 1.0，宽松夜返回配置的系数。</returns>
        public static double GetNightFactor(DateTime nightDate)
        {
            return IsRelaxedNight(nightDate) ? QuotaSettings.Current.RelaxedNightFactor : 1.0;
        }

        /// <summary>取该夜的单夜延后封顶（分钟）。</summary>
        /// <param name="nightDate">"夜"对应的自然日。</param>
        /// <returns>紧张夜与宽松夜各自的上限。</returns>
        public static int GetNightCap(DateTime nightDate)
        {
            QuotaSettings s = QuotaSettings.Current;
            return IsRelaxedNight(nightDate) ? s.RelaxedNightCapMinutes : s.WorknightCapMinutes;
        }

        // ---------- 账本读写 ----------

        /// <summary>
        /// 从注册表读出整份夜账本。格式为 <c>yyyy-MM-dd:总延后:附加池用量</c>，
        /// 多项以分号分隔；任何解析失败的条目会被安全忽略。
        /// </summary>
        /// <returns>账本中的所有夜记录（可能为空列表）。</returns>
        private static List<NightRecord> LoadLog()
        {
            var list = new List<NightRecord>();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                if (key?.GetValue(ValueNightLog) is not string raw || raw.Length == 0)
                    return list;

                foreach (string entry in raw.Split(';'))
                {
                    string[] parts = entry.Split(':');
                    if (parts.Length != 3)
                        continue;
                    if (!DateTime.TryParseExact(parts[0], DateFormat, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out DateTime night))
                        continue;
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total))
                        continue;
                    if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pool))
                        continue;

                    list.Add(new NightRecord { NightDate = night, TotalMinutes = total, PoolMinutes = pool });
                }
            }
            return list;
        }

        /// <summary>把整份夜账本写回注册表，并顺手裁掉 14 天以前的记录，避免值无限膨胀。</summary>
        /// <param name="list">要保存的夜记录集合。</param>
        private static void SaveLog(List<NightRecord> list)
        {
            DateTime cutoff = DateTime.Today.AddDays(-14);
            string text = string.Join(";", list
                .Where(r => r.NightDate >= cutoff)
                .OrderBy(r => r.NightDate)
                .Select(r => string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2}",
                    r.NightDate.ToString(DateFormat, CultureInfo.InvariantCulture),
                    r.TotalMinutes, r.PoolMinutes)));

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(ValueNightLog, text);
            }
        }

        /// <summary>在账本中取出指定"夜"的记录，不存在则新建一条零记录。</summary>
        /// <param name="list">账本集合。</param>
        /// <param name="nightDate">目标"夜"。</param>
        /// <returns>对应的夜记录（已挂入集合）。</returns>
        private static NightRecord GetOrCreate(List<NightRecord> list, DateTime nightDate)
        {
            NightRecord rec = list.FirstOrDefault(r => r.NightDate == nightDate);
            if (rec == null)
            {
                rec = new NightRecord { NightDate = nightDate };
                list.Add(rec);
            }
            return rec;
        }

        // ---------- 额度查询 ----------

        /// <summary>取今晚已经延后的总分钟数。</summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>今晚累计延后分钟数。</returns>
        public static int GetTonightSnoozedMinutes(DateTime now)
        {
            DateTime nightDate = GetNightDate(now);
            NightRecord rec = LoadLog().FirstOrDefault(r => r.NightDate == nightDate);
            return rec?.TotalMinutes ?? 0;
        }

        /// <summary>取滚动 7 天窗口（本夜及之前 6 夜）内已消耗的附加池分钟数。</summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>窗口内附加池消耗总量。</returns>
        public static int GetPoolUsed(DateTime now)
        {
            DateTime nightDate = GetNightDate(now);
            DateTime from = nightDate.AddDays(-6);
            return LoadLog()
                .Where(r => r.NightDate >= from && r.NightDate <= nightDate)
                .Sum(r => r.PoolMinutes);
        }

        /// <summary>取附加池剩余可支取分钟数。</summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>剩余分钟数，最小为 0。</returns>
        public static int GetPoolRemaining(DateTime now)
        {
            return Math.Max(0, QuotaSettings.Current.WeeklyPoolMinutes - GetPoolUsed(now));
        }

        /// <summary>
        /// 计算此刻"还能延后多少分钟"，同时受三重约束：单夜封顶、固定额度余额、附加池余额。
        /// 若允许透支，则附加池不再构成上限（透支额将转为债务）。
        /// </summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>此刻最多还能延后的分钟数。</returns>
        public static int GetAvailableMinutes(DateTime now)
        {
            QuotaSettings s = QuotaSettings.Current;
            DateTime nightDate = GetNightDate(now);
            int snoozed = GetTonightSnoozedMinutes(now);

            int room = GetNightCap(nightDate) - snoozed;
            if (room <= 0)
                return 0;

            if (s.AllowOverdraft)
                return room;

            int fixedLeft = Math.Max(0, s.FixedMinutesPerNight - snoozed);
            double factor = GetNightFactor(nightDate);
            int extraFromPool = (int)Math.Floor(GetPoolRemaining(now) / factor);

            return Math.Min(room, fixedLeft + extraFromPool);
        }

        // ---------- 记账 ----------

        /// <summary>
        /// 记一笔延后：先吃本夜固定额度，超出部分按本夜系数从附加池支取。
        /// 每次调用都会立刻写盘，因此断电、重启、强杀都不会丢账。
        /// </summary>
        /// <param name="now">当前时刻，用于归属"夜"与取系数。</param>
        /// <param name="minutes">本次希望延后的分钟数。</param>
        /// <returns>实际入账的分钟数（可能因封顶或额度不足被截断）。</returns>
        public static int RecordSnooze(DateTime now, int minutes)
        {
            if (minutes <= 0)
                return 0;

            int booked = Math.Min(minutes, GetAvailableMinutes(now));
            if (booked <= 0)
                return 0;

            DateTime nightDate = GetNightDate(now);
            List<NightRecord> list = LoadLog();
            NightRecord rec = GetOrCreate(list, nightDate);

            int fixedLeft = Math.Max(0, QuotaSettings.Current.FixedMinutesPerNight - rec.TotalMinutes);
            int fromFixed = Math.Min(booked, fixedLeft);
            int rest = booked - fromFixed;
            int poolCost = (int)Math.Ceiling(rest * GetNightFactor(nightDate));

            rec.TotalMinutes += booked;
            rec.PoolMinutes += poolCost;

            SaveLog(list);
            WriteHeartbeat(now);
            return booked;
        }

        /// <summary>
        /// 结清一次"正常走完"的延后：按实际经过的分钟数入账，并清除"延后中"标记。
        ///
        /// 与 <see cref="ReconcileOnStartup"/> 互斥——两者都会清除"延后中"标记，
        /// 因此同一次延后只会被结算一次，绝不会重复计费：正常走完走本方法，
        /// 被强杀 / 关机 / 注销则由下次启动的对账补上。
        /// </summary>
        /// <param name="start">延后开始时刻（<see cref="BeginSnooze"/> 写入的时刻）。</param>
        /// <param name="end">延后结束时刻，通常为倒计时归零的时刻。</param>
        /// <returns>本次实际入账的分钟数。</returns>
        public static int SettleSnooze(DateTime start, DateTime end)
        {
            if (end < start)
                end = start;

            int elapsed = (int)Math.Floor((end - start).TotalMinutes);
            int booked = elapsed > 0 ? RecordSnooze(end, elapsed) : 0;
            EndSnooze();
            return booked;
        }

        /// <summary>
        /// 生成给延后界面显示的额度摘要，例如
        /// <c>固定延后 10 分钟 ｜ 本周可附加 42 分钟 ｜ 宽松夜（系数 0.5）</c>。
        /// </summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>单行摘要文本。</returns>
        public static string DescribeQuota(DateTime now)
        {
            DateTime nightDate = GetNightDate(now);
            int fixedLeft = Math.Max(0, QuotaSettings.Current.FixedMinutesPerNight - GetTonightSnoozedMinutes(now));
            string tag = IsRelaxedNight(nightDate) ? "宽松夜" : "紧张夜";
            return string.Format(CultureInfo.InvariantCulture,
                "固定延后 {0} 分钟 ｜ 本周可附加 {1} 分钟 ｜ {2}（系数 {3:0.#}）",
                fixedLeft, GetPoolRemaining(now), tag, GetNightFactor(nightDate));
        }

        // ---------- 心跳与"延后中"标记 ----------

        /// <summary>写入一次心跳时间戳，作为"程序在当时还活着"的证据。</summary>
        /// <param name="now">当前时刻。</param>
        public static void WriteHeartbeat(DateTime now)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(ValueHeartbeat, now.ToString(StampFormat, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>读取上一次心跳时间戳。</summary>
        /// <returns>上次心跳时刻；不存在或格式非法时返回 null。</returns>
        public static DateTime? GetLastHeartbeat()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                if (key?.GetValue(ValueHeartbeat) is string raw &&
                    DateTime.TryParseExact(raw, StampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime t))
                    return t;
            }
            return null;
        }

        /// <summary>标记"延后开始"，作为程序重启后对账的锚点。</summary>
        /// <param name="now">延后开始时刻。</param>
        public static void BeginSnooze(DateTime now)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(ValueActiveSnooze, now.ToString(StampFormat, CultureInfo.InvariantCulture));
            }
            WriteHeartbeat(now);
        }

        /// <summary>延后正常结束时清除"延后中"标记。</summary>
        public static void EndSnooze()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath, true))
            {
                key?.DeleteValue(ValueActiveSnooze, false);
            }
        }

        /// <summary>读取"延后中"的起始时刻。</summary>
        /// <returns>延后起始时刻；当前不在延后中则返回 null。</returns>
        public static DateTime? GetActiveSnoozeStart()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                if (key?.GetValue(ValueActiveSnooze) is string raw &&
                    DateTime.TryParseExact(raw, StampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime t))
                    return t;
            }
            return null;
        }

        // ---------- 外部证据与违规 ----------

        /// <summary>
        /// 读取 Windows 记录的上次关机时刻（注册表
        /// <c>HKLM\SYSTEM\CurrentControlSet\Control\Windows\ShutdownTime</c>，REG_BINARY 的 FILETIME）。
        /// 该值由操作系统写入，用户改本程序自己的账本不影响它，可用于交叉对账。
        /// </summary>
        /// <returns>上次关机时刻；读不到时返回 null。</returns>
        public static DateTime? TryGetSystemShutdownTime()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Windows"))
                {
                    if (key?.GetValue("ShutdownTime") is byte[] raw && raw.Length >= 8)
                    {
                        long fileTime = BitConverter.ToInt64(raw, 0);
                        if (fileTime <= 0)
                            return null;
                        return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
                    }
                }
            }
            catch
            {
                // 读不到就当作没有这条证据，不影响主流程
            }
            return null;
        }

        /// <summary>取累计违约次数。</summary>
        /// <returns>违约计数。</returns>
        public static int GetViolationCount()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                return key?.GetValue(ValueViolations) is int v ? v : 0;
            }
        }

        /// <summary>累加一次违约。</summary>
        /// <param name="reason">违约原因，仅供日志与提示使用。</param>
        public static void AddViolation(string reason)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                if (key == null)
                    return;
                int current = key.GetValue(ValueViolations) is int v ? v : 0;
                key.SetValue(ValueViolations, current + 1);
                key.SetValue(ValueViolations + "LastReason", reason ?? string.Empty);
            }
        }

        // ---------- 启动对账 ----------

        /// <summary>
        /// 程序启动时的对账：结算上一次"延后中"若被中断所对应的时长，
        /// 并顺带检测时钟回拨。
        ///
        /// 结算时刻取"最后可确认程序还活着的时刻"——即
        /// <c>max(最后心跳, 系统记录的上次关机时刻)</c>，再被当前时刻截断，
        /// 以保证在强杀、拔电、注销场景下都能算出至少已消耗的时长。
        /// </summary>
        /// <param name="now">本次启动时刻。</param>
        /// <returns>给用户看的对账摘要；无需提示时返回 null。</returns>
        public static string? ReconcileOnStartup(DateTime now)
        {
            var notes = new List<string>();

            // 1) 结算被中断的"延后中"
            DateTime? start = GetActiveSnoozeStart();
            if (start.HasValue && start.Value <= now)
            {
                DateTime lastAlive = GetLastHeartbeat() ?? start.Value;
                if (lastAlive < start.Value)
                    lastAlive = start.Value;

                DateTime? shutdown = TryGetSystemShutdownTime();
                if (shutdown.HasValue && shutdown.Value > lastAlive && shutdown.Value <= now)
                    lastAlive = shutdown.Value;

                int elapsed = (int)Math.Floor((lastAlive - start.Value).TotalMinutes);
                if (elapsed > 0)
                {
                    int booked = RecordSnooze(now, elapsed);
                    notes.Add($"上次延后被中断，补记 {booked} 分钟（实际经过 {elapsed} 分钟）");
                }
                EndSnooze();
            }

            // 2) 时钟回拨检测
            DateTime? heartbeat = GetLastHeartbeat();
            if (heartbeat.HasValue && now < heartbeat.Value.AddMinutes(-ClockRollbackToleranceMinutes))
            {
                AddViolation("检测到系统时钟回拨");
                notes.Add($"检测到时钟回拨（上次心跳 {heartbeat.Value:HH:mm}，现在却是 {now:HH:mm}），已记一次违约");
            }

            return notes.Count > 0 ? string.Join("\n", notes) : null;
        }
    }
}

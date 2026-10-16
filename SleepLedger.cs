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
    ///   <item><description>附加额度——两个各自滚动 7 天的池子：<b>休息池</b>（容量
    ///     <see cref="QuotaSettings.RelaxedPoolMinutes"/> 分钟，只供宽松夜支取）与
    ///     <b>工作池</b>（容量 <see cref="QuotaSettings.WorknightPoolMinutes"/> 分钟，
    ///     只供紧张夜支取）。想超出固定额度继续延后，就从本夜对应的那个池里按"夜系数"支取。</description></item>
    /// </list>
    ///
    /// <b>两个池互不挤占</b>：周末挥霍掉的只会是休息池，工作日那份额度原样留着。
    /// 由于每个"夜"天然只属于宽松或紧张一种类型，账本无须额外记标记——
    /// 按夜型筛选夜记录再求和，两个池的用量就分开了。
    ///
    /// 夜系数按"夜"的类型浮动：<b>宽松夜</b>（次日不用上班，含周五夜、周六夜与长假前夜）
    /// 为 <see cref="QuotaSettings.RelaxedNightFactor"/>；<b>紧张夜</b>（次日要上班，
    /// 含调休补班的周五夜、单休制度下的周五夜）为 1.0。是否为宽松夜由
    /// <see cref="WorkdayCalendar"/> 判定，因此节假日、调休、大小周都会被自动覆盖。
    ///
    /// <b>账本的时间单位是秒</b>。设置项（固定额度 / 两池容量 / 单夜封顶）仍以分钟配置，
    /// 但一切记账、结算与余额都以秒计算。原因：若按分钟记账，"延后 1 分 59 秒就退出"
    /// 会被向下取整成 1 分钟，每次白嫖 59 秒——反复操作即可无限延后而不入账。
    /// 以秒记账后，实际流逝多少就记多少，没有任何截断空间。
    ///
    /// "夜"的归属以当天提醒开始时刻为界，跨零点仍算同一夜。
    /// 所有状态落在 <c>HKCU\Software\SleepReminder</c>，配合心跳时间戳与系统注册表
    /// <c>ShutdownTime</c> 做启动对账，使"注销 / 重启 / 强杀"都无法把账抹掉。
    /// </summary>
    public static class SleepLedger
    {
        private const string RegKeyPath = @"Software\SleepReminder";

        /// <summary>夜账本（秒级）。格式为 <c>yyyy-MM-dd:总延后秒:池消耗秒</c>。</summary>
        private const string ValueNightLog = "LedgerNightLogSec";

        /// <summary>旧的夜账本（分钟级）。仅用于一次性迁移，迁移完成后即删除。</summary>
        private const string ValueNightLogLegacy = "LedgerNightLog";

        private const string ValueActiveSnooze = "LedgerActiveSnoozeStart";
        private const string ValueHeartbeat = "LedgerHeartbeat";
        private const string ValueViolations = "LedgerViolations";

        private const string DateFormat = "yyyy-MM-dd";
        private const string StampFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>判定时钟回拨时允许的误差分钟数。</summary>
        private const int ClockRollbackToleranceMinutes = 2;

        /// <summary>账本中单"夜"的记录：该夜总延后秒数与其中从本夜所属池支取的秒数。</summary>
        private sealed class NightRecord
        {
            /// <summary>该笔记录归属的"夜"（对应的自然日）。</summary>
            public DateTime NightDate { get; set; }

            /// <summary>该夜累计延后秒数。</summary>
            public int TotalSeconds { get; set; }

            /// <summary>该夜累计从本夜所属池支取的秒数（已含夜系数）。</summary>
            public int PoolSeconds { get; set; }
        }

        // ---------- 时长格式化 ----------

        /// <summary>
        /// 把秒数格式化成紧凑计时（<c>mm:ss</c>，满一小时为 <c>H:mm:ss</c>），
        /// 供额度摘要、预览表格与倒计时等处的显示统一使用。
        /// </summary>
        /// <param name="seconds">秒数，负数按 0 处理。</param>
        /// <returns>形如 <c>08:30</c> 或 <c>1:52:30</c> 的文本。</returns>
        public static string FormatClock(int seconds)
        {
            if (seconds < 0)
                seconds = 0;

            int hours = seconds / 3600;
            int minutes = seconds % 3600 / 60;
            int rest = seconds % 60;

            return hours > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}", hours, minutes, rest)
                : string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", minutes, rest);
        }

        /// <summary>
        /// 把秒数格式化成叙述性文本（如 <c>15 分 30 秒</c>），
        /// 用于"补记了多少"这类出现在句子里的场合。
        /// </summary>
        /// <param name="seconds">秒数，负数按 0 处理。</param>
        /// <returns>可读的时长文本。</returns>
        public static string FormatDurationText(int seconds)
        {
            if (seconds < 0)
                seconds = 0;

            int hours = seconds / 3600;
            int minutes = seconds % 3600 / 60;
            int rest = seconds % 60;

            if (hours > 0)
                return $"{hours} 小时 {minutes} 分 {rest} 秒";
            return minutes > 0 ? $"{minutes} 分 {rest} 秒" : $"{rest} 秒";
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

        /// <summary>取该夜的单夜延后封顶（分钟，配置值的原始单位）。</summary>
        /// <param name="nightDate">"夜"对应的自然日。</param>
        /// <returns>紧张夜与宽松夜各自的上限。</returns>
        public static int GetNightCap(DateTime nightDate)
        {
            QuotaSettings s = QuotaSettings.Current;
            return IsRelaxedNight(nightDate) ? s.RelaxedNightCapMinutes : s.WorknightCapMinutes;
        }

        /// <summary>取该夜的单夜延后封顶（秒），供账本内部计算使用。</summary>
        /// <param name="nightDate">"夜"对应的自然日。</param>
        /// <returns>封顶秒数。</returns>
        private static int GetNightCapSeconds(DateTime nightDate)
        {
            return GetNightCap(nightDate) * 60;
        }

        // ---------- 账本读写 ----------

        /// <summary>
        /// 从注册表读出整份夜账本。格式为 <c>yyyy-MM-dd:总延后秒:池消耗秒</c>，
        /// 多项以分号分隔；任何解析失败的条目会被安全忽略。
        ///
        /// 若尚未写过秒级账本、却存在旧的分钟级账本（<c>LedgerNightLog</c>），
        /// 则在此按 ×60 一次性迁移到新键并删除旧值——账一笔不丢，且不会重复迁移。
        /// 迁移完成后同一夜的余额与单位都会是秒。
        /// </summary>
        /// <returns>账本中的所有夜记录（可能为空列表）。</returns>
        private static List<NightRecord> LoadLog()
        {
            var list = new List<NightRecord>();

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                if (key == null)
                    return list;

                if (key.GetValue(ValueNightLog) is string raw && raw.Length > 0)
                {
                    ParseLog(raw, list, legacyMinutes: false);
                    return list;
                }

                if (key.GetValue(ValueNightLogLegacy) is string legacy && legacy.Length > 0 &&
                    ParseLog(legacy, list, legacyMinutes: true))
                {
                    SaveLog(list);
                    key.DeleteValue(ValueNightLogLegacy, false);
                }
            }

            return list;
        }

        /// <summary>解析一份账本文本并追加到集合。</summary>
        /// <param name="raw">账本文本。</param>
        /// <param name="list">解析结果追加到此集合。</param>
        /// <param name="legacyMinutes">为 true 时把两个数值按分钟理解，统一 ×60 转成秒。</param>
        /// <returns>至少解析出一条记录返回 true。</returns>
        private static bool ParseLog(string raw, List<NightRecord> list, bool legacyMinutes)
        {
            bool any = false;
            int scale = legacyMinutes ? 60 : 1;

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

                list.Add(new NightRecord
                {
                    NightDate = night,
                    TotalSeconds = total * scale,
                    PoolSeconds = pool * scale
                });
                any = true;
            }

            return any;
        }

        /// <summary>把整份夜账本写回注册表（秒级），并顺手裁掉 14 天以前的记录，避免值无限膨胀。</summary>
        /// <param name="list">要保存的夜记录集合。</param>
        private static void SaveLog(List<NightRecord> list)
        {
            DateTime cutoff = DateTime.Today.AddDays(-14);
            string text = string.Join(";", list
                .Where(r => r.NightDate >= cutoff)
                .OrderBy(r => r.NightDate)
                .Select(r => string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2}",
                    r.NightDate.ToString(DateFormat, CultureInfo.InvariantCulture),
                    r.TotalSeconds, r.PoolSeconds)));

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

        /// <summary>给界面展示用的单夜摘要。</summary>
        public sealed class NightSummary
        {
            /// <summary>该笔记录归属的"夜"（对应的自然日）。</summary>
            public DateTime NightDate { get; init; }

            /// <summary>该夜累计延后秒数。</summary>
            public int TotalSeconds { get; init; }

            /// <summary>该夜从本夜所属池支取的秒数（已含夜系数）。</summary>
            public int PoolSeconds { get; init; }

            /// <summary>该夜是否为宽松夜。</summary>
            public bool IsRelaxed { get; init; }
        }

        /// <summary>
        /// 取最近若干夜（含本夜）的摘要，按日期升序，供"额度预览"窗口列表使用。
        /// 窗口内没有记录的夜会缺失，由调用方自行补零或跳过。
        /// </summary>
        /// <param name="now">当前时刻。</param>
        /// <param name="nights">要回溯的夜数，含本夜。</param>
        /// <returns>最近的夜摘要列表（可能为空）。</returns>
        public static List<NightSummary> GetRecentNights(DateTime now, int nights)
        {
            if (nights <= 0)
                return new List<NightSummary>();

            DateTime nightDate = GetNightDate(now);
            DateTime from = nightDate.AddDays(-(nights - 1));

            return LoadLog()
                .Where(r => r.NightDate >= from && r.NightDate <= nightDate)
                .OrderBy(r => r.NightDate)
                .Select(r => new NightSummary
                {
                    NightDate = r.NightDate,
                    TotalSeconds = r.TotalSeconds,
                    PoolSeconds = r.PoolSeconds,
                    IsRelaxed = IsRelaxedNight(r.NightDate)
                })
                .ToList();
        }

        /// <summary>取今晚已经延后的总秒数。</summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>今晚累计延后秒数。</returns>
        public static int GetTonightSnoozedSeconds(DateTime now)
        {
            DateTime nightDate = GetNightDate(now);
            NightRecord rec = LoadLog().FirstOrDefault(r => r.NightDate == nightDate);
            return rec?.TotalSeconds ?? 0;
        }

        /// <summary>
        /// 取滚动 7 天窗口（本夜及之前 6 夜）内、指定夜型所属池的消耗秒数。
        ///
        /// 每个"夜"天然只属于宽松或紧张一种类型，因此账本里不必额外记标记：
        /// 按夜型筛选夜记录再求和，休息池与工作池的消耗就完全分开了。
        /// </summary>
        /// <param name="now">当前时刻（决定窗口的终点）。</param>
        /// <param name="relaxed">true 统计休息池（宽松夜），false 统计工作池（紧张夜）。</param>
        /// <returns>窗口内该池的消耗总量（秒）。</returns>
        public static int GetPoolUsedSeconds(DateTime now, bool relaxed)
        {
            DateTime nightDate = GetNightDate(now);
            DateTime from = nightDate.AddDays(-6);
            return LoadLog()
                .Where(r => r.NightDate >= from && r.NightDate <= nightDate)
                .Where(r => IsRelaxedNight(r.NightDate) == relaxed)
                .Sum(r => r.PoolSeconds);
        }

        /// <summary>取指定夜型对应池的容量（秒）。</summary>
        /// <param name="relaxed">true 取休息池，false 取工作池。</param>
        /// <returns>池容量秒数。</returns>
        private static int GetPoolCapacitySeconds(bool relaxed)
        {
            QuotaSettings s = QuotaSettings.Current;
            return (relaxed ? s.RelaxedPoolMinutes : s.WorknightPoolMinutes) * 60;
        }

        /// <summary>取指定夜型对应池的剩余可支取秒数。</summary>
        /// <param name="now">当前时刻（决定窗口的终点）。</param>
        /// <param name="relaxed">true 取休息池，false 取工作池。</param>
        /// <returns>剩余秒数，最小为 0。</returns>
        public static int GetPoolRemainingSeconds(DateTime now, bool relaxed)
        {
            return Math.Max(0, GetPoolCapacitySeconds(relaxed) - GetPoolUsedSeconds(now, relaxed));
        }

        /// <summary>取某个"夜"所对应池的可读名称。</summary>
        /// <param name="nightDate">"夜"对应的自然日。</param>
        /// <returns><c>休息池</c> 或 <c>工作池</c>。</returns>
        public static string GetPoolName(DateTime nightDate)
        {
            return IsRelaxedNight(nightDate) ? "休息池" : "工作池";
        }

        /// <summary>
        /// 计算此刻"还能延后多少秒"，同时受三重约束：单夜封顶、固定额度余额、本夜所属池的余额
        /// （宽松夜看休息池、紧张夜看工作池）。
        /// 若允许透支，则池子不再构成上限（透支额将转为债务）。
        /// </summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>此刻最多还能延后的秒数。</returns>
        public static int GetAvailableSeconds(DateTime now)
        {
            QuotaSettings s = QuotaSettings.Current;
            DateTime nightDate = GetNightDate(now);
            int snoozed = GetTonightSnoozedSeconds(now);

            int room = GetNightCapSeconds(nightDate) - snoozed;
            if (room <= 0)
                return 0;

            if (s.AllowOverdraft)
                return room;

            int fixedLeft = Math.Max(0, s.FixedMinutesPerNight * 60 - snoozed);
            double factor = GetNightFactor(nightDate);
            int extraFromPool = (int)Math.Floor(GetPoolRemainingSeconds(now, IsRelaxedNight(nightDate)) / factor);

            return Math.Min(room, fixedLeft + extraFromPool);
        }

        // ---------- 记账 ----------

        /// <summary>
        /// 记一笔延后：先吃本夜固定额度，超出部分按本夜系数从本夜所属池支取。
        /// 每次调用都会立刻写盘，因此断电、重启、强杀都不会丢账。
        /// </summary>
        /// <param name="now">当前时刻，用于归属"夜"与取系数。</param>
        /// <param name="seconds">本次希望延后的秒数。</param>
        /// <returns>实际入账的秒数（可能因封顶或额度不足被截断）。</returns>
        public static int RecordSnoozeSeconds(DateTime now, int seconds)
        {
            if (seconds <= 0)
                return 0;

            int booked = Math.Min(seconds, GetAvailableSeconds(now));
            if (booked <= 0)
                return 0;

            DateTime nightDate = GetNightDate(now);
            List<NightRecord> list = LoadLog();
            NightRecord rec = GetOrCreate(list, nightDate);

            int fixedLeft = Math.Max(0, QuotaSettings.Current.FixedMinutesPerNight * 60 - rec.TotalSeconds);
            int fromFixed = Math.Min(booked, fixedLeft);
            int rest = booked - fromFixed;
            int poolCost = (int)Math.Ceiling(rest * GetNightFactor(nightDate));

            rec.TotalSeconds += booked;
            rec.PoolSeconds += poolCost;

            SaveLog(list);
            WriteHeartbeat(now);
            return booked;
        }

        /// <summary>
        /// 结清一次"正常走完"的延后：按实际经过的秒数入账，并清除"延后中"标记。
        ///
        /// 与 <see cref="ReconcileOnStartup"/> 互斥——两者都会清除"延后中"标记，
        /// 因此同一次延后只会被结算一次，绝不会重复计费：正常走完走本方法，
        /// 被强杀 / 关机 / 注销则由下次启动的对账补上。
        /// </summary>
        /// <param name="start">延后开始时刻（<see cref="BeginSnooze"/> 写入的时刻）。</param>
        /// <param name="end">延后结束时刻，通常为倒计时归零的时刻。</param>
        /// <returns>本次实际入账的秒数。</returns>
        public static int SettleSnooze(DateTime start, DateTime end)
        {
            if (end < start)
                end = start;

            int elapsed = (int)Math.Floor((end - start).TotalSeconds);
            int booked = elapsed > 0 ? RecordSnoozeSeconds(end, elapsed) : 0;
            EndSnooze();
            return booked;
        }

        /// <summary>
        /// 生成给延后界面显示的额度摘要，例如
        /// <c>固定额度剩 09:30 ｜ 工作池剩 42:00 ｜ 紧张夜（系数 1）</c>。
        ///
        /// 池名随本夜类型变化（宽松夜显示"休息池"、紧张夜显示"工作池"），
        /// 用词与“设置”窗口保持一致；两个数字都明确写成“剩多少”（而非设置值），
        /// 免得被误读成“一次能延后多久”。
        /// </summary>
        /// <param name="now">当前时刻。</param>
        /// <returns>单行摘要文本。</returns>
        public static string DescribeQuota(DateTime now)
        {
            DateTime nightDate = GetNightDate(now);
            bool relaxed = IsRelaxedNight(nightDate);
            int fixedLeft = Math.Max(0, QuotaSettings.Current.FixedMinutesPerNight * 60 - GetTonightSnoozedSeconds(now));
            string tag = relaxed ? "宽松夜" : "紧张夜";
            return string.Format(CultureInfo.InvariantCulture,
                "固定额度剩 {0} ｜ {1}剩 {2} ｜ {3}（系数 {4:0.#}）",
                FormatClock(fixedLeft), GetPoolName(nightDate),
                FormatClock(GetPoolRemainingSeconds(now, relaxed)), tag, GetNightFactor(nightDate));
        }

        /// <summary>
        /// 计算"延后进行中"的<b>本夜固定额度剩余秒数</b>：本夜剩余固定额度扣掉本次已流逝的秒数，
        /// 使界面上的固定额度能随倒计时逐秒递减，而不是停在进入延后那一刻的快照上。
        ///
        /// <b>纯计算，不写账本</b>——真正的入账仍由 <see cref="SettleSnooze"/> 在倒计时结束时
        /// 一次性完成；因此倒计时归零时（流逝秒数 = 本次计划秒数）这里算出的余额，
        /// 与随后 <see cref="SettleSnooze"/> 入账后的余额一致。
        /// </summary>
        /// <param name="snoozeStart">本次延后的开始时刻（用于归属"夜"）。</param>
        /// <param name="elapsedSeconds">本次延后已经流逝的秒数。</param>
        /// <returns>固定额度剩余秒数，最小为 0。</returns>
        public static int GetLiveFixedQuotaSeconds(DateTime snoozeStart, int elapsedSeconds)
        {
            int fixedLeft = Math.Max(0,
                QuotaSettings.Current.FixedMinutesPerNight * 60 - GetTonightSnoozedSeconds(snoozeStart));
            return Math.Max(0, fixedLeft - Math.Max(0, elapsedSeconds));
        }

        /// <summary>
        /// 计算"延后进行中"本次按夜系数计入<b>本夜所属池</b>的秒数（口径与 <see cref="RecordSnoozeSeconds"/> 一致：
        /// 先吃固定额度，超出部分才按系数折算）。纯计算，不写账本。
        /// </summary>
        /// <param name="snoozeStart">本次延后的开始时刻。</param>
        /// <param name="elapsedSeconds">本次延后已经流逝的秒数。</param>
        /// <returns>本次已计入该池的秒数。</returns>
        private static int GetLivePoolCost(DateTime snoozeStart, int elapsedSeconds)
        {
            QuotaSettings s = QuotaSettings.Current;
            int settled = GetTonightSnoozedSeconds(snoozeStart);
            int booked = Math.Min(Math.Max(0, elapsedSeconds), GetAvailableSeconds(snoozeStart));
            int fromFixed = Math.Min(booked, Math.Max(0, s.FixedMinutesPerNight * 60 - settled));
            int excess = booked - fromFixed;
            return excess <= 0 ? 0 : (int)Math.Ceiling(excess * GetNightFactor(GetNightDate(snoozeStart)));
        }

        /// <summary>
        /// 生成延后倒计时窗口上方那行<b>固定额度倒计时</b>，例如 <c>固定额度剩 07:23</c>；
        /// 固定额度被本次延后耗尽后显示 <c>固定额度剩 00:00</c>，此后开始动用本夜所属池。
        /// </summary>
        /// <param name="snoozeStart">本次延后的开始时刻。</param>
        /// <param name="elapsedSeconds">本次延后已经流逝的秒数。</param>
        /// <returns>单行倒计时文本。</returns>
        public static string DescribeLiveFixedQuota(DateTime snoozeStart, int elapsedSeconds)
        {
            return "固定额度剩 " + FormatClock(GetLiveFixedQuotaSeconds(snoozeStart, elapsedSeconds));
        }

        /// <summary>
        /// 生成延后倒计时窗口下方那行摘要——本夜所属池的余额与夜型，其中余额同样按
        /// "本次已流逝的秒数"实时投影，倒计时归零时与结算后的账本一致。
        /// </summary>
        /// <param name="snoozeStart">本次延后的开始时刻。</param>
        /// <param name="elapsedSeconds">本次延后已经流逝的秒数。</param>
        /// <returns>单行摘要文本，例如 <c>休息池剩 42:00 ｜ 宽松夜（系数 0.5）</c>。</returns>
        public static string DescribeLiveSummary(DateTime snoozeStart, int elapsedSeconds)
        {
            DateTime nightDate = GetNightDate(snoozeStart);
            bool relaxed = IsRelaxedNight(nightDate);
            int poolLeft = Math.Max(0,
                GetPoolRemainingSeconds(snoozeStart, relaxed) - GetLivePoolCost(snoozeStart, elapsedSeconds));
            string note = GetLivePoolCost(snoozeStart, elapsedSeconds) > 0 ? "（已开始支取）" : string.Empty;
            string tag = relaxed ? "宽松夜" : "紧张夜";
            return string.Format(CultureInfo.InvariantCulture,
                "{0}剩 {1}{2} ｜ {3}（系数 {4:0.#}）",
                GetPoolName(nightDate), FormatClock(poolLeft), note, tag, GetNightFactor(nightDate));
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
        /// 与账本一致，这里也按秒结算，不做任何分钟级截断。
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

                int elapsed = (int)Math.Floor((lastAlive - start.Value).TotalSeconds);
                if (elapsed > 0)
                {
                    int booked = RecordSnoozeSeconds(now, elapsed);
                    notes.Add($"上次延后被中断，补记 {FormatDurationText(booked)}（实际经过 {FormatDurationText(elapsed)}）");
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

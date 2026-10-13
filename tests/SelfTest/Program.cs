using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace SleepReminder.SelfTest
{
    /// <summary>
    /// SleepReminder 逻辑层的离屏回归自测。
    ///
    /// 覆盖六块纯逻辑：工作日日历（节假日 / 调休 / 周制 / 待业）、额度设置（周键与每周一次的修改锁）、
    /// 额度账本（夜归属 / 固定+附加计价 / 封顶 / 滚动 7 天 / 启动对账）、提醒外观（背景图与文字）、
    /// 预览数据，以及延后倒计时的实时额度投影。
    ///
    /// 自测会读写 <c>HKCU\Software\SleepReminder</c>，因此运行前请先备份该键，
    /// 运行结束后由外部脚本还原。进程退出码：0 = 全部通过，1 = 存在失败。
    /// </summary>
    internal static class Program
    {
        private const string RegKeyPath = @"Software\SleepReminder";

        private static int _pass;
        private static int _fail;

        private static int Main()
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch
            {
                // 控制台编码设置失败不影响测试
            }

            Console.WriteLine("=== SleepReminder 逻辑层回归自测 ===");
            Console.WriteLine($"运行时刻：{DateTime.Now:yyyy-MM-dd HH:mm:ss dddd}");
            Console.WriteLine();

            SectionA_WorkdayCalendar();
            SectionB_QuotaSettings();
            SectionC_LedgerQuota();
            SectionD_StartupReconcile();
            SectionE_Appearance();
            SectionF_PreviewData();
            SectionG_UnemployedMode();
            SectionH_LiveSnoozeQuota();

            ResetStore();

            Console.WriteLine();
            Console.WriteLine($"=== 结果：通过 {_pass}，失败 {_fail} ===");
            return _fail == 0 ? 0 : 1;
        }

        // ---------- A. 工作日日历 ----------

        /// <summary>验证节假日、调休与三种周制下的"上班 / 休息"和"宽松夜"判定。</summary>
        private static void SectionA_WorkdayCalendar()
        {
            ResetStore();
            Console.WriteLine("[A] 工作日日历：节假日 / 调休 / 周制");

            // 法定放假
            Eq("2026-10-01 国庆放假", WorkdayCalendar.IsRestDay(D("2026-10-01")), true);
            Eq("2026-10-07 假期最后一天仍放假", WorkdayCalendar.IsRestDay(D("2026-10-07")), true);
            Eq("2026-10-08 节后恢复上班", WorkdayCalendar.IsWorkday(D("2026-10-08")), true);
            Eq("2026-02-23 春节最后一天放假", WorkdayCalendar.IsRestDay(D("2026-02-23")), true);

            // 调休上班（本是周末）
            Eq("2026-10-10 调休周六上班", WorkdayCalendar.IsWorkday(D("2026-10-10")), true);
            Eq("2026-09-20 调休周日上班", WorkdayCalendar.IsWorkday(D("2026-09-20")), true);
            Eq("2026-02-28 调休周六上班", WorkdayCalendar.IsWorkday(D("2026-02-28")), true);
            Eq("2026-02-14 调休周六上班", WorkdayCalendar.IsWorkday(D("2026-02-14")), true);
            Eq("2026-01-04 调休周日上班", WorkdayCalendar.IsWorkday(D("2026-01-04")), true);
            Eq("2026-05-09 调休周六上班", WorkdayCalendar.IsWorkday(D("2026-05-09")), true);

            // 普通周末不受影响
            Eq("2026-10-17 普通周六休息", WorkdayCalendar.IsRestDay(D("2026-10-17")), true);
            Eq("2026-10-18 普通周日休息", WorkdayCalendar.IsRestDay(D("2026-10-18")), true);
            Eq("2026-10-16 普通周五上班", WorkdayCalendar.IsWorkday(D("2026-10-16")), true);

            // 宽松夜 = 次日是否上班
            Eq("10-09(五)夜 → 次日调休上班 → 紧张夜", WorkdayCalendar.IsRelaxedNight(D("2026-10-09")), false);
            Eq("09-30(三)夜 → 次日国庆放假 → 宽松夜", WorkdayCalendar.IsRelaxedNight(D("2026-09-30")), true);
            Eq("10-07(三)夜 → 次日节后上班 → 紧张夜", WorkdayCalendar.IsRelaxedNight(D("2026-10-07")), false);
            Eq("10-10(六)夜 → 次日周日休息 → 宽松夜", WorkdayCalendar.IsRelaxedNight(D("2026-10-10")), true);
            Eq("10-12(一)夜 → 次日周二上班 → 紧张夜", WorkdayCalendar.IsRelaxedNight(D("2026-10-12")), false);

            // 年份覆盖（跨年维护）
            Eq("2026 年已有放假数据", WorkdayCalendar.HasDataForYear(2026), true);
            Eq("2027 年尚无放假数据", WorkdayCalendar.HasDataForYear(2027), false);

            // 假期段识别
            WorkdayCalendar.HolidayBlock? mid = WorkdayCalendar.FindUpcomingHoliday(D("2026-09-24"), 10);
            Check("09-24 起识别到中秋节(3 天)",
                mid != null && mid.Name == "中秋节" && mid.Days.Count == 3,
                mid == null ? "未识别到" : $"{mid.Name} / {mid.Days.Count} 天");

            WorkdayCalendar.HolidayBlock? nat = WorkdayCalendar.FindUpcomingHoliday(D("2026-09-27"), 10);
            Check("09-27 起识别到国庆节(7 天)",
                nat != null && nat.Name == "国庆节" && nat.Days.Count == 7,
                nat == null ? "未识别到" : $"{nat.Name} / {nat.Days.Count} 天");

            WorkdayCalendar.HolidayBlock? none = WorkdayCalendar.FindUpcomingHoliday(D("2026-10-20"), 10);
            Check("10-20 起 10 天内无长假", none == null, none == null ? null : none.Name);

            // 单休
            QuotaSettings.Current.Pattern = WorkPattern.SingleRest;
            Eq("单休：周六上班", WorkdayCalendar.IsWorkday(D("2026-10-17")), true);
            Eq("单休：周日休息", WorkdayCalendar.IsRestDay(D("2026-10-18")), true);
            Eq("单休：周五夜(次日周六上班)紧张", WorkdayCalendar.IsRelaxedNight(D("2026-10-16")), false);
            Eq("单休：周六夜(次日周日休息)宽松", WorkdayCalendar.IsRelaxedNight(D("2026-10-17")), true);

            // 大小周：本周(10-12 起)确认为大周
            QuotaSettings.Current.Pattern = WorkPattern.BigSmallWeek;
            QuotaSettings.Current.BigWeekAnchorMonday = null;
            Eq("大小周：初始需确认", QuotaSettings.NeedsBigWeekConfirmation(), true);
            QuotaSettings.ConfirmBigWeek(D("2026-10-12"), isBigWeek: true);
            Eq("大小周：确认后不再询问", QuotaSettings.NeedsBigWeekConfirmation(), false);
            Eq("大小周·大周：10-17 周六休息", WorkdayCalendar.IsRestDay(D("2026-10-17")), true);
            Eq("大小周·小周：10-24 周六上班", WorkdayCalendar.IsWorkday(D("2026-10-24")), true);
            Eq("大小周：10-18 周日始终休息", WorkdayCalendar.IsRestDay(D("2026-10-18")), true);
            Eq("大小周：10-25 周日始终休息", WorkdayCalendar.IsRestDay(D("2026-10-25")), true);
            Eq("大小周：大周周五夜宽松", WorkdayCalendar.IsRelaxedNight(D("2026-10-16")), true);
            Eq("大小周：小周周五夜紧张", WorkdayCalendar.IsRelaxedNight(D("2026-10-23")), false);

            Console.WriteLine();
        }

        // ---------- B. 额度设置 ----------

        /// <summary>验证自然周键的计算与"每周只能改一次"的锁。</summary>
        private static void SectionB_QuotaSettings()
        {
            ResetStore();
            Console.WriteLine("[B] 额度设置：自然周键 / 每周一次的修改锁");

            Eq("周一：GetMonday(10-12)=10-12", QuotaSettings.GetMonday(D("2026-10-12")), D("2026-10-12"));
            Eq("周日：GetMonday(10-18)=10-12", QuotaSettings.GetMonday(D("2026-10-18")), D("2026-10-12"));
            Eq("周内两天周键一致",
                QuotaSettings.GetWeekKey(D("2026-10-12")) == QuotaSettings.GetWeekKey(D("2026-10-18")), true);
            Eq("跨周周键不同",
                QuotaSettings.GetWeekKey(D("2026-10-11")) == QuotaSettings.GetWeekKey(D("2026-10-12")), false);

            DateTime monday = D("2026-10-12");
            Eq("默认参数：固定额度 10", QuotaSettings.Current.FixedMinutesPerNight, 10);
            Eq("默认参数：附加池 60", QuotaSettings.Current.WeeklyPoolMinutes, 60);
            Eq("首次可改设置", QuotaSettings.CanModify(monday), true);

            QuotaSettings.Current.FixedMinutesPerNight = 12;
            Eq("周内第一次保存成功", QuotaSettings.Current.TrySave(monday, out _), true);
            Eq("保存后新值生效", QuotaSettings.Current.FixedMinutesPerNight, 12);
            Eq("同一周内不可再改", QuotaSettings.CanModify(monday), false);
            Eq("同周第二次保存被拒", QuotaSettings.Current.TrySave(monday, out _), false);
            Eq("下一自然周可再改", QuotaSettings.CanModify(D("2026-10-19")), true);
            Eq("修改次数累计为 1", QuotaSettings.GetChangeCount(), 1);

            Console.WriteLine();
        }

        // ---------- C. 额度账本 ----------

        /// <summary>验证夜归属、固定+附加计价、单夜封顶与滚动 7 天窗口。</summary>
        private static void SectionC_LedgerQuota()
        {
            ResetStore();
            Console.WriteLine("[C] 额度账本：夜归属 / 固定+附加计价 / 封顶 / 滚动 7 天");

            // 夜归属：跨零点算同一夜
            Eq("23:30 归属 10-12", SleepLedger.GetNightDate(new DateTime(2026, 10, 12, 23, 30, 0)), D("2026-10-12"));
            Eq("次日 00:30 仍归属 10-12",
                SleepLedger.GetNightDate(new DateTime(2026, 10, 13, 0, 30, 0)), D("2026-10-12"));

            // 夜系数
            Eq("宽松夜(10-10)系数 0.5", SleepLedger.GetNightFactor(D("2026-10-10")), 0.5);
            Eq("紧张夜(10-09)系数 1.0", SleepLedger.GetNightFactor(D("2026-10-09")), 1.0);
            Eq("宽松夜封顶 60", SleepLedger.GetNightCap(D("2026-10-10")), 60);
            Eq("紧张夜封顶 30", SleepLedger.GetNightCap(D("2026-10-09")), 30);

            // 宽松夜计价：10 分钟全走固定
            DateTime relaxed = new DateTime(2026, 10, 10, 23, 30, 0);
            Eq("初始可用额度 60", SleepLedger.GetAvailableMinutes(relaxed), 60);
            Eq("延后 10 分钟全部入账", SleepLedger.RecordSnooze(relaxed, 10), 10);
            Eq("今晚累计 10", SleepLedger.GetTonightSnoozedMinutes(relaxed), 10);
            Eq("池子未消耗", SleepLedger.GetPoolUsed(relaxed), 0);

            // 再延后 20：固定已用完，20 × 0.5 = 扣 10
            Eq("再延后 20 分钟入账", SleepLedger.RecordSnooze(relaxed, 20), 20);
            Eq("今晚累计 30", SleepLedger.GetTonightSnoozedMinutes(relaxed), 30);
            Eq("池子扣 10（系数 0.5）", SleepLedger.GetPoolUsed(relaxed), 10);
            Eq("池子剩余 50", SleepLedger.GetPoolRemaining(relaxed), 50);
            Eq("剩余可用 30", SleepLedger.GetAvailableMinutes(relaxed), 30);

            // 单夜封顶
            Eq("请求 100 分钟被截到 30", SleepLedger.RecordSnooze(relaxed, 100), 30);
            Eq("今晚累计到达封顶 60", SleepLedger.GetTonightSnoozedMinutes(relaxed), 60);
            Eq("池子累计扣 25", SleepLedger.GetPoolUsed(relaxed), 25);
            Eq("封顶后可用为 0", SleepLedger.GetAvailableMinutes(relaxed), 0);
            Eq("封顶后不再入账", SleepLedger.RecordSnooze(relaxed, 10), 0);

            // 紧张夜计价：系数 1.0
            DateTime tense = new DateTime(2026, 10, 12, 23, 30, 0);
            Eq("紧张夜延后 20 分钟入账", SleepLedger.RecordSnooze(tense, 20), 20);
            Eq("紧张夜池子扣 10（系数 1.0）", SleepLedger.GetPoolUsed(tense), 35);
            Eq("滚动窗口剩余 25", SleepLedger.GetPoolRemaining(tense), 25);

            // 滚动 7 天：8 天后的窗口不再包含 10-10
            DateTime later = new DateTime(2026, 10, 18, 23, 30, 0);
            Eq("滚动窗口 7 天后甩掉 10-10", SleepLedger.GetPoolUsed(later), 10);

            string summary = SleepLedger.DescribeQuota(tense);
            Check("额度摘要含固定与附加字段",
                summary.Contains("固定额度剩") && summary.Contains("附加池剩"), summary);

            Console.WriteLine();
        }

        // ---------- D. 启动对账 ----------

        /// <summary>验证"延后中被打断"的补记，以及时钟回拨检测。</summary>
        private static void SectionD_StartupReconcile()
        {
            ResetStore();
            Console.WriteLine("[D] 启动对账：延后中断补记 / 时钟回拨");

            // 模拟：23:30 开始延后，23:45 写完最后一次心跳后进程被强杀
            DateTime start = DateTime.Now.Date.AddHours(23).AddMinutes(30);
            SleepLedger.BeginSnooze(start);
            Eq("已标记延后中", SleepLedger.GetActiveSnoozeStart().HasValue, true);

            SleepLedger.WriteHeartbeat(start.AddMinutes(15));
            string? note = SleepLedger.ReconcileOnStartup(start.AddMinutes(16));

            Check("对账给出补记提示", note != null && note.Contains("补记"), note ?? "(null)");
            Eq("补记后今晚已入账 15 分钟",
                SleepLedger.GetTonightSnoozedMinutes(start.AddMinutes(16)), 15);
            Eq("对账后清除了延后中标记", SleepLedger.GetActiveSnoozeStart().HasValue, false);

            // 时钟回拨：心跳在未来 → 判定回拨并记违约
            int before = SleepLedger.GetViolationCount();
            SleepLedger.WriteHeartbeat(DateTime.Now.AddHours(3));
            SleepLedger.ReconcileOnStartup(DateTime.Now);
            Eq("检测到时钟回拨并记一次违约", SleepLedger.GetViolationCount(), before + 1);

            Console.WriteLine();
        }

        // ---------- E. 提醒外观 ----------

        /// <summary>验证外观设置的默认值、保存/装载往返、归一化与背景图可用性判定。</summary>
        private static void SectionE_Appearance()
        {
            ResetStore();
            Console.WriteLine("[E] 提醒外观：默认值 / 保存装载 / 归一化");

            AppearanceSettings def = AppearanceSettings.Current;
            Eq("默认大标题", def.Title, "快去睡觉");
            Eq("默认小标题", def.TipsHeader, "晚睡的危害");
            Eq("默认条目 5 条", def.Tips.Count, 5);
            Eq("默认无可用背景图", def.HasUsableImage, false);

            // 保存 / 装载往返
            AppearanceSettings a = AppearanceSettings.Current;
            a.Title = "别熬了";
            a.TipsHeader = "熬夜的代价";
            a.Tips = new List<string> { "第一条", "第二条" };
            a.BackgroundImagePath = @"C:\definitely\missing.png";
            a.Save();

            AppearanceSettings.Reload();
            AppearanceSettings loaded = AppearanceSettings.Current;
            Eq("往返：大标题", loaded.Title, "别熬了");
            Eq("往返：小标题", loaded.TipsHeader, "熬夜的代价");
            Eq("往返：条目数", loaded.Tips.Count, 2);
            Eq("往返：图片路径", loaded.BackgroundImagePath, @"C:\definitely\missing.png");
            Eq("图片不存在 → 判定不可用", loaded.HasUsableImage, false);

            // 归一化：空白标题回落默认，空条目被剔除
            AppearanceSettings n = AppearanceSettings.Current;
            n.Title = "   ";
            n.TipsHeader = "  ";
            n.Tips = new List<string> { "  有内容  ", "", "   " };
            n.Save();

            AppearanceSettings.Reload();
            AppearanceSettings norm = AppearanceSettings.Current;
            Eq("空白大标题回落默认", norm.Title, "快去睡觉");
            Eq("空白小标题允许留空（表示隐藏）", norm.TipsHeader, "");
            Eq("空条目被剔除并去空白", norm.Tips.Count == 1 && norm.Tips[0] == "有内容", true);

            // 真实存在的图片 → 判定可用
            string tmp = Path.Combine(Path.GetTempPath(), "sr_selftest_bg.txt");
            File.WriteAllText(tmp, "x");
            AppearanceSettings u = AppearanceSettings.Current;
            u.BackgroundImagePath = tmp;
            u.Save();
            AppearanceSettings.Reload();
            Eq("文件存在 → 背景图可用", AppearanceSettings.Current.HasUsableImage, true);
            try { File.Delete(tmp); } catch { /* 删不掉不影响测试 */ }

            Console.WriteLine();
        }

        // ---------- F. 预览数据 ----------

        /// <summary>验证"预览"窗口用到的两块数据：作息判定依据与近 7 夜摘要。</summary>
        private static void SectionF_PreviewData()
        {
            ResetStore();
            Console.WriteLine("[F] 预览数据：作息判定依据 / 近期作息 / 近 7 夜摘要");

            // 判定依据：能解释"这天凭什么休 / 上班"
            WorkdayCalendar.DayInfo nat = WorkdayCalendar.GetDayInfo(D("2026-10-01"));
            Eq("10-01 休息", nat.IsWorkday, false);
            Eq("10-01 依据=法定放假", nat.Reason, "法定放假");

            WorkdayCalendar.DayInfo makeup = WorkdayCalendar.GetDayInfo(D("2026-10-10"));
            Eq("10-10 上班", makeup.IsWorkday, true);
            Eq("10-10 依据=调休上班", makeup.Reason, "调休 / 单位安排上班");

            WorkdayCalendar.DayInfo plain = WorkdayCalendar.GetDayInfo(D("2026-10-14"));
            Eq("10-14 上班", plain.IsWorkday, true);
            Eq("10-14 依据=按周制作息", plain.Reason, "按周制作息");

            // 近期作息：今天排最前，天数正确
            List<WorkdayCalendar.DayInfo> upcoming = WorkdayCalendar.GetUpcomingDays(D("2026-10-12"), 7);
            Eq("近期作息返回 7 天", upcoming.Count, 7);
            Eq("近期作息第一天是今天", upcoming[0].Date, D("2026-10-12"));
            Eq("近期作息最后一天是 10-18", upcoming[6].Date, D("2026-10-18"));
            Eq("空请求返回空列表", WorkdayCalendar.GetUpcomingDays(D("2026-10-12"), 0).Count, 0);

            // 近 7 夜摘要：日期升序，且带上“是否宽松夜”
            SleepLedger.RecordSnooze(new DateTime(2026, 10, 10, 23, 30, 0), 10);
            SleepLedger.RecordSnooze(new DateTime(2026, 10, 12, 23, 30, 0), 12);

            List<SleepLedger.NightSummary> nights =
                SleepLedger.GetRecentNights(new DateTime(2026, 10, 12, 23, 40, 0), 7);
            Eq("近 7 夜返回 2 笔", nights.Count, 2);
            Eq("第一笔是 10-10", nights[0].NightDate, D("2026-10-10"));
            Eq("10-10 判为宽松夜", nights[0].IsRelaxed, true);
            Eq("10-10 延后 10 分钟", nights[0].TotalMinutes, 10);
            Eq("第二笔是 10-12", nights[1].NightDate, D("2026-10-12"));
            Eq("10-12 判为紧张夜", nights[1].IsRelaxed, false);
            Eq("10-12 延后 12 分钟", nights[1].TotalMinutes, 12);
            Eq("窗口外不返回", SleepLedger.GetRecentNights(new DateTime(2026, 10, 30, 23, 30, 0), 7).Count, 0);

            Console.WriteLine();
        }

        // ---------- G. 待业模式 ----------

        /// <summary>验证"待业模式"：每天都是放假，压过调休 / 放假表，因此恒为宽松夜。</summary>
        private static void SectionG_UnemployedMode()
        {
            ResetStore();
            Console.WriteLine("[G] 待业模式：每天都是放假 / 恒为宽松夜");

            QuotaSettings.Current.Pattern = WorkPattern.Unemployed;

            Eq("待业：普通工作日 10-14 也休息", WorkdayCalendar.IsRestDay(D("2026-10-14")), true);
            Eq("待业：调休上班日 10-10 也休息", WorkdayCalendar.IsRestDay(D("2026-10-10")), true);
            Eq("待业：法定假期 10-01 自然休息", WorkdayCalendar.IsRestDay(D("2026-10-01")), true);
            Eq("待业：任何一天都不上班", WorkdayCalendar.IsWorkday(D("2026-10-14")), false);

            Eq("待业：工作日次日 → 宽松夜", WorkdayCalendar.IsRelaxedNight(D("2026-10-14")), true);
            Eq("待业：夜间系数 0.5", SleepLedger.GetNightFactor(D("2026-10-14")), 0.5);
            Eq("待业：单夜封顶按宽松夜 60", SleepLedger.GetNightCap(D("2026-10-14")), 60);

            WorkdayCalendar.DayInfo info = WorkdayCalendar.GetDayInfo(D("2026-10-14"));
            Eq("待业：休息", info.IsWorkday, false);
            Eq("待业：判定依据=待业模式", info.Reason, "待业模式（每天放假）");

            List<WorkdayCalendar.DayInfo> week = WorkdayCalendar.GetUpcomingDays(D("2026-10-12"), 7);
            int workDays = 0;
            foreach (WorkdayCalendar.DayInfo d in week)
            {
                if (d.IsWorkday)
                    workDays++;
            }
            Eq("待业：未来 7 天里上班 0 天", workDays, 0);

            string described = QuotaSettings.Current.Describe();
            Check("待业：设置摘要标明待业", described.Contains("待业"), described);

            // 切回双休：待业只压判定、不改数据，节假日与调休应立即恢复生效
            QuotaSettings.Current.Pattern = WorkPattern.DoubleRest;
            Eq("切回双休：调休日 10-10 恢复上班", WorkdayCalendar.IsWorkday(D("2026-10-10")), true);
            Eq("切回双休：工作日 10-14 恢复上班", WorkdayCalendar.IsWorkday(D("2026-10-14")), true);
            Eq("切回双休：10-14 依据回到周制",
                WorkdayCalendar.GetDayInfo(D("2026-10-14")).Reason, "按周制作息");

            Console.WriteLine();
        }

        // ---------- H. 延后倒计时的实时额度 ----------

        /// <summary>
        /// 验证倒计时窗口用到的实时投影：固定额度随已流逝秒数递减、附加池按已流逝整分钟支取，
        /// 且这份投影<b>只读不写</b>；倒计时归零时的投影值应与随后结算出的账本一致。
        /// </summary>
        private static void SectionH_LiveSnoozeQuota()
        {
            ResetStore();
            Console.WriteLine("[H] 延后倒计时：固定额度实时递减（纯投影，不写账）");

            // 10-10 是周六、次日休息 → 宽松夜（系数 0.5、封顶 60）
            DateTime relaxed = new DateTime(2026, 10, 10, 23, 30, 0);
            Eq("起始：固定额度剩 10:00",
                SleepLedger.DescribeLiveFixedQuota(relaxed, 0), "固定额度剩 10:00");
            Eq("起始：附加池 60、标为宽松夜",
                SleepLedger.DescribeLiveSummary(relaxed, 0), "附加池剩 60 分钟 ｜ 宽松夜（系数 0.5）");

            Eq("流逝 1 分 30 秒：固定额度剩 08:30",
                SleepLedger.DescribeLiveFixedQuota(relaxed, 90), "固定额度剩 08:30");
            Eq("流逝 2 分钟：固定额度剩 08:00",
                SleepLedger.DescribeLiveFixedQuota(relaxed, 120), "固定额度剩 08:00");
            Eq("流逝 10 分钟：固定额度归零",
                SleepLedger.DescribeLiveFixedQuota(relaxed, 600), "固定额度剩 00:00");
            Eq("流逝 11 分钟：超出 1 分钟 × 0.5 向上取整 → 池剩 59",
                SleepLedger.DescribeLiveSummary(relaxed, 660), "附加池剩 59 分钟（已开始支取） ｜ 宽松夜（系数 0.5）");

            Eq("实时投影不写本夜账", SleepLedger.GetTonightSnoozedMinutes(relaxed), 0);
            Eq("实时投影不写心跳", SleepLedger.GetLastHeartbeat().HasValue, false);

            // 紧张夜：超出部分不打折（系数 1）
            DateTime tense = new DateTime(2026, 10, 12, 23, 30, 0);
            string tenseLine = SleepLedger.DescribeLiveSummary(tense, 660);
            Check("紧张夜：超出 1 分钟全额扣池（池剩 59）", tenseLine.Contains("附加池剩 59"), tenseLine);
            Check("紧张夜：标注为紧张夜", tenseLine.Contains("紧张夜"), tenseLine);

            // 本夜已被前一次延后吃掉一部分固定额度时，本次倒计时从那部分之后起算
            SleepLedger.RecordSnooze(tense, 4);
            Eq("本夜已用 4 分钟：实时剩余从 06:00 起算",
                SleepLedger.DescribeLiveFixedQuota(tense, 0), "固定额度剩 06:00");
            Eq("本夜已用 4 分钟：再流逝 60 秒 → 05:00",
                SleepLedger.DescribeLiveFixedQuota(tense, 60), "固定额度剩 05:00");
            Eq("本夜已用 4 分钟：池仍未被动用", SleepLedger.GetPoolUsed(tense), 0);

            // 归零时的投影 = 结算后的账本（10-16 周五夜、次日周六休息 → 宽松夜）
            DateTime settleNight = new DateTime(2026, 10, 16, 23, 30, 0);
            string projected = SleepLedger.DescribeLiveSummary(settleNight, 25 * 60);
            Check("归零时的投影：池剩 52（超出 15 × 0.5 → 8）",
                projected.Contains("附加池剩 52"), projected);

            SleepLedger.RecordSnooze(settleNight, 25);
            Eq("结算后本夜延后 25 分钟", SleepLedger.GetTonightSnoozedMinutes(settleNight), 25);
            Eq("结算后池剩与倒计时归零时的投影一致", SleepLedger.GetPoolRemaining(settleNight), 52);

            Console.WriteLine();
        }

        // ---------- 工具 ----------

        /// <summary>把 yyyy-MM-dd 解析成日期。</summary>
        private static DateTime D(string text)
        {
            return DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>断言布尔条件。</summary>
        private static void Check(string name, bool ok, string? detail)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine($"  PASS  {name}");
            }
            else
            {
                _fail++;
                Console.WriteLine($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : "  →  " + detail)}");
            }
        }

        /// <summary>断言两值相等。</summary>
        private static void Eq<T>(string name, T actual, T expected)
        {
            bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
            Check(name, ok, ok ? null : $"期望 {expected}，实际 {actual}");
        }

        /// <summary>清空本程序使用的注册表键，让每段测试都从干净状态开始。</summary>
        private static void ResetStore()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegKeyPath, false);
            }
            catch
            {
                // 键不存在或权限不足都无所谓
            }

            QuotaSettings.Reload();
            WorkdayCalendar.Reload();
        }
    }
}

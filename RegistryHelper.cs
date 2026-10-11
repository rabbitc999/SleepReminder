using Microsoft.Win32;

namespace SleepReminder
{
    public static class RegistryHelper
    {
        private const string RegKeyPath = @"Software\SleepReminder";

        /// <summary>
        /// 保存提醒时间区间到注册表
        /// </summary>
        public static void SaveReminderTime(int startHour, int startMinute, int endHour, int endMinute)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key.SetValue("StartHour", startHour);
                key.SetValue("StartMinute", startMinute);
                key.SetValue("EndHour", endHour);
                key.SetValue("EndMinute", endMinute);
            }
        }

        /// <summary>
        /// 从注册表加载提醒时间区间，若不存在则返回默认值 (23:00–23:59)
        /// </summary>
        public static (int startHour, int startMinute, int endHour, int endMinute) LoadReminderTime()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                if (key == null)
                    return (23, 0, 23, 59); // 默认区间

                int sh = (int)key.GetValue("StartHour", 23);
                int sm = (int)key.GetValue("StartMinute", 0);
                int eh = (int)key.GetValue("EndHour", 23);
                int em = (int)key.GetValue("EndMinute", 59);
                return (sh, sm, eh, em);
            }
        }

        /// <summary>
        /// 保存延后分钟数到注册表
        /// </summary>
        public static void SaveSnoozeMinutes(int minutes)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key.SetValue("SnoozeMinutes", minutes);
            }
        }

        /// <summary>
        /// 从注册表加载延后分钟数，若不存在则返回默认值 10
        /// </summary>
        public static int LoadSnoozeMinutes()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                if (key == null)
                    return 10;
                object value = key.GetValue("SnoozeMinutes", 10);
                if (value is int intVal)
                    return intVal;
                // 若存储为 string 则尝试 convert
                if (value is string strVal && int.TryParse(strVal, out int parsed))
                    return parsed;
                return 10;
            }
        }

        /// <summary>
        /// 将“已延后的日期”写入注册表，格式 yyyy-MM-dd。
        /// 用于跨重启 / 注销记忆“今晚是否已经延后过”。
        /// </summary>
        public static void SaveSnoozeDate(DateTime date)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key.SetValue("SnoozeDate", date.ToString("yyyy-MM-dd"));
            }
        }

        /// <summary>
        /// 从注册表读取已延后的日期；若不存在或格式非法则返回 null。
        /// </summary>
        public static DateTime? LoadSnoozeDate()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                if (key == null)
                    return null;
                object value = key.GetValue("SnoozeDate");
                if (value is string strVal &&
                    DateTime.TryParseExact(strVal, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out DateTime d))
                {
                    return d;
                }
                return null;
            }
        }

        /// <summary>
        /// 判断今天是否已经延后过。按自然日比较，写入注册表后
        /// 即使重启电脑、注销用户也依然有效，直到日历日变化。
        /// </summary>
        public static bool HasSnoozedToday()
        {
            DateTime? d = LoadSnoozeDate();
            return d.HasValue && d.Value.Date == DateTime.Today;
        }

        /// <summary>
        /// 标记“今天已延后”——把当前自然日写入注册表。
        /// </summary>
        public static void MarkSnoozedToday()
        {
            SaveSnoozeDate(DateTime.Today);
        }

        /// <summary>
        /// 清除延后记录（例如想手动重置时调用）。
        /// </summary>
        public static void ClearSnoozeDate()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath, true))
            {
                key?.DeleteValue("SnoozeDate", false);
            }
        }
    }
}
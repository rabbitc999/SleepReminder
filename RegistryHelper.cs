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
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SleepReminder
{
    /// <summary>
    /// 提醒页外观设置：霸屏提醒页的<b>背景图片</b>与<b>文字内容</b>均可自定义。
    ///
    /// 可自定义项：
    /// <list type="bullet">
    ///   <item><description><see cref="BackgroundImagePath"/>——背景图片的绝对路径，
    ///     留空则退回纯色背景；</description></item>
    ///   <item><description><see cref="Title"/>——最醒目的大标题（默认"快去睡觉"）；</description></item>
    ///   <item><description><see cref="TipsHeader"/>——条目区的小标题（默认"晚睡的危害"）；</description></item>
    ///   <item><description><see cref="Tips"/>——条目正文，一行一条。</description></item>
    /// </list>
    ///
    /// 与额度设置不同，外观不影响惩罚力度，因此<b>不设</b>"每周一次"的修改锁。
    /// 数据与账本同放在 <c>HKCU\Software\SleepReminder</c> 下。
    /// </summary>
    public sealed class AppearanceSettings
    {
        private const string RegKeyPath = @"Software\SleepReminder";
        private const string ValueImage = "WarningImagePath";
        private const string ValueTitle = "WarningTitle";
        private const string ValueTipsHeader = "WarningTipsHeader";
        private const string ValueTips = "WarningTips";

        /// <summary>默认大标题。</summary>
        public const string DefaultTitle = "快去睡觉";

        /// <summary>默认条目区小标题。</summary>
        public const string DefaultTipsHeader = "晚睡的危害";

        /// <summary>默认条目正文。</summary>
        public static readonly string[] DefaultTips =
        {
            "免疫力下降，容易感冒",
            "增加心脏病、糖尿病风险",
            "记忆力和注意力减退",
            "皮肤老化，出现黑眼圈",
            "情绪波动，易怒焦虑"
        };

        private static AppearanceSettings? _current;
        private static readonly object Gate = new object();

        /// <summary>背景图片路径；空字符串表示不使用背景图。</summary>
        public string BackgroundImagePath { get; set; } = string.Empty;

        /// <summary>最醒目的大标题。</summary>
        public string Title { get; set; } = DefaultTitle;

        /// <summary>条目区的小标题。</summary>
        public string TipsHeader { get; set; } = DefaultTipsHeader;

        /// <summary>条目正文，一行一条。</summary>
        public List<string> Tips { get; set; } = DefaultTips.ToList();

        /// <summary>
        /// 当前生效的外观设置（首次访问时从注册表装载并缓存）。
        /// 保存后调用 <see cref="Reload"/> 使其生效。
        /// </summary>
        public static AppearanceSettings Current
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

        /// <summary>背景图片是否可用——路径非空且文件确实存在。</summary>
        public bool HasUsableImage =>
            !string.IsNullOrWhiteSpace(BackgroundImagePath) && File.Exists(BackgroundImagePath);

        /// <summary>恢复全部默认外观（是否落盘由调用方决定）。</summary>
        public void ResetToDefault()
        {
            BackgroundImagePath = string.Empty;
            Title = DefaultTitle;
            TipsHeader = DefaultTipsHeader;
            Tips = DefaultTips.ToList();
        }

        /// <summary>收敛非法值并写回注册表。</summary>
        public void Save()
        {
            Normalize();
            Persist();
            Reload();
        }

        /// <summary>把各字段收敛到合理状态。</summary>
        public void Normalize()
        {
            BackgroundImagePath = (BackgroundImagePath ?? string.Empty).Trim();
            if (Title == null || Title.Trim().Length == 0)
                Title = DefaultTitle;
            else
                Title = Title.Trim();

            if (TipsHeader == null)
                TipsHeader = DefaultTipsHeader;
            else
                TipsHeader = TipsHeader.Trim();

            Tips ??= new List<string>();
            Tips = Tips
                .Select(t => (t ?? string.Empty).Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }

        /// <summary>从注册表装载外观设置；缺失项回退到默认值。</summary>
        /// <returns>装载到的外观设置实例。</returns>
        private static AppearanceSettings Load()
        {
            var a = new AppearanceSettings();

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                if (key == null)
                    return a;

                a.BackgroundImagePath = key.GetValue(ValueImage) as string ?? string.Empty;

                if (key.GetValue(ValueTitle) is string title && title.Trim().Length > 0)
                    a.Title = title;

                if (key.GetValue(ValueTipsHeader) is string header)
                    a.TipsHeader = header;

                if (key.GetValue(ValueTips) is string tips && tips.Length > 0)
                    a.Tips = tips
                        .Replace("\r\n", "\n")
                        .Split('\n')
                        .Select(t => t.Trim())
                        .Where(t => t.Length > 0)
                        .ToList();
            }

            a.Normalize();
            return a;
        }

        /// <summary>把当前外观设置写入注册表。</summary>
        private void Persist()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                if (key == null)
                    return;

                key.SetValue(ValueImage, BackgroundImagePath ?? string.Empty);
                key.SetValue(ValueTitle, Title ?? DefaultTitle);
                key.SetValue(ValueTipsHeader, TipsHeader ?? DefaultTipsHeader);
                key.SetValue(ValueTips, string.Join("\n", Tips ?? new List<string>()));
            }
        }
    }
}

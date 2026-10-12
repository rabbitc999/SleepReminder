using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SleepReminder
{
    /// <summary>
    /// 提醒外观设置窗口：挑选背景图片、编辑大标题 / 小标题 / 条目正文，并实时预览。
    ///
    /// 保存后立即写盘（见 <see cref="AppearanceSettings.Save"/>），调用方刷新一次
    /// <c>MainWindow.ApplyAppearance</c> 即可生效，无需重启。
    /// </summary>
    public partial class AppearanceSettingsWindow : Window
    {
        /// <summary>构造窗口并装载当前外观。</summary>
        public AppearanceSettingsWindow()
        {
            InitializeComponent();
            LoadIntoForm();
        }

        /// <summary>把当前外观设置填进表单，并刷新预览。</summary>
        private void LoadIntoForm()
        {
            AppearanceSettings a = AppearanceSettings.Current;

            txtImagePath.Text = a.BackgroundImagePath;
            txtTitle.Text = a.Title;
            txtTipsHeader.Text = a.TipsHeader;
            txtTips.Text = string.Join(Environment.NewLine, a.Tips);

            UpdatePreview();
        }

        /// <summary>"浏览…"：用系统文件对话框挑一张图片。</summary>
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择提醒背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
                CheckFileExists = true
            };

            if (dlg.ShowDialog(this) == true)
            {
                txtImagePath.Text = dlg.FileName;
                UpdatePreview();
            }
        }

        /// <summary>"清除"：回到纯色背景。</summary>
        private void ClearImage_Click(object sender, RoutedEventArgs e)
        {
            txtImagePath.Text = string.Empty;
            UpdatePreview();
        }

        /// <summary>路径被手动编辑时同步刷新预览。</summary>
        private void ImagePath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdatePreview();
        }

        /// <summary>"恢复默认"：把默认外观填进表单（尚未落盘，仍需点确定）。</summary>
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var def = new AppearanceSettings();
            def.ResetToDefault();

            txtImagePath.Text = def.BackgroundImagePath;
            txtTitle.Text = def.Title;
            txtTipsHeader.Text = def.TipsHeader;
            txtTips.Text = string.Join(Environment.NewLine, def.Tips);

            UpdatePreview();
        }

        /// <summary>按当前表单内容刷新缩略图预览。</summary>
        private void UpdatePreview()
        {
            string path = txtImagePath.Text.Trim();

            if (path.Length == 0)
            {
                imgPreview.Source = null;
                tbPreviewHint.Text = "（未设置背景图，将使用纯色背景）";
                return;
            }

            if (!File.Exists(path))
            {
                imgPreview.Source = null;
                tbPreviewHint.Text = "找不到该文件，将使用纯色背景";
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();

                imgPreview.Source = bitmap;
                tbPreviewHint.Text = string.Empty;
            }
            catch
            {
                imgPreview.Source = null;
                tbPreviewHint.Text = "无法加载该图片（格式不支持或文件损坏）";
            }
        }

        /// <summary>校验并保存外观设置。</summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string path = txtImagePath.Text.Trim();

            if (path.Length > 0 && !File.Exists(path))
            {
                bool keep = CustomMessageBox.ShowConfirm(
                    $"背景图片不存在：\n{path}\n\n是否仍然保存（届时会退回纯色背景）？",
                    "图片不存在", this);
                if (!keep)
                    return;
            }

            AppearanceSettings a = AppearanceSettings.Current;
            a.BackgroundImagePath = path;
            a.Title = txtTitle.Text;
            a.TipsHeader = txtTipsHeader.Text;
            a.Tips = txtTips.Text
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            a.Save();

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

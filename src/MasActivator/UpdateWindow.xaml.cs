using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace MasActivator
{
    /// <summary>
    /// 更新详情窗口：展示版本号与更新说明，让用户选择忽略或立即更新。
    /// </summary>
    public partial class UpdateWindow : Window
    {
        /// <summary>
        /// 记录用户选择忽略的版本号，位于临时目录，重启后仍生效。
        /// </summary>
        private static readonly string IgnoreFilePath =
            Path.Combine(Path.GetTempPath(), "MasActivator", "ignored_version.txt");

        /// <summary>
        /// 用户是否选择立即更新。
        /// </summary>
        internal bool IsUpdateConfirmed { get; private set; }

        /// <summary>
        /// 检测到的更新信息。
        /// </summary>
        private readonly UpdateService.UpdateInfo _update;

        /// <summary>
        /// 初始化更新窗口并填充版本与更新说明。
        /// </summary>
        /// <param name="currentVersion">当前程序版本号。</param>
        /// <param name="update">检测到的更新信息。</param>
        internal UpdateWindow(string currentVersion, UpdateService.UpdateInfo update)
        {
            InitializeComponent();
            _update = update;
            TxtCurrentVersion.Text = "v" + currentVersion;
            TxtNewVersion.Text = "v" + update.Version;
            TxtReleaseNotes.Text = update.ReleaseNotes;
            TxtSource.Text = "更新来源：" + update.Source;
        }

        /// <summary>
        /// 读取已被用户忽略的版本号，空文件时返回 null。
        /// </summary>
        /// <returns>忽略的版本字符串，未忽略时返回 null。</returns>
        internal static string ReadIgnoredVersion()
        {
            try
            {
                return File.Exists(IgnoreFilePath) ? File.ReadAllText(IgnoreFilePath).Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 标题栏鼠标按下事件，允许拖动窗口。
        /// </summary>
        /// <param name="sender">触发事件的标题栏边框。</param>
        /// <param name="e">鼠标事件参数。</param>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        /// <summary>
        /// 关闭按钮点击，按忽略处理并退出窗口。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            SaveIgnoredVersion();
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 忽略按钮点击，记录当前版本后关闭窗口，后续启动不再提示。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private void BtnIgnore_Click(object sender, RoutedEventArgs e)
        {
            SaveIgnoredVersion();
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 立即更新按钮点击，标记确认后关闭窗口。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            IsUpdateConfirmed = true;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// 将当前版本写入忽略记录，确保目录存在。
        /// </summary>
        private void SaveIgnoredVersion()
        {
            try
            {
                string directory = Path.GetDirectoryName(IgnoreFilePath);
                Directory.CreateDirectory(directory);
                File.WriteAllText(IgnoreFilePath, _update.Version.ToString());
            }
            catch (Exception)
            {
                // 忽略写入失败，仅影响本次记忆，不影响主流程。
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace MasActivator
{
    /// <summary>
    /// 关于窗口：展示作者公众号二维码、官网与项目开源地址。
    /// </summary>
    public partial class AboutWindow : Window
    {
        /// <summary>
        /// 初始化关于窗口：填入当前版本号并从内嵌资源加载公众号二维码。
        /// </summary>
        public AboutWindow()
        {
            InitializeComponent();
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            int plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
            {
                version = version.Substring(0, plusIndex);
            }

            TxtAboutVersion.Text = "v" + version + " · © 2026 源动力";
            LoadBrandLogo();
            LoadPromoImage();
        }

        /// <summary>
        /// 从程序集内嵌资源读取品牌 Logo 并绑定到关于窗口头像，读取失败时保持空白不影响其他信息。
        /// </summary>
        private void LoadBrandLogo()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("MasActivator.Resources.brand_logo.png"))
                {
                    if (stream == null)
                    {
                        return;
                    }

                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    ImgBrandLogo.Source = bitmap;
                }
            }
            catch (Exception)
            {
                // Logo 加载失败时静默跳过，关于窗口其余信息仍可正常查看。
            }
        }

        /// <summary>
        /// 从程序集内嵌资源读取公众号推广图片并原样绑定到界面，读取失败时保持空白不影响其他信息。
        /// </summary>
        private void LoadPromoImage()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("MasActivator.Resources.wechat_promo.png"))
                {
                    if (stream == null)
                    {
                        return;
                    }

                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    ImgPromo.Source = bitmap;
                }
            }
            catch (Exception)
            {
                // 推广图加载失败时静默跳过，关于窗口其余信息仍可正常查看。
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
        /// 关闭按钮点击事件。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 超链接点击后使用系统默认浏览器打开目标地址。
        /// </summary>
        /// <param name="sender">触发事件的超链接。</param>
        /// <param name="e">包含目标地址的事件参数。</param>
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // 浏览器不可用时静默失败，不打断关于窗口。
            }
        }
    }
}

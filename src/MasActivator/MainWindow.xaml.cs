using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace MasActivator
{
    /// <summary>
    /// 主窗口：展示系统信息、激活类型，并调用内置的 MAS 脚本完成激活。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 初始化窗口，并立即读取当前系统的版本与激活状态。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            LoadSystemInfo();
            RefreshLicenseStatus();
            UpdateTypeDescription();
            SetVersionText();
            Loaded += MainWindow_Loaded;
        }

        /// <summary>
        /// 缓存检测到的更新信息，供按钮点击时执行升级。
        /// </summary>
        private UpdateService.UpdateInfo _pendingUpdate;

        /// <summary>
        /// 窗口加载完成后自动检测新版本，发现后弹窗让用户选择忽略或更新。
        /// </summary>
        /// <param name="sender">触发事件的窗口。</param>
        /// <param name="e">路由事件参数。</param>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckForUpdateAsync(false, false);
        }

        /// <summary>
        /// 查询远端版本：发现新版本时高亮按钮并按配置弹窗，手动检查时始终弹窗。
        /// </summary>
        /// <param name="showResult">是否反馈「已是最新」等结果信息。</param>
        /// <param name="forcePrompt">为 true 时即使忽略过该版本也强制弹窗。</param>
        /// <returns>异步操作任务。</returns>
        private async Task CheckForUpdateAsync(bool showResult, bool forcePrompt)
        {
            BtnCheckUpdate.IsEnabled = false;
            try
            {
                string versionText = GetCurrentVersionText();
                Version current;
                if (!Version.TryParse(versionText, out current))
                {
                    current = new Version(1, 0, 0);
                }

                _pendingUpdate = await UpdateService.CheckAsync(current);
                if (_pendingUpdate != null)
                {
                    BtnCheckUpdate.Content = "有新版本 v" + _pendingUpdate.Version;
                    BtnCheckUpdate.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(255, 214, 102));
                    TxtStatus.Text = "发现新版本 v" + _pendingUpdate.Version + "，点击页脚「有新版本」查看详情";

                    string ignored = UpdateWindow.ReadIgnoredVersion();
                    bool alreadyIgnored = !forcePrompt &&
                        string.Equals(ignored, _pendingUpdate.Version.ToString(), StringComparison.OrdinalIgnoreCase);
                    if (!alreadyIgnored)
                    {
                        await PromptUpdateAsync(versionText);
                    }
                }
                else if (showResult)
                {
                    TxtStatus.Text = "已是最新版本";
                }
            }
            catch (Exception)
            {
                if (showResult)
                {
                    TxtStatus.Text = "检查更新失败，请稍后重试";
                }
            }
            finally
            {
                BtnCheckUpdate.IsEnabled = true;
            }
        }

        /// <summary>
        /// 读取当前程序版本号并去掉构建元数据后缀。
        /// </summary>
        /// <returns>标准化后的版本字符串。</returns>
        private static string GetCurrentVersionText()
        {
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            int plusIndex = version.IndexOf('+');
            return plusIndex >= 0 ? version.Substring(0, plusIndex) : version;
        }

        /// <summary>
        /// 弹出更新详情窗口，用户选择立即更新时执行升级。
        /// </summary>
        /// <param name="currentVersion">当前版本号，用于弹窗对比展示。</param>
        /// <returns>异步操作任务。</returns>
        private async Task PromptUpdateAsync(string currentVersion)
        {
            if (_pendingUpdate == null)
            {
                return;
            }

            var window = new UpdateWindow(currentVersion, _pendingUpdate)
            {
                Owner = this
            };
            bool? result = window.ShowDialog();
            if (result != true || !window.IsUpdateConfirmed)
            {
                TxtStatus.Text = "已忽略本次更新，可在页脚「检查更新」重新查看";
                return;
            }

            await InstallUpdateAsync();
        }

        /// <summary>
        /// 下载新版本并启动替换脚本，完成后关闭当前程序。
        /// </summary>
        /// <returns>异步操作任务。</returns>
        private async Task InstallUpdateAsync()
        {
            TxtStatus.Text = "正在下载新版本，请稍候…";
            BtnCheckUpdate.IsEnabled = false;
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                bool started = await UpdateService.InstallAsync(_pendingUpdate, exePath, Process.GetCurrentProcess().Id);
                if (started)
                {
                    TxtStatus.Text = "下载完成，程序即将自动重启完成升级";
                    await Task.Delay(800);
                    Close();
                }
                else
                {
                    TxtStatus.Text = "下载失败，请稍后重试";
                    BtnCheckUpdate.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "升级失败：" + ex.Message;
                BtnCheckUpdate.IsEnabled = true;
            }
        }

        /// <summary>
        /// 检查更新按钮点击：有可用更新则弹出详情，否则主动发起一次检测。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate != null)
            {
                await PromptUpdateAsync(GetCurrentVersionText());
                return;
            }

            TxtStatus.Text = "正在检查更新…";
            await CheckForUpdateAsync(true, true);
        }

        /// <summary>
        /// 关于我按钮点击，打开关于窗口。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            var window = new AboutWindow { Owner = this };
            window.ShowDialog();
        }

        /// <summary>
        /// 从程序集的 InformationalVersion 读取版本号并显示在标题栏和页脚，流水线构建时可自动注入新版本。
        /// </summary>
        private void SetVersionText()
        {
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

            int plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
            {
                version = version.Substring(0, plusIndex);
            }

            TxtVersionBadge.Text = "v" + version;
            TxtVersionFooter.Text = "v" + version + " · 内置 MAS 3.14 · © 2026 源动力";
        }

        /// <summary>
        /// 从注册表读取 Windows 版本信息并填充界面，读取失败时显示未知。
        /// </summary>
        private void LoadSystemInfo()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null)
                    {
                        SetUnknownSystemInfo();
                        return;
                    }

                    string productName = key.GetValue("ProductName")?.ToString();
                    string editionId = key.GetValue("EditionID")?.ToString();
                    string displayVersion = key.GetValue("DisplayVersion")?.ToString();
                    string currentBuild = key.GetValue("CurrentBuild")?.ToString();
                    object ubr = key.GetValue("UBR");
                    string installType = key.GetValue("InstallationType")?.ToString();

                    TxtOsName.Text = string.IsNullOrWhiteSpace(productName)
                        ? "未知"
                        : (string.IsNullOrWhiteSpace(editionId) ? productName : productName + " (" + editionId + ")");
                    TxtDisplayVersion.Text = string.IsNullOrWhiteSpace(displayVersion) ? "未知" : displayVersion;
                    TxtBuild.Text = string.IsNullOrWhiteSpace(currentBuild)
                        ? "未知"
                        : (ubr == null ? currentBuild : currentBuild + "." + ubr);
                    TxtArch.Text = Environment.Is64BitOperatingSystem ? "64 位" : "32 位";
                    TxtInstallType.Text = string.Equals(installType, "Server", StringComparison.OrdinalIgnoreCase) ? "服务器" : "客户端";
                }
            }
            catch (Exception)
            {
                SetUnknownSystemInfo();
            }
        }

        /// <summary>
        /// 当注册表信息不可用时，将系统信息区域统一置为未知。
        /// </summary>
        private void SetUnknownSystemInfo()
        {
            TxtOsName.Text = "未知";
            TxtDisplayVersion.Text = "未知";
            TxtBuild.Text = "未知";
            TxtArch.Text = Environment.Is64BitOperatingSystem ? "64 位" : "32 位";
            TxtInstallType.Text = "未知";
        }

        /// <summary>
        /// 通过 WMI 查询 Windows 许可证状态，并更新界面的激活状态文字与颜色。
        /// </summary>
        private void RefreshLicenseStatus()
        {
            try
            {
                bool activated = false;
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        using (item)
                        {
                            string name = item["Name"]?.ToString();
                            if (string.IsNullOrEmpty(name) || name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }

                            int status = Convert.ToInt32(item["LicenseStatus"]);
                            if (status == 1)
                            {
                                activated = true;
                                break;
                            }
                        }
                    }
                }

                TxtLicense.Text = activated ? "已激活" : "未激活";
                TxtLicense.Foreground = activated
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 119, 6));
            }
            catch (Exception)
            {
                TxtLicense.Text = "无法查询";
                TxtLicense.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128));
            }
        }

        /// <summary>
        /// 将内置脚本释放到临时目录，供激活流程直接调用，避免依赖外部文件。
        /// </summary>
        /// <returns>释放后的脚本完整路径。</returns>
        private string ExtractScript()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("MAS_AIO.cmd", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(resourceName))
            {
                throw new InvalidOperationException("内置激活脚本缺失，请重新下载完整程序。");
            }

            string directory = Path.Combine(Path.GetTempPath(), "MasActivator");
            Directory.CreateDirectory(directory);
            string scriptPath = Path.Combine(directory, "MAS_AIO.cmd");

            using (Stream source = assembly.GetManifestResourceStream(resourceName))
            {
                if (source == null)
                {
                    throw new InvalidOperationException("读取内置激活脚本失败。");
                }

                using (FileStream target = File.Create(scriptPath))
                {
                    source.CopyTo(target);
                }
            }

            return scriptPath;
        }

        /// <summary>
        /// 根据当前选中的激活类型，返回 MAS 脚本对应的无人值守参数。
        /// </summary>
        /// <returns>脚本命令行参数；未选中任何类型时返回空字符串。</returns>
        private string GetSelectedArguments()
        {
            switch (CmbActivation.SelectedIndex)
            {
                case 0:
                    return "/HWID /S";
                case 1:
                    return "/Ohook /S";
                case 2:
                    return "/HWID /Ohook /S";
                case 3:
                    return "/Z-WindowsESUOffice /S";
                case 4:
                    return "/K-WindowsOffice /S";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// 根据下拉框当前选项，刷新下方的激活方式说明。
        /// </summary>
        private void UpdateTypeDescription()
        {
            if (TxtTypeTitle == null || TxtTypeDesc == null)
            {
                return;
            }

            switch (CmbActivation.SelectedIndex)
            {
                case 1:
                    TxtTypeTitle.Text = "Ohook";
                    TxtTypeDesc.Text = "适用于 Office 2016—2024 / Microsoft 365，激活后无需重复操作。";
                    break;
                case 2:
                    TxtTypeTitle.Text = "Windows + Office";
                    TxtTypeDesc.Text = "一次完成操作系统与办公软件的激活，适合全新安装的电脑。";
                    break;
                case 3:
                    TxtTypeTitle.Text = "TSforge 兼容性方案";
                    TxtTypeDesc.Text = "支持 Windows / ESU / Office，用于常规方式不适用时的替代方案。";
                    break;
                case 4:
                    TxtTypeTitle.Text = "Online KMS";
                    TxtTypeDesc.Text = "适合临时或批量环境，有效期 180 天，需要保持联网。";
                    break;
                default:
                    TxtTypeTitle.Text = "HWID · 推荐";
                    TxtTypeDesc.Text = "自动匹配当前版本，激活信息与硬件绑定，重装系统后仍可自动激活。";
                    break;
            }
        }

        /// <summary>
        /// 下拉框选项变化事件，同步更新说明文字。
        /// </summary>
        /// <param name="sender">触发事件的下拉框。</param>
        /// <param name="e">选项变化事件参数。</param>
        private void CmbActivation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTypeDescription();
        }

        /// <summary>
        /// 打开官方教程链接，用户取消授权或启动失败时不影响主界面。
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
            catch (Exception ex)
            {
                TxtStatus.Text = "打开浏览器失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 自定义标题栏鼠标按下事件，实现拖动窗口；点击标题栏按钮时不触发拖动。
        /// </summary>
        /// <param name="sender">触发事件的标题栏边框。</param>
        /// <param name="e">鼠标事件参数。</param>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Button)
            {
                return;
            }

            DragMove();
        }

        /// <summary>
        /// 最小化按钮点击事件，将窗口最小化到任务栏。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>
        /// 关闭按钮点击事件，退出应用程序。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 一键激活按钮点击事件：释放脚本、申请管理员权限并等待激活流程结束。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            BtnActivate.IsEnabled = false;
            TxtStatus.Text = "正在准备…";

            try
            {
                string arguments = GetSelectedArguments();
                if (string.IsNullOrEmpty(arguments))
                {
                    TxtStatus.Text = "请先选择一种激活类型。";
                    return;
                }

                string scriptPath = ExtractScript();
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"\"" + scriptPath + "\" " + arguments + "\"",
                    WorkingDirectory = Path.GetDirectoryName(scriptPath),
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new InvalidOperationException("无法启动激活程序。");
                    }

                    TxtStatus.Text = "已启动，请在黑色窗口中按提示操作";
                    await Task.Run(() => process.WaitForExit());

                    int exitCode = process.ExitCode;
                    RefreshLicenseStatus();
                    TxtStatus.Text = "已完成（退出码 " + exitCode + "），请查看窗口结果";
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                TxtStatus.Text = "已取消授权，激活未执行";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "启动失败：" + ex.Message;
            }
            finally
            {
                BtnActivate.IsEnabled = true;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace MasActivator
{
    /// <summary>
    /// 主窗口：展示系统信息与激活状态，使用微软官方 GVLK 密钥通过 slmgr 完成 KMS 在线激活。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 内置 KMS 服务器地址，slmgr /skms 使用。
        /// </summary>
        private const string KmsServer = "kms.loli.best";

        /// <summary>
        /// 各 Windows 版本（EditionID）对应的微软官方 GVLK 客户端密钥。
        /// 来源：微软官方文档《KMS 客户端激活和产品密钥》，Win10/11 及 LTSC 通用。
        /// 家庭版（Core）不在 KMS 批量授权范围内，未收录。
        /// </summary>
        private static readonly Dictionary<string, string> GvlkKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Professional", "W269N-WFGWX-YVC9B-4J6C9-T83GX" },
            { "ProfessionalN", "MH37W-N47XK-V7XM9-C7227-GCQG9" },
            { "ProfessionalWorkstation", "NRG8B-VKK3Q-CXVCJ-9G2XF-6Q84J" },
            { "ProfessionalWorkstationN", "9FNHH-K3HBT-3W4TD-6383H-6XYWF" },
            { "ProfessionalEducation", "6TP4R-GNPTD-KYYHQ-7B7DP-J447Y" },
            { "ProfessionalEducationN", "YVWGF-BXNMC-HTQYQ-CPQ99-66QFC" },
            { "Education", "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2" },
            { "EducationN", "2WH4N-8QGBV-H22JP-CT43Q-MDWWJ" },
            { "Enterprise", "NPPR9-FWDCX-D2C8J-H872K-2YT43" },
            { "EnterpriseN", "DPH2V-TTNVB-4X9Q3-TJR4H-KHJW4" },
            { "EnterpriseG", "YYVX9-NTFWV-6MDM3-9PT4T-4M68B" },
            { "EnterpriseGN", "44RPN-FTY23-9VTTB-MP9BX-T84FV" },
            { "EnterpriseS", "M7XTQ-FN8P6-TTKYV-9D4CC-J462D" },
            { "EnterpriseSN", "92NFX-8DJQP-P6BBQ-THF9C-7CG2H" },
            { "IoTEnterpriseS", "KBN8V-HFGQ4-MGXVD-347P6-PDQGT" },
            { "EnterpriseLTSB", "DCPHK-NFMTC-H88MJ-PFHPY-QJ4BJ" },
            { "EnterpriseLTSBN", "QFFDN-GRT3P-VKWWX-X7T3R-8B639" }
        };

        /// <summary>
        /// 初始化窗口，并立即读取当前系统的版本与激活状态。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            LoadSystemInfo();
            RefreshLicenseStatus();
            SetVersionText();
            BuildCleanItems();
            Loaded += MainWindow_Loaded;
        }

        /// <summary>确认弹层的结果回调；等待期间挂起的任务由按钮点击结束。</summary>
        private TaskCompletionSource<bool> _confirmTcs;

        /// <summary>显示应用内确认弹层并等待用户选择；true 表示已确认。</summary>
        private Task<bool> ShowConfirmAsync(string title, string body, bool danger)
        {
            _confirmTcs = new TaskCompletionSource<bool>();
            TxtConfirmTitle.Text = title;
            TxtConfirmBody.Text = body;
            BtnConfirmOk.Content = danger ? "仍然清空" : "确定清理";
            BtnConfirmOk.Style = (Style)FindResource(danger ? "WarnSmallButton" : "SmallSolidButton");
            ConfirmOverlay.Visibility = Visibility.Visible;
            return _confirmTcs.Task;
        }

        /// <summary>确认弹层：确定。</summary>
        private void BtnConfirmOk_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            if (_confirmTcs != null) _confirmTcs.TrySetResult(true);
        }

        /// <summary>确认弹层：取消。</summary>
        private void BtnConfirmCancel_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            if (_confirmTcs != null) _confirmTcs.TrySetResult(false);
        }

        /// <summary>
        /// 当前系统的 EditionID，用于匹配 GVLK 密钥；读取失败时为空。
        /// </summary>
        private string _editionId;

        /// <summary>
        /// 缓存检测到的更新信息，供按钮点击时执行升级。
        /// </summary>
        private UpdateService.UpdateInfo _pendingUpdate;

        /// <summary>
        /// 记录最近一次查询到的激活状态，决定主按钮显示“一键激活”还是“卸载激活”。
        /// </summary>
        private bool _isActivated;


        /// <summary>
        /// 窗口加载完成后自动检测新版本，发现后弹窗让用户选择忽略或更新。
        /// </summary>
        /// <param name="sender">触发事件的窗口。</param>
        /// <param name="e">路由事件参数。</param>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            BtnActivate.Focus();
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
            TxtVersionFooter.Text = "Windows 激活 · 清理助手 · v" + version + " · © 2026 源动力";
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
                    _editionId = editionId;
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
        /// 查询本机 Windows 是否已激活，供界面刷新与激活结果轮询共用。
        /// </summary>
        /// <returns>已激活返回 true；未激活返回 false；查询失败返回 null。</returns>
        private bool? QueryActivationStatus()
        {
            try
            {
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

                            if (Convert.ToInt32(item["LicenseStatus"]) == 1)
                            {
                                return true;
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 通过 WMI 查询 Windows 许可证状态，并更新界面的激活状态文字、颜色与主按钮形态。
        /// </summary>
        private void RefreshLicenseStatus()
        {
            bool? activated = QueryActivationStatus();
            if (activated == null)
            {
                TxtLicense.Text = "无法查询";
                TxtLicense.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128));
                return;
            }

            _isActivated = activated.Value;
            TxtLicense.Text = _isActivated ? "已激活" : "未激活";
            TxtLicense.Foreground = _isActivated
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 119, 6));
            UpdatePrimaryButton();
        }

        /// <summary>
        /// 根据激活状态切换主按钮：未激活显示“一键激活”（蓝色），已激活显示“卸载激活”（红色警示）。
        /// </summary>
        private void UpdatePrimaryButton()
        {
            if (_isActivated)
            {
                BtnActivate.Content = "卸 载 激 活";
                BtnActivate.Style = (Style)FindResource("WarnButton");
                TxtStatus.Text = "系统已激活，点击可卸载激活状态";
            }
            else
            {
                BtnActivate.Content = "一 键 激 活";
                BtnActivate.Style = (Style)FindResource("AccentButton");
                TxtStatus.Text = "点击开始激活，全程自动完成，无需任何操作";
            }
        }

        /// <summary>
        /// 轮询等待系统完成激活。
        /// slmgr /ato 返回后许可证状态可能延迟几秒才刷新，不能只看命令输出，必须轮询确认。
        /// </summary>
        /// <param name="timeout">最长等待时间。</param>
        /// <returns>检测到已激活返回 true；超时或查询失败返回 false。</returns>
        private async Task<bool> WaitForActivationAsync(TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now.Add(timeout);
            while (DateTime.Now < deadline)
            {
                await Task.Delay(3000);
                if (QueryActivationStatus() == true)
                {
                    return true;
                }

                int waitedSeconds = (int)(timeout.TotalSeconds - (deadline - DateTime.Now).TotalSeconds);
                TxtStatus.Text = "正在等待系统确认激活…（已等待 " + waitedSeconds + " 秒）";
            }

            return false;
        }

        /// <summary>
        /// 根据当前系统的 EditionID 匹配微软官方 GVLK 密钥。
        /// </summary>
        /// <returns>匹配到的密钥；版本不在 KMS 支持范围（如家庭版）时返回 null。</returns>
        private string GetGvlkKey()
        {
            if (string.IsNullOrWhiteSpace(_editionId))
            {
                return null;
            }

            return GvlkKeys.TryGetValue(_editionId, out string key) ? key : null;
        }

        /// <summary>
        /// 以无窗口方式执行 slmgr.vbs 子命令并返回完整输出。
        /// 用 cscript //nologo 调用而不是直接运行 slmgr.exe，避免每个步骤弹出 Windows Script Host 对话框。
        /// </summary>
        /// <param name="arguments">slmgr 子命令参数，例如 /ipk、/skms、/ato。</param>
        /// <returns>命令输出文本；进程启动失败时返回 null。</returns>
        private static async Task<string> RunSlmgrAsync(string arguments)
        {
            string slmgrPath = Path.Combine(Environment.SystemDirectory, "slmgr.vbs");
            var startInfo = new ProcessStartInfo
            {
                FileName = "cscript.exe",
                Arguments = "//nologo \"" + slmgrPath + "\" " + arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
            };

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    return null;
                }

                // 先挂上输出读取任务再等待退出，防止输出缓冲写满导致进程被阻塞
                Task<string> readOutput = process.StandardOutput.ReadToEndAsync();
                Task<string> readError = process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());
                string output = await readOutput;
                string error = await readError;
                return output + error;
            }
        }

        /// <summary>
        /// 取多行输出中的第一行非空文本，用于状态栏提示。
        /// </summary>
        /// <param name="text">slmgr 的原始输出。</param>
        /// <returns>第一行非空文本；没有内容时返回空字符串。</returns>
        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        /// <summary>拼出便于反馈排查的系统描述，如「Windows 10 企业版 LTSC 24H2 26100」。</summary>
        private static string OsLabel()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        string name = key.GetValue("ProductName") as string;
                        string build = key.GetValue("DisplayVersion") as string;
                        string current = key.GetValue("CurrentBuild") as string;
                        string os = (name ?? "Windows") + (string.IsNullOrEmpty(build) ? "" : " " + build)
                                    + (string.IsNullOrEmpty(current) ? "" : " 内部版本 " + current);
                        return os + " · " + (Environment.Is64BitOperatingSystem ? "64 位" : "32 位");
                    }
                }
            }
            catch
            {
                // 读注册表失败时退回系统自带描述。
            }
            return Environment.OSVersion.VersionString;
        }

        /// <summary>
        /// 打开「问题反馈」弹层，并自动带上版本号、系统版本等环境信息。
        /// </summary>
        private void BtnFeedback_Click(object sender, RoutedEventArgs e)
        {
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            int plus = version.IndexOf('+');
            if (plus > 0) version = version.Substring(0, plus);
            TxtFeedbackMeta.Text = "环境信息：Windows 激活 · 清理助手 v" + version + " · " + OsLabel();
            FeedbackOverlay.Visibility = Visibility.Visible;
            TxtFeedbackInput.Focus();
        }

        /// <summary>关闭「问题反馈」弹层。</summary>
        private void BtnFeedbackCancel_Click(object sender, RoutedEventArgs e)
        {
            FeedbackOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 把反馈内容（含环境信息）复制到剪贴板，并打开 Gitee 反馈页。
        /// </summary>
        private void BtnFeedbackSubmit_Click(object sender, RoutedEventArgs e)
        {
            string body = TxtFeedbackInput.Text.Trim();
            string text = (body.Length == 0 ? "" : body + Environment.NewLine + Environment.NewLine)
                          + TxtFeedbackMeta.Text;
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // 剪贴板被其它程序占用时忽略，仅影响复制。
            }
            try
            {
                Process.Start(new ProcessStartInfo("https://yuandl.cn/feedback")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                TxtFeedbackMeta.Text = "打开浏览器失败：" + ex.Message;
                return;
            }
            FeedbackOverlay.Visibility = Visibility.Collapsed;
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
        /// 主按钮点击事件：未激活时执行一键激活，已激活时执行卸载激活。
        /// 全程在后台静默完成（黑窗口隐藏、WMI 卸载无窗口），只通过状态栏与激活状态文字反馈结果。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            BtnActivate.IsEnabled = false;

            try
            {
                if (_isActivated)
                {
                    await DeactivateAsync();
                }
                else
                {
                    await ActivateAsync();
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "操作失败：" + ex.Message;
            }
            finally
            {
                BtnActivate.IsEnabled = true;
            }
        }

        /// <summary>
        /// 执行一键激活：安装官方 GVLK 密钥 → 设置 KMS 服务器 → 激活，并轮询真实激活结果。
        /// 程序清单已要求管理员权限运行，slmgr 全程通过 cscript 无窗口执行，不弹任何对话框。
        /// </summary>
        /// <returns>异步操作任务。</returns>
        private async Task ActivateAsync()
        {
            string gvlk = GetGvlkKey();
            if (string.IsNullOrEmpty(gvlk))
            {
                TxtStatus.Text = "当前版本（" + (string.IsNullOrWhiteSpace(_editionId) ? "未知" : _editionId) +
                    "）不在 KMS 支持范围，家庭版请使用数字权利激活";
                return;
            }

            TxtStatus.Text = "正在安装产品密钥…";
            string ipkOutput = await RunSlmgrAsync("/ipk " + gvlk);
            if (ipkOutput == null)
            {
                TxtStatus.Text = "安装密钥失败：无法启动 slmgr";
                return;
            }

            if (ipkOutput.IndexOf("错误", StringComparison.Ordinal) >= 0 ||
                ipkOutput.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                TxtStatus.Text = "安装密钥失败：" + FirstLine(ipkOutput);
                return;
            }

            TxtStatus.Text = "正在设置 KMS 服务器…";
            string skmsOutput = await RunSlmgrAsync("/skms " + KmsServer);
            if (skmsOutput != null &&
                (skmsOutput.IndexOf("错误", StringComparison.Ordinal) >= 0 ||
                 skmsOutput.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                TxtStatus.Text = "设置 KMS 服务器失败：" + FirstLine(skmsOutput);
                return;
            }

            TxtStatus.Text = "正在激活，请稍候，全程无需任何操作…";
            string atoOutput = await RunSlmgrAsync("/ato");

            TxtStatus.Text = "正在等待系统确认激活…";
            bool activated = await WaitForActivationAsync(TimeSpan.FromSeconds(120));
            RefreshLicenseStatus();

            if (activated)
            {
                TxtStatus.Text = "激活成功，系统已完成激活";
                return;
            }

            string failReason = FirstLine(atoOutput);
            TxtStatus.Text = failReason.Length > 0
                ? "激活未完成：" + failReason
                : "激活未完成：请检查网络后重试";
        }

        /// <summary>
        /// 执行卸载激活：通过 WMI 卸载 Windows 产品密钥并刷新状态。
        /// </summary>
        /// <returns>异步操作任务。</returns>
        private async Task DeactivateAsync()
        {
            TxtStatus.Text = "正在卸载激活…";

            bool anyUninstalled = await Task.Run(UninstallProductKeys);
            await Task.Delay(1500);
            RefreshLicenseStatus();

            TxtStatus.Text = anyUninstalled && !_isActivated
                ? "已取消激活，系统恢复为未激活状态"
                : "卸载未完成，请稍后重试";
        }

        /// <summary>
        /// 逐个卸载 Windows 的产品密钥；WMI 的 UninstallProductKey 返回 0 表示成功。
        /// </summary>
        /// <returns>是否有至少一个密钥被成功卸载。</returns>
        private bool UninstallProductKeys()
        {
            bool any = false;

            using (var searcher = new ManagementObjectSearcher(
                "SELECT ID, Name, PartialProductKey FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
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

                        try
                        {
                            object result = item.InvokeMethod("UninstallProductKey", null);
                            if (result != null && Convert.ToUInt32(result) == 0)
                            {
                                any = true;
                            }
                        }
                        catch (Exception)
                        {
                            // 单个许可证条目失败不影响其余条目的卸载
                        }
                    }
                }
            }

            // 请求许可证服务刷新缓存状态，让 LicenseStatus 尽快反映卸载结果
            try
            {
                using (var svcSearcher = new ManagementObjectSearcher("SELECT * FROM SoftwareLicensingService"))
                {
                    foreach (ManagementObject svc in svcSearcher.Get())
                    {
                        using (svc)
                        {
                            svc.InvokeMethod("RefreshLicenseStatus", null);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 刷新失败不影响卸载结果判定
            }

            // WMI 卸载不可用时回退到 slmgr /upk：cscript 静默执行，不弹任何对话框
            if (!any)
            {
                try
                {
                    string slmgrPath = Path.Combine(Environment.SystemDirectory, "slmgr.vbs");
                    var fallbackInfo = new ProcessStartInfo
                    {
                        FileName = "cscript.exe",
                        Arguments = "//nologo \"" + slmgrPath + "\" /upk",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (Process fallback = Process.Start(fallbackInfo))
                    {
                        if (fallback != null)
                        {
                            fallback.StandardOutput.ReadToEnd();
                            fallback.StandardError.ReadToEnd();
                            fallback.WaitForExit();
                            any = fallback.ExitCode == 0;
                        }
                    }
                }
                catch (Exception)
                {
                    // 回退路径失败时保持 any=false，由调用方提示重试
                }
            }

            return any;
        }
    }
}
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
        /// 一键激活按钮点击事件：释放脚本、申请管理员权限并等待激活流程结束。
        /// </summary>
        /// <param name="sender">触发事件的按钮。</param>
        /// <param name="e">路由事件参数。</param>
        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            BtnActivate.IsEnabled = false;
            TxtStatus.Text = "正在准备激活程序…";

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

                    TxtStatus.Text = "激活程序已启动，请在弹出的窗口中按提示操作，完成后窗口会自动关闭…";
                    await Task.Run(() => process.WaitForExit());

                    int exitCode = process.ExitCode;
                    RefreshLicenseStatus();
                    TxtStatus.Text = "激活流程已结束（退出码 " + exitCode + "），请查看窗口中的结果。";
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                TxtStatus.Text = "已取消授权，激活未执行。需要管理员权限才能激活。";
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

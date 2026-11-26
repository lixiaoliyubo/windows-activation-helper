using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace MasActivator
{
    /// <summary>
    /// 更新服务：优先从 Gitee Release 检测并下载新版本，失败时回退到 GitHub Release。
    /// </summary>
    internal static class UpdateService
    {
        /// <summary>
        /// Gitee 仓库最新 Release 接口地址。
        /// </summary>
        private const string GiteeLatestUrl =
            "https://gitee.com/api/v5/repos/lixiaoliyubo/windows-activation-helper/releases/latest";

        /// <summary>
        /// GitHub 仓库最新 Release 接口地址。
        /// </summary>
        private const string GitHubLatestUrl =
            "https://api.github.com/repos/lixiaoliyubo/windows-activation-helper/releases/latest";

        /// <summary>
        /// Gitee 仓库中始终指向最新构建产物的 raw 下载地址。
        /// </summary>
        private const string GiteeRawDownloadUrl =
            "https://gitee.com/lixiaoliyubo/windows-activation-helper/raw/main/publish/WindowsActivationHelper.exe";

        /// <summary>
        /// 触发更新的本地进程 ID，更新脚本据此等待程序退出。
        /// </summary>
        private static int _currentProcessId;

        /// <summary>
        /// 更新信息：远端版本号与安装包下载地址。
        /// </summary>
        internal sealed class UpdateInfo
        {
            /// <summary>
            /// 远端最新版本号。
            /// </summary>
            internal Version Version { get; set; }

            /// <summary>
            /// 安装包下载地址。
            /// </summary>
            internal string DownloadUrl { get; set; }

            /// <summary>
            /// 下载来源，用于界面提示。
            /// </summary>
            internal string Source { get; set; }

            /// <summary>
            /// Release 说明正文，用于弹窗展示本次更新内容。
            /// </summary>
            internal string ReleaseNotes { get; set; }
        }

        /// <summary>
        /// 检测是否存在新版本：先查 Gitee，失败后查 GitHub。
        /// </summary>
        /// <param name="currentVersion">当前程序版本，用于比较大小。</param>
        /// <returns>存在新版本时返回更新信息，否则返回 null。</returns>
        internal static async Task<UpdateInfo> CheckAsync(Version currentVersion)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            ReleaseInfo release = await TryFetchAsync(GiteeLatestUrl, "Gitee").ConfigureAwait(false);
            string downloadUrl = GiteeRawDownloadUrl;

            if (release == null)
            {
                release = await TryFetchAsync(GitHubLatestUrl, "GitHub").ConfigureAwait(false);
                downloadUrl = release == null ? null : FindAssetUrl(release);
            }
            else
            {
                // Gitee 的 Release 接口不提供附件上传，下载地址固定使用仓库 raw 文件。
                downloadUrl = GiteeRawDownloadUrl;
            }

            if (release == null || string.IsNullOrWhiteSpace(release.TagName) ||
                string.IsNullOrWhiteSpace(downloadUrl))
            {
                return null;
            }

            Version remoteVersion = ParseVersion(release.TagName);
            if (remoteVersion == null || remoteVersion <= currentVersion)
            {
                return null;
            }

            return new UpdateInfo
            {
                Version = remoteVersion,
                DownloadUrl = downloadUrl,
                Source = release.Source,
                ReleaseNotes = string.IsNullOrWhiteSpace(release.Body)
                    ? "暂无更新说明，请访问项目主页查看。"
                    : release.Body.Trim()
            };
        }

        /// <summary>
        /// 下载新版本并启动替换流程：脚本等待当前进程退出后覆盖 exe 并重新打开。
        /// </summary>
        /// <param name="info">检测到的更新信息。</param>
        /// <param name="targetPath">当前正在运行的 exe 完整路径。</param>
        /// <param name="currentProcessId">当前进程 ID，用于等待退出。</param>
        /// <returns>替换脚本已启动时返回 true，否则返回 false。</returns>
        internal static async Task<bool> InstallAsync(UpdateInfo info, string targetPath, int currentProcessId)
        {
            _currentProcessId = currentProcessId;

            string directory = Path.Combine(Path.GetTempPath(), "MasActivator");
            Directory.CreateDirectory(directory);
            string downloadPath = Path.Combine(directory, "WindowsActivationHelper.update.exe");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            {
                byte[] data = await client.GetByteArrayAsync(info.DownloadUrl).ConfigureAwait(false);
                if (data == null || data.Length < 1024)
                {
                    return false;
                }

                File.WriteAllBytes(downloadPath, data);
            }

            string scriptPath = Path.Combine(Path.GetTempPath(), "MasActivatorUpdate.cmd");
            string script = BuildUpdateScript(currentProcessId, downloadPath, targetPath);
            File.WriteAllText(scriptPath, script, Encoding.Default);

            // 直接由 cmd 隐藏执行脚本：不能用 start 另开窗口，否则升级过程中会弹出黑色控制台
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(startInfo);
            return true;
        }

        /// <summary>
        /// 生成等待进程退出、替换文件并重启应用的批处理脚本。
        /// 脚本不删除自身：批处理运行中删除自身会输出「找不到批处理文件」等错误行，
        /// 且脚本每次升级都会被同名文件覆盖，无需清理。
        /// </summary>
        /// <param name="processId">需要等待退出的进程 ID。</param>
        /// <param name="sourcePath">下载完成的新 exe 路径。</param>
        /// <param name="targetPath">需要被替换的 exe 路径。</param>
        /// <returns>批处理脚本内容。</returns>
        private static string BuildUpdateScript(int processId, string sourcePath, string targetPath)
        {
            return "@echo off\r\n"
                + ":wait\r\n"
                + "tasklist /FI \"PID eq " + processId + "\" 2>NUL | find \"" + processId + "\" >NUL\r\n"
                + "if %ERRORLEVEL%==1 goto replace\r\n"
                + "ping -n 2 127.0.0.1 >NUL\r\n"
                + "goto wait\r\n"
                + ":replace\r\n"
                + "copy /Y \"" + sourcePath + "\" \"" + targetPath + "\" >NUL\r\n"
                + "del \"" + sourcePath + "\" >NUL 2>&1\r\n"
                + "start \"\" \"" + targetPath + "\"\r\n";
        }

        /// <summary>
        /// 请求并解析一个 Release 接口，失败时返回 null。
        /// </summary>
        /// <param name="url">Release 接口地址。</param>
        /// <param name="source">来源名称，写入解析结果用于提示。</param>
        /// <returns>解析后的 Release 信息，失败返回 null。</returns>
        private static async Task<ReleaseInfo> TryFetchAsync(string url, string source)
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "WindowsActivationHelper");
                    string json = await client.GetStringAsync(url).ConfigureAwait(false);
                    var serializer = new DataContractJsonSerializer(typeof(ReleaseInfo));
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var release = serializer.ReadObject(stream) as ReleaseInfo;
                        if (release != null)
                        {
                            release.Source = source;
                        }

                        return release;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从 tag 名称中解析版本号，兼容 v 前缀。
        /// </summary>
        /// <param name="tagName">Release 的标签名称。</param>
        /// <returns>解析成功返回版本号，否则返回 null。</returns>
        private static Version ParseVersion(string tagName)
        {
            string text = tagName.Trim().TrimStart('v', 'V');
            Version version;
            return Version.TryParse(text, out version) ? version : null;
        }

        /// <summary>
        /// 从 Release 资产中查找可下载的 exe 安装包地址。
        /// </summary>
        /// <param name="release">Release 信息。</param>
        /// <returns>安装包下载地址，未找到返回 null。</returns>
        private static string FindAssetUrl(ReleaseInfo release)
        {
            if (release.Assets == null)
            {
                return null;
            }

            foreach (ReleaseAsset asset in release.Assets)
            {
                if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(asset.Name) ||
                    asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return asset.BrowserDownloadUrl;
                }
            }

            return null;
        }

        /// <summary>
        /// Release 接口的 JSON 数据结构，仅保留更新所需字段。
        /// </summary>
        [DataContract]
        private sealed class ReleaseInfo
        {
            /// <summary>
            /// 版本标签名称。
            /// </summary>
            [DataMember(Name = "tag_name")]
            internal string TagName { get; set; }

            /// <summary>
            /// Release 附带的文件列表。
            /// </summary>
            [DataMember(Name = "assets")]
            internal List<ReleaseAsset> Assets { get; set; }

            /// <summary>
            /// Release 说明正文。
            /// </summary>
            [DataMember(Name = "body")]
            internal string Body { get; set; }

            /// <summary>
            /// 来源平台名称，不来自接口 JSON。
            /// </summary>
            internal string Source { get; set; }
        }

        /// <summary>
        /// Release 附件的 JSON 数据结构。
        /// </summary>
        [DataContract]
        private sealed class ReleaseAsset
        {
            /// <summary>
            /// 附件文件名。
            /// </summary>
            [DataMember(Name = "name")]
            internal string Name { get; set; }

            /// <summary>
            /// 附件浏览器下载地址。
            /// </summary>
            [DataMember(Name = "browser_download_url")]
            internal string BrowserDownloadUrl { get; set; }
        }
    }
}

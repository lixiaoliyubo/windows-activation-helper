using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace MasActivator
{
    /// <summary>
    /// 更新服务：优先查 Release 接口，接口不可用时退回仓库内 version.txt / 成品 exe 的版本号，
    /// 下载后还会校验文件版本，避免把旧包当成新包装给用户。
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
        /// GitHub 仓库中始终指向最新构建产物的 raw 下载地址。
        /// </summary>
        private const string GitHubRawDownloadUrl =
            "https://raw.githubusercontent.com/lixiaoliyubo/windows-activation-helper/main/publish/WindowsActivationHelper.exe";

        /// <summary>
        /// Gitee 仓库版本清单地址，内容为纯版本号。
        /// </summary>
        private const string GiteeVersionUrl =
            "https://gitee.com/lixiaoliyubo/windows-activation-helper/raw/main/publish/version.txt";

        /// <summary>
        /// GitHub 仓库版本清单地址。
        /// </summary>
        private const string GitHubVersionUrl =
            "https://raw.githubusercontent.com/lixiaoliyubo/windows-activation-helper/main/publish/version.txt";

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
        /// 检测是否存在新版本：Release 接口与仓库版本清单取较高者，接口失败时只用版本清单。
        /// </summary>
        /// <param name="currentVersion">当前程序版本，用于比较大小。</param>
        /// <returns>存在新版本时返回更新信息，否则返回 null。</returns>
        internal static async Task<UpdateInfo> CheckAsync(Version currentVersion)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            UpdateInfo releaseInfo = await CheckViaReleasesAsync(currentVersion).ConfigureAwait(false);
            UpdateInfo buildInfo = await CheckViaVersionFileAsync(currentVersion).ConfigureAwait(false);

            if (releaseInfo == null)
            {
                return buildInfo;
            }

            if (buildInfo == null)
            {
                return releaseInfo;
            }

            // 两个来源都可用时取版本更高的那个：仓库刚构建完、Release 还没建出来时也不漏更新。
            return buildInfo.Version >= releaseInfo.Version ? buildInfo : releaseInfo;
        }

        /// <summary>
        /// 通过 Release 接口检测新版本：先查 Gitee，失败后查 GitHub。
        /// </summary>
        /// <param name="currentVersion">当前程序版本。</param>
        /// <returns>存在新版本时返回更新信息，否则返回 null。</returns>
        private static async Task<UpdateInfo> CheckViaReleasesAsync(Version currentVersion)
        {
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
        /// 读取仓库里的版本清单检测新版本：不依赖 Release 接口，接口被限流时仍能发现更新。
        /// </summary>
        /// <param name="currentVersion">当前程序版本。</param>
        /// <returns>存在新版本时返回更新信息，否则返回 null。</returns>
        private static async Task<UpdateInfo> CheckViaVersionFileAsync(Version currentVersion)
        {
            string[] urls = { GiteeVersionUrl, GitHubVersionUrl };
            string[] downloads = { GiteeRawDownloadUrl, GitHubRawDownloadUrl };
            string[] sources = { "Gitee", "GitHub" };

            for (int i = 0; i < urls.Length; i++)
            {
                string text = await TryFetchTextAsync(urls[i]).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                Version version = ParseVersion(text);
                if (version == null || version <= currentVersion)
                {
                    continue;
                }

                return new UpdateInfo
                {
                    Version = version,
                    DownloadUrl = downloads[i],
                    Source = sources[i],
                    ReleaseNotes = "仓库已更新到 v" + version + "，包含清理页与删除可靠性改进，详见项目主页。"
                };
            }

            return null;
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

            // 依次尝试首选地址、Gitee 仓库、GitHub 仓库；每次下载后都校验文件版本，
            // 只有版本不低于远端声明的版本才会用来替换，避免旧包被当成新包装上去后反复提示更新。
            var candidates = new List<string>();
            AddCandidate(candidates, info.DownloadUrl);
            AddCandidate(candidates, GiteeRawDownloadUrl);
            AddCandidate(candidates, GitHubRawDownloadUrl);

            bool downloaded = false;
            foreach (string url in candidates)
            {
                byte[] data = await TryDownloadAsync(url).ConfigureAwait(false);
                if (data == null || data.Length < 1024)
                {
                    continue;
                }

                File.WriteAllBytes(downloadPath, data);
                Version fileVersion = ReadFileVersion(downloadPath);
                if (fileVersion == null || fileVersion < info.Version)
                {
                    continue;
                }

                downloaded = true;
                break;
            }

            if (!downloaded)
            {
                return false;
            }

            string scriptPath = Path.Combine(Path.GetTempPath(), "MasActivatorUpdate.cmd");
            string script = BuildUpdateScript(currentProcessId, downloadPath, targetPath);
            File.WriteAllText(scriptPath, script, Encoding.Default);

            // 直接由 cmd 隐藏执行脚本：不能用 start 另开窗口，否则升级过程中会弹出黑色控制台
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(startInfo);
            return true;
        }

        /// <summary>
        /// 向候选下载地址列表追加一个地址，跳过空值与重复项。
        /// </summary>
        /// <param name="list">候选地址列表。</param>
        /// <param name="url">待追加的地址。</param>
        private static void AddCandidate(List<string> list, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            foreach (string item in list)
            {
                if (string.Equals(item, url, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            list.Add(url);
        }

        /// <summary>
        /// 下载一个地址的内容，失败时返回 null。
        /// </summary>
        /// <param name="url">下载地址。</param>
        /// <returns>下载得到的字节，失败返回 null。</returns>
        private static async Task<byte[]> TryDownloadAsync(string url)
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "WindowsActivationHelper");
                    return await client.GetByteArrayAsync(url).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 读取 exe 的文件版本号，用于校验下载到的安装包是否真的是新版本。
        /// </summary>
        /// <param name="path">exe 路径。</param>
        /// <returns>解析成功返回版本号，失败返回 null。</returns>
        private static Version ReadFileVersion(string path)
        {
            try
            {
                string text = FileVersionInfo.GetVersionInfo(path).FileVersion;
                Version version;
                return Version.TryParse(text, out version) ? version : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 生成等待进程退出、替换文件并重启应用的批处理脚本。
        /// 路径先转成 8.3 短路径，避免中文目录或空格在批处理里被解析坏；
        /// 替换失败会重试若干次（刚退出时文件偶尔仍被系统占用）。
        /// </summary>
        /// <param name="processId">需要等待退出的进程 ID。</param>
        /// <param name="sourcePath">下载完成的新 exe 路径。</param>
        /// <param name="targetPath">需要被替换的 exe 路径。</param>
        /// <returns>批处理脚本内容。</returns>
        private static string BuildUpdateScript(int processId, string sourcePath, string targetPath)
        {
            string source = ToShortPath(sourcePath);
            string target = ToShortPath(targetPath);

            return "@echo off\r\n"
                + "setlocal\r\n"
                + ":wait\r\n"
                + "tasklist /FI \"PID eq " + processId + "\" 2>NUL | find \"" + processId + "\" >NUL\r\n"
                + "if %ERRORLEVEL%==1 goto replace\r\n"
                + "ping -n 2 127.0.0.1 >NUL\r\n"
                + "goto wait\r\n"
                + ":replace\r\n"
                + "set TRIES=0\r\n"
                + ":copy\r\n"
                + "copy /Y \"" + source + "\" \"" + target + "\" >NUL 2>&1\r\n"
                + "if errorlevel 1 goto retry\r\n"
                + "goto done\r\n"
                + ":retry\r\n"
                + "set /a TRIES+=1\r\n"
                + "if %TRIES% GEQ 10 goto startold\r\n"
                + "ping -n 2 127.0.0.1 >NUL\r\n"
                + "goto copy\r\n"
                + ":startold\r\n"
                + "start \"\" \"" + target + "\"\r\n"
                + "exit /b 1\r\n"
                + ":done\r\n"
                + "del \"" + source + "\" >NUL 2>&1\r\n"
                + "start \"\" \"" + target + "\"\r\n";
        }

        /// <summary>
        /// 把长路径转换为 8.3 短路径，失败时原样返回。
        /// </summary>
        /// <param name="path">原始路径。</param>
        /// <returns>短路径或原路径。</returns>
        private static string ToShortPath(string path)
        {
            try
            {
                var buffer = new StringBuilder(512);
                uint length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
                if (length > 0 && length < buffer.Capacity)
                {
                    return buffer.ToString();
                }
            }
            catch (Exception)
            {
            }

            return path;
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
        /// 拉取一个纯文本地址，失败时返回 null。
        /// </summary>
        /// <param name="url">文本地址。</param>
        /// <returns>文本内容，失败返回 null。</returns>
        private static async Task<string> TryFetchTextAsync(string url)
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "WindowsActivationHelper");
                    return await client.GetStringAsync(url).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从 tag 名称或版本清单文本中解析版本号，兼容 v 前缀与多余空白。
        /// </summary>
        /// <param name="tagName">Release 标签或版本清单内容。</param>
        /// <returns>解析成功返回版本号，否则返回 null。</returns>
        private static Version ParseVersion(string tagName)
        {
            string text = tagName.Trim().TrimStart('v', 'V');
            int lineEnd = text.IndexOfAny(new[] { '\r', '\n', ' ', '\t' });
            if (lineEnd >= 0)
            {
                text = text.Substring(0, lineEnd);
            }

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
        /// 取 8.3 短路径，供批处理安全拼接。
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferSize);

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

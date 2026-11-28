using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace MasActivator
{
    /// <summary>清理对象的类型，决定扫描与删除算法。</summary>
    public enum CleanupKind
    {
        /// <summary>普通目录：递归统计/删除其中的文件。</summary>
        Directory,

        /// <summary>单个文件。</summary>
        File,

        /// <summary>缩略图缓存目录：只处理 thumbcache_*。</summary>
        Thumbnail,

        /// <summary>回收站：走 Shell API。</summary>
        RecycleBin,

        /// <summary>注册表 MRU 痕迹：只统计/删除指定键下的值，删除前自动导出备份。</summary>
        RegistryMru
    }

    /// <summary>一条清理项，对应界面上的一行可勾选对象。</summary>
    public sealed class CleanupItem
    {
        /// <summary>界面显示名。</summary>
        public string Title { get; set; }

        /// <summary>所属分组名。</summary>
        public string Category { get; set; }

        /// <summary>清理类型。</summary>
        public CleanupKind Kind { get; set; }

        /// <summary>路径模板集合，支持末段通配符 *。</summary>
        public List<string> Paths { get; set; } = new List<string>();

        /// <summary>是否默认勾选。</summary>
        public bool DefaultChecked { get; set; }

        /// <summary>是否标记为「谨慎清理」。</summary>
        public bool Cautious { get; set; }

        /// <summary>扫描到的字节数。</summary>
        public long SizeBytes { get; set; }

        /// <summary>扫描到的记录条数（注册表值个数等）。</summary>
        public int RecordCount { get; set; }

        /// <summary>是否已完成扫描。</summary>
        public bool Scanned { get; set; }

        /// <summary>鼠标悬浮提示。</summary>
        public string Hint { get; set; }

        /// <summary>右侧展示的体量文字：注册表类显示条数，其余显示体积。</summary>
        public string SizeText
        {
            get
            {
                if (!Scanned) return "";
                if (Kind == CleanupKind.RegistryMru) return RecordCount + " 条记录";
                return SizeBytes <= 0 ? "0" : DiskCleaner.FormatSize(SizeBytes);
            }
        }

        /// <summary>扫描出的明细（一个目录 / 文件 / 注册表键一条），可逐条勾选。</summary>
        public List<CleanupDetail> Details { get; } = new List<CleanupDetail>();

        /// <summary>当前勾选明细合计的字节数；未扫描时退回整项大小。</summary>
        public long SelectedBytes
        {
            get
            {
                if (Details.Count == 0) return SizeBytes;
                long sum = 0;
                foreach (CleanupDetail d in Details) if (d.Checked) sum += d.SizeBytes;
                return sum;
            }
        }

        /// <summary>是否有任意明细处于勾选状态。</summary>
        public bool AnySelected
        {
            get
            {
                if (Details.Count == 0) return SizeBytes > 0 || RecordCount > 0;
                foreach (CleanupDetail d in Details) if (d.Checked) return true;
                return false;
            }
        }
    }

    /// <summary>清理项内部的一条明细。</summary>
    public sealed class CleanupDetail
    {
        /// <summary>实际路径（注册表为键路径，回收站为占位串）。</summary>
        public string Path;

        /// <summary>该条明细的字节数。</summary>
        public long SizeBytes;

        /// <summary>注册表类：该键下的值数量。</summary>
        public int RecordCount;

        /// <summary>是否勾选（默认勾选）。</summary>
        public bool Checked = true;

        /// <summary>界面右侧体量文字。</summary>
        public string SizeText
        {
            get
            {
                if (RecordCount > 0) return RecordCount + " 条记录";
                return SizeBytes <= 0 ? "0" : DiskCleaner.FormatSize(SizeBytes);
            }
        }
    }

    /// <summary>
    /// 清理清单与扫描/清理实现。
    /// 只处理白名单内可再生成的缓存、日志、更新残留，以及显式列出的用户痕迹项；
    /// 不碰个人文件、已装软件、系统组件、浏览器历史与登录数据。
    /// </summary>
    public static class DiskCleaner
    {
        public const string CatSystem = "系统垃圾";
        public const string CatUpdate = "系统更新与补丁";
        public const string CatApp = "常用软件缓存";
        public const string CatTrail = "使用痕迹";
        public const string CatRisk = "高风险项";

        private static readonly string[] BrowserCacheSubs =
        {
            "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "Service Worker", "Shared Dictionary", "VideoDecodeStats"
        };

        private static readonly string[] BrowserRootCacheSubs =
        {
            "GrShaderCache", "ShaderCache", "GPUPersistentCache",
            "component_crx_cache", "extensions_crx_cache", "Crashpad"
        };

        /// <summary>构建清理清单，只保留本机真实存在的对象。</summary>
        public static List<CleanupItem> BuildItems()
        {
            var items = new List<CleanupItem>();

            // ---------- 系统垃圾 ----------
            Dir(items, CatSystem, "系统临时文件", true, @"%SystemRoot%\Temp");
            Dir(items, CatSystem, "用户临时文件", true, @"%TEMP%");
            Files(items, CatSystem, "内存转储文件", true, @"%SystemRoot%\MEMORY.DMP", @"%SystemRoot%\Minidump");
            Dir(items, CatSystem, "崩溃转储与错误报告", true,
                @"%LOCALAPPDATA%\CrashDumps", @"%ProgramData%\Microsoft\Windows\WER");
            Dir(items, CatSystem, "预读文件缓存", true, @"%SystemRoot%\Prefetch");
            Thumbnail(items, CatSystem, "缩略图缓存", true);
            Dir(items, CatSystem, "远程桌面缓存", true, @"%LOCALAPPDATA%\Microsoft\Terminal Server Client\Cache");
            Dir(items, CatSystem, "DirectX 着色器缓存", true, @"%LOCALAPPDATA%\D3DSCache");
            Dir(items, CatSystem, "NVIDIA 着色器缓存", true,
                @"%LOCALAPPDATA%\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\GLCache");
            Dir(items, CatSystem, "IE / WebView 缓存", true, @"%LOCALAPPDATA%\Microsoft\Windows\INetCache");
            Dir(items, CatSystem, "系统日志", true, @"%SystemRoot%\Logs");

            // ---------- 系统更新与补丁 ----------
            Dir(items, CatUpdate, "Windows 更新下载缓存", true, @"%SystemRoot%\SoftwareDistribution\Download");
            Dir(items, CatUpdate, "传递优化缓存", false, @"%ProgramData%\Microsoft\Windows\DeliveryOptimization\Cache");
            Dir(items, CatUpdate, "IIS 日志", true, @"%SystemRoot%\System32\LogFiles");

            // ---------- 常用软件缓存 ----------
            Dir(items, CatApp, "微信缓存", true,
                @"%APPDATA%\Tencent\WeChat\log", @"%APPDATA%\Tencent\WeChat\crash", @"%APPDATA%\Tencent\WeChat\bak",
                @"%APPDATA%\Tencent\xwechat\log", @"%APPDATA%\Tencent\xwechat\crashinfo");
            Dir(items, CatApp, "QQ 缓存", true,
                @"%APPDATA%\Tencent\QQ\Temp", @"%APPDATA%\Tencent\QQ\Log", @"%APPDATA%\Tencent\QQ\Crashpad");
            Dir(items, CatApp, "抖音缓存", true, @"%APPDATA%\douyin");
            Dir(items, CatApp, "飞书缓存", true, @"%APPDATA%\LarkShell");
            AddElectron(items, "豆包", @"%LOCALAPPDATA%\Doubao\User Data");
            AddElectron(items, "豆包工作", @"%LOCALAPPDATA%\DoubaoWork\User Data");
            AddElectron(items, "Trae CN", @"%APPDATA%\Trae CN");
            AddElectron(items, "VS Code", @"%APPDATA%\Code");
            AddElectron(items, "VoiceStudio", @"%APPDATA%\VoiceStudio");
            AddElectron(items, "GrClean", @"%APPDATA%\grclean");
            AddElectron(items, "SmartSub", @"%APPDATA%\smartsub");
            Dir(items, CatApp, "常用软件日志", true,
                @"%APPDATA%\Trae CN\logs", @"%APPDATA%\Code\logs",
                @"%LOCALAPPDATA%\Doubao\logs", @"%LOCALAPPDATA%\DoubaoWork\logs",
                @"%LOCALAPPDATA%\VoiceStudio\logs");
            Dir(items, CatApp, ".NET 临时程序缓存", true,
                @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\Temporary ASP.NET Files",
                @"%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\Temporary ASP.NET Files");

            AddBrowser(items, "Chrome", @"%LOCALAPPDATA%\Google\Chrome\User Data");
            AddBrowser(items, "Edge", @"%LOCALAPPDATA%\Microsoft\Edge\User Data");
            AddBrowser(items, "360 安全浏览器", @"%APPDATA%\360se6\User Data");
            AddBrowser(items, "360 极速浏览器X", @"%APPDATA%\360ChromeX\User Data");

            Dir(items, CatApp, "npm 缓存", true, @"%LOCALAPPDATA%\npm-cache");
            Dir(items, CatApp, "pnpm 缓存", true, @"%LOCALAPPDATA%\pnpm-cache");
            Dir(items, CatApp, "pip 缓存", true, @"%LOCALAPPDATA%\pip\cache");
            Dir(items, CatApp, "Flutter / Dart pub 缓存", true, @"%LOCALAPPDATA%\Pub\Cache\hosted");
            Dir(items, CatApp, "Gradle 构建缓存", true, @"%USERPROFILE%\.gradle\caches");
            AddUpdater(items);

            // ---------- 使用痕迹（默认不勾） ----------
            Dir(items, CatTrail, "最近打开文档记录", false, true, @"%APPDATA%\Microsoft\Windows\Recent");
            Dir(items, CatTrail, "任务栏跳转列表", false, true,
                @"%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations",
                @"%APPDATA%\Microsoft\Windows\Recent\CustomDestinations");
            Mru(items, CatTrail, "资源管理器地址栏历史",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths");
            Mru(items, CatTrail, "运行对话框历史",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU");
            Mru(items, CatTrail, "资源管理器搜索历史",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery");

            // ---------- 高风险项 ----------
            var recycle = new CleanupItem
            {
                Title = "回收站垃圾",
                Category = CatRisk,
                Kind = CleanupKind.RecycleBin,
                DefaultChecked = true,
                Cautious = true,
                Hint = "清空回收站中的全部文件，清空后不可恢复"
            };
            recycle.Paths.Add("RecycleBin");
            items.Add(recycle);

            Dir(items, CatRisk, "下载文件夹(Downloads)", true, true, @"%USERPROFILE%\Downloads");

            return items;
        }

        private static void Dir(List<CleanupItem> items, string category, string title, bool defaultChecked,
            params string[] paths)
        {
            Dir(items, category, title, defaultChecked, false, paths);
        }

        private static void Dir(List<CleanupItem> items, string category, string title, bool defaultChecked,
            bool cautious, params string[] paths)
        {
            var resolved = ExpandAll(paths);
            if (resolved.Count == 0) return;

            items.Add(new CleanupItem
            {
                Title = title,
                Category = category,
                Kind = CleanupKind.Directory,
                Paths = resolved,
                DefaultChecked = defaultChecked,
                Cautious = cautious,
                Hint = resolved[0] + (resolved.Count > 1 ? " 等 " + resolved.Count + " 处" : "")
            });
        }

        private static void Files(List<CleanupItem> items, string category, string title, bool defaultChecked,
            params string[] paths)
        {
            var resolved = ExpandAll(paths);
            if (resolved.Count == 0) return;

            items.Add(new CleanupItem
            {
                Title = title,
                Category = category,
                Kind = CleanupKind.File,
                Paths = resolved,
                DefaultChecked = defaultChecked,
                Hint = resolved[0]
            });
        }

        private static void Thumbnail(List<CleanupItem> items, string category, string title, bool defaultChecked)
        {
            string dir = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\Windows\Explorer");
            if (!Directory.Exists(dir)) return;
            try
            {
                if (!Directory.EnumerateFiles(dir, "thumbcache_*").Any()) return;
            }
            catch { return; }

            items.Add(new CleanupItem
            {
                Title = title,
                Category = category,
                Kind = CleanupKind.Thumbnail,
                Paths = new List<string> { dir },
                DefaultChecked = defaultChecked,
                Hint = dir
            });
        }

        private static void Mru(List<CleanupItem> items, string category, string title, string keyPath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, false))
                {
                    if (key == null || key.ValueCount == 0) return;
                }
            }
            catch { return; }

            items.Add(new CleanupItem
            {
                Title = title,
                Category = category,
                Kind = CleanupKind.RegistryMru,
                Paths = new List<string> { keyPath },
                DefaultChecked = false,
                Cautious = true,
                Hint = "HKCU\\" + keyPath + "（删除前自动备份为 .reg）"
            });
        }

        /// <summary>浏览器：聚合 profile 缓存与根级缓存为一个条目。</summary>
        private static void AddBrowser(List<CleanupItem> items, string label, string rootVar)
        {
            string root = Environment.ExpandEnvironmentVariables(rootVar);
            if (!Directory.Exists(root)) return;

            var paths = new List<string>();
            foreach (string profile in SafeDirs(root))
            {
                string name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ")) continue;
                foreach (string sub in BrowserCacheSubs)
                {
                    string p = Path.Combine(profile, sub);
                    if (Directory.Exists(p)) paths.Add(p);
                }
            }
            foreach (string sub in BrowserRootCacheSubs)
            {
                string p = Path.Combine(root, sub);
                if (Directory.Exists(p)) paths.Add(p);
            }
            if (paths.Count == 0) return;

            items.Add(new CleanupItem
            {
                Title = label + " 缓存",
                Category = CatApp,
                Kind = CleanupKind.Directory,
                Paths = paths,
                DefaultChecked = true,
                Hint = root + " 下 " + paths.Count + " 个缓存目录"
            });
        }

        /// <summary>Electron 应用：聚合缓存目录为一个条目。</summary>
        private static void AddElectron(List<CleanupItem> items, string label, string rootVar)
        {
            string root = Environment.ExpandEnvironmentVariables(rootVar);
            if (!Directory.Exists(root)) return;

            var paths = new List<string>();
            string[] profiles = SafeDirs(root)
                .Where(d => Path.GetFileName(d) == "Default" || Path.GetFileName(d).StartsWith("Profile "))
                .ToArray();

            if (profiles.Length > 0)
            {
                foreach (string profile in profiles)
                {
                    foreach (string sub in BrowserCacheSubs)
                    {
                        string p = Path.Combine(profile, sub);
                        if (Directory.Exists(p)) paths.Add(p);
                    }
                }
            }
            else
            {
                foreach (string sub in BrowserCacheSubs)
                {
                    string p = Path.Combine(root, sub);
                    if (Directory.Exists(p)) paths.Add(p);
                }
            }
            if (paths.Count == 0) return;

            items.Add(new CleanupItem
            {
                Title = label + " 缓存",
                Category = CatApp,
                Kind = CleanupKind.Directory,
                Paths = paths,
                DefaultChecked = true,
                Hint = root
            });
        }

        /// <summary>应用更新残留：所有 updater 目录的安装包与待装目录。</summary>
        private static void AddUpdater(List<CleanupItem> items)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!Directory.Exists(local)) return;

            var paths = new List<string>();
            int apps = 0;
            foreach (string dir in SafeDirs(local))
            {
                if (!Path.GetFileName(dir).ToLowerInvariant().Contains("updater")) continue;
                bool found = false;
                string installer = Path.Combine(dir, "installer.exe");
                if (File.Exists(installer)) { paths.Add(installer); found = true; }
                string pending = Path.Combine(dir, "pending");
                if (Directory.Exists(pending)) { paths.Add(pending); found = true; }
                if (found) apps++;
            }
            if (paths.Count == 0) return;

            items.Add(new CleanupItem
            {
                Title = "应用更新安装包残留（" + apps + " 个应用）",
                Category = CatApp,
                Kind = CleanupKind.Directory,
                Paths = paths,
                DefaultChecked = true,
                Hint = local + " 下各 *updater 目录"
            });
        }

        private static IEnumerable<string> SafeDirs(string path)
        {
            try { return Directory.EnumerateDirectories(path).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>展开环境变量与末段通配符，返回真实存在的路径。</summary>
        public static List<string> ExpandAll(IEnumerable<string> templates)
        {
            var result = new List<string>();
            foreach (string raw in templates)
            {
                string expanded = Environment.ExpandEnvironmentVariables(raw).TrimEnd('\\');
                foreach (string p in ExpandOne(expanded))
                {
                    if (!result.Contains(p)) result.Add(p);
                }
            }
            return result;
        }

        private static IEnumerable<string> ExpandOne(string path)
        {
            int star = path.IndexOf('*');
            if (star < 0)
            {
                yield return path;
                yield break;
            }

            int sep = path.LastIndexOf('\\', Math.Max(0, star - 1));
            string parent = sep > 0 ? path.Substring(0, sep) : null;
            string pattern = path.Substring(sep + 1);
            if (parent == null || !Directory.Exists(parent)) yield break;

            string[] dirs;
            try { dirs = Directory.GetDirectories(parent, pattern); }
            catch { yield break; }

            foreach (string d in dirs)
            {
                foreach (string p in ExpandOne(d + path.Substring(star + pattern.Length)))
                {
                    yield return p;
                }
            }
        }

        // ================= 扫描 =================

        /// <summary>扫描单条清理项；report 回调用于上报当前正在处理的目录。</summary>
        public static long ScanItem(CleanupItem item, Action<string> report, CancellationToken ct)
        {
            item.Details.Clear();
            item.RecordCount = 0;

            if (item.Kind == CleanupKind.RecycleBin)
            {
                report("回收站");
                long rb = GetRecycleBinSize();
                int count = GetRecycleBinCount();
                item.RecordCount = count;
                item.Details.Add(new CleanupDetail { Path = "RecycleBin", SizeBytes = rb, RecordCount = count });
                return rb;
            }

            if (item.Kind == CleanupKind.RegistryMru)
            {
                int count = 0;
                foreach (string key in item.Paths)
                {
                    if (ct.IsCancellationRequested) break;
                    int values = CountMruValues(key);
                    count += values;
                    item.Details.Add(new CleanupDetail { Path = key, RecordCount = values });
                }
                item.RecordCount = count;
                report("注册表 " + item.Title);
                return 0;
            }

            long total = 0;
            foreach (string path in item.Paths)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (File.Exists(path))
                    {
                        report(path);
                        long len = new FileInfo(path).Length;
                        total += len;
                        item.Details.Add(new CleanupDetail { Path = path, SizeBytes = len });
                    }
                    else if (Directory.Exists(path))
                    {
                        if (item.Kind == CleanupKind.Thumbnail)
                        {
                            // 缩略图按文件逐条列出，便于单独勾选
                            report(path);
                            try
                            {
                                foreach (string f in Directory.EnumerateFiles(path, "thumbcache_*"))
                                {
                                    if (ct.IsCancellationRequested) break;
                                    try
                                    {
                                        long len = new FileInfo(f).Length;
                                        total += len;
                                        item.Details.Add(new CleanupDetail { Path = f, SizeBytes = len });
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                        else
                        {
                            long size = ScanTree(path, item.Kind, report, ct);
                            total += size;
                            item.Details.Add(new CleanupDetail { Path = path, SizeBytes = size });
                        }
                    }
                }
                catch { }
            }
            return total;
        }

        private static long ScanTree(string root, CleanupKind kind, Action<string> report, CancellationToken ct)
        {
            if (kind == CleanupKind.Thumbnail)
            {
                report(root);
                long thumb = 0;
                try
                {
                    foreach (string f in Directory.EnumerateFiles(root, "thumbcache_*"))
                    {
                        try { thumb += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                return thumb;
            }

            long sum = 0;
            var stack = new Stack<string>();
            stack.Push(root);
            int visited = 0;
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                string dir = stack.Pop();
                if (++visited > 300000) break;
                report(dir);

                // 跳过符号链接/联接点，避免目录环导致无限递归
                try
                {
                    if (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                    {
                        var attrs = new DirectoryInfo(dir).Attributes;
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    }
                }
                catch { }

                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir))
                    {
                        if (ct.IsCancellationRequested) break;
                        try { sum += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }

                try
                {
                    foreach (string d in Directory.EnumerateDirectories(dir)) stack.Push(d);
                }
                catch { }
            }
            return sum;
        }

        // ================= 清理 =================

                        /// <summary>单次清理的结果：实际释放字节、成功删除的文件数、失败（占用 / 无权限）的文件数。</summary>
        public sealed class CleanResult
        {
            /// <summary>实际释放的字节数。</summary>
            public long Freed;

            /// <summary>成功删除的文件（注册表为值）条数。</summary>
            public int Deleted;

            /// <summary>删除失败（被占用、无权限等）的条数。</summary>
            public int Failed;
        }

        /// <summary>执行清理；被占用或无权限的文件会跳过并计入失败数。</summary>
        public static CleanResult CleanItem(CleanupItem item, Action<string> report, CancellationToken ct)
        {
            var result = new CleanResult();

            // 只处理被勾选的明细；未扫描（没有明细）时按整项处理
            List<string> targets;
            if (item.Details.Count > 0)
            {
                targets = item.Details.Where(d => d.Checked).Select(d => d.Path).ToList();
                if (targets.Count == 0) return result;
            }
            else
            {
                targets = item.Paths;
            }

            if (item.Kind == CleanupKind.RecycleBin)
            {
                report("清空回收站");
                ClearRecycleBin();
                long after = GetRecycleBinSize();
                result.Freed = Math.Max(0, item.SizeBytes - after);
                if (after > 0) result.Failed++;
                return result;
            }

            if (item.Kind == CleanupKind.RegistryMru)
            {
                foreach (string key in targets)
                {
                    if (ct.IsCancellationRequested) break;
                    string backup = BackupRegistry(key);
                    report("注册表 " + key);
                    int removed = DeleteMruValues(key);
                    result.Deleted += removed;
                    if (removed > 0 && backup == null) result.Failed++;
                    if (backup != null) MruBackupPath = backup;
                }
                return result;
            }

            foreach (string path in targets)
            {
                if (ct.IsCancellationRequested) break;
                bool isDir;
                if (!ExistsPath(path, out isDir)) continue;

                if (!isDir)
                {
                    report(path);
                    long len;
                    if (DeleteFileWin(path, out len))
                    {
                        result.Freed += len;
                        result.Deleted++;
                    }
                    else
                    {
                        result.Failed++;
                    }
                }
                else
                {
                    CleanTree(path, item.Kind, report, ct, result);
                }
            }
            return result;
        }

        /// <summary>
        /// 清空目录内容（根目录保留）：逐文件删除，跳过 junction / 符号链接，
        /// 只读文件自动去属性重试，最后自底向上删除空目录。
        /// </summary>
        private static void CleanTree(string root, CleanupKind kind, Action<string> report,
            CancellationToken ct, CleanResult result)
        {
            if (kind == CleanupKind.Thumbnail)
            {
                report(root);
                foreach (string f in ListFiles(root, "thumbcache_*"))
                {
                    if (ct.IsCancellationRequested) break;
                    long len;
                    if (DeleteFileWin(f, out len)) { result.Freed += len; result.Deleted++; }
                    else result.Failed++;
                }
                return;
            }

            var dirs = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                string dir = stack.Pop();
                report(dir);
                dirs.Add(dir);

                var files = new List<string>();
                var subs = new List<string>();
                ListEntries(dir, files, subs);

                foreach (string f in files)
                {
                    if (ct.IsCancellationRequested) break;
                    long len;
                    if (DeleteFileWin(f, out len)) { result.Freed += len; result.Deleted++; }
                    else result.Failed++;
                }

                foreach (string d in subs)
                {
                    // 不跟随 reparse point，避免顺着链接删到目录以外的数据
                    if (IsReparse(d)) continue;
                    stack.Push(d);
                }
            }

            // 自底向上删除已空子目录，根目录本身保留
            for (int i = dirs.Count - 1; i >= 0; i--)
            {
                string d = dirs[i];
                if (string.Equals(d, root, StringComparison.OrdinalIgnoreCase)) continue;
                DeleteEmptyDir(d);
            }
        }

        // ================= 路径与删除辅助（走 Win32，天然支持长路径） =================

        /// <summary>拼出支持超过 260 字符的 \\?\ 路径。</summary>
        private static string WPath(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;

            string full = path;
            try { full = Path.GetFullPath(path); } catch { }
            if (full.StartsWith(@"\\")) return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }

        /// <summary>判断路径是否存在并返回它是不是目录。</summary>
        private static bool ExistsPath(string path, out bool isDir)
        {
            isDir = false;
            uint attrs = NativeMethods.GetFileAttributesW(WPath(path));
            if (attrs == NativeMethods.INVALID_FILE_ATTRIBUTES) return false;
            isDir = (attrs & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0;
            return true;
        }

        /// <summary>是否为 junction / 符号链接等重解析点。</summary>
        private static bool IsReparse(string path)
        {
            uint attrs = NativeMethods.GetFileAttributesW(WPath(path));
            return attrs == NativeMethods.INVALID_FILE_ATTRIBUTES
                   || (attrs & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }

        /// <summary>枚举目录下的文件与子目录；长路径同样可用。</summary>
        private static void ListEntries(string dir, List<string> files, List<string> dirs)
        {
            var data = new NativeMethods.WIN32_FIND_DATA();
            string pattern = WPath(dir.TrimEnd('\\')) + "\\*";
            IntPtr h = NativeMethods.FindFirstFileW(pattern, out data);
            if (h == NativeMethods.INVALID_HANDLE_VALUE || h == IntPtr.Zero) return;

            try
            {
                do
                {
                    string name = data.cFileName;
                    if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;
                    string full = dir.TrimEnd('\\') + "\\" + name;
                    if ((data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0)
                        dirs.Add(full);
                    else
                        files.Add(full);
                }
                while (NativeMethods.FindNextFileW(h, out data));
            }
            finally
            {
                NativeMethods.FindClose(h);
            }
        }

        /// <summary>按通配符列出文件（缩略图缓存用）。</summary>
        private static string[] ListFiles(string dir, string pattern)
        {
            var files = new List<string>();
            var subs = new List<string>();

            var data = new NativeMethods.WIN32_FIND_DATA();
            string find = WPath(dir.TrimEnd('\\')) + "\\" + pattern;
            IntPtr h = NativeMethods.FindFirstFileW(find, out data);
            if (h == NativeMethods.INVALID_HANDLE_VALUE || h == IntPtr.Zero) return files.ToArray();

            try
            {
                do
                {
                    string name = data.cFileName;
                    if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;
                    if ((data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
                    files.Add(dir.TrimEnd('\\') + "\\" + name);
                }
                while (NativeMethods.FindNextFileW(h, out data));
            }
            finally
            {
                NativeMethods.FindClose(h);
            }
            return files.ToArray();
        }

        /// <summary>删除单个文件：只读先去属性再重试，成功才算释放字节。</summary>
        private static bool DeleteFileWin(string path, out long len)
        {
            len = 0;
            try { len = new FileInfo(path).Length; } catch { }

            string w = WPath(path);
            if (NativeMethods.DeleteFileW(w))
            {
                return true;
            }

            // 只读 / 隐藏 / 系统属性 → 去掉属性后再删一次
            NativeMethods.SetFileAttributesW(w, NativeMethods.FILE_ATTRIBUTE_NORMAL);
            if (NativeMethods.DeleteFileW(w))
            {
                return true;
            }

            bool isDir;
            if (!ExistsPath(path, out isDir))
            {
                len = 0;
                return true;
            }
            len = 0;
            return false;
        }

        /// <summary>删除空目录；仅用于清理后剩下的空壳。</summary>
        private static bool DeleteEmptyDir(string path)
        {
            string w = WPath(path);
            if (NativeMethods.RemoveDirectoryW(w)) return true;

            NativeMethods.SetFileAttributesW(w, NativeMethods.FILE_ATTRIBUTE_NORMAL);
            return NativeMethods.RemoveDirectoryW(w);
        }

// ================= 回收站 / 注册表 =================

        /// <summary>清理更新缓存前需要暂停的服务（Windows 更新、传递优化）。</summary>
        public static readonly string[] UpdateCacheServices = { "wuauserv", "dosvc" };

        /// <summary>
        /// 暂停更新相关服务，返回“原本在运行、已被我们停下”的服务列表，供清理后恢复。
        /// 只停正在运行的服务；停不掉也不阻断后续删除（文件会被跳过并计入失败）。
        /// </summary>
        public static List<string> StopUpdateServices()
        {
            var stopped = new List<string>();
            foreach (string name in UpdateCacheServices)
            {
                if (!WaitServiceState(name, true, 1500)) continue;
                RunSc("stop", name);
                WaitServiceState(name, false, 15000);
                stopped.Add(name);
            }
            return stopped;
        }

        /// <summary>恢复清理前停下的服务。</summary>
        public static void RestoreServices(List<string> names)
        {
            if (names == null) return;
            foreach (string name in names) RunSc("start", name);
        }

        private static bool RunSc(string verb, string name)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", verb + " " + name)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(10000);
                    return output.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) < 0;
                }
            }
            catch { return false; }
        }

        /// <summary>等待服务进入目标状态；running=true 等 RUNNING，false 等 STOPPED。</summary>
        private static bool WaitServiceState(string name, bool running, int timeoutMs)
        {
            int waited = 0;
            while (true)
            {
                string state = QueryServiceState(name);
                if (running ? state == "RUNNING" : state == "STOPPED") return true;
                if (waited >= timeoutMs) return false;
                System.Threading.Thread.Sleep(400);
                waited += 400;
            }
        }

        private static string QueryServiceState(string name)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "query " + name)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    foreach (string line in output.Split('\n'))
                    {
                        string text = line.Trim();
                        if (!text.StartsWith("STATE", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] parts = text.Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2) return parts[parts.Length - 1];
                    }
                }
            }
            catch { }
            return "";
        }


        private static long GetRecycleBinSize()
        {
            try
            {
                NativeMethods.SHQUERYRBINFO info = new NativeMethods.SHQUERYRBINFO();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(info);
                int hr = NativeMethods.SHQueryRecycleBin(string.Empty, ref info);
                if (hr == 0 && info.i64Size > 0) return info.i64Size;
            }
            catch { }
            return 0;
        }

        private static int GetRecycleBinCount()
        {
            try
            {
                NativeMethods.SHQUERYRBINFO info = new NativeMethods.SHQUERYRBINFO();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(info);
                int hr = NativeMethods.SHQueryRecycleBin(string.Empty, ref info);
                if (hr == 0) return (int)info.i64NumItems;
            }
            catch { }
            return 0;
        }

        private static void ClearRecycleBin()
        {
            try
            {
                NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                    NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
            }
            catch { }
        }

        private static int CountMruValues(string keyPath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, false))
                {
                    if (key == null) return 0;
                    return key.GetValueNames().Count(n => n.Length > 0 && !n.Equals("MRUList", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { return 0; }
        }

        /// <summary>删除前把目标键导出为 .reg 备份，失败返回 null。</summary>
        private static string BackupRegistry(string keyPath)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "清理备份");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "注册表备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".reg");

                string args = "export \"HKCU\\" + keyPath + "\" \"" + file + "\" /y";
                var psi = new System.Diagnostics.ProcessStartInfo("reg.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    proc.WaitForExit(8000);
                }
                return File.Exists(file) ? file : null;
            }
            catch { return null; }
        }

        /// <summary>最近一次注册表备份文件位置，供界面提示用户回滚。</summary>
        public static string MruBackupPath { get; private set; }

        private static int DeleteMruValues(string keyPath)
        {
            int removed = 0;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                {
                    if (key == null) return 0;
                    foreach (string name in key.GetValueNames())
                    {
                        try
                        {
                            if (name.Length == 0) continue;
                            key.DeleteValue(name, false);
                            removed++;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>格式化字节为易读文本。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 MB";
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return bytes + " B";
        }
    }

    /// <summary>回收站相关 Win32 API。</summary>
    internal static class NativeMethods
    {
        public const uint SHERB_NOCONFIRMATION = 0x00000001;
        public const uint SHERB_NOPROGRESSUI = 0x00000002;
        public const uint SHERB_NOSOUND = 0x00000004;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        // ---------- 文件删除相关（带 \\?\ 前缀时不受 260 字符限制） ----------

        public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteFileW(string lpFileName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern bool RemoveDirectoryW(string lpPathName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFileAttributesW(string lpFileName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FindClose(IntPtr hFindFile);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }


        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }
    }
}

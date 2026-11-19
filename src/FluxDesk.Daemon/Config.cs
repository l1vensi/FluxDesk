using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace WorkSpace
{
    public class DesktopInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string WallpaperPath { get; set; } = "";
        public string WallpaperEngineItem { get; set; } = "";
    }

    public class ScheduleRule
    {
        public string Id { get; set; } = "";
        public string DesktopId { get; set; } = "";
        public string Time { get; set; } = "09:00"; // HH:mm
        public List<int> Days { get; set; } = new List<int> { 1, 2, 3, 4, 5 }; // DayOfWeek (0=Sun..6=Sat)
        public bool Enabled { get; set; } = true;
    }

    public class SwitchTrigger
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string When { get; set; } = "to"; // "to" or "from"
        public string DesktopId { get; set; } = "";
        public string Command { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public class AppConfig
    {
        private static readonly object _logLock = new object();
        private static readonly Regex SafeIdRegex = new Regex(@"^[a-zA-Z0-9_-]{1,32}$", RegexOptions.Compiled);
        private const string ConfigMutexName = @"Local\FluxDesk_Config_Mutex";
        private const string OriginalDesktopRegKey = @"Software\FluxDesk";

        public int CurrentIndex { get; set; } = 0;
        public bool StartWithWindows { get; set; } = true;
        public bool RestorePrimaryOnShutdown { get; set; } = true;
        public string Language { get; set; } = "en";
        public bool AutoMovePublicDesktopItems { get; set; } = false;
        public bool IndependentWallpapers { get; set; } = false;
        public string DefaultWallpaperPath { get; set; } = "";
        public bool ContextMenuIntegration { get; set; } = false;
        public bool ShowTrayIcon { get; set; } = true;
        public bool QuickSwitcherEnabled { get; set; } = true;
        public bool ShowPreviewIcons { get; set; } = true;
        public bool WallpaperEngineEnabled { get; set; } = false;
        public string WallpaperEngineExe { get; set; } = "";
        public bool HotkeysEnabled { get; set; } = true;
        public string HotkeyModifier { get; set; } = "Alt";
        public Dictionary<string, int> DesktopHotkeys { get; set; } = new Dictionary<string, int>();
        public bool ScheduleEnabled { get; set; } = false;
        public List<ScheduleRule> Schedules { get; set; } = new List<ScheduleRule>();
        public List<SwitchTrigger> Triggers { get; set; } = new List<SwitchTrigger>();
        public List<DesktopInfo> Desktops { get; set; } = new List<DesktopInfo>();

        [JsonIgnore]
        public bool IsTransientFallback { get; set; } = false;

        public static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FluxDesk"
        );
        public static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
        public static readonly string ConfigBackupPath = Path.Combine(ConfigDir, "config.json.bak");
        public static readonly string CommandsDir = Path.Combine(ConfigDir, "commands");
        public static readonly string LogPath = Path.Combine(ConfigDir, "log.txt");
        public static readonly string DesktopsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktops"
        );

        public static bool IsSafeId(string? id) => !string.IsNullOrWhiteSpace(id) && SafeIdRegex.IsMatch(id);

        public static string GetExecutableDirectory()
        {
            string? procDir = Path.GetDirectoryName(Environment.ProcessPath);
            return !string.IsNullOrEmpty(procDir) ? procDir : AppContext.BaseDirectory;
        }

        public static void AppendLog(string message)
        {
            lock (_logLock)
            {
                try
                {
                    if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                    if (File.Exists(LogPath))
                    {
                        var info = new FileInfo(LogPath);
                        if (info.Length > 1024 * 1024)
                        {
                            File.WriteAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [Log rotated]\n");
                        }
                    }
                    File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
                }
                catch { }
            }
        }

        public static string? FindUiExecutable()
        {
            string[] candidates =
            {
                Path.Combine(GetExecutableDirectory(), "FluxDesk.exe"),
                Path.Combine(AppContext.BaseDirectory, "FluxDesk.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "FluxDesk", "FluxDesk.exe")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        public static string DetectWallpaperEnginePath()
        {
            // 1. Check active processes
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("wallpaper64");
                if (procs.Length == 0) procs = System.Diagnostics.Process.GetProcessesByName("wallpaper32");
                if (procs.Length > 0)
                {
                    string? p = null;
                    try { p = procs[0].MainModule?.FileName; } catch { }
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                }
            }
            catch { }

            // 2. Check Steam registry HKCU\Software\Valve\Steam\SteamPath
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
                {
                    string norm = steamPath.Replace('/', '\\');
                    string c64 = Path.Combine(norm, "steamapps", "common", "wallpaper_engine", "wallpaper64.exe");
                    if (File.Exists(c64)) return c64;
                    string c32 = Path.Combine(norm, "steamapps", "common", "wallpaper_engine", "wallpaper32.exe");
                    if (File.Exists(c32)) return c32;
                }
            }
            catch { }

            // 3. Check Steam App 431960 uninstall registry
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 431960");
                if (key?.GetValue("InstallLocation") is string installLoc && !string.IsNullOrWhiteSpace(installLoc))
                {
                    string c64 = Path.Combine(installLoc, "wallpaper64.exe");
                    if (File.Exists(c64)) return c64;
                    string c32 = Path.Combine(installLoc, "wallpaper32.exe");
                    if (File.Exists(c32)) return c32;
                }
            }
            catch { }

            // 4. Check common file locations
            string[] standardDrives = { "C", "D", "E", "F", "G" };
            foreach (var d in standardDrives)
            {
                string[] paths =
                {
                    $@"{d}:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
                    $@"{d}:\Program Files\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
                    $@"{d}:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
                    $@"{d}:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper64.exe",
                    $@"{d}:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper32.exe",
                    $@"{d}:\Program Files\Steam\steamapps\common\wallpaper_engine\wallpaper32.exe",
                    $@"{d}:\Steam\steamapps\common\wallpaper_engine\wallpaper32.exe",
                    $@"{d}:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper32.exe"
                };
                foreach (var p in paths)
                {
                    if (File.Exists(p)) return p;
                }
            }

            return "";
        }

        public static string GetWallpaperEngineWorkshopDir(string? exePath = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    string? dir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        // From steamapps\common\wallpaper_engine -> steamapps\workshop\content\431960
                        string candidate = Path.GetFullPath(Path.Combine(dir, "..", "..", "workshop", "content", "431960"));
                        if (Directory.Exists(candidate)) return candidate;
                    }
                }

                // Check via SteamPath
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
                {
                    string candidate = Path.Combine(steamPath.Replace('/', '\\'), "steamapps", "workshop", "content", "431960");
                    if (Directory.Exists(candidate)) return candidate;
                }

                string[] standardDrives = { "C", "D", "E", "F", "G" };
                foreach (var d in standardDrives)
                {
                    string candidate = $@"{d}:\Program Files (x86)\Steam\steamapps\workshop\content\431960";
                    if (Directory.Exists(candidate)) return candidate;
                    candidate = $@"{d}:\Steam\steamapps\workshop\content\431960";
                    if (Directory.Exists(candidate)) return candidate;
                    candidate = $@"{d}:\SteamLibrary\steamapps\workshop\content\431960";
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return "";
        }

        public static string GetUniqueDesktopPath(List<DesktopInfo> existing, string prefix, out string finalName)
        {
            var usedNames = new HashSet<string>(
                existing.Select(d => d.Name.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var usedPaths = new HashSet<string>(
                existing.Select(d => d.Path.TrimEnd('\\', '/')),
                StringComparer.OrdinalIgnoreCase);

            int num = 2;
            while (usedNames.Contains($"{prefix} {num}") ||
                   usedNames.Contains($"Стол {num}") ||
                   usedNames.Contains($"Desktop {num}"))
            {
                num++;
            }

            finalName = $"{prefix} {num}";
            string candidatePath = Path.Combine(DesktopsRoot, finalName);

            bool IsEmptyOrMissing(string p)
            {
                if (!Directory.Exists(p)) return true;
                try { return Directory.GetFileSystemEntries(p).Length == 0; }
                catch { return false; }
            }

            if (!usedPaths.Contains(candidatePath.TrimEnd('\\', '/')) && IsEmptyOrMissing(candidatePath))
            {
                return candidatePath;
            }

            int suffix = 2;
            while (true)
            {
                string altPath = Path.Combine(DesktopsRoot, $"{finalName} ({suffix})");
                if (!usedPaths.Contains(altPath.TrimEnd('\\', '/')) && IsEmptyOrMissing(altPath))
                {
                    return altPath;
                }
                suffix++;
            }
        }

        public static bool IsSafeToRecycleFolder(string folderPath, List<DesktopInfo> allDesktops, string? liveDesktopPath, int deletingIndex = -1)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath)) return false;
                if (folderPath.StartsWith(@"\\", StringComparison.Ordinal)) return false; // Reject UNC / device paths

                string fullTarget = Path.GetFullPath(folderPath).TrimEnd('\\', '/');
                string fullRoot = Path.GetFullPath(DesktopsRoot).TrimEnd('\\', '/');
                string fullRootPrefix = fullRoot + Path.DirectorySeparatorChar;

                // Must be a direct child inside %USERPROFILE%\Desktops\
                if (!fullTarget.StartsWith(fullRootPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                string? parentDir = Path.GetDirectoryName(fullTarget)?.TrimEnd('\\', '/');
                if (!string.Equals(parentDir, fullRoot, StringComparison.OrdinalIgnoreCase)) return false;

                // Never delete symlinks / directory junctions (on DesktopsRoot or target)
                if (Directory.Exists(fullRoot) && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (Directory.Exists(fullTarget) && (File.GetAttributes(fullTarget) & FileAttributes.ReparsePoint) != 0)
                    return false;

                // Never delete the live active Windows Desktop folder!
                if (!string.IsNullOrWhiteSpace(liveDesktopPath))
                {
                    string liveNorm = Path.GetFullPath(liveDesktopPath).TrimEnd('\\', '/');
                    if (PathsOverlap(fullTarget, liveNorm)) return false;
                }

                // Never overlap with Primary Desktop (index 0) or any surviving desktop
                for (int i = 0; i < allDesktops.Count; i++)
                {
                    if (i == deletingIndex && i > 0) continue;
                    if (string.IsNullOrWhiteSpace(allDesktops[i].Path)) continue;
                    string other = Path.GetFullPath(allDesktops[i].Path).TrimEnd('\\', '/');
                    if (PathsOverlap(fullTarget, other)) return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool PathsOverlap(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            if (b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static T WithConfigMutex<T>(Func<T> action)
        {
            using var mutex = new Mutex(false, ConfigMutexName);
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(2500); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    AppendLog("[Config] Mutex timeout: could not acquire lock within 2500ms.");
                    return default!;
                }
                return action();
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static AppConfig? TryReadFileUnsafe(string path)
        {
            if (!File.Exists(path)) return null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    string json = sr.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        Thread.Sleep(35);
                        continue;
                    }
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null && cfg.Desktops != null && cfg.Desktops.Count > 0)
                    {
                        EnsureDesktopIds(cfg);
                        if (string.IsNullOrEmpty(cfg.Language)) cfg.Language = "en";
                        if (cfg.CurrentIndex < 0 || cfg.CurrentIndex >= cfg.Desktops.Count)
                            cfg.CurrentIndex = 0;
                        return cfg;
                    }
                }
                catch (IOException) { Thread.Sleep(35); }
                catch (UnauthorizedAccessException) { Thread.Sleep(35); }
                catch (JsonException) { Thread.Sleep(35); }
                catch { return null; }
            }
            return null;
        }

        public static AppConfig? TryLoadExisting()
        {
            return WithConfigMutex(() =>
            {
                var cfg = TryReadFileUnsafe(ConfigPath);
                if (cfg != null) return cfg;
                if (File.Exists(ConfigBackupPath))
                {
                    return TryReadFileUnsafe(ConfigBackupPath);
                }
                return null;
            });
        }

        public static AppConfig Load()
        {
            var existing = TryLoadExisting();
            if (existing != null)
            {
                RecordOriginalPrimaryDesktop(existing.Desktops[0].Path);
                bool idsAdded = EnsureDesktopIds(existing);
                if (idsAdded) existing.Save();
                return existing;
            }

            bool fileAlreadyExists = File.Exists(ConfigPath);
            var defaultConfig = new AppConfig { IsTransientFallback = fileAlreadyExists };
            EnsurePrimaryDesktop(defaultConfig);
            EnsureDesktopIds(defaultConfig);

            if (!fileAlreadyExists)
            {
                defaultConfig.Save();
            }
            else
            {
                AppendLog("[Config] WARNING: config.json exists but could not be read; running with IsTransientFallback=true (will NOT overwrite disk).");
            }
            return defaultConfig;
        }

        private static void RecordOriginalPrimaryDesktop(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full = Path.GetFullPath(path).TrimEnd('\\', '/');
                string rootPrefix = Path.GetFullPath(DesktopsRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return;

                using var key = Registry.CurrentUser.CreateSubKey(OriginalDesktopRegKey);
                if (key != null && string.IsNullOrWhiteSpace(key.GetValue("OriginalDesktopPath") as string))
                {
                    key.SetValue("OriginalDesktopPath", full);
                }
            }
            catch { }
        }

        private static string ResolveSafePrimaryDesktopPath()
        {
            string rootPrefix = Path.GetFullPath(DesktopsRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

            // 1. Check live Windows Desktop folder first (if NOT redirected inside DesktopsRoot)
            string live = ShellHelper.GetLiveDesktopFolder();
            if (!string.IsNullOrWhiteSpace(live) && Directory.Exists(live))
            {
                string fullLive = Path.GetFullPath(live).TrimEnd('\\', '/');
                if (!fullLive.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    RecordOriginalPrimaryDesktop(fullLive);
                    return fullLive;
                }
            }

            // 2. Recover saved OriginalDesktopPath from Registry if Windows Desktop is currently redirected
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(OriginalDesktopRegKey);
                if (key?.GetValue("OriginalDesktopPath") is string saved &&
                    !string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                {
                    return saved;
                }
            }
            catch { }

            // 3. Fallback to standard OneDrive or UserProfile\Desktop
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] fallbacks =
            {
                Path.Combine(userProfile, "OneDrive", "Desktop"),
                Path.Combine(userProfile, "Desktop")
            };
            foreach (var fb in fallbacks)
            {
                if (Directory.Exists(fb)) return fb;
            }
            return Path.Combine(userProfile, "Desktop");
        }

        public static bool EnsureDesktopIds(AppConfig cfg)
        {
            bool changed = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < cfg.Desktops.Count; i++)
            {
                string id = cfg.Desktops[i].Id;
                if (!IsSafeId(id) || !seen.Add(id))
                {
                    string newId = (i == 0 && !seen.Contains("primary"))
                        ? "primary"
                        : Guid.NewGuid().ToString("N")[..10];
                    cfg.Desktops[i].Id = newId;
                    seen.Add(newId);
                    changed = true;
                }
            }
            return changed;
        }

        private static void EnsurePrimaryDesktop(AppConfig cfg)
        {
            if (cfg.Desktops.Count == 0)
            {
                string primaryDesktop = ResolveSafePrimaryDesktopPath();
                cfg.Desktops.Add(new DesktopInfo
                {
                    Id = "primary",
                    Name = "Desktop 1",
                    Path = primaryDesktop
                });

                string path2 = GetUniqueDesktopPath(cfg.Desktops, "Desktop", out string name2);
                cfg.Desktops.Add(new DesktopInfo
                {
                    Id = Guid.NewGuid().ToString("N")[..10],
                    Name = name2,
                    Path = path2
                });

                string path3 = GetUniqueDesktopPath(cfg.Desktops, "Desktop", out string name3);
                cfg.Desktops.Add(new DesktopInfo
                {
                    Id = Guid.NewGuid().ToString("N")[..10],
                    Name = name3,
                    Path = path3
                });

                try { Directory.CreateDirectory(path2); } catch { }
                try { Directory.CreateDirectory(path3); } catch { }
            }
        }

        public string? Save()
        {
            if (IsTransientFallback)
            {
                AppendLog("[Config] Save skipped: IsTransientFallback=true (protecting existing config.json).");
                return null;
            }

            return WithConfigMutex(() => SaveUnsafe(this));
        }

        /// <summary>
        /// Atomic cross-process read-modify-write under ConfigMutex so Daemon and UI never overwrite each other's changes.
        /// Returns the saved JSON snapshot (or null on failure).
        /// </summary>
        public static string? UpdateAndSave(Action<AppConfig> mutator, AppConfig? fallbackInMemory = null)
        {
            if (fallbackInMemory?.IsTransientFallback == true) return null;

            return WithConfigMutex(() =>
            {
                var latest = TryReadFileUnsafe(ConfigPath) ?? fallbackInMemory;
                if (latest == null || latest.IsTransientFallback) return null;

                mutator(latest);
                if (latest.CurrentIndex < 0 || latest.CurrentIndex >= latest.Desktops.Count)
                    latest.CurrentIndex = 0;

                return SaveUnsafe(latest);
            });
        }

        private static string? SaveUnsafe(AppConfig cfg)
        {
            string tempPath = ConfigPath + $".{Environment.ProcessId}.{Thread.CurrentThread.ManagedThreadId}.tmp";
            bool moved = false;
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    try
                    {
                        File.WriteAllText(tempPath, json);
                        File.Move(tempPath, ConfigPath, overwrite: true);
                        moved = true;

                        try { File.WriteAllText(ConfigBackupPath, json); } catch { }
                        return json;
                    }
                    catch (IOException) { Thread.Sleep(30); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(30); }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Config] Save error: {ex.Message}");
            }
            finally
            {
                if (!moved && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
            return null;
        }
    }
}

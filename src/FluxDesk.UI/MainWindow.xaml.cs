using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskSpace
{
    public class DesktopInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string WallpaperPath { get; set; } = "";
    }

    public class ScheduleRule
    {
        public string Id { get; set; } = "";
        public string DesktopId { get; set; } = "";
        public string Time { get; set; } = "09:00";
        public List<int> Days { get; set; } = new List<int> { 1, 2, 3, 4, 5 };
        public bool Enabled { get; set; } = true;
    }

    public class SwitchTrigger
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string When { get; set; } = "to";
        public string DesktopId { get; set; } = "";
        public string Command { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public class AppConfig
    {
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
        public bool HotkeysEnabled { get; set; } = true;
        public string HotkeyModifier { get; set; } = "Alt";
        public Dictionary<string, int> DesktopHotkeys { get; set; } = new Dictionary<string, int>();
        public bool ScheduleEnabled { get; set; } = false;
        public List<ScheduleRule> Schedules { get; set; } = new List<ScheduleRule>();
        public List<SwitchTrigger> Triggers { get; set; } = new List<SwitchTrigger>();
        public List<DesktopInfo> Desktops { get; set; } = new List<DesktopInfo>();

        [JsonIgnore]
        public bool IsTransientFallback { get; set; } = false;

        public static string ConfigDir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FluxDesk"
        );
        public static string ConfigPath => System.IO.Path.Combine(ConfigDir, "config.json");
        public static string ConfigBackupPath => System.IO.Path.Combine(ConfigDir, "config.json.bak");
        public static string CommandsDir => System.IO.Path.Combine(ConfigDir, "commands");
        public static string DesktopsRoot => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktops"
        );

        public static bool IsSafeId(string? id) => !string.IsNullOrWhiteSpace(id) && SafeIdRegex.IsMatch(id);

        public static string GetUniqueDesktopPath(List<DesktopInfo> existing, string prefix, out string finalName)
        {
            var usedNames = new HashSet<string>(existing.Select(d => d.Name.Trim()), StringComparer.OrdinalIgnoreCase);
            var usedPaths = new HashSet<string>(existing.Select(d => d.Path.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);

            int num = 2;
            while (usedNames.Contains($"{prefix} {num}") ||
                   usedNames.Contains($"Стол {num}") ||
                   usedNames.Contains($"Desktop {num}"))
            {
                num++;
            }

            finalName = $"{prefix} {num}";
            string candidatePath = System.IO.Path.Combine(DesktopsRoot, finalName);

            bool IsEmptyOrMissing(string p)
            {
                if (!Directory.Exists(p)) return true;
                try { return Directory.GetFileSystemEntries(p).Length == 0; }
                catch { return false; }
            }

            if (!usedPaths.Contains(candidatePath.TrimEnd('\\', '/')) && IsEmptyOrMissing(candidatePath))
                return candidatePath;

            int suffix = 2;
            while (true)
            {
                string altPath = System.IO.Path.Combine(DesktopsRoot, $"{finalName} ({suffix})");
                if (!usedPaths.Contains(altPath.TrimEnd('\\', '/')) && IsEmptyOrMissing(altPath))
                    return altPath;
                suffix++;
            }
        }

        private static T WithConfigMutex<T>(Func<T> action)
        {
            using var mutex = new Mutex(false, ConfigMutexName);
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(2500); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return default!;
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
                        if (cfg.CurrentIndex < 0 || cfg.CurrentIndex >= cfg.Desktops.Count) cfg.CurrentIndex = 0;
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

        public static string ResolveSafePrimaryDesktopPath()
        {
            string rootPrefix = System.IO.Path.GetFullPath(DesktopsRoot).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            string live = MainWindow.GetLiveDesktopFolder();
            if (!string.IsNullOrWhiteSpace(live) && Directory.Exists(live))
            {
                string fullLive = System.IO.Path.GetFullPath(live).TrimEnd('\\', '/');
                if (!fullLive.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    return fullLive;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(OriginalDesktopRegKey);
                if (key?.GetValue("OriginalDesktopPath") is string saved &&
                    !string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                    return saved;
            }
            catch { }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] fallbacks =
            {
                System.IO.Path.Combine(userProfile, "OneDrive", "Desktop"),
                System.IO.Path.Combine(userProfile, "Desktop")
            };
            foreach (var fb in fallbacks)
            {
                if (Directory.Exists(fb)) return fb;
            }
            return System.IO.Path.Combine(userProfile, "Desktop");
        }

        public static AppConfig Load()
        {
            var existing = TryLoadExisting();
            if (existing != null) return existing;

            bool fileAlreadyExists = File.Exists(ConfigPath);
            string primaryDesktop = ResolveSafePrimaryDesktopPath();

            var def = new AppConfig
            {
                Language = "en",
                IsTransientFallback = fileAlreadyExists
            };
            def.Desktops.Add(new DesktopInfo { Id = "primary", Name = "Desktop 1", Path = primaryDesktop });
            string path2 = GetUniqueDesktopPath(def.Desktops, "Desktop", out string name2);
            def.Desktops.Add(new DesktopInfo { Id = Guid.NewGuid().ToString("N")[..10], Name = name2, Path = path2 });
            string path3 = GetUniqueDesktopPath(def.Desktops, "Desktop", out string name3);
            def.Desktops.Add(new DesktopInfo { Id = Guid.NewGuid().ToString("N")[..10], Name = name3, Path = path3 });

            if (!fileAlreadyExists)
            {
                try { Directory.CreateDirectory(path2); } catch { }
                try { Directory.CreateDirectory(path3); } catch { }
                def.Save();
            }
            return def;
        }

        public void Save()
        {
            if (IsTransientFallback) return;

            WithConfigMutex(() =>
            {
                string tempPath = ConfigPath + $".{Environment.ProcessId}.{Thread.CurrentThread.ManagedThreadId}.tmp";
                bool moved = false;
                try
                {
                    if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                    string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        try
                        {
                            File.WriteAllText(tempPath, json);
                            File.Move(tempPath, ConfigPath, overwrite: true);
                            moved = true;
                            try { File.WriteAllText(ConfigBackupPath, json); } catch { }
                            return true;
                        }
                        catch (IOException) { Thread.Sleep(30); }
                        catch (UnauthorizedAccessException) { Thread.Sleep(30); }
                    }
                }
                catch { }
                finally
                {
                    if (!moved && File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
                return false;
            });
        }
    }

    public partial class MainWindow : Window
    {
        private const string SuspendHotkeysEventName = @"Local\FluxDesk_SuspendHotkeys";
        private static readonly Guid FOLDERID_Desktop = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

        private AppConfig _config;
        private bool _isInitializing = true;
        private volatile bool _isClosing = false;
        private bool _pendingConfigReload = false;
        private ContextMenu? _openCardContextMenu = null;
        private FileSystemWatcher? _configWatcher;
        private FileSystemWatcher? _previewWatcher;
        private readonly DispatcherTimer _configReloadTimer;
        private readonly DispatcherTimer _previewReloadTimer;
        private DateTime _lastPreviewRequestUtc = DateTime.MinValue;
        private EventWaitHandle? _suspendHotkeysEvent;

        private readonly Dictionary<string, (long ticks, long len, BitmapImage bmp)> _previewBitmapCache = new Dictionary<string, (long, long, BitmapImage)>(StringComparer.OrdinalIgnoreCase);
        private bool _isSelectionMode = false;
        private readonly HashSet<string> _selectedDesktopIds = new HashSet<string>();
        private List<DesktopInfo> _pendingDeleteTargets = new List<DesktopInfo>();
        private string? _recordingDesktopId = null;
        private string? _pendingConflictTargetId = null;
        private int _pendingConflictVk = 0;
        private List<string> _pendingConflictOtherIds = new List<string>();
        private string? _renamingDesktopId = null;

        #region Win32 Interop (Shell, Recycle Bin, Maximize WorkArea)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHSetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, string pszPath);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, System.Text.StringBuilder pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_WANTNUKEWARNING = 0x4000;
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const int SHCNE_UPDATEDIR = 0x00001000;
        private const int SHCNF_IDLIST = 0x0000;
        private const int SHCNF_PATHW = 0x0005;
        private const uint SPI_GETDESKWALLPAPER = 0x0073;
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        public static string GetLiveDesktopFolder()
        {
            IntPtr pPath = IntPtr.Zero;
            try
            {
                var guid = FOLDERID_Desktop;
                if (SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out pPath) == 0 && pPath != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(pPath) ?? "";
                }
            }
            catch { }
            finally
            {
                if (pPath != IntPtr.Zero) Marshal.FreeCoTaskMem(pPath);
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        private static bool RestoreDesktopFolderDirect(string targetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath) || !System.IO.Path.IsPathFullyQualified(targetPath))
                    return false;
                if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);

                var guid = FOLDERID_Desktop;
                int hr = SHSetKnownFolderPath(ref guid, 0, IntPtr.Zero, targetPath);
                if (hr < 0) return false;

                IntPtr pPath = Marshal.StringToHGlobalUni(targetPath);
                try { SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, pPath, IntPtr.Zero); }
                finally { Marshal.FreeHGlobal(pPath); }
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch { return false; }
        }

        private static string GetCurrentWindowsWallpaper()
        {
            try
            {
                var sb = new System.Text.StringBuilder(512);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
                    return sb.ToString();
            }
            catch { }
            return "";
        }

        private static void SetWindowsWallpaperDirect(string imagePath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                {
                    SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                }
            }
            catch { }
        }

        private static bool SendFolderToRecycleBin(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return false;
                string norm = System.IO.Path.GetFullPath(folderPath).TrimEnd('\\', '/');
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = norm + "\0\0",
                    // FOF_WANTNUKEWARNING ensures Windows NEVER silently permanently deletes folders too large for Recycle Bin!
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING | FOF_NOCONFIRMATION | FOF_SILENT)
                };
                int res = SHFileOperation(ref op);
                return res == 0 && !op.fAnyOperationsAborted;
            }
            catch { return false; }
        }

        private static bool PathsOverlap(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith(b + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            if (b.StartsWith(a + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool IsSafeToRecycleFolder(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath)) return false;
                if (folderPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;

                string fullTarget = System.IO.Path.GetFullPath(folderPath).TrimEnd('\\', '/');
                string fullRoot = System.IO.Path.GetFullPath(AppConfig.DesktopsRoot).TrimEnd('\\', '/');
                string fullRootPrefix = fullRoot + System.IO.Path.DirectorySeparatorChar;

                if (!fullTarget.StartsWith(fullRootPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                string? parentDir = System.IO.Path.GetDirectoryName(fullTarget)?.TrimEnd('\\', '/');
                if (!string.Equals(parentDir, fullRoot, StringComparison.OrdinalIgnoreCase)) return false;

                if (Directory.Exists(fullRoot) && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (Directory.Exists(fullTarget) && (File.GetAttributes(fullTarget) & FileAttributes.ReparsePoint) != 0)
                    return false;

                // Never delete the live active Windows Desktop folder
                string liveDesktop = GetLiveDesktopFolder();
                if (!string.IsNullOrWhiteSpace(liveDesktop))
                {
                    string liveNorm = System.IO.Path.GetFullPath(liveDesktop).TrimEnd('\\', '/');
                    if (PathsOverlap(fullTarget, liveNorm)) return false;
                }

                // Never overlap with Primary Desktop or any surviving desktop
                foreach (var d in _config.Desktops)
                {
                    if (string.IsNullOrWhiteSpace(d.Path)) continue;
                    string other = System.IO.Path.GetFullPath(d.Path).TrimEnd('\\', '/');
                    if (PathsOverlap(fullTarget, other)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref mi))
                    {
                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        mmi.ptMaxPosition.x = Math.Abs(mi.rcWork.left - mi.rcMonitor.left);
                        mmi.ptMaxPosition.y = Math.Abs(mi.rcWork.top - mi.rcMonitor.top);
                        mmi.ptMaxSize.x = Math.Abs(mi.rcWork.right - mi.rcWork.left);
                        mmi.ptMaxSize.y = Math.Abs(mi.rcWork.bottom - mi.rcWork.top);
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                }
            }
            else if (msg == App.WM_FLUXDESK_OPEN_UNINSTALL)
            {
                // Another instance asked us to open the uninstall dialog (e.g. launched from Windows Settings)
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    BtnOpenUninstall_Click(this, new RoutedEventArgs());
                }));
                handled = true;
            }
            return IntPtr.Zero;
        }

        #endregion

        public MainWindow()
        {
            InitializeComponent();
            _config = AppConfig.Load();

            try
            {
                _suspendHotkeysEvent = new EventWaitHandle(false, EventResetMode.ManualReset, SuspendHotkeysEventName);
            }
            catch { }

            SyncControlsFromConfig();

            _configReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _configReloadTimer.Tick += (s, e) =>
            {
                _configReloadTimer.Stop();
                if (!_isClosing) RefreshConfigIfChanged();
            };

            _previewReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _previewReloadTimer.Tick += (s, e) =>
            {
                _previewReloadTimer.Stop();
                if (_isClosing) return;
                if (!IsAnyModalOpen() && _openCardContextMenu == null)
                {
                    PopulateDesktopCards();
                }
            };

            ApplyLanguage(_config.Language);
            RefreshAllDynamicViews();

            SetupConfigWatcher();
            SetupPreviewWatcher();
            EnsureBackgroundDaemonRunning();
            RequestPreviewRefresh();

            Activated += (s, e) =>
            {
                if (_isClosing) return;
                RefreshConfigIfChanged();
                RequestPreviewRefresh();
            };
            Deactivated += (s, e) =>
            {
                // Always release hotkey recording lock if user switches away from FluxDesk!
                CancelHotkeyRecording(refreshList: true);
            };
            Loaded += (s, e) =>
            {
                // Launched from Windows "Apps & Features" uninstall button → open dialog immediately
                if (OpenUninstallDialogOnLoad)
                {
                    OpenUninstallDialogOnLoad = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                        new Action(() => BtnOpenUninstall_Click(this, new RoutedEventArgs())));
                }
            };
            Closing += (s, e) =>
            {
                _isClosing = true;
                CancelHotkeyRecording(refreshList: false);
                StopSmoothScroll();
                _configReloadTimer.Stop();
                _previewReloadTimer.Stop();
                if (_configWatcher != null) { _configWatcher.EnableRaisingEvents = false; _configWatcher.Dispose(); }
                if (_previewWatcher != null) { _previewWatcher.EnableRaisingEvents = false; _previewWatcher.Dispose(); }
                try { _suspendHotkeysEvent?.Reset(); _suspendHotkeysEvent?.Dispose(); } catch { }
            };
        }

        /// <summary>Set by App.xaml.cs when launched with --uninstall flag.</summary>
        internal bool OpenUninstallDialogOnLoad { get; set; }

        private bool IsAnyModalOpen()
        {
            return RenameModalOverlay.Visibility == Visibility.Visible ||
                   DeleteModalOverlay.Visibility == Visibility.Visible ||
                   ConflictModalOverlay.Visibility == Visibility.Visible ||
                   UninstallModalOverlay.Visibility == Visibility.Visible;
        }

        private void SetModalState(Grid overlay, bool open, Control? focusTarget = null)
        {
            if (open)
            {
                CancelHotkeyRecording(refreshList: true);
                MainContentGrid.IsEnabled = false;
                overlay.Visibility = Visibility.Visible;
                if (focusTarget != null)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        focusTarget.Focus();
                        Keyboard.Focus(focusTarget);
                        if (focusTarget is TextBox tb) tb.SelectAll();
                    }));
                }
            }
            else
            {
                overlay.Visibility = Visibility.Collapsed;
                if (!IsAnyModalOpen())
                {
                    MainContentGrid.IsEnabled = true;
                    if (_pendingConfigReload)
                    {
                        _pendingConfigReload = false;
                        RefreshConfigIfChanged();
                    }
                }
            }
        }

        private void SetHotkeyRecording(string? desktopId)
        {
            _recordingDesktopId = desktopId;
            try
            {
                if (desktopId != null) _suspendHotkeysEvent?.Set();
                else _suspendHotkeysEvent?.Reset();
            }
            catch { }
        }

        private void CancelHotkeyRecording(bool refreshList)
        {
            if (_recordingDesktopId == null) return;
            SetHotkeyRecording(null);
            if (refreshList && !_isClosing)
            {
                PopulateHotkeysList();
                if (_pendingConfigReload && !IsAnyModalOpen())
                {
                    _pendingConfigReload = false;
                    RefreshConfigIfChanged();
                }
            }
        }

        private void SyncControlsFromConfig()
        {
            _isInitializing = true;
            CmbLanguage.SelectedIndex = (_config.Language == "ru") ? 1 : 0;
            ChkAutoMovePublic.IsChecked = _config.AutoMovePublicDesktopItems;
            ChkStartWithWindows.IsChecked = _config.StartWithWindows;
            ChkRestorePrimaryOnShutdown.IsChecked = _config.RestorePrimaryOnShutdown;
            ChkIndependentWallpapers.IsChecked = _config.IndependentWallpapers;
            ChkContextMenu.IsChecked = _config.ContextMenuIntegration;
            ChkShowTrayIcon.IsChecked = _config.ShowTrayIcon;
            ChkQuickSwitcher.IsChecked = _config.QuickSwitcherEnabled;
            ChkHotkeysEnabled.IsChecked = _config.HotkeysEnabled;

            CmbHotkeyModifier.SelectedIndex = (_config.HotkeyModifier ?? "Alt") switch
            {
                "Ctrl" => 1,
                "Win" => 2,
                _ => 0
            };
            _isInitializing = false;
        }

        private void RefreshAllDynamicViews()
        {
            PopulateDesktopCards();
            PopulateHotkeysList();
        }

        private string? FindDaemonExecutable()
        {
            string procDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string[] candidates =
            {
                System.IO.Path.Combine(procDir, "FluxDeskDaemon.exe"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "FluxDeskDaemon.exe"),
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "FluxDesk", "FluxDeskDaemon.exe")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static bool IsDaemonRunning()
        {
            try
            {
                if (Mutex.TryOpenExisting(@"Local\FluxDesk_Daemon_SingleInstance", out var existing))
                {
                    existing.Dispose();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void EnsureBackgroundDaemonRunning()
        {
            try
            {
                if (IsDaemonRunning())
                {
                    DaemonWarningBanner.Visibility = Visibility.Collapsed;
                    return;
                }

                string? daemonPath = FindDaemonExecutable();
                if (!string.IsNullOrEmpty(daemonPath))
                {
                    DaemonWarningBanner.Visibility = Visibility.Collapsed;
                    using var p = Process.Start(new ProcessStartInfo(daemonPath) { UseShellExecute = true });
                }
                else
                {
                    DaemonWarningBanner.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private void SetupConfigWatcher()
        {
            try
            {
                if (_configWatcher != null) { _configWatcher.EnableRaisingEvents = false; _configWatcher.Dispose(); }
                string dir = AppConfig.ConfigDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                _configWatcher = new FileSystemWatcher(dir, "config.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    InternalBufferSize = 32768,
                    EnableRaisingEvents = true
                };

                void ScheduleRefresh()
                {
                    if (_isClosing) return;
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_isClosing) return;
                            _configReloadTimer.Stop();
                            _configReloadTimer.Start();
                        }));
                    }
                    catch { }
                }

                _configWatcher.Changed += (s, e) => ScheduleRefresh();
                _configWatcher.Created += (s, e) => ScheduleRefresh();
                _configWatcher.Renamed += (s, e) => ScheduleRefresh();
                _configWatcher.Error += (s, e) =>
                {
                    if (_isClosing) return;
                    Dispatcher.BeginInvoke(new Action(() => { if (!_isClosing) { SetupConfigWatcher(); RefreshConfigIfChanged(); } }));
                };
            }
            catch { }
        }

        private void SetupPreviewWatcher()
        {
            try
            {
                if (_previewWatcher != null) { _previewWatcher.EnableRaisingEvents = false; _previewWatcher.Dispose(); }
                string previewsDir = System.IO.Path.Combine(AppConfig.ConfigDir, "previews");
                if (!Directory.Exists(previewsDir)) Directory.CreateDirectory(previewsDir);

                _previewWatcher = new FileSystemWatcher(previewsDir, "preview_*.png")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    InternalBufferSize = 32768,
                    EnableRaisingEvents = true
                };

                void SchedulePreviewRefresh()
                {
                    if (_isClosing) return;
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_isClosing) return;
                            _previewReloadTimer.Stop();
                            _previewReloadTimer.Start();
                        }));
                    }
                    catch { }
                }

                _previewWatcher.Changed += (s, e) => SchedulePreviewRefresh();
                _previewWatcher.Created += (s, e) => SchedulePreviewRefresh();
                _previewWatcher.Renamed += (s, e) => SchedulePreviewRefresh();
                _previewWatcher.Error += (s, e) =>
                {
                    if (_isClosing) return;
                    Dispatcher.BeginInvoke(new Action(() => { if (!_isClosing) SetupPreviewWatcher(); }));
                };
            }
            catch { }
        }

        private void SendIpcCommand(string commandText)
        {
            try
            {
                string cmdDir = AppConfig.CommandsDir;
                if (!Directory.Exists(cmdDir)) Directory.CreateDirectory(cmdDir);
                string baseName = $"{DateTime.UtcNow.Ticks:D20}_{Guid.NewGuid():N}";
                string tmpPath = System.IO.Path.Combine(cmdDir, baseName + ".tmp");
                string finalPath = System.IO.Path.Combine(cmdDir, baseName + ".cmd");
                File.WriteAllText(tmpPath, commandText);
                File.Move(tmpPath, finalPath, overwrite: true);
            }
            catch { }
        }

        private void RequestPreviewRefresh()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPreviewRequestUtc).TotalSeconds < 2.5) return;
            _lastPreviewRequestUtc = now;
            SendIpcCommand("refresh_previews");
        }

        private BitmapImage? TryLoadPreviewBitmap(string desktopId)
        {
            try
            {
                if (!AppConfig.IsSafeId(desktopId)) return null;
                string previewFile = System.IO.Path.Combine(AppConfig.ConfigDir, "previews", $"preview_{desktopId}.png");
                if (!File.Exists(previewFile)) return null;

                var fi = new FileInfo(previewFile);
                long ticks = fi.LastWriteTimeUtc.Ticks;
                long len = fi.Length;
                if (len <= 0) return null;

                if (_previewBitmapCache.TryGetValue(desktopId, out var cached) &&
                    cached.ticks == ticks && cached.len == len)
                {
                    return cached.bmp;
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(previewFile);
                        if (bytes.Length == 0) return null;

                        var bmp = new BitmapImage();
                        using var ms = new MemoryStream(bytes);
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();

                        _previewBitmapCache[desktopId] = (ticks, len, bmp);
                        return bmp;
                    }
                    catch (IOException) { Thread.Sleep(15); }
                }
            }
            catch { }
            return null;
        }

        private void RefreshConfigIfChanged()
        {
            try
            {
                if (IsAnyModalOpen() || _recordingDesktopId != null || _openCardContextMenu != null)
                {
                    _pendingConfigReload = true;
                    return;
                }

                var fresh = AppConfig.TryLoadExisting();
                if (fresh == null) return;

                string oldSnap = JsonSerializer.Serialize(_config);
                string newSnap = JsonSerializer.Serialize(fresh);
                if (oldSnap != newSnap)
                {
                    bool langChanged = !string.Equals(_config.Language, fresh.Language, StringComparison.OrdinalIgnoreCase);
                    _config = fresh;

                    // Keep selection mode consistent if desktops changed externally
                    if (_isSelectionMode)
                    {
                        var validIds = new HashSet<string>(_config.Desktops.Skip(1).Select(d => d.Id));
                        _selectedDesktopIds.IntersectWith(validIds);
                        if (_config.Desktops.Count <= 1)
                        {
                            _isSelectionMode = false;
                            _selectedDesktopIds.Clear();
                            BulkDesktopActions.Visibility = Visibility.Collapsed;
                            NormalDesktopActions.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            UpdateBulkDeleteButtonText();
                        }
                    }

                    SyncControlsFromConfig();
                    if (langChanged) ApplyLanguage(_config.Language);
                    RefreshAllDynamicViews();
                }
            }
            catch { }
        }

        private void ChkAutoMovePublic_Click(object sender, RoutedEventArgs e)
        {
            _config.AutoMovePublicDesktopItems = (ChkAutoMovePublic.IsChecked == true);
            _config.Save();
        }

        private void ChkStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            _config.StartWithWindows = (ChkStartWithWindows.IsChecked == true);
            _config.Save();
            // Also apply directly in case Daemon isn't currently running
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    string? daemonExe = FindDaemonExecutable();
                    if (_config.StartWithWindows && !string.IsNullOrEmpty(daemonExe))
                        key.SetValue("FluxDesk", $"\"{daemonExe}\"");
                    else
                        key.DeleteValue("FluxDesk", false);
                }
            }
            catch { }
        }

        private void ChkRestorePrimaryOnShutdown_Click(object sender, RoutedEventArgs e)
        {
            _config.RestorePrimaryOnShutdown = (ChkRestorePrimaryOnShutdown.IsChecked == true);
            _config.Save();
        }

        private void ChkIndependentWallpapers_Click(object sender, RoutedEventArgs e)
        {
            _config.IndependentWallpapers = (ChkIndependentWallpapers.IsChecked == true);
            if (_config.IndependentWallpapers && string.IsNullOrWhiteSpace(_config.DefaultWallpaperPath))
            {
                string wp = GetCurrentWindowsWallpaper();
                if (!string.IsNullOrWhiteSpace(wp) && File.Exists(wp)) _config.DefaultWallpaperPath = wp;
            }
            _config.Save();
            PopulateDesktopCards();
        }

        private void ChkContextMenu_Click(object sender, RoutedEventArgs e)
        {
            _config.ContextMenuIntegration = (ChkContextMenu.IsChecked == true);
            _config.Save();
        }

        private void ChkShowTrayIcon_Click(object sender, RoutedEventArgs e)
        {
            _config.ShowTrayIcon = (ChkShowTrayIcon.IsChecked == true);
            _config.Save();
        }

        private void ChkQuickSwitcher_Click(object sender, RoutedEventArgs e)
        {
            _config.QuickSwitcherEnabled = (ChkQuickSwitcher.IsChecked == true);
            _config.Save();
            PopulateHotkeysList();
        }

        private void BtnBrowseDefault_Click(object sender, RoutedEventArgs e)
        {
            bool isRu = (_config.Language == "ru");
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = isRu ? "Выберите основную папку рабочего стола" : "Select default desktop folder",
                InitialDirectory = (_config.Desktops.Count > 0 && Directory.Exists(_config.Desktops[0].Path))
                    ? _config.Desktops[0].Path
                    : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName) && _config.Desktops.Count > 0)
            {
                string picked = System.IO.Path.GetFullPath(dlg.FolderName).TrimEnd('\\', '/');
                string rootPrefix = System.IO.Path.GetFullPath(AppConfig.DesktopsRoot).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;

                // Prevent selecting drive roots, secondary DesktopsRoot folders, or another desktop's folder
                if (System.IO.Path.GetPathRoot(picked)?.TrimEnd('\\', '/').Equals(picked, StringComparison.OrdinalIgnoreCase) == true)
                    return;
                if (picked.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    return;
                for (int i = 1; i < _config.Desktops.Count; i++)
                {
                    string other = System.IO.Path.GetFullPath(_config.Desktops[i].Path).TrimEnd('\\', '/');
                    if (PathsOverlap(picked, other)) return;
                }

                _config.Desktops[0].Path = picked;
                _config.Save();
                ApplyLanguage(_config.Language);
                PopulateDesktopCards();
            }
        }

        private void ChkHotkeysEnabled_Click(object sender, RoutedEventArgs e)
        {
            CancelHotkeyRecording(refreshList: false);
            _config.HotkeysEnabled = (ChkHotkeysEnabled.IsChecked == true);
            _config.Save();
            PopulateHotkeysList();
        }

        private void CmbHotkeyModifier_DropDownOpened(object? sender, EventArgs e)
        {
            CancelHotkeyRecording(refreshList: true);
        }

        private void CmbHotkeyModifier_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            CancelHotkeyRecording(refreshList: false);
            if (CmbHotkeyModifier.SelectedItem is ComboBoxItem item && item.Tag is string mod)
            {
                _config.HotkeyModifier = mod;
                _config.Save();
                PopulateHotkeysList();
            }
        }

        private void SendSwitchCommand(string desktopId)
        {
            EnsureBackgroundDaemonRunning();
            SendIpcCommand($"switch_id:{desktopId}");
        }

        private const string GitHubRepoUrl = "https://github.com/l1vensi/FluxDesk";

        private void UpdateWindowClip()
        {
            if (WindowState == WindowState.Maximized)
            {
                OuterWindowBorder.CornerRadius = new CornerRadius(0);
                OuterWindowBorder.BorderThickness = new Thickness(0);
                TitleBarBg.CornerRadius = new CornerRadius(0);
                RootClipGrid.Clip = null;
                IcoMaximize.Data = (Geometry)FindResource("IconRestore");
            }
            else
            {
                OuterWindowBorder.CornerRadius = new CornerRadius(12);
                OuterWindowBorder.BorderThickness = new Thickness(1);
                TitleBarBg.CornerRadius = new CornerRadius(11, 11, 0, 0);
                IcoMaximize.Data = (Geometry)FindResource("IconMaximize");
                if (RootClipGrid.ActualWidth > 0 && RootClipGrid.ActualHeight > 0)
                {
                    var clip = new RectangleGeometry
                    {
                        Rect = new Rect(0, 0, RootClipGrid.ActualWidth, RootClipGrid.ActualHeight),
                        RadiusX = 11,
                        RadiusY = 11
                    };
                    clip.Freeze();
                    RootClipGrid.Clip = clip;
                }
            }
        }

        private void RootClipGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateWindowClip();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            UpdateWindowClip();
        }

        private void BtnOpenRepo_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(GitHubRepoUrl) ||
                !GitHubRepoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                using var p = Process.Start(new ProcessStartInfo(GitHubRepoUrl) { UseShellExecute = true });
            }
            catch { }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2)
            {
                WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    if (WindowState == WindowState.Maximized)
                    {
                        var pt = e.GetPosition(this);
                        double ratioX = ActualWidth > 0 ? pt.X / ActualWidth : 0.5;
                        WindowState = WindowState.Normal;
                        var screenPt = PointToScreen(pt);
                        Left = screenPt.X - Width * ratioX;
                        Top = Math.Max(0, screenPt.Y - 18);
                    }
                    DragMove();
                }
                catch { }
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        #region Smooth Scrolling

        private ScrollViewer? _activeScrollViewer = null;
        private double _currentScrollOffset = 0;
        private double _targetScrollOffset = 0;
        private bool _isSmoothScrolling = false;
        private long _lastRenderTimestamp = 0;

        private void ScrollViewer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down)
            {
                StopSmoothScroll();
            }
        }

        private void ScrollViewer_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            StopSmoothScroll();
        }

        private void ScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var current = dep;
                while (current != null)
                {
                    if (current is ScrollBar)
                    {
                        StopSmoothScroll();
                        return;
                    }
                    current = (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                        ? VisualTreeHelper.GetParent(current)
                        : LogicalTreeHelper.GetParent(current);
                }
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            if (e.OriginalSource is DependencyObject dep)
            {
                var current = dep;
                while (current != null && current != sv)
                {
                    if (current is ComboBox cb && cb.IsDropDownOpen) return;
                    current = (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                        ? VisualTreeHelper.GetParent(current)
                        : LogicalTreeHelper.GetParent(current);
                }
            }

            if (sv.ScrollableHeight <= 0) return;
            e.Handled = true;

            if (_activeScrollViewer != sv || !_isSmoothScrolling)
            {
                _activeScrollViewer = sv;
                _currentScrollOffset = sv.VerticalOffset;
                _targetScrollOffset = sv.VerticalOffset;
            }

            double lines = Math.Clamp(SystemParameters.WheelScrollLines, 1, 10);
            double step = (e.Delta / 120.0) * (lines * 38.0);
            double nextTarget = Math.Max(0, Math.Min(sv.ScrollableHeight, _targetScrollOffset - step));
            if (Math.Abs(nextTarget - _currentScrollOffset) < 0.35 && !_isSmoothScrolling) return;

            _targetScrollOffset = nextTarget;

            if (!_isSmoothScrolling)
            {
                _isSmoothScrolling = true;
                _lastRenderTimestamp = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnSmoothScrollRendering;
            }
        }

        private void OnSmoothScrollRendering(object? sender, EventArgs e)
        {
            if (!_isSmoothScrolling || _activeScrollViewer == null)
            {
                StopSmoothScroll();
                return;
            }

            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastRenderTimestamp) / (double)Stopwatch.Frequency;
            _lastRenderTimestamp = now;

            if (dt <= 0) dt = 0.016;
            if (dt > 0.05) dt = 0.05;

            double maxScroll = _activeScrollViewer.ScrollableHeight;
            if (_targetScrollOffset > maxScroll) _targetScrollOffset = maxScroll;
            if (_targetScrollOffset < 0) _targetScrollOffset = 0;

            double diff = _targetScrollOffset - _currentScrollOffset;
            if (Math.Abs(diff) < 0.35)
            {
                _currentScrollOffset = _targetScrollOffset;
                _activeScrollViewer.ScrollToVerticalOffset(_currentScrollOffset);
                StopSmoothScroll();
                return;
            }

            double alpha = 1.0 - Math.Exp(-10.5 * dt);
            _currentScrollOffset += diff * alpha;
            _activeScrollViewer.ScrollToVerticalOffset(_currentScrollOffset);
        }

        private void StopSmoothScroll()
        {
            if (_isSmoothScrolling)
            {
                _isSmoothScrolling = false;
                CompositionTarget.Rendering -= OnSmoothScrollRendering;
            }
        }

        #endregion

        private Grid? _activeView = null;

        private void SwitchViewWithAnimation(Grid targetView)
        {
            StopSmoothScroll();
            CancelHotkeyRecording(refreshList: true);
            if (targetView == null) return;
            if (_activeView == null) _activeView = ViewDesktops;

            if (_activeView == targetView && targetView.Visibility == Visibility.Visible)
                return;

            var oldView = _activeView;
            _activeView = targetView;

            if (oldView != null && oldView != targetView && oldView.Visibility == Visibility.Visible)
            {
                oldView.IsHitTestVisible = false;
                var fadeOut = new DoubleAnimation(oldView.Opacity, 0.0, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                var viewToHide = oldView;
                fadeOut.Completed += (s, e) =>
                {
                    if (_activeView != viewToHide) viewToHide.Visibility = Visibility.Collapsed;
                };
                oldView.BeginAnimation(OpacityProperty, fadeOut);
            }

            targetView.Visibility = Visibility.Visible;
            targetView.IsHitTestVisible = true;

            var translate = targetView.RenderTransform as TranslateTransform;
            if (translate == null)
            {
                translate = new TranslateTransform();
                targetView.RenderTransform = translate;
            }

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slideIn = new DoubleAnimation(14.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            targetView.BeginAnimation(OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.YProperty, slideIn);
        }

        private void UpdateSidebarHighlight(int activeTab)
        {
            var accent = (Brush)FindResource("AccentBrush");
            var primary = (Brush)FindResource("TextPrimaryBrush");
            var secondary = (Brush)FindResource("TextSecondaryBrush");

            IcoNavDesktops.Fill = (activeTab == 0) ? accent : secondary;
            TxtNavDesktops.Foreground = (activeTab == 0) ? primary : secondary;

            IcoNavHotkeys.Fill = (activeTab == 1) ? accent : secondary;
            TxtNavHotkeys.Foreground = (activeTab == 1) ? primary : secondary;

            IcoNavSettings.Fill = (activeTab == 2) ? accent : secondary;
            TxtNavSettings.Foreground = (activeTab == 2) ? primary : secondary;

            IcoNavAbout.Fill = (activeTab == 3) ? accent : secondary;
            TxtNavAbout.Foreground = (activeTab == 3) ? primary : secondary;
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag != null)
            {
                int tabIndex = int.Parse(rb.Tag.ToString()!);
                UpdateSidebarHighlight(tabIndex);
                Grid? target = tabIndex switch
                {
                    0 => ViewDesktops,
                    1 => ViewHotkeys,
                    2 => ViewSettings,
                    3 => ViewAbout,
                    _ => null
                };
                if (target != null) SwitchViewWithAnimation(target);
            }
        }

        private static void RefreshComboSelection(ComboBox cmb)
        {
            int idx = cmb.SelectedIndex;
            cmb.SelectedIndex = -1;
            cmb.SelectedIndex = idx;
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            string selectedLang = (CmbLanguage.SelectedIndex == 1) ? "ru" : "en";
            _config.Language = selectedLang;
            _config.Save();

            ApplyLanguage(selectedLang);
            RefreshAllDynamicViews();
        }

        private void ApplyLanguage(string lang)
        {
            bool isRu = (lang == "ru");

            TxtNavDesktops.Text = isRu ? "Рабочие столы" : "Desktops";
            TxtNavHotkeys.Text = isRu ? "Горячие клавиши" : "Hotkeys";
            TxtNavSettings.Text = isRu ? "Настройки" : "Settings";
            TxtNavAbout.Text = isRu ? "О программе" : "About";

            TxtTitleDesktops.Text = isRu ? "Рабочие столы" : "Desktops";
            TxtBtnCreateDesktop.Text = isRu ? "Создать стол" : "Create Desktop";
            TxtBtnSelectMode.Text = isRu ? "Выбрать" : "Select";
            TxtBtnCancelSelect.Text = isRu ? "Отмена" : "Cancel";
            TxtDaemonWarning.Text = isRu
                ? "FluxDeskDaemon.exe не найден рядом с FluxDesk.exe. Поместите оба файла в одну папку для переключения столов и горячих клавиш."
                : "FluxDeskDaemon.exe was not found next to FluxDesk.exe. Place both files in the same folder for desktop switching & hotkeys.";
            UpdateBulkDeleteButtonText();

            TxtTitleHotkeys.Text = isRu ? "Горячие клавиши" : "Hotkeys";
            TxtHotkeysEnabled.Text = isRu ? "Включено" : "Enabled";
            TxtHotkeysModifier.Text = isRu ? "Модификатор" : "Modifier";
            TxtBtnResetHotkeys.Text = isRu ? "По умолчанию" : "Reset to defaults";
            TxtHotkeysFeedback.Visibility = Visibility.Collapsed;
            TxtHotkeysFeedback.Text = "";

            TxtTitleSettings.Text = isRu ? "Настройки" : "Settings";
            TxtSecLanguage.Text = isRu ? "Язык интерфейса" : "Language / Язык интерфейса";
            TxtLangTitle.Text = isRu ? "Язык интерфейса" : "Application Language";
            TxtLangDesc.Text = isRu ? "Выберите язык: Английский (по умолчанию) или Русский" : "Choose between English (Default) and Russian";

            TxtSecGeneral.Text = isRu ? "Общие" : "General";
            TxtPubTitle.Text = isRu ? "Авто-перемещение элементов Общего стола" : "Auto-move Public Desktop items";
            TxtPubDesc.Text = isRu ? "Автоматически перемещать файлы из Общего рабочего стола на текущий активный стол (может требовать права администратора)" : "Automatically move items from Public Desktop to active desktop on switch (may require admin permissions)";
            string primaryPath = (_config.Desktops.Count > 0 && !string.IsNullOrWhiteSpace(_config.Desktops[0].Path))
                ? _config.Desktops[0].Path
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            TxtDefFolderTitle.Text = isRu ? "Основная папка рабочего стола" : "Default desktop folder";
            TxtDefFolderDesc.Text = isRu ? $"Управляется FluxDesk ({primaryPath})" : $"Managed by FluxDesk ({primaryPath})";
            TxtBtnBrowse.Text = isRu ? "Обзор" : "Browse";

            TxtSecPersonalization.Text = isRu ? "Персонализация" : "Personalization";
            TxtWpTitle.Text = isRu ? "Раздельные обои для столов" : "Independent Wallpapers";
            TxtWpBadge.Text = isRu ? "ДОСТУПНО" : "INCLUDED";
            TxtWpDesc.Text = isRu ? "Выберите свои обои через меню [...] на карточке любого стола — FluxDesk будет менять их автоматически." : "Pick custom wallpapers via [...] on any desktop card — FluxDesk switches them automatically.";

            TxtSecSystem.Text = isRu ? "Система" : "System";
            TxtStartupTitle.Text = isRu ? "Автозапуск при входе в систему" : "Launch on startup";
            TxtStartupDesc.Text = isRu ? "Автоматически запускать FluxDesk при старте Windows" : "Automatically start when Windows starts";
            TxtRestoreShutdownTitle.Text = isRu ? "Возвращать Стол 1 при выключении ПК" : "Restore Desktop 1 on shutdown/reboot";
            TxtRestoreShutdownDesc.Text = isRu
                ? "При перезагрузке или выключении Windows возвращать основной рабочий стол. Если выключено — откроется последний активный стол."
                : "Return to your primary desktop on Windows shutdown or restart. When off, your last active desktop remains active.";
            TxtCtxMenuTitle.Text = isRu ? "Интеграция в контекстное меню Windows" : "Windows context menu integration";
            TxtCtxMenuDesc.Text = isRu ? "Добавить пункт FluxDesk в меню правой кнопки мыши на рабочем столе" : "Add FluxDesk entry to desktop right-click menu";
            TxtTrayTitle.Text = isRu ? "Значок в трее" : "Show tray icon";
            TxtTrayDesc.Text = isRu ? "Показывать значок FluxDesk в области уведомлений Windows." : "Show the FluxDesk icon in the system tray.";

            TxtSecKeyboard.Text = isRu ? "Сочетания клавиш" : "Keyboard Shortcuts";
            TxtQuickSwitcherTitle.Text = isRu ? "Быстрое переключение (Quick Switcher)" : "Quick Switcher";
            TxtQuickSwitcherDesc.Text = isRu ? "Включить всплывающее меню для быстрого переключения столов" : "Enable the quick switcher overlay for fast desktop switching";
            TxtQsForwardTitle.Text = isRu ? "Быстрое переключение (Вперед)" : "Quick Switcher (Forward)";
            TxtQsForwardDesc.Text = isRu ? "Перейти к следующему рабочему столу" : "Cycle to next desktop";
            TxtQsBackwardTitle.Text = isRu ? "Быстрое переключение (Назад)" : "Quick Switcher (Backward)";
            TxtQsBackwardDesc.Text = isRu ? "Перейти к предыдущему рабочему столу" : "Cycle to previous desktop";

            TxtSecResetSettings.Text = isRu ? "Сброс настроек" : "Reset Settings";
            TxtResetSettingsTitle.Text = isRu ? "Сбросить настройки по умолчанию" : "Reset Settings to Defaults";
            TxtResetSettingsDesc.Text = isRu
                ? "Возвращает горячие клавиши, автозапуск, переключатель и оформление к исходным значениям. Ваши столы и файлы останутся нетронутыми."
                : "Restores hotkeys, autostart, switchers and appearance to factory settings. Your desktops and files will remain untouched.";
            TxtBtnResetSettings.Text = isRu ? "Сбросить..." : "Reset Defaults";

            TxtSecUninstall.Text = isRu ? "Удаление программы" : "Uninstall";
            TxtUninstallTitle.Text = isRu ? "Удалить FluxDesk" : "Uninstall FluxDesk";
            TxtUninstallDesc.Text = isRu
                ? "Безопасно вернёт основной рабочий стол (Стол 1), уберёт автозапуск и предложит сохранить папку с резервной копией всех столов на Столе 1."
                : "Safely restores your primary Windows Desktop (Desktop 1), removes autostart, and lets you save a backup folder of all workspaces on Desktop 1.";
            TxtBtnUninstall.Text = isRu ? "Удалить..." : "Uninstall...";

            TxtTitleAbout.Text = isRu ? "О программе FluxDesk" : "About FluxDesk";
            TxtAboutVersion.Text = isRu ? "Версия 2.0.0 (x64 Релиз)" : "Version 2.0.0 (x64 Release)";
            TxtAboutDesc.Text = isRu ? "Независимый, бесплатный и локальный менеджер рабочих столов для Windows 10 и 11." : "Independent, free & private desktop workspace manager for Windows 10 & 11.";
            TxtAboutF1.Text = isRu ? "• Полностью бесплатно: без подписок, рекламы и ограничений по числу столов." : "• Free & Open Source: No paywalls, no ads, unlimited workspaces.";
            TxtAboutF2.Text = isRu ? "• 100% локально и конфиденциально: без серверов и телеметрии." : "• 100% Private & Local: No servers, no tracking, zero telemetry.";
            TxtAboutF3.Text = isRu ? "• Нативная интеграция с оболочкой: чистая переадресация Shell API." : "• Native Shell Integration: Clean Win32 shell redirection.";

            TxtContactTitle.Text = isRu ? "Автор и обратная связь" : "Author & Links";
            TxtContactDesc.Text = isRu
                ? "Нашли ошибку, есть идеи по улучшению или любые вопросы (включая правовые) — напишите автору напрямую:"
                : "Found a bug, have feedback, or need to reach the author for any inquiries (including legal)? Contact directly:";
            TxtTgLabel.Text = isRu ? "Telegram (автор):" : "Telegram (Author):";
            TxtRepoLabel.Text = isRu ? "Репозиторий GitHub" : "GitHub Repository";
            bool hasRepo = !string.IsNullOrWhiteSpace(GitHubRepoUrl);
            TxtRepoStatus.Text = hasRepo ? GitHubRepoUrl : (isRu ? "Ссылка появится после публикации репозитория" : "Repository link coming soon");
            TxtBtnOpenRepo.Text = hasRepo ? (isRu ? "Открыть GitHub" : "Open GitHub") : (isRu ? "Скоро" : "Coming soon");
            BtnOpenRepo.IsEnabled = hasRepo;
            BtnOpenRepo.Opacity = hasRepo ? 1.0 : 0.6;
        }

        #region Hotkeys & Recording

        private static string FormatVirtualKey(int vk)
        {
            if (vk <= 0) return "";
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x7B) return $"F{vk - 0x6F}";
            return vk switch
            {
                0xBD => "-",
                0xBB => "=",
                0xDB => "[",
                0xDD => "]",
                0xBA => ";",
                0xDE => "'",
                0xBC => ",",
                0xBE => ".",
                0xBF => "/",
                0xC0 => "`",
                0xDC => "\\",
                _ => KeyInterop.KeyFromVirtualKey(vk).ToString()
            };
        }

        /// <summary>
        /// Exact two-pass hotkey resolution matching Daemon's KeyboardHook.UpdateHotkeyMap 100%!
        /// </summary>
        private Dictionary<string, int> ResolveEffectiveHotkeys()
        {
            var deskToVk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var claimedVks = new HashSet<int>();
            var custom = _config.DesktopHotkeys ?? new Dictionary<string, int>();
            string mod = _config.HotkeyModifier ?? "Alt";

            // Pass 1: Explicit user-assigned hotkeys
            foreach (var d in _config.Desktops)
            {
                if (string.IsNullOrEmpty(d.Id)) continue;
                if (custom.TryGetValue(d.Id, out int boundVk))
                {
                    if (boundVk > 0)
                    {
                        if (mod == "Alt" && _config.QuickSwitcherEnabled && (boundVk == 0x57 || boundVk == 0x51))
                            continue;
                        if (claimedVks.Add(boundVk)) deskToVk[d.Id] = boundVk;
                    }
                    else
                    {
                        deskToVk[d.Id] = 0; // Explicitly cleared
                    }
                }
            }

            // Pass 2: Defaults 1..9 for desktops with no explicit entry (skip already-claimed VKs!)
            for (int i = 0; i < _config.Desktops.Count && i < 9; i++)
            {
                var d = _config.Desktops[i];
                if (string.IsNullOrEmpty(d.Id)) continue;
                if (custom.ContainsKey(d.Id)) continue;

                int defaultVk = 0x31 + i;
                if (claimedVks.Add(defaultVk))
                {
                    deskToVk[d.Id] = defaultVk;
                }
            }

            return deskToVk;
        }

        private void ShowHotkeyFeedback(string msg)
        {
            TxtHotkeysFeedback.Text = msg;
            TxtHotkeysFeedback.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (RenameModalOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    CloseRenameModal();
                    e.Handled = true;
                }
                return;
            }

            if (DeleteModalOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    CloseDeleteModal();
                    e.Handled = true;
                }
                return;
            }

            if (ConflictModalOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    CloseConflictModal();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    BtnConflictConfirm_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                return;
            }

            if (UninstallModalOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    CloseUninstallModal();
                    e.Handled = true;
                }
                return;
            }

            if (_recordingDesktopId == null || !_config.HotkeysEnabled) return;

            Key key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                Key.DeadCharProcessed => e.DeadCharProcessedKey,
                _ => e.Key
            };

            if (key == Key.Escape)
            {
                CancelHotkeyRecording(refreshList: true);
                e.Handled = true;
                return;
            }

            // Ignore pure modifier keys so user can press e.g. Alt+2 or just 2
            if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                e.Handled = true;
                return;
            }

            bool isRu = (_config.Language == "ru");
            string mod = _config.HotkeyModifier ?? "Alt";
            int vk = KeyInterop.VirtualKeyFromKey(key);

            // Reject unmatchable or critical OS system keys
            if (vk <= 0 || vk is 0xE5 or 0xE7 or 0x2C or 0x09 or 0x0D or 0x20 or 0x14 or 0x90 or 0x91 ||
                (mod == "Alt" && vk == 0x73)) // Alt+F4
            {
                ShowHotkeyFeedback(isRu
                    ? "Эта системная клавиша зарезервирована Windows — выберите другую (например 1–9, A–Z, F1–F12)."
                    : "This system key is reserved by Windows — please pick another key (e.g. 1–9, A–Z, F1–F12).");
                e.Handled = true;
                return;
            }

            // Reserve Alt+W / Alt+Q only while Quick Switcher is enabled
            if (mod == "Alt" && _config.QuickSwitcherEnabled && (vk == 0x57 || vk == 0x51))
            {
                ShowHotkeyFeedback(isRu
                    ? "Alt+W и Alt+Q заняты Быстрым переключателем (Quick Switcher)."
                    : "Alt+W and Alt+Q are reserved by Quick Switcher.");
                e.Handled = true;
                return;
            }

            ShowHotkeyFeedback("");
            _config.DesktopHotkeys ??= new Dictionary<string, int>();

            // Check conflicts using exact two-pass effective hotkeys!
            var effective = ResolveEffectiveHotkeys();
            var conflicting = _config.Desktops
                .Where(d => d.Id != _recordingDesktopId && effective.TryGetValue(d.Id, out int evk) && evk == vk)
                .ToList();

            if (conflicting.Count > 0)
            {
                var targetDesk = _config.Desktops.FirstOrDefault(d => d.Id == _recordingDesktopId);
                string targetName = targetDesk?.Name ?? "";
                string conflictNames = string.Join(", ", conflicting.Select(c => isRu ? $"«{c.Name}»" : $"\"{c.Name}\""));
                string modDisplay = GetModifierDisplay(mod);
                string comboStr = $"{modDisplay} + {FormatVirtualKey(vk)}";

                _pendingConflictTargetId = _recordingDesktopId;
                _pendingConflictVk = vk;
                _pendingConflictOtherIds = conflicting.Select(c => c.Id).ToList();
                SetHotkeyRecording(null);

                TxtConflictTitle.Text = isRu ? "Клавиша уже занята" : "Hotkey already assigned";
                TxtConflictMessage.Text = isRu
                    ? $"Комбинация {comboStr} уже назначена на {conflictNames}.\n\nПереназначить её на «{targetName}»?"
                    : $"The shortcut {comboStr} is already assigned to {conflictNames}.\n\nReassign it to \"{targetName}\"?";
                BtnConflictCancel.Content = isRu ? "Отмена" : "Cancel";
                BtnConflictConfirm.Content = isRu ? "Переназначить" : "Replace";

                PopulateHotkeysList();
                SetModalState(ConflictModalOverlay, true, BtnConflictConfirm);
                e.Handled = true;
                return;
            }

            _config.DesktopHotkeys[_recordingDesktopId] = vk;
            _config.Save();
            SetHotkeyRecording(null);
            PopulateHotkeysList();
            e.Handled = true;
        }

        private void CloseConflictModal()
        {
            _pendingConflictTargetId = null;
            _pendingConflictVk = 0;
            _pendingConflictOtherIds.Clear();
            SetModalState(ConflictModalOverlay, false);
        }

        private void BtnConflictCancel_Click(object sender, RoutedEventArgs e) => CloseConflictModal();

        private void BtnConflictConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_pendingConflictTargetId) && _pendingConflictVk > 0)
            {
                _config.DesktopHotkeys ??= new Dictionary<string, int>();
                foreach (var oldId in _pendingConflictOtherIds)
                {
                    _config.DesktopHotkeys[oldId] = 0;
                }
                _config.DesktopHotkeys[_pendingConflictTargetId] = _pendingConflictVk;
                _config.Save();
            }
            CloseConflictModal();
            PopulateHotkeysList();
        }

        private static string GetModifierDisplay(string? mod) => (mod ?? "Alt") switch
        {
            "Ctrl" => "Ctrl",
            "Win" => "Win",
            "Ctrl+Alt" => "Ctrl + Alt",
            "Win+Alt" => "Win + Alt",
            _ => "Alt"
        };

        private void PopulateHotkeysList()
        {
            HotkeysListPanel.Children.Clear();
            bool isRu = (_config.Language == "ru");
            string modDisplay = GetModifierDisplay(_config.HotkeyModifier);
            var effective = ResolveEffectiveHotkeys();

            for (int i = 0; i < _config.Desktops.Count; i++)
            {
                var d = _config.Desktops[i];
                string deskId = d.Id;
                bool isRecordingThis = (_recordingDesktopId == deskId);
                int vk = effective.TryGetValue(deskId, out int evk) ? evk : 0;
                bool hasKey = vk > 0;

                string keyLabel = isRecordingThis
                    ? (isRu ? "Нажмите клавишу..." : "Press any key...")
                    : (hasKey ? $"{modDisplay} + {FormatVirtualKey(vk)}" : (isRu ? "Назначить клавишу" : "Assign key"));

                var border = new Border
                {
                    Background = (Brush)FindResource("CardBrush"),
                    BorderBrush = isRecordingThis ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(isRecordingThis ? 1.5 : 1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(18, 10, 18, 10),
                    Margin = new Thickness(0, 0, 0, 8),
                    Opacity = _config.HotkeysEnabled ? 1.0 : 0.45
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var keyActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var keyBtn = new Button
                {
                    MinWidth = 148,
                    Height = 32,
                    Padding = new Thickness(14, 0, 14, 0),
                    Background = isRecordingThis ? (Brush)FindResource("AccentTintBrush") : new SolidColorBrush(Color.FromRgb(19, 23, 31)),
                    BorderBrush = isRecordingThis ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    IsEnabled = _config.HotkeysEnabled
                };

                var tpl = new ControlTemplate(typeof(Button));
                var bFactory = new FrameworkElementFactory(typeof(Border));
                bFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                bFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                bFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                bFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                bFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bFactory.AppendChild(cp);
                tpl.VisualTree = bFactory;
                keyBtn.Template = tpl;

                keyBtn.Content = new TextBlock
                {
                    Text = keyLabel,
                    FontWeight = (hasKey || isRecordingThis) ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 13,
                    Foreground = (hasKey || isRecordingThis) ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextMutedBrush")
                };

                keyBtn.Click += (s, e) =>
                {
                    ShowHotkeyFeedback("");
                    SetHotkeyRecording(isRecordingThis ? null : deskId);
                    PopulateHotkeysList();
                };
                keyActions.Children.Add(keyBtn);

                if (isRecordingThis)
                {
                    var cancelBtn = CreateDarkIconPillButton("×", 32, 32,
                        isRu ? "Отменить выбор клавиши (Esc)" : "Cancel recording (Esc)",
                        new SolidColorBrush(Color.FromRgb(30, 36, 48)),
                        (Brush)FindResource("BorderBrush"),
                        (Brush)FindResource("TextPrimaryBrush"));
                    cancelBtn.Click += (s, e) =>
                    {
                        ShowHotkeyFeedback("");
                        CancelHotkeyRecording(refreshList: true);
                    };
                    keyActions.Children.Add(cancelBtn);
                }
                else if (hasKey)
                {
                    var clearBtn = CreateDarkIconPillButton("×", 28, 32,
                        isRu ? "Убрать горячую клавишу" : "Clear hotkey",
                        Brushes.Transparent,
                        Brushes.Transparent,
                        (Brush)FindResource("TextSecondaryBrush"));
                    clearBtn.IsEnabled = _config.HotkeysEnabled;
                    clearBtn.Click += (s, e) =>
                    {
                        _config.DesktopHotkeys ??= new Dictionary<string, int>();
                        _config.DesktopHotkeys[deskId] = 0;
                        _config.Save();
                        PopulateHotkeysList();
                    };
                    keyActions.Children.Add(clearBtn);
                }

                Grid.SetColumn(keyActions, 0);
                row.Children.Add(keyActions);

                var deskText = new TextBlock
                {
                    Text = $"{i + 1}. {d.Name}",
                    FontSize = 14,
                    FontWeight = FontWeights.Medium,
                    Foreground = (Brush)FindResource("TextPrimaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(16, 0, 0, 0)
                };
                Grid.SetColumn(deskText, 1);
                row.Children.Add(deskText);

                border.Child = row;
                HotkeysListPanel.Children.Add(border);
            }
        }

        private void BtnResetHotkeys_Click(object sender, RoutedEventArgs e)
        {
            CancelHotkeyRecording(refreshList: false);

            _config.HotkeysEnabled = true;
            _config.HotkeyModifier = "Alt";
            _config.DesktopHotkeys ??= new Dictionary<string, int>();
            _config.DesktopHotkeys.Clear();

            // Default bindings: 1..9 for desks 0..8
            for (int i = 0; i < Math.Min(9, _config.Desktops.Count); i++)
            {
                _config.DesktopHotkeys[_config.Desktops[i].Id] = 0x31 + i; // '1'..'9'
            }

            _config.Save();

            ChkHotkeysEnabled.IsChecked = true;
            CmbHotkeyModifier.SelectedIndex = 0; // Alt
            TxtHotkeysFeedback.Visibility = Visibility.Collapsed;
            PopulateHotkeysList();
        }

        private void BtnResetSettings_Click(object sender, RoutedEventArgs e)
        {
            bool isRu = (_config.Language == "ru");

            // Reset non-desktop settings to safe defaults
            _config.StartWithWindows = true;
            _config.RestorePrimaryOnShutdown = true;
            _config.AutoMovePublicDesktopItems = false;
            _config.IndependentWallpapers = false;
            _config.ContextMenuIntegration = false;
            _config.ShowTrayIcon = true;
            _config.QuickSwitcherEnabled = true;
            _config.HotkeysEnabled = true;
            _config.HotkeyModifier = "Alt";

            // Reset hotkeys to 1..9
            _config.DesktopHotkeys ??= new Dictionary<string, int>();
            _config.DesktopHotkeys.Clear();
            for (int i = 0; i < Math.Min(9, _config.Desktops.Count); i++)
            {
                _config.DesktopHotkeys[_config.Desktops[i].Id] = 0x31 + i;
            }

            _config.Save();

            SyncControlsFromConfig();
            PopulateHotkeysList();

            // Ensure autostart & shell integration match
            string exePath = Environment.ProcessPath ?? "";
            string? daemonExe = FindDaemonExecutable();
            if (!string.IsNullOrEmpty(daemonExe))
            {
                // Daemon manages autostart
            }
        }

        private static Button CreateDarkIconPillButton(string glyph, double w, double h, string tooltip, Brush bg, Brush border, Brush fg)
        {
            var btn = new Button
            {
                Width = w,
                Height = h,
                Margin = new Thickness(6, 0, 0, 0),
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;
            btn.Content = new TextBlock
            {
                Text = glyph,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg,
                Margin = new Thickness(0, -1, 0, 0)
            };
            return btn;
        }

        #endregion

        #region Uninstall & Safe Backup

        private void BtnOpenUninstall_Click(object sender, RoutedEventArgs e)
        {
            bool isRu = (_config.Language == "ru");
            TxtUninstallModalTitle.Text = isRu ? "Удалить FluxDesk" : "Uninstall FluxDesk";
            TxtUninstallModalSub.Text = isRu ? "Подтверждение удаления программы" : "Before removing the application";
            TxtUninstallModalMsg.Text = isRu
                ? "FluxDesk безопасно вернёт ваш основной рабочий стол (Стол 1), очистит автозапуск и контекстное меню Windows, после чего удалит себя."
                : "FluxDesk will safely restore your main Windows Desktop (Desktop 1), remove autostart & context menu entries, and remove itself.";
            TxtBackupChoiceTitle.Text = isRu ? "Сохранить папку с данными на Столе 1 (FluxDesk Backup)" : "Save backup folder on Desktop 1 (FluxDesk Backup)";
            TxtBackupChoiceDesc.Text = isRu
                ? "Создаст на основном рабочем столе папку «FluxDesk Backup» со всеми дополнительными столами, файлами и последними позициями иконок."
                : "Creates 'FluxDesk Backup' on your primary Desktop with all secondary workspace folders, files, and last saved icon layouts.";
            BtnUninstallCancel.Content = isRu ? "Отмена" : "Cancel";
            BtnUninstallConfirm.Content = isRu ? "Удалить FluxDesk" : "Uninstall FluxDesk";
            BtnUninstallConfirm.IsEnabled = true;
            BtnUninstallCancel.IsEnabled = true;
            ChkBackupBeforeUninstall.IsChecked = true;
            SetModalState(UninstallModalOverlay, true, BtnUninstallCancel);
        }

        private void CloseUninstallModal() => SetModalState(UninstallModalOverlay, false);

        private void BtnUninstallCancel_Click(object sender, RoutedEventArgs e) => CloseUninstallModal();

        private static void CopyDirectoryRecursiveSafe(string sourceDir, string destDir)
        {
            if (!Directory.Exists(sourceDir)) return;
            string fullSrc = System.IO.Path.GetFullPath(sourceDir).TrimEnd('\\', '/');
            string fullDst = System.IO.Path.GetFullPath(destDir).TrimEnd('\\', '/');

            // Never copy a folder into itself or follow directory symlinks/junctions!
            if (PathsOverlap(fullSrc, fullDst)) return;
            if ((File.GetAttributes(fullSrc) & FileAttributes.ReparsePoint) != 0) return;

            Directory.CreateDirectory(fullDst);

            try
            {
                foreach (var file in Directory.GetFiles(fullSrc))
                {
                    string fn = System.IO.Path.GetFileName(file);
                    if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                        File.Copy(file, System.IO.Path.Combine(fullDst, fn), overwrite: true);
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var sub in Directory.GetDirectories(fullSrc))
                {
                    try
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                        string dirName = System.IO.Path.GetFileName(sub);
                        CopyDirectoryRecursiveSafe(sub, System.IO.Path.Combine(fullDst, dirName));
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static readonly HashSet<string> ReservedWinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private static string SanitizeFolderName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(name)) return "Desktop";
            if (ReservedWinNames.Contains(name)) name = "_" + name;
            return name;
        }

        private static string GetUniqueBackupRoot(string primaryDesktop)
        {
            string basePath = System.IO.Path.Combine(primaryDesktop, "FluxDesk Backup");
            if (!Directory.Exists(basePath)) return basePath;
            string stamped = System.IO.Path.Combine(primaryDesktop, $"FluxDesk Backup ({DateTime.Now:yyyy-MM-dd_HH-mm})");
            if (!Directory.Exists(stamped)) return stamped;
            int n = 2;
            while (Directory.Exists($"{stamped} ({n})")) n++;
            return $"{stamped} ({n})";
        }

        private async void BtnUninstallConfirm_Click(object sender, RoutedEventArgs e)
        {
            bool createBackup = (ChkBackupBeforeUninstall.IsChecked == true);
            bool isRu = (_config.Language == "ru");

            BtnUninstallConfirm.IsEnabled = false;
            BtnUninstallCancel.IsEnabled = false;
            BtnUninstallConfirm.Content = isRu ? "Удаление..." : "Uninstalling...";

            string primaryDesktop = (_config.Desktops.Count > 0 && Directory.Exists(_config.Desktops[0].Path))
                ? _config.Desktops[0].Path
                : AppConfig.ResolveSafePrimaryDesktopPath();
            var desktopsSnapshot = _config.Desktops.Select(d => new DesktopInfo { Id = d.Id, Name = d.Name, Path = d.Path, WallpaperPath = d.WallpaperPath }).ToList();
            string restoreWp = _config.IndependentWallpapers
                ? ((!string.IsNullOrWhiteSpace(_config.Desktops.FirstOrDefault()?.WallpaperPath) && File.Exists(_config.Desktops[0].WallpaperPath))
                    ? _config.Desktops[0].WallpaperPath
                    : _config.DefaultWallpaperPath)
                : "";

            // 1. Gracefully ask Daemon to restore Desktop 1 & exit, then wait up to 3s for current-session daemon exit
            SendIpcCommand("exit_restore");

            await Task.Run(() =>
            {
                int mySession;
                using (var cur = Process.GetCurrentProcess()) mySession = cur.SessionId;

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 3000)
                {
                    bool anyAlive = false;
                    foreach (var p in Process.GetProcessesByName("FluxDeskDaemon"))
                    {
                        try { if (p.SessionId == mySession && !p.HasExited) anyAlive = true; }
                        catch { }
                        finally { p.Dispose(); }
                    }
                    if (!anyAlive) break;
                    Thread.Sleep(100);
                }

                // Kill only current-session daemon if it timed out
                foreach (var p in Process.GetProcessesByName("FluxDeskDaemon"))
                {
                    try { if (p.SessionId == mySession && !p.HasExited) p.Kill(); }
                    catch { }
                    finally { p.Dispose(); }
                }

                // 2. ALWAYS verify & directly restore Windows Desktop to Desktop 1 from UI so user is NEVER stranded!
                string liveDesktop = GetLiveDesktopFolder();
                if (!string.Equals(liveDesktop.TrimEnd('\\', '/'), primaryDesktop.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    RestoreDesktopFolderDirect(primaryDesktop);
                }
                if (!string.IsNullOrWhiteSpace(restoreWp) && File.Exists(restoreWp))
                {
                    SetWindowsWallpaperDirect(restoreWp);
                }

                // 3. Clean FluxDesk registry keys (never touch third-party keys!)
                try
                {
                    using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    runKey?.DeleteValue("FluxDesk", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\DesktopBackground\Shell\FluxDesk", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\FluxDesk", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\FluxDesk", false);
                    // Remove "Apps & Features" entry
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FluxDesk", false);
                }
                catch { }

                // 4. Optional non-colliding backup folder on Primary Desktop (runs off UI thread!)
                if (createBackup && Directory.Exists(primaryDesktop))
                {
                    try
                    {
                        string backupRoot = GetUniqueBackupRoot(primaryDesktop);
                        Directory.CreateDirectory(backupRoot);

                        var summaryLines = new List<string>
                        {
                            $"FluxDesk Backup ({DateTime.Now:yyyy-MM-dd HH:mm})",
                            "========================================",
                            ""
                        };

                        for (int i = 1; i < desktopsSnapshot.Count; i++)
                        {
                            var d = desktopsSnapshot[i];
                            string safeName = SanitizeFolderName($"{i + 1}. {d.Name}");
                            string destSub = System.IO.Path.Combine(backupRoot, safeName);
                            summaryLines.Add($"[{i + 1}] {d.Name}  (Source: {d.Path})");
                            if (Directory.Exists(d.Path) && !PathsOverlap(d.Path, primaryDesktop))
                            {
                                CopyDirectoryRecursiveSafe(d.Path, destSub);
                            }
                        }

                        string layoutsBackup = System.IO.Path.Combine(backupRoot, "_layouts_backup");
                        string layoutsSrc = System.IO.Path.Combine(AppConfig.ConfigDir, "layouts");
                        if (Directory.Exists(layoutsSrc))
                        {
                            CopyDirectoryRecursiveSafe(layoutsSrc, layoutsBackup);
                        }
                        if (File.Exists(AppConfig.ConfigPath))
                        {
                            Directory.CreateDirectory(layoutsBackup);
                            File.Copy(AppConfig.ConfigPath, System.IO.Path.Combine(layoutsBackup, "config.json"), overwrite: true);
                        }

                        File.WriteAllLines(System.IO.Path.Combine(backupRoot, "README.txt"), summaryLines);
                    }
                    catch { }
                }
            });

            // 5. Detached self-removal passing ALL paths strictly via Environment Variables (zero cmd.exe %VAR% injection!)
            try
            {
                string currentExe = Environment.ProcessPath ?? "";
                string currentDir = !string.IsNullOrEmpty(currentExe) ? (System.IO.Path.GetDirectoryName(currentExe) ?? "") : "";
                string siblingDaemon = !string.IsNullOrEmpty(currentDir) ? System.IO.Path.Combine(currentDir, "FluxDeskDaemon.exe") : "";
                string localProgDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "FluxDesk");
                string localDaemon = System.IO.Path.Combine(localProgDir, "FluxDeskDaemon.exe");
                string localUi = System.IO.Path.Combine(localProgDir, "FluxDesk.exe");

                string psScript =
                    "Start-Sleep -Seconds 2; " +
                    "foreach ($p in @($env:FD_CUR_EXE, $env:FD_SIB_DAEMON, $env:FD_LOC_DAEMON, $env:FD_LOC_UI)) { " +
                    "  if ($p -and (Test-Path -LiteralPath $p)) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue } " +
                    "}; " +
                    "if ($env:FD_LOC_DIR -and (Test-Path -LiteralPath $env:FD_LOC_DIR) -and @(Get-ChildItem -LiteralPath $env:FD_LOC_DIR -Force -ErrorAction SilentlyContinue).Count -eq 0) { " +
                    "  Remove-Item -LiteralPath $env:FD_LOC_DIR -Force -ErrorAction SilentlyContinue " +
                    "}; " +
                    "if ($env:FD_APPDATA -and (Test-Path -LiteralPath $env:FD_APPDATA)) { " +
                    "  Remove-Item -LiteralPath $env:FD_APPDATA -Recurse -Force -ErrorAction SilentlyContinue " +
                    "}";

                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(psScript);

                psi.Environment["FD_CUR_EXE"] = currentExe;
                psi.Environment["FD_SIB_DAEMON"] = siblingDaemon;
                psi.Environment["FD_LOC_DAEMON"] = localDaemon;
                psi.Environment["FD_LOC_UI"] = localUi;
                psi.Environment["FD_LOC_DIR"] = localProgDir;
                psi.Environment["FD_APPDATA"] = AppConfig.ConfigDir;

                using var p = Process.Start(psi);
            }
            catch { }

            Application.Current.Shutdown();
        }

        #endregion

        #region Desktops, Selection, Delete & Rename

        private void UpdateBulkDeleteButtonText()
        {
            bool isRu = (_config.Language == "ru");
            int count = _selectedDesktopIds.Count;
            int totalDeletable = Math.Max(0, _config.Desktops.Count - 1);
            bool allSelected = totalDeletable > 0 && count == totalDeletable;

            TxtBtnSelectAll.Text = allSelected
                ? (isRu ? "Снять все" : "Deselect all")
                : (isRu ? "Выбрать все" : "Select all");
            TxtBtnDeleteSelected.Text = isRu ? $"Удалить ({count})" : $"Delete ({count})";
            BtnDeleteSelected.IsEnabled = count > 0;
            BtnDeleteSelected.Opacity = count > 0 ? 1.0 : 0.45;
        }

        private void BtnEnterSelectMode_Click(object sender, RoutedEventArgs e)
        {
            if (_config.Desktops.Count <= 1) return;
            _isSelectionMode = true;
            _selectedDesktopIds.Clear();
            NormalDesktopActions.Visibility = Visibility.Collapsed;
            BulkDesktopActions.Visibility = Visibility.Visible;
            UpdateBulkDeleteButtonText();
            PopulateDesktopCards();
        }

        private void BtnCancelSelectMode_Click(object sender, RoutedEventArgs e) => ExitSelectionMode(refreshCards: true);

        private void ExitSelectionMode(bool refreshCards)
        {
            _isSelectionMode = false;
            _selectedDesktopIds.Clear();
            BulkDesktopActions.Visibility = Visibility.Collapsed;
            NormalDesktopActions.Visibility = Visibility.Visible;
            if (refreshCards) PopulateDesktopCards();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            var deletable = _config.Desktops.Skip(1).Select(d => d.Id).ToList();
            if (_selectedDesktopIds.Count == deletable.Count)
            {
                _selectedDesktopIds.Clear();
            }
            else
            {
                _selectedDesktopIds.Clear();
                foreach (var id in deletable) _selectedDesktopIds.Add(id);
            }
            UpdateBulkDeleteButtonText();
            PopulateDesktopCards();
        }

        private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var targets = _config.Desktops.Skip(1).Where(d => _selectedDesktopIds.Contains(d.Id)).ToList();
            if (targets.Count == 0) return;
            OpenDeleteModal(targets);
        }

        private void OpenDeleteModal(List<DesktopInfo> targets)
        {
            if (targets == null || targets.Count == 0) return;
            _pendingDeleteTargets = targets;
            bool isRu = (_config.Language == "ru");

            if (targets.Count == 1)
            {
                var d = targets[0];
                TxtDeleteTitle.Text = isRu ? "Удаление рабочего стола" : "Delete Desktop";
                TxtDeleteSubtitle.Text = d.Name;
                TxtDeleteMessage.Text = isRu
                    ? $"Удалить рабочий стол «{d.Name}»?\nПапка: {d.Path}"
                    : $"Delete desktop \"{d.Name}\"?\nFolder: {d.Path}";
                BtnDeleteConfirm.Content = isRu ? "Удалить" : "Delete";
            }
            else
            {
                TxtDeleteTitle.Text = isRu ? $"Удалить столы ({targets.Count} шт.)" : $"Delete {targets.Count} Desktops";
                TxtDeleteSubtitle.Text = isRu ? "Массовое удаление рабочих столов" : "Bulk desktop removal";
                string preview = string.Join(", ", targets.Take(5).Select(t => t.Name)) + (targets.Count > 5 ? $" +{targets.Count - 5}" : "");
                TxtDeleteMessage.Text = isRu
                    ? $"Выбрано столов: {targets.Count}\n({preview})"
                    : $"Selected desktops: {targets.Count}\n({preview})";
                BtnDeleteConfirm.Content = isRu ? $"Удалить ({targets.Count})" : $"Delete ({targets.Count})";
            }

            TxtDeleteFilesTitle.Text = isRu ? "Также удалить папки и файлы (в Корзину)" : "Also delete folders & files (to Recycle Bin)";
            TxtDeleteFilesDesc.Text = isRu
                ? "Папки выбранных столов отправятся в Корзину Windows (можно восстановить). Если выключено — файлы останутся на диске."
                : "Moves selected folders to the Windows Recycle Bin (recoverable). Leave off to keep all files safely on disk.";
            BtnDeleteCancel.Content = isRu ? "Отмена" : "Cancel";
            ChkDeleteFilesToRecycle.IsChecked = false;
            SetModalState(DeleteModalOverlay, true, BtnDeleteCancel);
        }

        private void CloseDeleteModal()
        {
            _pendingDeleteTargets.Clear();
            SetModalState(DeleteModalOverlay, false);
        }

        private void BtnDeleteCancel_Click(object sender, RoutedEventArgs e) => CloseDeleteModal();

        private async void BtnDeleteConfirm_Click(object sender, RoutedEventArgs e)
        {
            bool recycleFiles = (ChkDeleteFilesToRecycle.IsChecked == true);
            var targetIds = new HashSet<string>(_pendingDeleteTargets.Select(t => t.Id));
            CloseDeleteModal();

            if (targetIds.Count == 0) return;

            // Reload fresh config before mutating so we never overwrite Daemon's latest CurrentIndex/Desktops!
            var latest = AppConfig.TryLoadExisting();
            if (latest != null) _config = latest;

            string activeId = (_config.CurrentIndex >= 0 && _config.CurrentIndex < _config.Desktops.Count)
                ? _config.Desktops[_config.CurrentIndex].Id
                : "";
            bool deletingActive = targetIds.Contains(activeId);

            var removedList = new List<DesktopInfo>();
            for (int i = _config.Desktops.Count - 1; i >= 1; i--)
            {
                if (targetIds.Contains(_config.Desktops[i].Id))
                {
                    removedList.Add(_config.Desktops[i]);
                    _config.Desktops.RemoveAt(i);
                }
            }

            if (deletingActive)
            {
                _config.CurrentIndex = 0;
            }
            else
            {
                int newIdx = _config.Desktops.FindIndex(d => d.Id == activeId);
                _config.CurrentIndex = newIdx >= 0 ? newIdx : 0;
            }

            // Prune deleted desktop IDs from Hotkeys, Schedules, Triggers, Layouts, Previews
            foreach (var rem in removedList)
            {
                _config.DesktopHotkeys?.Remove(rem.Id);
                _config.Schedules?.RemoveAll(s => s.DesktopId == rem.Id);
                _config.Triggers?.RemoveAll(t => t.DesktopId == rem.Id);
                _previewBitmapCache.Remove(rem.Id);

                if (AppConfig.IsSafeId(rem.Id))
                {
                    try
                    {
                        string layoutFile = System.IO.Path.Combine(AppConfig.ConfigDir, "layouts", $"layout_{rem.Id}.json");
                        if (File.Exists(layoutFile)) File.Delete(layoutFile);
                        string previewFile = System.IO.Path.Combine(AppConfig.ConfigDir, "previews", $"preview_{rem.Id}.png");
                        if (File.Exists(previewFile)) File.Delete(previewFile);
                    }
                    catch { }
                }
            }

            // Save config FIRST (Daemon's ReloadConfigOnUiThread also handles switching away from deleted active desktop!)
            _config.Save();

            if (deletingActive && _config.Desktops.Count > 0)
            {
                string primaryId = _config.Desktops[0].Id;
                string primaryPath = _config.Desktops[0].Path;
                SendSwitchCommand(primaryId);

                if (recycleFiles)
                {
                    // Wait until live Windows FOLDERID_Desktop actually leaves every deleted folder (up to 2.5s)!
                    await Task.Run(() =>
                    {
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 2500)
                        {
                            string live = GetLiveDesktopFolder().TrimEnd('\\', '/');
                            bool stillInsideDeleted = removedList.Any(r =>
                                !string.IsNullOrWhiteSpace(r.Path) &&
                                PathsOverlap(live, System.IO.Path.GetFullPath(r.Path).TrimEnd('\\', '/')));
                            if (!stillInsideDeleted) return;
                            Thread.Sleep(80);
                        }

                        // Fallback if Daemon wasn't running: restore Desktop 0 directly from UI before recycling
                        RestoreDesktopFolderDirect(primaryPath);
                    });
                }
            }

            if (recycleFiles)
            {
                await Task.Run(() =>
                {
                    foreach (var rem in removedList)
                    {
                        if (!string.IsNullOrWhiteSpace(rem.Path) && IsSafeToRecycleFolder(rem.Path))
                        {
                            SendFolderToRecycleBin(rem.Path);
                        }
                    }
                });
            }

            if (_isSelectionMode) ExitSelectionMode(refreshCards: false);
            RefreshAllDynamicViews();
        }

        private static int CountVisibleDesktopItems(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return 0;
            int count = 0;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folderPath))
                {
                    string fn = System.IO.Path.GetFileName(entry);
                    if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                        fn.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var attrs = File.GetAttributes(entry);
                        if ((attrs & FileAttributes.Hidden) != 0 && (attrs & FileAttributes.System) != 0)
                            continue;
                    }
                    catch { }
                    count++;
                }
            }
            catch { }
            return count;
        }

        private System.Windows.Shapes.Path CreateMenuIcon(string resourceKey, Brush fill)
        {
            return new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource(resourceKey),
                Fill = fill,
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform
            };
        }

        private void PopulateDesktopCards()
        {
            DesktopCardsPanel.Children.Clear();
            bool isRu = (_config.Language == "ru");

            BtnEnterSelectMode.Visibility = (_config.Desktops.Count > 1) ? Visibility.Visible : Visibility.Collapsed;

            var secondaryBrush = (Brush)FindResource("TextSecondaryBrush");
            var accentBrush = (Brush)FindResource("AccentBrush");
            var dangerBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));

            for (int i = 0; i < _config.Desktops.Count; i++)
            {
                var d = _config.Desktops[i];
                string deskId = d.Id;
                bool isActive = (i == _config.CurrentIndex);
                bool isSelected = _isSelectionMode && i > 0 && _selectedDesktopIds.Contains(deskId);
                int itemCount = CountVisibleDesktopItems(d.Path);

                var cardBorder = new Border
                {
                    Background = isSelected
                        ? new SolidColorBrush(Color.FromRgb(42, 24, 28))
                        : (isActive ? (SolidColorBrush)FindResource("AccentTintBrush") : (SolidColorBrush)FindResource("CardBrush")),
                    BorderBrush = isSelected
                        ? new SolidColorBrush(Color.FromRgb(220, 38, 38))
                        : (isActive ? (SolidColorBrush)FindResource("AccentBrush") : (SolidColorBrush)FindResource("BorderBrush")),
                    BorderThickness = new Thickness((isActive || isSelected) ? 1.5 : 1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16),
                    Margin = new Thickness(0, 0, 0, 14),
                    Cursor = (_isSelectionMode && i > 0) ? Cursors.Hand : Cursors.Arrow
                };

                if (_isSelectionMode && i > 0)
                {
                    cardBorder.MouseLeftButtonUp += (s, e) =>
                    {
                        if (_selectedDesktopIds.Contains(deskId)) _selectedDesktopIds.Remove(deskId);
                        else _selectedDesktopIds.Add(deskId);
                        UpdateBulkDeleteButtonText();
                        PopulateDesktopCards();
                    };
                }

                var grid = new Grid();
                if (_isSelectionMode)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                }
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                int colOffset = 0;
                if (_isSelectionMode)
                {
                    colOffset = 1;
                    if (i > 0)
                    {
                        var selBox = new Border
                        {
                            Width = 20,
                            Height = 20,
                            CornerRadius = new CornerRadius(5),
                            Background = isSelected ? new SolidColorBrush(Color.FromRgb(220, 38, 38)) : new SolidColorBrush(Color.FromRgb(24, 29, 38)),
                            BorderBrush = isSelected ? dangerBrush : (Brush)FindResource("BorderBrush"),
                            BorderThickness = new Thickness(1.5),
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        if (isSelected)
                        {
                            selBox.Child = new System.Windows.Shapes.Path
                            {
                                Data = (Geometry)FindResource("IconCheck"),
                                Fill = Brushes.White,
                                Width = 11,
                                Height = 11,
                                Stretch = Stretch.Uniform,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                        }
                        Grid.SetColumn(selBox, 0);
                        grid.Children.Add(selBox);
                    }
                }

                // 1. Live Desktop Thumbnail Preview (cached frozen BitmapImage)
                var previewBmp = TryLoadPreviewBitmap(deskId);
                var thumbBorder = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Width = 124,
                    Height = 70,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 16, 0),
                    BorderBrush = isActive
                        ? new SolidColorBrush(Color.FromArgb(120, 190, 230, 253))
                        : new SolidColorBrush(Color.FromRgb(45, 54, 70)),
                    BorderThickness = new Thickness(1)
                };
                if (previewBmp != null)
                {
                    RenderOptions.SetBitmapScalingMode(thumbBorder, BitmapScalingMode.HighQuality);
                    thumbBorder.Background = new ImageBrush(previewBmp) { Stretch = Stretch.UniformToFill };
                }
                else
                {
                    thumbBorder.Background = new LinearGradientBrush(Color.FromRgb(18, 38, 36), Color.FromRgb(14, 20, 26), 45);
                    thumbBorder.Child = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconDesktops"),
                        Fill = accentBrush,
                        Width = 24,
                        Height = 24,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                Grid.SetColumn(thumbBorder, colOffset + 0);
                grid.Children.Add(thumbBorder);

                // 2. Info Block
                var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var titleStack = new StackPanel { Orientation = Orientation.Horizontal };

                var titleText = new TextBlock
                {
                    Text = d.Name,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Cursor = Cursors.Hand,
                    ToolTip = _isSelectionMode ? null : (isRu ? "Нажмите, чтобы переименовать" : "Click to rename")
                };
                titleText.MouseDown += (s, e) =>
                {
                    if (!_isSelectionMode && e.ChangedButton == MouseButton.Left) OpenRenameModalById(deskId);
                };
                titleStack.Children.Add(titleText);

                if (i == 0)
                {
                    titleStack.Children.Add(new Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = accentBrush,
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                infoStack.Children.Add(titleStack);

                bool hasWallpaper = _config.IndependentWallpapers && !string.IsNullOrWhiteSpace(d.WallpaperPath) && File.Exists(d.WallpaperPath);
                string subtitle = isRu
                    ? ($"Элементов: {itemCount}" + (i == 0 ? " • Основной стол" : "") + (hasWallpaper ? " • Свои обои" : ""))
                    : ($"Items: {itemCount}" + (i == 0 ? " • OS Default" : "") + (hasWallpaper ? " • Custom wallpaper" : ""));

                infoStack.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12.5,
                    Foreground = secondaryBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                Grid.SetColumn(infoStack, colOffset + 1);
                grid.Children.Add(infoStack);

                // 3. Actions Block
                var actStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = _isSelectionMode ? Visibility.Collapsed : Visibility.Visible
                };

                if (isActive)
                {
                    var activeStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                    activeStack.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconCheck"),
                        Fill = accentBrush,
                        Width = 14,
                        Height = 14,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    activeStack.Children.Add(new TextBlock
                    {
                        Text = isRu ? "Активен" : "Active",
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = accentBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    actStack.Children.Add(activeStack);
                }
                else
                {
                    var switchBtn = new Button
                    {
                        Style = (Style)FindResource("AccentButtonStyle"),
                        Padding = new Thickness(20, 7, 20, 7),
                        Margin = new Thickness(0, 0, 10, 0),
                        Content = isRu ? "Перейти" : "Switch",
                        FontSize = 13
                    };
                    switchBtn.Click += (s, e) => SendSwitchCommand(deskId);
                    actStack.Children.Add(switchBtn);
                }

                // Options [...] Button with dark vector ContextMenu
                var moreBtn = new Button
                {
                    Width = 34,
                    Height = 34,
                    Background = new SolidColorBrush(Color.FromRgb(32, 38, 50)),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                var moreTemplate = new ControlTemplate(typeof(Button));
                var mBorder = new FrameworkElementFactory(typeof(Border));
                mBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                mBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                var mPath = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
                mPath.SetValue(System.Windows.Shapes.Path.DataProperty, (Geometry)FindResource("IconMore"));
                mPath.SetValue(System.Windows.Shapes.Path.FillProperty, secondaryBrush);
                mPath.SetValue(System.Windows.Shapes.Path.WidthProperty, 14.0);
                mPath.SetValue(System.Windows.Shapes.Path.HeightProperty, 14.0);
                mPath.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.Uniform);
                mBorder.AppendChild(mPath);
                moreTemplate.VisualTree = mBorder;
                moreBtn.Template = moreTemplate;

                var cm = new ContextMenu();
                cm.Opened += (s, e) => _openCardContextMenu = cm;
                cm.Closed += (s, e) =>
                {
                    if (_openCardContextMenu == cm) _openCardContextMenu = null;
                    if (_pendingConfigReload && !IsAnyModalOpen())
                    {
                        _pendingConfigReload = false;
                        RefreshConfigIfChanged();
                    }
                };

                var miRename = new MenuItem
                {
                    Header = isRu ? "Переименовать" : "Rename workspace",
                    Icon = CreateMenuIcon("IconEdit", accentBrush)
                };
                miRename.Click += (s, e) => OpenRenameModalById(deskId);
                cm.Items.Add(miRename);

                var miWallpaper = new MenuItem
                {
                    Header = isRu ? "Выбрать обои..." : "Set wallpaper...",
                    Icon = CreateMenuIcon("IconImage", accentBrush)
                };
                miWallpaper.Click += (s, e) =>
                {
                    var liveDesk = _config.Desktops.FirstOrDefault(x => x.Id == deskId);
                    if (liveDesk == null) return;

                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = isRu ? $"Обои для «{liveDesk.Name}»" : $"Wallpaper for \"{liveDesk.Name}\"",
                        // Only formats natively supported by Win32 SPI_SETDESKWALLPAPER & GDI+
                        Filter = isRu
                            ? "Изображения (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
                            : "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
                    };
                    if (dlg.ShowDialog() == true && File.Exists(dlg.FileName))
                    {
                        // Re-resolve by ID in case _config refreshed while dialog was open!
                        var freshDesk = _config.Desktops.FirstOrDefault(x => x.Id == deskId);
                        if (freshDesk == null) return;

                        if (string.IsNullOrWhiteSpace(_config.DefaultWallpaperPath))
                        {
                            string curWp = GetCurrentWindowsWallpaper();
                            if (!string.IsNullOrWhiteSpace(curWp) && File.Exists(curWp))
                                _config.DefaultWallpaperPath = curWp;
                        }

                        freshDesk.WallpaperPath = dlg.FileName;
                        _config.IndependentWallpapers = true;
                        ChkIndependentWallpapers.IsChecked = true;
                        _config.Save();
                        PopulateDesktopCards();
                    }
                };
                cm.Items.Add(miWallpaper);

                if (!string.IsNullOrWhiteSpace(d.WallpaperPath))
                {
                    var miClearWp = new MenuItem
                    {
                        Header = isRu ? "Убрать обои" : "Clear wallpaper",
                        Icon = CreateMenuIcon("IconClose", secondaryBrush)
                    };
                    miClearWp.Click += (s, e) =>
                    {
                        var freshDesk = _config.Desktops.FirstOrDefault(x => x.Id == deskId);
                        if (freshDesk == null) return;
                        freshDesk.WallpaperPath = "";
                        _config.Save();
                        PopulateDesktopCards();
                    };
                    cm.Items.Add(miClearWp);
                }

                var miOpen = new MenuItem
                {
                    Header = isRu ? "Открыть папку в Проводнике" : "Open folder in Explorer",
                    Icon = CreateMenuIcon("IconFolder", secondaryBrush)
                };
                miOpen.Click += (s, e) =>
                {
                    try
                    {
                        var freshDesk = _config.Desktops.FirstOrDefault(x => x.Id == deskId);
                        if (freshDesk == null) return;
                        if (!Directory.Exists(freshDesk.Path)) Directory.CreateDirectory(freshDesk.Path);
                        var psi = new ProcessStartInfo("explorer.exe");
                        psi.ArgumentList.Add(freshDesk.Path);
                        using var p = Process.Start(psi);
                    }
                    catch { }
                };
                cm.Items.Add(miOpen);

                if (i > 0)
                {
                    var miDelete = new MenuItem
                    {
                        Header = isRu ? "Удалить рабочий стол" : "Delete workspace",
                        Foreground = dangerBrush,
                        Icon = CreateMenuIcon("IconTrash", dangerBrush)
                    };
                    miDelete.Click += (s, e) =>
                    {
                        var freshDesk = _config.Desktops.FirstOrDefault(x => x.Id == deskId);
                        if (freshDesk != null) OpenDeleteModal(new List<DesktopInfo> { freshDesk });
                    };
                    cm.Items.Add(miDelete);
                }

                moreBtn.ContextMenu = cm;
                moreBtn.Click += (s, e) =>
                {
                    cm.PlacementTarget = moreBtn;
                    cm.IsOpen = true;
                };

                actStack.Children.Add(moreBtn);
                Grid.SetColumn(actStack, colOffset + 2);
                grid.Children.Add(actStack);

                cardBorder.Child = grid;
                DesktopCardsPanel.Children.Add(cardBorder);
            }
        }

        private void BtnCreateDesktop_Click(object sender, RoutedEventArgs e)
        {
            bool isRu = _config.Language == "ru";
            string prefix = isRu ? "Стол" : "Desktop";
            string newPath = AppConfig.GetUniqueDesktopPath(_config.Desktops, prefix, out string newName);

            try { if (!Directory.Exists(newPath)) Directory.CreateDirectory(newPath); } catch { }

            _config.Desktops.Add(new DesktopInfo
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = newName,
                Path = newPath
            });
            _config.Save();
            RefreshAllDynamicViews();
        }

        private void OpenRenameModalById(string desktopId)
        {
            var d = _config.Desktops.FirstOrDefault(x => x.Id == desktopId);
            if (d == null) return;

            _renamingDesktopId = d.Id;
            bool isRu = _config.Language == "ru";

            TxtRenameTitle.Text = isRu ? "Переименовать рабочий стол" : "Rename Workspace";
            TxtRenameSubtitle.Text = isRu ? "Введите новое название для этого стола" : "Enter a new name for this workspace";
            TxtRenamePathInfo.Text = (isRu ? "Папка: " : "Folder: ") + d.Path;
            BtnRenameCancel.Content = isRu ? "Отмена" : "Cancel";
            BtnRenameSave.Content = isRu ? "Сохранить" : "Save";
            TxtRenameInput.Text = d.Name;

            SetModalState(RenameModalOverlay, true, TxtRenameInput);
        }

        private void CloseRenameModal()
        {
            _renamingDesktopId = null;
            SetModalState(RenameModalOverlay, false);
        }

        private void BtnRenameCancel_Click(object sender, RoutedEventArgs e) => CloseRenameModal();

        private void BtnRenameSave_Click(object sender, RoutedEventArgs e) => SaveRenamedDesktop();

        private void SaveRenamedDesktop()
        {
            if (!string.IsNullOrEmpty(_renamingDesktopId))
            {
                var d = _config.Desktops.FirstOrDefault(x => x.Id == _renamingDesktopId);
                string newName = TxtRenameInput.Text.Trim();
                if (d != null && !string.IsNullOrWhiteSpace(newName))
                {
                    d.Name = newName;
                    _config.Save();
                    RefreshAllDynamicViews();
                }
            }
            CloseRenameModal();
        }

        private void TxtRenameInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SaveRenamedDesktop();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseRenameModal();
                e.Handled = true;
            }
        }

        private void RenameModal_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseRenameModal();
                e.Handled = true;
            }
        }

        #endregion
    }
}

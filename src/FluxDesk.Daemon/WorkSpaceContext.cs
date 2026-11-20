using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WorkSpace
{
    public class WorkSpaceContext : ApplicationContext
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static void Log(string msg) => AppConfig.AppendLog(msg);

        private readonly AppConfig _config;
        private readonly NotifyIcon _trayIcon;
        private readonly SwitcherWindow _switcher;
        private readonly KeyboardHook _keyboardHook;
        private readonly DarkMenuRenderer _darkRenderer = new DarkMenuRenderer();
        private readonly Font _menuFontRegular = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        private readonly Font _menuFontBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        private readonly System.Windows.Forms.Timer _configReloadDebounce;
        private readonly System.Windows.Forms.Timer _publicDesktopDebounce;
        private readonly object _publicDesktopLock = new object();
        private readonly object _cmdProcessLock = new object();
        private readonly object _wallpaperLock = new object();

        private FileSystemWatcher? _cmdWatcher;
        private FileSystemWatcher? _publicDesktopWatcher;
        private FileSystemWatcher? _configWatcher;

        private IntPtr _currentTrayHIcon = IntPtr.Zero;
        private int _isSwitching = 0;
        private int _switchGeneration = 0;
        private string? _pendingSwitchTargetId = null;
        private bool _pendingSwitchForce = false;
        private volatile string _lastRestoredId = "";
        private string? _lastSelfSavedJson = null;
        private int _revertedToPrimary = 0;
        private int _disposed = 0;

        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public WorkSpaceContext()
        {
            try
            {
                Log("1. Load config");
                _config = AppConfig.Load();

                // Capture initial Windows wallpaper as DefaultWallpaperPath if not recorded yet
                if (string.IsNullOrWhiteSpace(_config.DefaultWallpaperPath))
                {
                    string sysWp = ShellHelper.GetCurrentWallpaper();
                    if (!string.IsNullOrWhiteSpace(sysWp) && File.Exists(sysWp))
                    {
                        _config.DefaultWallpaperPath = sysWp;
                        SaveConfigSnapshot();
                    }
                }

                Log("2. Create SwitcherWindow & UI-thread handle");
                _switcher = new SwitcherWindow(_config, idx => SwitchDesktop(idx), OnEndSessionMessage);
                _ = _switcher.Handle;

                _configReloadDebounce = new System.Windows.Forms.Timer { Interval = 140 };
                _configReloadDebounce.Tick += (s, e) =>
                {
                    _configReloadDebounce.Stop();
                    ReloadConfigOnUiThread();
                };

                _publicDesktopDebounce = new System.Windows.Forms.Timer { Interval = 450 };
                _publicDesktopDebounce.Tick += (s, e) =>
                {
                    _publicDesktopDebounce.Stop();
                    TriggerPublicDesktopMove();
                };

                Log("3. Install KeyboardHook on dedicated hook thread");
                _keyboardHook = new KeyboardHook(_config, () => _switcher.IsSwitcherVisible, (actionId, desktopId) =>
                {
                    try
                    {
                        if (!IsDisposed && _switcher.IsHandleCreated)
                        {
                            _switcher.BeginInvoke(new Action(() => ProcessHotkey(actionId, desktopId)));
                        }
                    }
                    catch { }
                });

                Log("4. Create NotifyIcon");
                ToolStripManager.Renderer = _darkRenderer;
                _trayIcon = new NotifyIcon
                {
                    Text = "FluxDesk",
                    Visible = _config.ShowTrayIcon
                };
                _trayIcon.DoubleClick += (s, e) => LaunchUi();

                UpdateTrayIcon();
                UpdateTrayMenu();

                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                ShellHelper.SetAutoStart(_config.StartWithWindows, exePath);
                ShellHelper.SetContextMenuIntegration(_config.ContextMenuIntegration, AppConfig.FindUiExecutable());

                // Register in Windows "Apps & Features" so user can uninstall safely from Windows Settings
                ShellHelper.RegisterUninstallEntry(AppConfig.FindUiExecutable());

                if (_config.RestorePrimaryOnShutdown && _config.CurrentIndex != 0 && _config.Desktops.Count > 0)
                {
                    Log($"Startup: RestorePrimaryOnShutdown is active, resetting from {_config.CurrentIndex} to 0.");
                    _config.CurrentIndex = 0;
                    SaveCurrentIndexAtomically(_config.Desktops[0].Id);
                }

                Log("5. SwitchDesktop to current: " + _config.CurrentIndex);
                _lastRestoredId = "";
                SwitchDesktop(_config.CurrentIndex, force: true, isStartup: true);

                Application.ApplicationExit += OnExit;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                Application.ThreadException += OnThreadException;

                SetupCommandWatcher();
                SetupPublicDesktopWatcher();
                SetupConfigWatcher();

                Log("Context constructor finished successfully!");
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION in WorkSpaceContext constructor: {ex}");
                Dispose(true);
                throw;
            }
        }

        private void SaveConfigSnapshot()
        {
            string? saved = _config.Save();
            if (saved != null) _lastSelfSavedJson = saved;
        }

        private void SaveCurrentIndexAtomically(string activeDesktopId)
        {
            string? saved = AppConfig.UpdateAndSave(cfg =>
            {
                int idx = cfg.Desktops.FindIndex(d => d.Id == activeDesktopId);
                if (idx >= 0) cfg.CurrentIndex = idx;
                if (string.IsNullOrWhiteSpace(cfg.DefaultWallpaperPath) && !string.IsNullOrWhiteSpace(_config.DefaultWallpaperPath))
                {
                    cfg.DefaultWallpaperPath = _config.DefaultWallpaperPath;
                }
            }, _config);
            if (saved != null) _lastSelfSavedJson = saved;
        }

        private void SetWallpaperAsync(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return;
            string pathCopy = imagePath;
            Task.Run(() =>
            {
                lock (_wallpaperLock)
                {
                    if (IsDisposed) return;
                    string curWp = ShellHelper.GetCurrentWallpaper();
                    if (!string.Equals(curWp, pathCopy, StringComparison.OrdinalIgnoreCase))
                    {
                        ShellHelper.SetWallpaper(pathCopy);
                    }
                }
            });
        }

        private string GetEffectiveWallpaperFor(DesktopInfo desktop)
        {
            if (_config.IndependentWallpapers &&
                !string.IsNullOrWhiteSpace(desktop.WallpaperPath) &&
                File.Exists(desktop.WallpaperPath))
            {
                return desktop.WallpaperPath;
            }
            return _config.DefaultWallpaperPath ?? "";
        }

        private static void LaunchUi()
        {
            try
            {
                string? uiExe = AppConfig.FindUiExecutable();
                if (!string.IsNullOrEmpty(uiExe))
                {
                    using var p = Process.Start(new ProcessStartInfo(uiExe) { UseShellExecute = true });
                }
            }
            catch { }
        }

        private static string GetPublicDesktopPath()
        {
            string pub = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrEmpty(pub) && Directory.Exists(pub)) return pub;

            string? pubEnv = Environment.GetEnvironmentVariable("PUBLIC");
            if (!string.IsNullOrEmpty(pubEnv))
            {
                string fallback = Path.Combine(pubEnv, "Desktop");
                if (Directory.Exists(fallback)) return fallback;
            }
            return "";
        }

        private void SetupPublicDesktopWatcher()
        {
            try
            {
                _publicDesktopWatcher?.Dispose();
                _publicDesktopWatcher = null;

                string pub = GetPublicDesktopPath();
                if (string.IsNullOrEmpty(pub)) return;

                _publicDesktopWatcher = new FileSystemWatcher(pub)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 32768,
                    EnableRaisingEvents = true
                };

                void ScheduleMove()
                {
                    try
                    {
                        if (IsDisposed || !_switcher.IsHandleCreated) return;
                        _switcher.BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed || !_config.AutoMovePublicDesktopItems) return;
                            _publicDesktopDebounce.Stop();
                            _publicDesktopDebounce.Start();
                        }));
                    }
                    catch { }
                }

                _publicDesktopWatcher.Created += (s, e) => ScheduleMove();
                _publicDesktopWatcher.Renamed += (s, e) => ScheduleMove();
                _publicDesktopWatcher.Error += (s, e) =>
                {
                    Log($"[PublicDesktop] Watcher error: {e.GetException().Message}");
                    if (!IsDisposed && _switcher.IsHandleCreated)
                    {
                        _switcher.BeginInvoke(new Action(() => { if (!IsDisposed) SetupPublicDesktopWatcher(); }));
                    }
                };
            }
            catch (Exception ex)
            {
                Log($"Error in SetupPublicDesktopWatcher: {ex.Message}");
            }
        }

        private void TriggerPublicDesktopMove()
        {
            if (IsDisposed || !_config.AutoMovePublicDesktopItems) return;
            if (_config.CurrentIndex < 0 || _config.CurrentIndex >= _config.Desktops.Count) return;

            string targetPath = _config.Desktops[_config.CurrentIndex].Path;
            Task.Run(() => MovePublicDesktopFiles(targetPath));
        }

        public void MovePublicDesktopFiles(string targetDesktopPath)
        {
            lock (_publicDesktopLock)
            {
                try
                {
                    string pub = GetPublicDesktopPath();
                    if (string.IsNullOrEmpty(pub) || !Directory.Exists(targetDesktopPath)) return;

                    var files = Directory.GetFiles(pub);
                    bool movedAny = false;
                    foreach (var file in files)
                    {
                        string name = Path.GetFileName(file);
                        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                        string dest = Path.Combine(targetDesktopPath, name);
                        try
                        {
                            if (File.Exists(dest))
                            {
                                string baseName = Path.GetFileNameWithoutExtension(name);
                                string ext = Path.GetExtension(name);
                                int counter = 1;
                                do
                                {
                                    dest = Path.Combine(targetDesktopPath, $"{baseName} ({counter++}){ext}");
                                } while (File.Exists(dest));
                            }
                            File.Move(file, dest);
                            movedAny = true;
                            Log($"[PublicDesktop] Auto-moved '{name}' -> '{dest}'");
                        }
                        catch (Exception ex)
                        {
                            Log($"[PublicDesktop] Failed to move '{name}': {ex.Message}");
                        }
                    }

                    // NEVER call SetDesktopFolder from a background thread (would hijack active desktop!); only notify directory update
                    if (movedAny)
                    {
                        ShellHelper.NotifyDirectoryUpdated(pub);
                        ShellHelper.NotifyDirectoryUpdated(targetDesktopPath);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error in MovePublicDesktopFiles: {ex.Message}");
                }
            }
        }

        private void SetupCommandWatcher()
        {
            try
            {
                _cmdWatcher?.Dispose();
                _cmdWatcher = null;

                string cmdDir = AppConfig.CommandsDir;
                if (!Directory.Exists(cmdDir)) Directory.CreateDirectory(cmdDir);

                _cmdWatcher = new FileSystemWatcher(cmdDir, "*.cmd")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    InternalBufferSize = 32768,
                    EnableRaisingEvents = true
                };

                _cmdWatcher.Created += (s, e) => DrainCommandQueue();
                _cmdWatcher.Changed += (s, e) => DrainCommandQueue();
                _cmdWatcher.Renamed += (s, e) => DrainCommandQueue();
                _cmdWatcher.Error += (s, e) =>
                {
                    Log($"[IPC] Command watcher error: {e.GetException().Message}");
                    if (!IsDisposed && _switcher.IsHandleCreated)
                    {
                        _switcher.BeginInvoke(new Action(() =>
                        {
                            if (!IsDisposed) SetupCommandWatcher();
                        }));
                    }
                };

                // Drain any commands written while Daemon was starting up!
                DrainCommandQueue();
            }
            catch (Exception ex)
            {
                Log($"Error in SetupCommandWatcher: {ex.Message}");
            }
        }

        private void DrainCommandQueue()
        {
            if (IsDisposed) return;

            lock (_cmdProcessLock)
            {
                try
                {
                    string cmdDir = AppConfig.CommandsDir;
                    if (!Directory.Exists(cmdDir)) return;

                    var files = Directory.GetFiles(cmdDir, "*.cmd")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var file in files)
                    {
                        if (IsDisposed) return;

                        string procFile = file + ".proc";
                        string text = "";

                        for (int attempt = 0; attempt < 4; attempt++)
                        {
                            if (!File.Exists(file)) break;
                            try
                            {
                                // Claim exclusively by renaming BEFORE reading so no second thread or overwrite can race
                                File.Move(file, procFile, overwrite: true);
                                text = File.ReadAllText(procFile).Trim();
                                try { File.Delete(procFile); } catch { }
                                break;
                            }
                            catch (IOException) { Thread.Sleep(20); }
                            catch (UnauthorizedAccessException) { Thread.Sleep(20); }
                        }

                        if (!string.IsNullOrEmpty(text))
                        {
                            DispatchIpcCommand(text);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[IPC] DrainCommandQueue error: {ex.Message}");
                }
            }
        }

        private void DispatchIpcCommand(string text)
        {
            Log($"[IPC] Command received: {text}");
            if (IsDisposed || !_switcher.IsHandleCreated) return;

            if (text.Equals("exit_restore", StringComparison.OrdinalIgnoreCase))
            {
                _switcher.BeginInvoke(new Action(() =>
                {
                    ShellHelper.SetAutoStart(false, "");
                    ShellHelper.SetContextMenuIntegration(false, null);
                    ExitThread();
                }));
            }
            else if (text.StartsWith("switch_id:", StringComparison.OrdinalIgnoreCase))
            {
                string targetId = text.Substring(10).Trim();
                _switcher.BeginInvoke(new Action(() =>
                {
                    // Ensure latest config is loaded before resolving switch_id (prevents UI delete + switch race!)
                    ReloadConfigOnUiThread(suppressPreviewRefresh: true);
                    int idx = _config.Desktops.FindIndex(d => d.Id == targetId);
                    if (idx >= 0) SwitchDesktop(idx);
                }));
            }
            else if (text.StartsWith("switch:", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(text.Substring(7).Trim(), out int idx))
            {
                _switcher.BeginInvoke(new Action(() =>
                {
                    ReloadConfigOnUiThread(suppressPreviewRefresh: true);
                    if (idx >= 0 && idx < _config.Desktops.Count) SwitchDesktop(idx);
                }));
            }
            else if (text.Equals("refresh_previews", StringComparison.OrdinalIgnoreCase))
            {
                _switcher.BeginInvoke(new Action(() =>
                {
                    ReloadConfigOnUiThread(suppressPreviewRefresh: true);
                    _switcher.ClearPreviewCache();
                    RefreshAllPreviewsAsync();
                }));
            }
        }

        private void SetupConfigWatcher()
        {
            try
            {
                _configWatcher?.Dispose();
                _configWatcher = null;

                string dir = AppConfig.ConfigDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                _configWatcher = new FileSystemWatcher(dir, "config.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    InternalBufferSize = 32768,
                    EnableRaisingEvents = true
                };

                void ScheduleReload()
                {
                    try
                    {
                        if (IsDisposed || !_switcher.IsHandleCreated) return;
                        _switcher.BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed) return;
                            _configReloadDebounce.Stop();
                            _configReloadDebounce.Start();
                        }));
                    }
                    catch { }
                }

                _configWatcher.Changed += (s, e) => ScheduleReload();
                _configWatcher.Created += (s, e) => ScheduleReload();
                _configWatcher.Renamed += (s, e) => ScheduleReload();
                _configWatcher.Error += (s, e) =>
                {
                    Log($"[Config] Watcher error: {e.GetException().Message}");
                    if (!IsDisposed && _switcher.IsHandleCreated)
                    {
                        _switcher.BeginInvoke(new Action(() =>
                        {
                            if (!IsDisposed)
                            {
                                SetupConfigWatcher();
                                ReloadConfigOnUiThread();
                            }
                        }));
                    }
                };
            }
            catch (Exception ex)
            {
                Log($"Error in SetupConfigWatcher: {ex.Message}");
            }
        }

        private void ReloadConfigOnUiThread(bool suppressPreviewRefresh = false)
        {
            if (IsDisposed) return;
            var newCfg = AppConfig.TryLoadExisting();
            if (newCfg == null || newCfg.Desktops.Count == 0) return;

            // Suppress self-echo storm when Daemon itself just saved config.json during SwitchDesktop!
            string incomingJson = System.Text.Json.JsonSerializer.Serialize(newCfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            if (string.Equals(incomingJson, _lastSelfSavedJson, StringComparison.Ordinal))
            {
                return;
            }

            DesktopInfo? prevActiveObj = (_config.CurrentIndex >= 0 && _config.CurrentIndex < _config.Desktops.Count)
                ? _config.Desktops[_config.CurrentIndex]
                : null;
            string prevActiveId = prevActiveObj?.Id ?? "";
            string prevActivePath = prevActiveObj?.Path ?? "";
            string prevEffectiveWp = prevActiveObj != null ? GetEffectiveWallpaperFor(prevActiveObj) : "";
            string prevWeItem = prevActiveObj?.WallpaperEngineItem ?? "";

            bool prevAutoStart = _config.StartWithWindows;
            bool prevCtxMenu = _config.ContextMenuIntegration;
            bool prevIndWp = _config.IndependentWallpapers;
            bool prevShowIcons = _config.ShowPreviewIcons;
            bool prevWeEnabled = _config.WallpaperEngineEnabled;
            string prevWeExe = _config.WallpaperEngineExe ?? "";
            var prevIds = new HashSet<string>(_config.Desktops.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

            _config.Desktops = newCfg.Desktops;
            _config.Language = newCfg.Language;
            _config.StartWithWindows = newCfg.StartWithWindows;
            _config.RestorePrimaryOnShutdown = newCfg.RestorePrimaryOnShutdown;
            _config.AutoMovePublicDesktopItems = newCfg.AutoMovePublicDesktopItems;
            _config.IndependentWallpapers = newCfg.IndependentWallpapers;
            if (!string.IsNullOrWhiteSpace(newCfg.DefaultWallpaperPath))
                _config.DefaultWallpaperPath = newCfg.DefaultWallpaperPath;
            _config.ContextMenuIntegration = newCfg.ContextMenuIntegration;
            _config.ShowTrayIcon = newCfg.ShowTrayIcon;
            _config.QuickSwitcherEnabled = newCfg.QuickSwitcherEnabled;
            _config.ShowPreviewIcons = newCfg.ShowPreviewIcons;
            _config.WallpaperEngineEnabled = newCfg.WallpaperEngineEnabled;
            _config.WallpaperEngineExe = newCfg.WallpaperEngineExe;
            _config.HotkeysEnabled = newCfg.HotkeysEnabled;
            _config.HotkeyModifier = string.IsNullOrWhiteSpace(newCfg.HotkeyModifier) ? "Alt" : newCfg.HotkeyModifier;
            _config.DesktopHotkeys = newCfg.DesktopHotkeys ?? new Dictionary<string, int>();
            _config.ScheduleEnabled = newCfg.ScheduleEnabled;
            _config.Schedules = newCfg.Schedules ?? new List<ScheduleRule>();
            _config.Triggers = newCfg.Triggers ?? new List<SwitchTrigger>();
            _config.IsTransientFallback = false;

            if (prevShowIcons != _config.ShowPreviewIcons)
            {
                _switcher.ClearPreviewCache();
            }

            _keyboardHook.UpdateHotkeyMap(_config);
            _trayIcon.Visible = _config.ShowTrayIcon;

            if (_config.StartWithWindows != prevAutoStart)
            {
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                ShellHelper.SetAutoStart(_config.StartWithWindows, exePath);
            }

            if (_config.ContextMenuIntegration != prevCtxMenu)
            {
                ShellHelper.SetContextMenuIntegration(_config.ContextMenuIntegration, AppConfig.FindUiExecutable());
            }

            // Mark any deleted desktop IDs in PreviewGenerator so queued renders never resurrect them
            var newIds = new HashSet<string>(_config.Desktops.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var oldId in prevIds)
            {
                if (!newIds.Contains(oldId)) DesktopPreviewGenerator.DeletePreview(oldId);
            }

            int matchedIndex = string.IsNullOrEmpty(prevActiveId)
                ? -1
                : _config.Desktops.FindIndex(d => d.Id == prevActiveId);

            if (matchedIndex >= 0)
            {
                _config.CurrentIndex = matchedIndex;
                var curDesktop = _config.Desktops[matchedIndex];

                // If Default Folder (or active desktop path) changed in UI, apply it live!
                if (!string.IsNullOrWhiteSpace(prevActivePath) &&
                    !string.Equals(prevActivePath.TrimEnd('\\', '/'), curDesktop.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Active desktop path changed externally ('{prevActivePath}' -> '{curDesktop.Path}'); re-applying live.");
                    _lastRestoredId = "";
                    SwitchDesktop(matchedIndex, force: true);
                    return;
                }

                // Apply live wallpaper change (including clearing a wallpaper OR toggling IndependentWallpapers OFF!)
                string newEffectiveWp = GetEffectiveWallpaperFor(curDesktop);
                if (!string.Equals(prevEffectiveWp, newEffectiveWp, StringComparison.OrdinalIgnoreCase) ||
                    prevIndWp != _config.IndependentWallpapers)
                {
                    if (!string.IsNullOrWhiteSpace(newEffectiveWp) && File.Exists(newEffectiveWp))
                    {
                        SetWallpaperAsync(newEffectiveWp);
                    }
                }

                // Apply live Wallpaper Engine change if active desktop's item changed OR enabled state changed!
                string curWeItem = curDesktop.WallpaperEngineItem ?? "";
                if (!string.Equals(prevWeItem, curWeItem, StringComparison.OrdinalIgnoreCase) ||
                    prevWeEnabled != _config.WallpaperEngineEnabled ||
                    !string.Equals(prevWeExe, _config.WallpaperEngineExe, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyWallpaperEngineAsync(curDesktop);
                }

                UpdateTrayIcon();
                UpdateTrayMenu();
                _switcher.SyncWithConfig();
                if (!suppressPreviewRefresh || prevShowIcons != _config.ShowPreviewIcons)
                {
                    RefreshAllPreviewsAsync();
                }
            }
            else
            {
                Log("Active desktop was deleted externally; switching Windows Desktop back to 0.");
                _config.CurrentIndex = 0;
                _lastRestoredId = "";
                _switcher.SyncWithConfig();
                SwitchDesktop(0, force: true);
            }
        }

        private void RefreshAllPreviewsAsync()
        {
            if (IsDisposed || _config.Desktops.Count == 0) return;
            if (Volatile.Read(ref _isSwitching) != 0) return; // Never snapshot half-switched Explorer icons

            string activeId = (_config.CurrentIndex >= 0 && _config.CurrentIndex < _config.Desktops.Count)
                ? _config.Desktops[_config.CurrentIndex].Id
                : "";
            var snapshotDesktops = _config.Desktops
                .Select(d => new DesktopInfo { Id = d.Id, Name = d.Name, Path = d.Path, WallpaperPath = d.WallpaperPath, WallpaperEngineItem = d.WallpaperEngineItem })
                .ToList();
            bool indWp = _config.IndependentWallpapers;
            string defWp = _config.DefaultWallpaperPath ?? "";
            bool showIcons = _config.ShowPreviewIcons;

            // Offload GetCurrentIcons() off the UI thread so WH_KEYBOARD_LL and WinForms stay 100% responsive
            Task.Run(() =>
            {
                if (IsDisposed || Volatile.Read(ref _isSwitching) != 0) return;
                var liveIcons = IconLayoutManager.GetCurrentIcons();
                var activeDesk = snapshotDesktops.FirstOrDefault(d => d.Id == activeId);
                if (activeDesk != null)
                {
                    DesktopPreviewGenerator.QueueRender(activeDesk, indWp, defWp, liveIcons, showIcons);
                }
                foreach (var d in snapshotDesktops)
                {
                    if (d.Id == activeId) continue;
                    DesktopPreviewGenerator.QueueRender(d, indWp, defWp, null, showIcons);
                }
            });
        }

        private static void SanitizeClipboardBeforeDesktopSwitch(string? prevDesktopPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prevDesktopPath)) return;
                if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return;

                string prevNorm = Path.GetFullPath(prevDesktopPath).TrimEnd('\\', '/');
                string prevPrefix = prevNorm + Path.DirectorySeparatorChar;

                // Single GetDataObject() call prevents TOCTOU mismatch between FileDrop and Preferred DropEffect!
                var currentData = Clipboard.GetDataObject();
                if (currentData == null) return;

                if (!currentData.GetDataPresent("Shell IDList Array")) return;
                if (!currentData.GetDataPresent(DataFormats.FileDrop)) return;

                if (currentData.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
                    return;

                // ONLY rewrite clipboard if at least one copied item lives inside the outgoing desktop folder!
                // (Prevents hijacking non-desktop Cut/Copy, Win+V duplicates, and offline UNC File.Exists freezes!)
                bool referencesOutgoingDesktop = false;
                foreach (string? p in files)
                {
                    if (string.IsNullOrWhiteSpace(p) || p.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                    string norm = p.TrimEnd('\\', '/');
                    if (norm.StartsWith(prevPrefix, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(norm, prevNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        referencesOutgoingDesktop = true;
                        break;
                    }
                }
                if (!referencesOutgoingDesktop) return;

                var validFiles = new System.Collections.Specialized.StringCollection();
                foreach (string? path in files)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (!path.StartsWith(@"\\", StringComparison.Ordinal) &&
                        (File.Exists(path) || Directory.Exists(path)))
                    {
                        validFiles.Add(path);
                    }
                }
                if (validFiles.Count == 0) return;

                byte[]? effect4Bytes = null;
                if (currentData.GetDataPresent("Preferred DropEffect"))
                {
                    object? raw = currentData.GetData("Preferred DropEffect");
                    if (raw is MemoryStream ms)
                    {
                        using (ms)
                        {
                            byte[] all = ms.ToArray();
                            if (all.Length >= 4)
                            {
                                effect4Bytes = new byte[4];
                                Buffer.BlockCopy(all, 0, effect4Bytes, 0, 4);
                            }
                        }
                    }
                    else if (raw is byte[] bytes && bytes.Length >= 4)
                    {
                        effect4Bytes = new byte[4];
                        Buffer.BlockCopy(bytes, 0, effect4Bytes, 0, 4);
                    }
                }

                var cleanData = new DataObject();
                cleanData.SetFileDropList(validFiles);

                if (effect4Bytes != null)
                {
                    using var effectStream = new MemoryStream(effect4Bytes);
                    cleanData.SetData("Preferred DropEffect", effectStream);
                    Clipboard.SetDataObject(cleanData, true, 5, 25);
                }
                else
                {
                    Clipboard.SetDataObject(cleanData, true, 5, 25);
                }

                Log($"[Clipboard] Anchored {validFiles.Count} desktop item(s) to physical paths before switch.");
            }
            catch (Exception ex)
            {
                Log($"[Clipboard] Sanitize skipped: {ex.Message}");
            }
        }

        public void ProcessHotkey(int actionId, string? targetDesktopId = null)
        {
            if (IsDisposed) return;
            try
            {
                switch (actionId)
                {
                    case KeyboardHook.ACTION_CYCLE_NEXT:
                        _switcher.ShowSwitcher(advanceSelection: true);
                        break;
                    case KeyboardHook.ACTION_CYCLE_PREV:
                        _switcher.ShowSwitcher(advanceSelection: false);
                        break;
                    case KeyboardHook.ACTION_NAV_LEFT:
                    case KeyboardHook.ACTION_NAV_RIGHT:
                    case KeyboardHook.ACTION_NAV_UP:
                    case KeyboardHook.ACTION_NAV_DOWN:
                    case KeyboardHook.ACTION_SWITCHER_COMMIT:
                    case KeyboardHook.ACTION_SWITCHER_CANCEL:
                        _switcher.HandleHookAction(actionId);
                        break;
                    case KeyboardHook.ACTION_DIRECT_DESKTOP when !string.IsNullOrEmpty(targetDesktopId):
                        int target = _config.Desktops.FindIndex(d => d.Id == targetDesktopId);
                        if (target >= 0 && target < _config.Desktops.Count)
                        {
                            SwitchDesktop(target);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"Error in ProcessHotkey: {ex.Message}");
            }
        }

        public void SwitchDesktop(int index, bool force = false, bool isStartup = false)
        {
            if (IsDisposed) return;
            if (index < 0 || index >= _config.Desktops.Count) return;
            if (!force && index == _config.CurrentIndex) return;

            if (Interlocked.CompareExchange(ref _isSwitching, 1, 0) != 0)
            {
                // Coalesce instead of dropping so critical switches (e.g. active-desktop deletion recovery) always execute!
                _pendingSwitchTargetId = _config.Desktops[index].Id;
                _pendingSwitchForce = _pendingSwitchForce || force;
                Log($"SwitchDesktop({index}) queued behind active switch.");
                return;
            }

            try
            {
                int myGen = Interlocked.Increment(ref _switchGeneration);

                var curIconsBeforeSwitch = IconLayoutManager.GetCurrentIcons();
                int oldItemCount = curIconsBeforeSwitch.Count;

                DesktopInfo? prevDesktopObj = (_config.CurrentIndex >= 0 && _config.CurrentIndex < _config.Desktops.Count)
                    ? _config.Desktops[_config.CurrentIndex]
                    : null;
                string prevId = prevDesktopObj?.Id ?? "";
                string prevPath = prevDesktopObj?.Path ?? "";

                if (!force && !string.IsNullOrEmpty(prevId) && _lastRestoredId == prevId)
                {
                    if (curIconsBeforeSwitch.Count > 0)
                    {
                        IconLayoutManager.SaveLayoutFromList(prevId, curIconsBeforeSwitch);
                    }
                    if (prevDesktopObj != null)
                    {
                        DesktopPreviewGenerator.QueueRender(prevDesktopObj, _config.IndependentWallpapers, _config.DefaultWallpaperPath, curIconsBeforeSwitch, _config.ShowPreviewIcons);
                    }
                }
                else
                {
                    Log($"SwitchDesktop: skipped SaveLayout for '{prevId}' (force={force}, lastRestored='{_lastRestoredId}')");
                }

                var target = _config.Desktops[index];
                Log($"Switching to desktop {index} ({target.Id}): {target.Name} -> {target.Path}");

                if (!Directory.Exists(target.Path))
                {
                    Directory.CreateDirectory(target.Path);
                }

                if (_config.AutoMovePublicDesktopItems)
                {
                    string tp = target.Path;
                    Task.Run(() => MovePublicDesktopFiles(tp));
                }

                // Anchor Desktop-origin clipboard items to real physical paths BEFORE redirecting FOLDERID_Desktop
                if (!isStartup)
                {
                    SanitizeClipboardBeforeDesktopSwitch(prevPath);
                }

                bool ok = ShellHelper.SetDesktopFolder(target.Path);
                if (!ok)
                {
                    Log($"SwitchDesktop({index}) aborted: SetDesktopFolder returned false.");
                    return;
                }

                _config.CurrentIndex = index;
                if (!isStartup)
                {
                    SaveCurrentIndexAtomically(target.Id);
                }

                // Apply target desktop wallpaper (or DefaultWallpaperPath if custom wallpaper is cleared/disabled)
                if (_config.IndependentWallpapers)
                {
                    string effectiveWp = GetEffectiveWallpaperFor(target);
                    if (!string.IsNullOrWhiteSpace(effectiveWp))
                    {
                        SetWallpaperAsync(effectiveWp);
                    }
                }

                // Apply Wallpaper Engine if enabled
                ApplyWallpaperEngineAsync(target);

                if (!force)
                {
                    // Desktop switched
                }

                string targetId = target.Id;
                string targetPath = target.Path;
                var targetSnapshot = new DesktopInfo
                {
                    Id = target.Id,
                    Name = target.Name,
                    Path = target.Path,
                    WallpaperPath = target.WallpaperPath,
                    WallpaperEngineItem = target.WallpaperEngineItem
                };
                bool indWp = _config.IndependentWallpapers;
                string defWp = _config.DefaultWallpaperPath ?? "";
                bool showIcons = _config.ShowPreviewIcons;
                bool renderAllAfter = isStartup;
                var allDesktopsSnapshot = isStartup
                    ? _config.Desktops.Select(d => new DesktopInfo { Id = d.Id, Name = d.Name, Path = d.Path, WallpaperPath = d.WallpaperPath, WallpaperEngineItem = d.WallpaperEngineItem }).ToList()
                    : null;
                Func<bool> isCancelled = () => IsDisposed || Volatile.Read(ref _switchGeneration) != myGen;

                Task.Run(() =>
                {
                    try
                    {
                        bool transitioned = IconLayoutManager.WaitForDesktopChange(
                            oldItemCount, prevPath, targetPath, timeoutMs: 1500, isCancelled: isCancelled);
                        if (isCancelled()) return;

                        IconLayoutManager.RestoreLayout(targetId, isCancelled: isCancelled, allowDirtySave: transitioned);
                        if (isCancelled()) return;

                        if (transitioned && _switcher.IsHandleCreated)
                        {
                            // Commit _lastRestoredId atomically on UI thread only if generation still matches
                            _switcher.BeginInvoke(new Action(() =>
                            {
                                if (!IsDisposed && Volatile.Read(ref _switchGeneration) == myGen)
                                {
                                    _lastRestoredId = targetId;
                                }
                            }));
                        }

                        Thread.Sleep(150);
                        if (isCancelled()) return;

                        IconLayoutManager.RestoreLayout(targetId, isCancelled: isCancelled, allowDirtySave: transitioned);
                        if (isCancelled()) return;

                        var freshIcons = IconLayoutManager.GetCurrentIcons();
                        DesktopPreviewGenerator.QueueRender(targetSnapshot, indWp, defWp, freshIcons, showIcons);

                        if (renderAllAfter && allDesktopsSnapshot != null)
                        {
                            foreach (var d in allDesktopsSnapshot)
                            {
                                if (d.Id != targetId)
                                {
                                    DesktopPreviewGenerator.QueueRender(d, indWp, defWp, null, showIcons);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Background RestoreLayout error: {ex.Message}");
                    }
                });

                UpdateTrayIcon();
                UpdateTrayMenu();
            }
            finally
            {
                Interlocked.Exchange(ref _isSwitching, 0);

                // Drain coalesced pending switch if one arrived while we were switching
                string? nextId = _pendingSwitchTargetId;
                bool nextForce = _pendingSwitchForce;
                _pendingSwitchTargetId = null;
                _pendingSwitchForce = false;

                if (!string.IsNullOrEmpty(nextId) && !IsDisposed && _switcher.IsHandleCreated)
                {
                    _switcher.BeginInvoke(new Action(() =>
                    {
                        int nextIdx = _config.Desktops.FindIndex(d => d.Id == nextId);
                        if (nextIdx >= 0) SwitchDesktop(nextIdx, force: nextForce);
                    }));
                }
            }
        }

        public void ConfirmAndDeleteDesktop(int index)
        {
            if (index <= 0 || index >= _config.Desktops.Count) return;

            var d = _config.Desktops[index];
            string targetId = d.Id; // Capture stable ID across modal TaskDialog message loop!
            bool isRu = (_config.Language == "ru");

            var page = new TaskDialogPage
            {
                Caption = isRu ? "Удаление рабочего стола" : "Delete Desktop",
                Heading = isRu ? $"Удалить «{d.Name}»?" : $"Delete \"{d.Name}\"?",
                Text = isRu
                    ? $"Выберите, что сделать с файлами этого рабочего стола:\n{d.Path}"
                    : $"Choose what to do with the files in this desktop:\n{d.Path}",
                Icon = TaskDialogIcon.Warning,
                AllowCancel = true
            };

            var btnRecycle = new TaskDialogCommandLinkButton(
                isRu ? "Отправить файлы в Корзину (освободить место)" : "Move files to Recycle Bin",
                isRu
                    ? "Папка стола со всеми файлами переместится в Корзину Windows. При необходимости файлы можно восстановить."
                    : "The desktop folder and its files will be sent to the Windows Recycle Bin (recoverable)."
            );

            var btnKeep = new TaskDialogCommandLinkButton(
                isRu ? "Оставить файлы на диске (только убрать стол)" : "Keep files on disk (remove desktop only)",
                isRu
                    ? "Стол удалится из переключения Alt+W, но все файлы останутся в сохранности на диске."
                    : "Removes this desktop from FluxDesk while keeping all files safely on disk."
            );

            page.Buttons.Add(btnRecycle);
            page.Buttons.Add(btnKeep);
            page.Buttons.Add(TaskDialogButton.Cancel);

            var result = TaskDialog.ShowDialog(page);
            if (result != btnRecycle && result != btnKeep) return;

            // Re-resolve by stable ID in case UI modified desktops while TaskDialog was open!
            int resolvedIndex = _config.Desktops.FindIndex(x => x.Id == targetId);
            if (resolvedIndex <= 0) return;

            DeleteDesktop(resolvedIndex, sendFilesToRecycleBin: result == btnRecycle);
        }

        private void DeleteDesktop(int index, bool sendFilesToRecycleBin)
        {
            if (index <= 0 || index >= _config.Desktops.Count) return;

            var d = _config.Desktops[index];

            if (_config.CurrentIndex == index)
            {
                SwitchDesktop(0, force: true);
                if (_config.CurrentIndex == index)
                {
                    Log($"DeleteDesktop({index}) aborted: could not switch away from active desktop.");
                    return;
                }
            }
            else if (_config.CurrentIndex > index)
            {
                _config.CurrentIndex--;
            }

            _config.Desktops.RemoveAt(index);
            _config.DesktopHotkeys?.Remove(d.Id);
            _config.Schedules?.RemoveAll(s => s.DesktopId == d.Id);
            _config.Triggers?.RemoveAll(t => t.DesktopId == d.Id);
            SaveConfigSnapshot();

            _keyboardHook.UpdateHotkeyMap(_config);
            _switcher.SyncWithConfig();
            IconLayoutManager.DeleteLayoutFile(d.Id);
            DesktopPreviewGenerator.DeletePreview(d.Id);

            if (sendFilesToRecycleBin && Directory.Exists(d.Path))
            {
                string liveDesktop = ShellHelper.GetLiveDesktopFolder();
                if (AppConfig.IsSafeToRecycleFolder(d.Path, _config.Desktops, liveDesktop, deletingIndex: -1))
                {
                    try
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            d.Path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        Log($"Sent folder to Recycle Bin: {d.Path}");
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to send folder to Recycle Bin: {ex.Message}");
                    }
                }
                else
                {
                    Log($"Skipped recycling '{d.Path}': folder failed safety check.");
                }
            }

            UpdateTrayMenu();
            UpdateTrayIcon();
        }

        private static string EscapeMenuText(string text) => text.Replace("&", "&&");

        private void UpdateTrayMenu()
        {
            bool isRu = (_config.Language == "ru");

            var menu = new ContextMenuStrip
            {
                Renderer = _darkRenderer,
                BackColor = Color.FromArgb(24, 26, 31),
                ForeColor = Color.FromArgb(235, 240, 248),
                Font = _menuFontRegular,
                Padding = new Padding(4, 6, 4, 6),
                ShowImageMargin = true,
                ShowCheckMargin = false
            };

            string headerText = isRu
                ? $"FluxDesk (Стол {_config.CurrentIndex + 1})"
                : $"FluxDesk (Desktop {_config.CurrentIndex + 1})";

            var titleItem = new ToolStripMenuItem(headerText)
            {
                Enabled = false,
                Tag = "header",
                Font = _menuFontBold,
                Padding = new Padding(0, 3, 8, 3)
            };
            menu.Items.Add(titleItem);
            menu.Items.Add(new ToolStripSeparator());

            int maxTrayDesktops = Math.Min(5, _config.Desktops.Count);
            for (int i = 0; i < maxTrayDesktops; i++)
            {
                int idx = i;
                var item = new ToolStripMenuItem($"{i + 1}. {EscapeMenuText(_config.Desktops[i].Name)}")
                {
                    Checked = (i == _config.CurrentIndex),
                    Padding = new Padding(0, 3, 8, 3)
                };
                item.Click += (s, e) => SwitchDesktop(idx);
                menu.Items.Add(item);
            }

            if (_config.Desktops.Count > 5)
            {
                if (_config.CurrentIndex >= 5 && _config.CurrentIndex < _config.Desktops.Count)
                {
                    int activeIdx = _config.CurrentIndex;
                    var activeItem = new ToolStripMenuItem($"{activeIdx + 1}. {EscapeMenuText(_config.Desktops[activeIdx].Name)}")
                    {
                        Checked = true,
                        Padding = new Padding(0, 3, 8, 3)
                    };
                    activeItem.Click += (s, e) => SwitchDesktop(activeIdx);
                    menu.Items.Add(activeItem);
                }

                string moreLabel = isRu
                    ? $"⋯  Все столы ({_config.Desktops.Count})..."
                    : $"⋯  All desktops ({_config.Desktops.Count})...";
                var moreDesktopsItem = new ToolStripMenuItem(moreLabel) { Padding = new Padding(0, 3, 8, 3) };
                moreDesktopsItem.Click += (s, e) => LaunchUi();
                menu.Items.Add(moreDesktopsItem);
            }

            menu.Items.Add(new ToolStripSeparator());

            var addItem = new ToolStripMenuItem(isRu ? "➕ Добавить новый стол..." : "➕ Add new desktop...")
            {
                Padding = new Padding(0, 3, 8, 3)
            };
            addItem.Click += (s, e) => AddNewDesktop();
            menu.Items.Add(addItem);

            var deleteMenu = new ToolStripMenuItem(isRu ? "🗑️ Удалить стол" : "🗑️ Delete desktop")
            {
                Padding = new Padding(0, 3, 8, 3)
            };
            deleteMenu.DropDown.Renderer = _darkRenderer;
            deleteMenu.DropDown.BackColor = Color.FromArgb(24, 26, 31);
            deleteMenu.DropDown.ForeColor = Color.FromArgb(235, 240, 248);
            deleteMenu.DropDown.Font = _menuFontRegular;
            deleteMenu.DropDown.Padding = new Padding(4, 6, 4, 6);

            for (int i = 1; i < _config.Desktops.Count; i++)
            {
                int idx = i;
                string delLabel = isRu
                    ? $"Удалить {i + 1}. {EscapeMenuText(_config.Desktops[i].Name)}"
                    : $"Delete {i + 1}. {EscapeMenuText(_config.Desktops[i].Name)}";
                var delItem = new ToolStripMenuItem(delLabel) { Padding = new Padding(0, 3, 8, 3) };
                delItem.Click += (s, e) => ConfirmAndDeleteDesktop(idx);
                deleteMenu.DropDownItems.Add(delItem);
            }
            if (_config.Desktops.Count <= 1) deleteMenu.Enabled = false;
            menu.Items.Add(deleteMenu);

            var openFolderItem = new ToolStripMenuItem(isRu ? "📂 Открыть папку этого стола" : "📂 Open active desktop folder")
            {
                Padding = new Padding(0, 3, 8, 3)
            };
            openFolderItem.Click += (s, e) =>
            {
                try
                {
                    if (_config.CurrentIndex >= 0 && _config.CurrentIndex < _config.Desktops.Count)
                    {
                        string path = _config.Desktops[_config.CurrentIndex].Path;
                        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                        var psi = new ProcessStartInfo("explorer.exe");
                        psi.ArgumentList.Add(path);
                        using var p = Process.Start(psi);
                    }
                }
                catch { }
            };
            menu.Items.Add(openFolderItem);

            var openUiItem = new ToolStripMenuItem(isRu ? "💻 Панель управления FluxDesk" : "💻 Open FluxDesk Control Panel")
            {
                Padding = new Padding(0, 3, 8, 3)
            };
            openUiItem.Click += (s, e) => LaunchUi();
            menu.Items.Add(openUiItem);

            var autoStartItem = new ToolStripMenuItem(isRu ? "⚙️ Запуск вместе с Windows" : "⚙️ Start with Windows")
            {
                Checked = _config.StartWithWindows,
                Padding = new Padding(0, 3, 8, 3)
            };
            autoStartItem.Click += (s, e) =>
            {
                _config.StartWithWindows = !_config.StartWithWindows;
                SaveConfigSnapshot();
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                ShellHelper.SetAutoStart(_config.StartWithWindows, exePath);
                autoStartItem.Checked = _config.StartWithWindows;
            };
            menu.Items.Add(autoStartItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem(isRu ? "❌ Выход (вернуть Стол 1)" : "❌ Exit (restore Desktop 1)")
            {
                Padding = new Padding(0, 3, 8, 3)
            };
            exitItem.Click += (s, e) => ExitThread();
            menu.Items.Add(exitItem);

            // Defer disposal of oldMenu via BeginInvoke so we NEVER dispose a ContextMenuStrip while its Click handler is unwinding!
            var oldMenu = _trayIcon.ContextMenuStrip;
            _trayIcon.ContextMenuStrip = menu;
            if (oldMenu != null)
            {
                if (!IsDisposed && _switcher.IsHandleCreated)
                {
                    _switcher.BeginInvoke(new Action(() =>
                    {
                        try { oldMenu.Dispose(); } catch { }
                    }));
                }
                else
                {
                    try { oldMenu.Dispose(); } catch { }
                }
            }
        }

        private void AddNewDesktop()
        {
            bool isRu = (_config.Language == "ru");
            string prefix = isRu ? "Стол" : "Desktop";
            string newPath = AppConfig.GetUniqueDesktopPath(_config.Desktops, prefix, out string newName);

            try { Directory.CreateDirectory(newPath); } catch { }

            _config.Desktops.Add(new DesktopInfo
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = newName,
                Path = newPath
            });
            SaveConfigSnapshot();
            _keyboardHook.UpdateHotkeyMap(_config);

            UpdateTrayMenu();
            SwitchDesktop(_config.Desktops.Count - 1);
        }

        private void UpdateTrayIcon()
        {
            IntPtr newHIcon = IntPtr.Zero;
            Icon? newIcon = null;
            try
            {
                using var bmp = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Transparent);

                    var rect = new Rectangle(1, 1, 29, 29);
                    int r = 7;
                    using var path = new GraphicsPath();
                    path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                    path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                    path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                    path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                    path.CloseFigure();

                    using var bgBrush = new SolidBrush(Color.FromArgb(20, 22, 27));
                    g.FillPath(bgBrush, path);

                    using var borderPen = new Pen(Color.FromArgb(190, 230, 253), 1.6f);
                    g.DrawPath(borderPen, path);

                    using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    string num = (_config.CurrentIndex + 1).ToString();
                    using var textBrush = new SolidBrush(Color.FromArgb(245, 248, 255));
                    g.DrawString(num, font, textBrush, new RectangleF(0, 0, 32, 32), sf);
                }

                newHIcon = bmp.GetHicon();
                newIcon = Icon.FromHandle(newHIcon);

                var oldIcon = _trayIcon.Icon;
                IntPtr oldHIcon = _currentTrayHIcon;

                // Assign _trayIcon.Icon BEFORE transferring ownership of _currentTrayHIcon
                _trayIcon.Icon = newIcon;
                _currentTrayHIcon = newHIcon;
                newHIcon = IntPtr.Zero;
                newIcon = null;

                if (oldIcon != null && !ReferenceEquals(oldIcon, SystemIcons.Application))
                {
                    oldIcon.Dispose();
                }
                if (oldHIcon != IntPtr.Zero)
                {
                    DestroyIcon(oldHIcon);
                }
            }
            catch (Exception ex)
            {
                newIcon?.Dispose();
                if (newHIcon != IntPtr.Zero) DestroyIcon(newHIcon);
                Log($"TrayIcon error: {ex.Message}");
                _trayIcon.Icon = SystemIcons.Application;
            }
        }

        private void SaveCurrentLayoutIfRestored()
        {
            try
            {
                string curId = (_config.CurrentIndex >= 0 && _config.CurrentIndex < _config.Desktops.Count)
                    ? _config.Desktops[_config.CurrentIndex].Id
                    : "";
                if (!string.IsNullOrEmpty(curId) && _lastRestoredId == curId)
                {
                    var cur = IconLayoutManager.GetCurrentIcons();
                    if (cur.Count > 0) IconLayoutManager.SaveLayoutFromList(curId, cur);
                }
            }
            catch { }
        }

        private void SafeRevertToPrimaryDesktop(string reason, bool isShuttingDown)
        {
            if (Interlocked.Exchange(ref _revertedToPrimary, 1) == 1) return;
            Log($"SafeRevertToPrimaryDesktop ({reason}): reverting to desktop 0");

            if (!isShuttingDown)
            {
                SaveCurrentLayoutIfRestored();
            }

            try
            {
                if (_config.Desktops.Count > 0 && !string.IsNullOrWhiteSpace(_config.Desktops[0].Path))
                {
                    bool ok = ShellHelper.SetDesktopFolder(_config.Desktops[0].Path);
                    if (ok)
                    {
                        _config.CurrentIndex = 0;
                        SaveCurrentIndexAtomically(_config.Desktops[0].Id);
                    }

                    // Restore primary/default wallpaper if independent wallpapers was active
                    if (_config.IndependentWallpapers)
                    {
                        string primaryWp = !string.IsNullOrWhiteSpace(_config.Desktops[0].WallpaperPath) && File.Exists(_config.Desktops[0].WallpaperPath)
                            ? _config.Desktops[0].WallpaperPath
                            : _config.DefaultWallpaperPath;
                        if (!string.IsNullOrWhiteSpace(primaryWp) && File.Exists(primaryWp))
                        {
                            ShellHelper.SetWallpaper(primaryWp);
                        }
                    }

                    if (_config.WallpaperEngineEnabled)
                    {
                        ApplyWallpaperEngineAsync(_config.Desktops[0]);
                    }
                }
            }
            catch { }
        }

        private void ApplyWallpaperEngineAsync(DesktopInfo target)
        {
            if (!_config.WallpaperEngineEnabled)
            {
                // If disabled while running, stop any active live wallpaper
                string maybeExe = AppConfig.GetActiveOrConfiguredWallpaperEngineExe(_config.WallpaperEngineExe);
                if (!string.IsNullOrWhiteSpace(maybeExe) && File.Exists(maybeExe))
                {
                    bool isRunning = Process.GetProcessesByName("wallpaper32").Length > 0 ||
                                     Process.GetProcessesByName("wallpaper64").Length > 0;
                    if (isRunning)
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = maybeExe,
                                    Arguments = "-control stop",
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };
                                using var p = Process.Start(psi);
                                p?.WaitForExit(3000);
                            }
                            catch { }
                        });
                    }
                }
                return;
            }

            string exe = AppConfig.GetActiveOrConfiguredWallpaperEngineExe(_config.WallpaperEngineExe);
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Log($"[WallpaperEngine] Executable not found. Configured: '{_config.WallpaperEngineExe}'");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    bool isRunning = Process.GetProcessesByName("wallpaper32").Length > 0 ||
                                     Process.GetProcessesByName("wallpaper64").Length > 0;

                    if (!isRunning && !string.IsNullOrWhiteSpace(target.WallpaperEngineItem))
                    {
                        Log($"[WallpaperEngine] Starting background process: {exe}");
                        var startPsi = new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = "-silent",
                            UseShellExecute = true
                        };
                        using var starter = Process.Start(startPsi);
                        Thread.Sleep(1000);
                    }

                    if (!string.IsNullOrWhiteSpace(target.WallpaperEngineItem) &&
                        (File.Exists(target.WallpaperEngineItem) || Directory.Exists(target.WallpaperEngineItem)))
                    {
                        Log($"[WallpaperEngine] Applying wallpaper for '{target.Name}': {target.WallpaperEngineItem}");
                        var psi = new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = $"-control openWallpaper -file \"{target.WallpaperEngineItem}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(3000);
                        Log($"[WallpaperEngine] openWallpaper exit code: {p?.ExitCode}");
                    }
                    else
                    {
                        Log($"[WallpaperEngine] Stopping live wallpaper for '{target.Name}' (no item selected)");
                        var psi = new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = "-control stop",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(3000);
                        Log($"[WallpaperEngine] stop exit code: {p?.ExitCode}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[WallpaperEngine] Switch error: {ex.Message}");
                }
            });
        }

        private void OnEndSessionMessage(bool actuallyEnding)
        {
            // WM_ENDSESSION with wParam == 0 means shutdown was CANCELLED by user/app -> do NOT revert or lock latch!
            if (!actuallyEnding)
            {
                Log("WM_ENDSESSION: shutdown cancelled by user/system.");
                return;
            }

            if (_config.RestorePrimaryOnShutdown)
            {
                SafeRevertToPrimaryDesktop("WM_ENDSESSION", isShuttingDown: true);
            }
            else
            {
                Log($"WM_ENDSESSION: RestorePrimaryOnShutdown is OFF — saving layout and keeping desktop {_config.CurrentIndex}.");
                SaveCurrentLayoutIfRestored();
                Interlocked.Exchange(ref _revertedToPrimary, 1);
            }
        }

        private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            Log($"UnhandledException: {e.ExceptionObject}");
            SafeRevertToPrimaryDesktop("UnhandledException", isShuttingDown: true);
            try { _trayIcon.Visible = false; } catch { }
        }

        private void OnThreadException(object? sender, ThreadExceptionEventArgs e)
        {
            Log($"ThreadException (recovered): {e.Exception}");
        }

        private void OnExit(object? sender, EventArgs e)
        {
            Dispose(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (disposing)
                {
                    // 1. Unhook keyboard & stop timers/watchers FIRST so nothing fires during teardown
                    try { _keyboardHook?.Dispose(); } catch { }
                    try { _configReloadDebounce?.Stop(); _configReloadDebounce?.Dispose(); } catch { }
                    try { _publicDesktopDebounce?.Stop(); _publicDesktopDebounce?.Dispose(); } catch { }
                    try { _cmdWatcher?.Dispose(); } catch { }
                    try { _configWatcher?.Dispose(); } catch { }
                    try { _publicDesktopWatcher?.Dispose(); } catch { }

                    // 2. Unhook global events
                    Application.ApplicationExit -= OnExit;
                    AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                    Application.ThreadException -= OnThreadException;

                    // 3. Revert Windows Desktop to Desktop 0 cleanly
                    SafeRevertToPrimaryDesktop("ApplicationExit", isShuttingDown: false);

                    // 4. Clean up preview worker, tray, GDI resources, and switcher
                    try { DesktopPreviewGenerator.Shutdown(); } catch { }
                    try
                    {
                        if (_trayIcon != null)
                        {
                            _trayIcon.Visible = false;
                            _trayIcon.ContextMenuStrip?.Dispose();
                            if (_trayIcon.Icon != null && !ReferenceEquals(_trayIcon.Icon, SystemIcons.Application))
                            {
                                _trayIcon.Icon.Dispose();
                            }
                            _trayIcon.Dispose();
                        }
                    }
                    catch { }
                    try { _menuFontRegular.Dispose(); } catch { }
                    try { _menuFontBold.Dispose(); } catch { }
                    try { _switcher?.Dispose(); } catch { }
                }
            }

            // Always release native HICON handle even on Dispose(false) finalization
            IntPtr hIcon = Interlocked.Exchange(ref _currentTrayHIcon, IntPtr.Zero);
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
            }

            base.Dispose(disposing);
        }
    }

    public class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color DarkBg = Color.FromArgb(24, 26, 31);
        private static readonly Color DarkBorder = Color.FromArgb(44, 46, 52);
        private static readonly Color DarkHover = Color.FromArgb(40, 43, 50);

        public override Color ToolStripDropDownBackground => DarkBg;
        public override Color ImageMarginGradientBegin => DarkBg;
        public override Color ImageMarginGradientMiddle => DarkBg;
        public override Color ImageMarginGradientEnd => DarkBg;
        public override Color ImageMarginRevealedGradientBegin => DarkBg;
        public override Color ImageMarginRevealedGradientMiddle => DarkBg;
        public override Color ImageMarginRevealedGradientEnd => DarkBg;

        public override Color MenuBorder => DarkBorder;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => DarkHover;
        public override Color MenuItemSelectedGradientBegin => DarkHover;
        public override Color MenuItemSelectedGradientEnd => DarkHover;
        public override Color MenuItemPressedGradientBegin => DarkHover;
        public override Color MenuItemPressedGradientEnd => DarkHover;

        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color ButtonCheckedHighlight => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;

        public override Color SeparatorDark => DarkBorder;
        public override Color SeparatorLight => Color.Transparent;
    }

    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = true;
        }

        private static GraphicsPath CreateRoundedRect(Rectangle r, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
            {
                var prevMode = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                try
                {
                    var rect = new Rectangle(4, 1, Math.Max(1, e.Item.Width - 8), Math.Max(1, e.Item.Height - 2));
                    using var path = CreateRoundedRect(rect, 5);
                    using var brush = new SolidBrush(Color.FromArgb(42, 45, 53));
                    e.Graphics.FillPath(brush, path);
                }
                finally
                {
                    e.Graphics.SmoothingMode = prevMode;
                }
            }
            else
            {
                using var bg = new SolidBrush(Color.FromArgb(24, 26, 31));
                e.Graphics.FillRectangle(bg, new Rectangle(Point.Empty, e.Item.Size));
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color textColor;
            if (e.Item.Tag as string == "header")
            {
                textColor = Color.FromArgb(190, 230, 253);
            }
            else if (!e.Item.Enabled)
            {
                textColor = Color.FromArgb(115, 120, 130);
            }
            else if (e.Item.Selected)
            {
                textColor = Color.FromArgb(255, 255, 255);
            }
            else
            {
                textColor = Color.FromArgb(232, 235, 240);
            }

            int x = e.TextRectangle.X + 2;
            int w = Math.Max(10, e.Item.Width - x - 20);
            var centeredRect = new Rectangle(x, 0, w, e.Item.Height - 1);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, centeredRect, textColor, flags);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            var prevMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                int cx = e.ImageRectangle.Left + e.ImageRectangle.Width / 2;
                int cy = e.Item.Height / 2;

                using var checkPen = new Pen(Color.FromArgb(190, 230, 253), 1.9f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };

                PointF p1 = new PointF(cx - 4.5f, cy + 0.2f);
                PointF p2 = new PointF(cx - 1.2f, cy + 3.6f);
                PointF p3 = new PointF(cx + 5.0f, cy - 3.6f);
                g.DrawLines(checkPen, new[] { p1, p2, p3 });
            }
            finally
            {
                g.SmoothingMode = prevMode;
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(180, 185, 195);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Color.FromArgb(42, 45, 52), 1f);
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(46, 48, 54), 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Color.FromArgb(24, 26, 31));
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
    }
}

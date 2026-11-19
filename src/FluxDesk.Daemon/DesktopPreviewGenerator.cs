using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WorkSpace
{
    public static class DesktopPreviewGenerator
    {
        public const int PreviewWidth = 256;
        public const int PreviewHeight = 144;
        private const int MaxExtIconCacheEntries = 128;

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        private const uint SHGSI_ICON = 0x000000100;
        private const uint SHGSI_SMALLICON = 0x000000001;
        private const uint SIID_DOCNOASSOC = 0;
        private const uint SIID_FOLDER = 3;
        private const uint SIID_MYNETWORK = 17;
        private const uint SIID_RECYCLER = 31;
        private const uint SIID_DESKTOPPC = 94;
        private const uint SIID_SETTINGS = 106;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szPath;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll")]
        private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private sealed class RenderJob
        {
            public string DesktopId = "";
            public string DesktopPath = "";
            public string WallpaperPath = "";
            public string DefaultWallpaperPath = "";
            public bool IndependentWallpapers;
            public List<IconPosition>? LiveIcons;
        }

        // Coalescing queue: newest job for a DesktopId overwrites pending job; deleted IDs are never resurrected
        private static readonly object _queueLock = new object();
        private static readonly Dictionary<string, RenderJob> _pendingJobs = new Dictionary<string, RenderJob>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> _jobOrder = new Queue<string>();
        private static readonly HashSet<string> _deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private static volatile bool _shuttingDown = false;
        private static readonly Thread _staWorker;

        // Cached exclusively on the single STA worker thread
        private static Bitmap? _cachedFolderIcon;
        private static Bitmap? _cachedGenericFileIcon;
        private static Bitmap? _cachedRecycleBinIcon;
        private static Bitmap? _cachedThisPcIcon;
        private static Bitmap? _cachedNetworkIcon;
        private static Bitmap? _cachedControlPanelIcon;
        private static readonly Dictionary<string, Bitmap> _extIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        static DesktopPreviewGenerator()
        {
            _staWorker = new Thread(WorkerLoop)
            {
                Name = "FluxDesk.PreviewSTA",
                IsBackground = true
            };
            _staWorker.SetApartmentState(ApartmentState.STA);
            _staWorker.Start();
        }

        public static string PreviewsDir
        {
            get
            {
                string dir = Path.Combine(AppConfig.ConfigDir, "previews");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string GetPreviewPath(string desktopId)
        {
            string safeId = AppConfig.IsSafeId(desktopId) ? desktopId : "invalid";
            return Path.Combine(PreviewsDir, $"preview_{safeId}.png");
        }

        public static void DeletePreview(string desktopId)
        {
            if (!AppConfig.IsSafeId(desktopId)) return;
            lock (_queueLock)
            {
                _deletedIds.Add(desktopId);
                _pendingJobs.Remove(desktopId);
            }
            try
            {
                string file = GetPreviewPath(desktopId);
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }

        public static void QueueRender(DesktopInfo desktop, bool independentWallpapers, string? defaultWallpaperPath = null, List<IconPosition>? liveIcons = null)
        {
            if (_shuttingDown || desktop == null || !AppConfig.IsSafeId(desktop.Id)) return;

            var job = new RenderJob
            {
                DesktopId = desktop.Id,
                DesktopPath = desktop.Path ?? "",
                WallpaperPath = desktop.WallpaperPath ?? "",
                DefaultWallpaperPath = defaultWallpaperPath ?? "",
                IndependentWallpapers = independentWallpapers,
                LiveIcons = liveIcons != null ? new List<IconPosition>(liveIcons) : null
            };

            bool shouldSignal = false;
            lock (_queueLock)
            {
                if (_shuttingDown) return;
                _deletedIds.Remove(desktop.Id);
                if (!_pendingJobs.ContainsKey(desktop.Id))
                {
                    _jobOrder.Enqueue(desktop.Id);
                    shouldSignal = true;
                }
                _pendingJobs[desktop.Id] = job;
            }

            if (shouldSignal)
            {
                try { _signal.Release(); } catch { }
            }
        }

        public static void QueueRenderAll(AppConfig config, string? activeDesktopId = null, List<IconPosition>? activeLiveIcons = null)
        {
            if (_shuttingDown || config == null || config.Desktops == null) return;
            foreach (var d in config.Desktops)
            {
                bool isActive = !string.IsNullOrEmpty(activeDesktopId) && d.Id == activeDesktopId;
                QueueRender(d, config.IndependentWallpapers, config.DefaultWallpaperPath, isActive ? activeLiveIcons : null);
            }
        }

        public static void Shutdown()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            lock (_queueLock)
            {
                _pendingJobs.Clear();
                _jobOrder.Clear();
            }
            try { _signal.Release(); } catch { }
            try { _staWorker.Join(1200); } catch { }
            try { _signal.Dispose(); } catch { }
        }

        private static void WorkerLoop()
        {
            try
            {
                while (!_shuttingDown)
                {
                    try { _signal.Wait(); }
                    catch (ObjectDisposedException) { break; }

                    if (_shuttingDown) break;

                    RenderJob? job = null;
                    lock (_queueLock)
                    {
                        while (_jobOrder.Count > 0)
                        {
                            string id = _jobOrder.Dequeue();
                            if (_pendingJobs.TryGetValue(id, out var found))
                            {
                                _pendingJobs.Remove(id);
                                if (!_deletedIds.Contains(id))
                                {
                                    job = found;
                                    break;
                                }
                            }
                        }
                    }

                    if (job == null) continue;

                    try
                    {
                        RenderPreviewInternal(job);
                    }
                    catch (Exception ex)
                    {
                        AppConfig.AppendLog($"[Preview] Render error for '{job.DesktopId}': {ex.Message}");
                    }
                }
            }
            finally
            {
                DisposeCachedBitmaps();
            }
        }

        private static void DisposeCachedBitmaps()
        {
            try { _cachedFolderIcon?.Dispose(); _cachedFolderIcon = null; } catch { }
            try { _cachedGenericFileIcon?.Dispose(); _cachedGenericFileIcon = null; } catch { }
            try { _cachedRecycleBinIcon?.Dispose(); _cachedRecycleBinIcon = null; } catch { }
            try { _cachedThisPcIcon?.Dispose(); _cachedThisPcIcon = null; } catch { }
            try { _cachedNetworkIcon?.Dispose(); _cachedNetworkIcon = null; } catch { }
            try { _cachedControlPanelIcon?.Dispose(); _cachedControlPanelIcon = null; } catch { }
            foreach (var bmp in _extIconCache.Values)
            {
                try { bmp.Dispose(); } catch { }
            }
            _extIconCache.Clear();
        }

        private static bool IsDeleted(string desktopId)
        {
            lock (_queueLock) return _deletedIds.Contains(desktopId);
        }

        private static void RenderPreviewInternal(RenderJob job)
        {
            if (_shuttingDown || IsDeleted(job.DesktopId)) return;

            var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            if (screen.Width <= 0 || screen.Height <= 0) screen = new Rectangle(0, 0, 1920, 1080);

            using var canvas = new Bitmap(PreviewWidth, PreviewHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                // 1. Wallpaper background
                string wpPath = "";
                if (job.IndependentWallpapers && !string.IsNullOrWhiteSpace(job.WallpaperPath) && File.Exists(job.WallpaperPath))
                {
                    wpPath = job.WallpaperPath;
                }
                else if (!string.IsNullOrWhiteSpace(job.DefaultWallpaperPath) && File.Exists(job.DefaultWallpaperPath))
                {
                    wpPath = job.DefaultWallpaperPath;
                }
                else
                {
                    string sysWp = ShellHelper.GetCurrentWallpaper();
                    if (!string.IsNullOrWhiteSpace(sysWp) && File.Exists(sysWp))
                    {
                        wpPath = sysWp;
                    }
                }

                DrawWallpaperBackground(g, wpPath);

                // 2. Entry lookup map
                var entryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var desktopOwnEntries = new List<string>();
                AddDirectoryEntriesToMap(job.DesktopPath, entryMap, desktopOwnEntries);

                string pubDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (!string.IsNullOrEmpty(pubDesktop) &&
                    !string.Equals(pubDesktop.TrimEnd('\\'), job.DesktopPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    AddDirectoryEntriesToMap(pubDesktop, entryMap, null);
                }

                // 3. Icons & positions
                List<IconPosition> iconsToDraw = ResolveIconsForDesktop(job, entryMap, desktopOwnEntries, screen);

                // 4. Render miniature icons
                float sx = (float)PreviewWidth / screen.Width;
                float sy = (float)PreviewHeight / screen.Height;
                int iconSize = Math.Clamp((int)Math.Round(Math.Min(76 * sx, 82 * sy) * 0.78f), 7, 11);

                using var labelShadowBrush = new SolidBrush(Color.FromArgb(145, 0, 0, 0));
                using var labelBarBrush = new SolidBrush(Color.FromArgb(200, 238, 245, 255));

                foreach (var item in iconsToDraw)
                {
                    if (_shuttingDown || IsDeleted(job.DesktopId)) return;

                    if (item.X < screen.Left - 80 || item.X > screen.Right + 40 ||
                        item.Y < screen.Top - 80 || item.Y > screen.Bottom + 40)
                        continue;

                    int drawX = Math.Clamp((int)Math.Round((item.X - screen.Left) * sx), 1, PreviewWidth - iconSize - 1);
                    int drawY = Math.Clamp((int)Math.Round((item.Y - screen.Top) * sy), 1, PreviewHeight - iconSize - 3);

                    Bitmap? iconBmp = ResolveIconBitmap(item.Name, entryMap, out bool shouldDispose);
                    try
                    {
                        if (iconBmp != null)
                        {
                            g.DrawImage(iconBmp, new Rectangle(drawX, drawY, iconSize, iconSize));
                        }
                        else
                        {
                            using var fallbackBrush = new SolidBrush(Color.FromArgb(220, 190, 230, 253));
                            g.FillRectangle(fallbackBrush, drawX + 1, drawY + 1, iconSize - 2, iconSize - 2);
                        }

                        int barW = Math.Max(4, iconSize - 2);
                        int barX = drawX + (iconSize - barW) / 2;
                        int barY = drawY + iconSize + 1;
                        if (barY < PreviewHeight - 1)
                        {
                            g.FillRectangle(labelShadowBrush, barX, barY + 1, barW, 1);
                            g.FillRectangle(labelBarBrush, barX, barY, barW, 1);
                        }
                    }
                    finally
                    {
                        if (shouldDispose) iconBmp?.Dispose();
                    }
                }
            }

            if (_shuttingDown || IsDeleted(job.DesktopId)) return;

            // 5. Atomic save using a temp filename that does NOT match UI's "preview_*.png" FileSystemWatcher filter!
            string targetFile = GetPreviewPath(job.DesktopId);
            string tempFile = Path.Combine(PreviewsDir, $"~tmp_{job.DesktopId}.{Environment.ProcessId}.{Thread.CurrentThread.ManagedThreadId}.tmp");
            try
            {
                canvas.Save(tempFile, ImageFormat.Png);
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (_shuttingDown || IsDeleted(job.DesktopId)) return;
                    try
                    {
                        File.Move(tempFile, targetFile, overwrite: true);
                        tempFile = "";
                        return;
                    }
                    catch (IOException) { Thread.Sleep(25); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(25); }
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private static void DrawWallpaperBackground(Graphics g, string wpPath)
        {
            bool drewImage = false;
            if (!string.IsNullOrWhiteSpace(wpPath) && File.Exists(wpPath))
            {
                try
                {
                    using var fs = new FileStream(wpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var img = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
                    if (img.Width > 0 && img.Height > 0)
                    {
                        float scale = Math.Max((float)PreviewWidth / img.Width, (float)PreviewHeight / img.Height);
                        float drawW = img.Width * scale;
                        float drawH = img.Height * scale;
                        float offsetX = (PreviewWidth - drawW) / 2f;
                        float offsetY = (PreviewHeight - drawH) / 2f;
                        g.DrawImage(img, offsetX, offsetY, drawW, drawH);
                        drewImage = true;
                    }
                }
                catch { }
            }

            if (!drewImage)
            {
                Color? solidBg = TryReadWindowsDesktopColor();
                if (solidBg.HasValue)
                {
                    using var b = new SolidBrush(solidBg.Value);
                    g.FillRectangle(b, 0, 0, PreviewWidth, PreviewHeight);
                }
                else
                {
                    using var grad = new LinearGradientBrush(
                        new Rectangle(0, 0, PreviewWidth, PreviewHeight),
                        Color.FromArgb(18, 38, 62),
                        Color.FromArgb(11, 18, 28),
                        45f);
                    g.FillRectangle(grad, 0, 0, PreviewWidth, PreviewHeight);
                }
            }
        }

        private static Color? TryReadWindowsDesktopColor()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
                if (key?.GetValue("Background") is string rgb)
                {
                    var parts = rgb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 &&
                        int.TryParse(parts[0], out int r) &&
                        int.TryParse(parts[1], out int gr) &&
                        int.TryParse(parts[2], out int b))
                    {
                        return Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(gr, 0, 255), Math.Clamp(b, 0, 255));
                    }
                }
            }
            catch { }
            return null;
        }

        private static void AddDirectoryEntriesToMap(string dirPath, Dictionary<string, string> entryMap, List<string>? collectList)
        {
            if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return;
            try
            {
                foreach (var fullPath in Directory.GetFileSystemEntries(dirPath))
                {
                    string fileName = Path.GetFileName(fullPath);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                        continue;

                    FileAttributes attrs = FileAttributes.Normal;
                    try
                    {
                        attrs = File.GetAttributes(fullPath);
                        if ((attrs & FileAttributes.Hidden) != 0 && (attrs & FileAttributes.System) != 0)
                            continue;
                    }
                    catch { }

                    collectList?.Add(fullPath);

                    if (!entryMap.ContainsKey(fileName))
                        entryMap[fileName] = fullPath;

                    // Only strip extensions for files — NEVER strip dotted folder names like "Project.v2"!
                    if ((attrs & FileAttributes.Directory) == 0)
                    {
                        string noExt = Path.GetFileNameWithoutExtension(fileName);
                        if (!string.IsNullOrWhiteSpace(noExt) && !entryMap.ContainsKey(noExt))
                            entryMap[noExt] = fullPath;
                    }
                }
            }
            catch { }
        }

        private static bool IsKnownSystemDesktopIcon(string name)
        {
            return name.Equals("Корзина", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Recycle Bin", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Этот компьютер", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("This PC", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Компьютер", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Computer", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Сеть", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Network", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Панель управления", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Control Panel", StringComparison.OrdinalIgnoreCase);
        }

        private static List<IconPosition> ResolveIconsForDesktop(
            RenderJob job,
            Dictionary<string, string> entryMap,
            List<string> desktopOwnEntries,
            Rectangle screen)
        {
            if (job.LiveIcons != null && job.LiveIcons.Count > 0)
            {
                return job.LiveIcons;
            }

            var result = new List<IconPosition>();
            var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var occupied = new List<(int x, int y)>();

            var saved = IconLayoutManager.TryReadSavedLayout(job.DesktopId);
            if (saved != null)
            {
                foreach (var s in saved)
                {
                    if (string.IsNullOrWhiteSpace(s.Name)) continue;

                    if (entryMap.TryGetValue(s.Name, out string? mappedPath))
                    {
                        result.Add(s);
                        matchedPaths.Add(mappedPath);
                        occupied.Add((s.X, s.Y));
                    }
                    else if (IsKnownSystemDesktopIcon(s.Name))
                    {
                        result.Add(s);
                        occupied.Add((s.X, s.Y));
                    }
                }
            }

            int startX = screen.Left + 22;
            int startY = screen.Top + 2;
            int stepX = 76;
            int stepY = 82;
            int maxRows = Math.Max(4, (screen.Height - 60) / stepY);
            int maxCols = Math.Max(8, (screen.Width - 40) / stepX);

            foreach (var fullPath in desktopOwnEntries)
            {
                if (matchedPaths.Contains(fullPath)) continue;

                string displayName = Path.GetFileName(fullPath);
                var slot = FindFreeSlot(occupied, startX, startY, stepX, stepY, maxCols, maxRows);
                occupied.Add(slot);
                result.Add(new IconPosition { Name = displayName, X = slot.x, Y = slot.y });
            }

            return result;
        }

        private static (int x, int y) FindFreeSlot(
            List<(int x, int y)> occupied,
            int startX, int startY, int stepX, int stepY, int maxCols, int maxRows)
        {
            for (int col = 0; col < maxCols; col++)
            {
                for (int row = 0; row < maxRows; row++)
                {
                    int tx = startX + col * stepX;
                    int ty = startY + row * stepY;
                    bool taken = false;
                    foreach (var p in occupied)
                    {
                        if (Math.Abs(p.x - tx) < 48 && Math.Abs(p.y - ty) < 56)
                        {
                            taken = true;
                            break;
                        }
                    }
                    if (!taken) return (tx, ty);
                }
            }
            return (startX, startY);
        }

        private static Bitmap? ResolveIconBitmap(string displayName, Dictionary<string, string> entryMap, out bool shouldDispose)
        {
            shouldDispose = false;

            if (displayName.Equals("Корзина", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("Recycle Bin", StringComparison.OrdinalIgnoreCase))
            {
                _cachedRecycleBinIcon ??= GetStockIconBitmap(SIID_RECYCLER);
                return _cachedRecycleBinIcon;
            }

            if (displayName.Equals("Этот компьютер", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("This PC", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("Компьютер", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("Computer", StringComparison.OrdinalIgnoreCase))
            {
                _cachedThisPcIcon ??= GetStockIconBitmap(SIID_DESKTOPPC);
                return _cachedThisPcIcon;
            }

            if (displayName.Equals("Сеть", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                _cachedNetworkIcon ??= GetStockIconBitmap(SIID_MYNETWORK);
                return _cachedNetworkIcon;
            }

            if (displayName.Equals("Панель управления", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("Control Panel", StringComparison.OrdinalIgnoreCase))
            {
                _cachedControlPanelIcon ??= GetStockIconBitmap(SIID_SETTINGS);
                return _cachedControlPanelIcon;
            }

            if (entryMap.TryGetValue(displayName, out string? fullPath))
            {
                if (Directory.Exists(fullPath))
                {
                    bool hasCustomIcon = !fullPath.StartsWith(@"\\", StringComparison.Ordinal) &&
                                         File.Exists(Path.Combine(fullPath, "desktop.ini"));
                    if (!hasCustomIcon)
                    {
                        _cachedFolderIcon ??= GetStockIconBitmap(SIID_FOLDER);
                        return _cachedFolderIcon;
                    }

                    var customFolder = GetFileIconBitmap(fullPath, useFileAttributes: false);
                    if (customFolder != null)
                    {
                        shouldDispose = true;
                        return customFolder;
                    }

                    _cachedFolderIcon ??= GetStockIconBitmap(SIID_FOLDER);
                    return _cachedFolderIcon;
                }

                if (File.Exists(fullPath))
                {
                    string ext = Path.GetExtension(fullPath);
                    bool uniquePerFile =
                        ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase);

                    if (uniquePerFile && !fullPath.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        var perFileBmp = GetFileIconBitmap(fullPath, useFileAttributes: false);
                        if (perFileBmp != null)
                        {
                            shouldDispose = true;
                            return perFileBmp;
                        }
                    }
                    else if (!string.IsNullOrEmpty(ext))
                    {
                        if (_extIconCache.TryGetValue(ext, out var cached))
                        {
                            return cached;
                        }

                        var extBmp = GetFileIconBitmap("file" + ext, useFileAttributes: true);
                        if (extBmp != null)
                        {
                            if (_extIconCache.Count < MaxExtIconCacheEntries)
                            {
                                _extIconCache[ext] = extBmp;
                                return extBmp;
                            }
                            shouldDispose = true;
                            return extBmp;
                        }
                    }
                }
            }

            _cachedGenericFileIcon ??= GetStockIconBitmap(SIID_DOCNOASSOC);
            return _cachedGenericFileIcon;
        }

        private static Bitmap? GetFileIconBitmap(string path, bool useFileAttributes)
        {
            var shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | (useFileAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
            uint attrs = useFileAttributes ? FILE_ATTRIBUTE_NORMAL : 0;

            IntPtr res = SHGetFileInfo(path, attrs, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (shfi.hIcon == IntPtr.Zero) return null;

            try
            {
                if (res == IntPtr.Zero) return null;
                using var ico = Icon.FromHandle(shfi.hIcon);
                return ico.ToBitmap();
            }
            catch
            {
                return null;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }

        private static Bitmap? GetStockIconBitmap(uint siid)
        {
            var sii = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
            int hr = SHGetStockIconInfo(siid, SHGSI_ICON | SHGSI_SMALLICON, ref sii);
            if (sii.hIcon == IntPtr.Zero) return null;

            try
            {
                if (hr < 0) return null;
                using var ico = Icon.FromHandle(sii.hIcon);
                return ico.ToBitmap();
            }
            catch
            {
                return null;
            }
            finally
            {
                DestroyIcon(sii.hIcon);
            }
        }
    }
}

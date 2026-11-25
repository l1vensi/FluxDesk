using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WorkSpace
{
    public class SwitcherWindow : Form
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_ENDSESSION = 0x0016;
        private const int VK_MENU = 0x12;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        private readonly AppConfig _config;
        private readonly Action<int> _onSelectDesktop;
        private readonly Action<bool>? _onEndSession;

        private int _selectedIndex = 0;
        private int _columnsPerRow = 1;
        private int _effectiveCardWidth = BASE_CARD_WIDTH;
        private int _effectiveCardHeight = BASE_CARD_HEIGHT;
        private List<DesktopInfo> _qsDesktops = new List<DesktopInfo>();
        private volatile bool _isSwitcherVisible = false;
        private readonly System.Windows.Forms.Timer _altWatchdogTimer;
        private Image? _cachedWallpaper = null;
        private readonly Dictionary<string, (long ticks, Bitmap bmp)> _previewCache = new Dictionary<string, (long, Bitmap)>();

        private const int BASE_CARD_WIDTH = 178;
        private const int BASE_CARD_HEIGHT = 104;
        private const int BASE_GAP = 12;
        private const int BASE_PAD = 16;

        public bool IsSwitcherVisible => _isSwitcherVisible;

        public SwitcherWindow(AppConfig config, Action<int> onSelectDesktop, Action<bool>? onEndSession = null)
        {
            _config = config;
            _onSelectDesktop = onSelectDesktop;
            _onEndSession = onEndSession;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 24, 27);
            ForeColor = Color.White;

            // Backup watchdog in case Alt key-up was lost to an elevated window or UAC prompt
            _altWatchdogTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _altWatchdogTimer.Tick += OnAltWatchdogTick;

            LoadWallpaperThumbnail();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ENDSESSION)
            {
                bool actuallyEnding = m.WParam != IntPtr.Zero;
                try { _onEndSession?.Invoke(actuallyEnding); } catch { }
            }
            base.WndProc(ref m);
        }

        private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Desktop)
            {
                LoadWallpaperThumbnail();
            }
        }

        private void LoadWallpaperThumbnail()
        {
            try
            {
                string wpPath = ShellHelper.GetCurrentWallpaper();
                if (!string.IsNullOrWhiteSpace(wpPath) && File.Exists(wpPath))
                {
                    using var stream = new FileStream(wpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                    var bmp = new Bitmap(img, new Size(256, 144));
                    var old = _cachedWallpaper;
                    _cachedWallpaper = bmp;
                    old?.Dispose();
                }
            }
            catch { }
        }

        private float DpiScale => Math.Clamp(DeviceDpi / 96f, 1.0f, 2.5f);
        private int CardWidth => (int)Math.Round(BASE_CARD_WIDTH * DpiScale);
        private int CardHeight => (int)Math.Round(BASE_CARD_HEIGHT * DpiScale);
        private int Gap => (int)Math.Round(BASE_GAP * DpiScale);
        private int Pad => (int)Math.Round(BASE_PAD * DpiScale);

        private void UpdateRoundedRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            int radius = (int)Math.Round(16 * DpiScale);
            using var path = GetRoundedRect(new Rectangle(0, 0, Width, Height), radius);
            var oldRegion = Region;
            Region = new Region(path);
            oldRegion?.Dispose();
        }

        public void SyncWithConfig()
        {
            // Prune cached preview bitmaps for deleted desktops
            var activeIds = new HashSet<string>(_config.Desktops.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            var staleKeys = _previewCache.Keys.Where(k => !activeIds.Contains(k)).ToList();
            foreach (var k in staleKeys)
            {
                _previewCache[k].bmp.Dispose();
                _previewCache.Remove(k);
            }

            _qsDesktops = _config.GetQuickSwitcherDesktops();

            if (!_config.QuickSwitcherEnabled || _qsDesktops.Count == 0)
            {
                if (_isSwitcherVisible) CancelAndClose();
                return;
            }

            if (_selectedIndex >= _qsDesktops.Count)
                _selectedIndex = Math.Max(0, _qsDesktops.Count - 1);

            if (_isSwitcherVisible)
            {
                LayoutWindowOnActiveScreen();
                Invalidate();
            }
        }

        private void LayoutWindowOnActiveScreen()
        {
            _qsDesktops = _config.GetQuickSwitcherDesktops();
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            int totalCards = Math.Max(1, _qsDesktops.Count);
            int cw = CardWidth;
            int ch = CardHeight;
            int gap = Gap;
            int pad = Pad;

            // Always keep up to 9 cards in 1 clean horizontal row; shrink proportionally on narrower screens
            int maxAvailW = Math.Max(200, screen.Width - 48);
            int neededW = pad * 2 + totalCards * cw + (totalCards - 1) * gap;
            if (neededW > maxAvailW)
            {
                cw = Math.Max((int)Math.Round(72 * DpiScale), (maxAvailW - pad * 2 - (totalCards - 1) * gap) / totalCards);
                ch = Math.Max((int)Math.Round(44 * DpiScale), (int)Math.Round(cw * ((double)BASE_CARD_HEIGHT / BASE_CARD_WIDTH)));
            }

            _effectiveCardWidth = cw;
            _effectiveCardHeight = ch;
            _columnsPerRow = totalCards;

            int w = pad * 2 + _columnsPerRow * cw + (_columnsPerRow - 1) * gap;
            int h = pad * 2 + ch;
            if (h > screen.Height - 40) h = screen.Height - 40;

            Size = new Size(w, h);
            Location = new Point(
                screen.Left + (screen.Width - w) / 2,
                screen.Bottom - h - (int)Math.Round(42 * DpiScale)
            );
            UpdateRoundedRegion();
        }

        public void ShowSwitcher(bool advanceSelection = true)
        {
            _altWatchdogTimer.Stop();

            _qsDesktops = _config.GetQuickSwitcherDesktops();
            if (_qsDesktops.Count == 0) return;

            int count = _qsDesktops.Count;
            string activeId = (_config.CurrentIndex >= 0 && _config.CurrentIndex < _config.Desktops.Count)
                ? _config.Desktops[_config.CurrentIndex].Id
                : "";
            int curQsIdx = _qsDesktops.FindIndex(d => d.Id == activeId);

            if (advanceSelection)
            {
                if (!_isSwitcherVisible)
                    _selectedIndex = curQsIdx >= 0 ? (curQsIdx + 1) % count : 0;
                else
                    _selectedIndex = (_selectedIndex + 1) % count;
            }
            else
            {
                if (!_isSwitcherVisible)
                    _selectedIndex = curQsIdx >= 0 ? (curQsIdx - 1 + count) % count : count - 1;
                else
                    _selectedIndex = (_selectedIndex - 1 + count) % count;
            }

            // Fast-tap optimization: if user already released Alt before BeginInvoke arrived, switch immediately (zero 800ms lag!)
            bool altStillHeld = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            if (!_isSwitcherVisible && !altStillHeld)
            {
                CommitAndClose();
                return;
            }

            LayoutWindowOnActiveScreen();

            _isSwitcherVisible = true;
            TopMost = true;
            if (!Visible)
            {
                Show();
            }

            // Strictly HWND_TOPMOST + SWP_NOACTIVATE: ALWAYS above every window & never steals focus from games!
            SetWindowPos(Handle, HWND_TOPMOST, Left, Top, Width, Height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            Invalidate();

            _altWatchdogTimer.Start();
        }

        public void HandleHookAction(int actionId)
        {
            if (!_isSwitcherVisible || _qsDesktops.Count == 0) return;

            switch (actionId)
            {
                case KeyboardHook.ACTION_NAV_RIGHT:
                    _selectedIndex = (_selectedIndex + 1) % _qsDesktops.Count;
                    Invalidate();
                    break;
                case KeyboardHook.ACTION_NAV_LEFT:
                    _selectedIndex = (_selectedIndex - 1 + _qsDesktops.Count) % _qsDesktops.Count;
                    Invalidate();
                    break;
                case KeyboardHook.ACTION_NAV_DOWN:
                    if (_selectedIndex + _columnsPerRow < _qsDesktops.Count)
                    {
                        _selectedIndex += _columnsPerRow;
                        Invalidate();
                    }
                    break;
                case KeyboardHook.ACTION_NAV_UP:
                    if (_selectedIndex - _columnsPerRow >= 0)
                    {
                        _selectedIndex -= _columnsPerRow;
                        Invalidate();
                    }
                    break;
                case KeyboardHook.ACTION_SWITCHER_COMMIT:
                    CommitAndClose();
                    break;
                case KeyboardHook.ACTION_SWITCHER_CANCEL:
                    CancelAndClose();
                    break;
            }
        }

        private void OnAltWatchdogTick(object? sender, EventArgs e)
        {
            if (!_isSwitcherVisible)
            {
                _altWatchdogTimer.Stop();
                return;
            }

            bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            if (!altDown)
            {
                CommitAndClose();
            }
        }

        private void CancelAndClose()
        {
            _altWatchdogTimer.Stop();
            _isSwitcherVisible = false;
            if (Visible) Hide();
        }

        private void CommitAndClose()
        {
            _altWatchdogTimer.Stop();
            _isSwitcherVisible = false;
            if (Visible) Hide();
            if (_selectedIndex >= 0 && _selectedIndex < _qsDesktops.Count)
            {
                string targetId = _qsDesktops[_selectedIndex].Id;
                int realIndex = _config.Desktops.FindIndex(d => d.Id == targetId);
                if (realIndex >= 0)
                {
                    _onSelectDesktop(realIndex);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < _qsDesktops.Count; i++)
            {
                if (GetCardRect(i).Contains(e.Location))
                {
                    _selectedIndex = i;
                    CommitAndClose();
                    return;
                }
            }
        }

        private Rectangle GetCardRect(int index)
        {
            int cols = Math.Max(1, _columnsPerRow);
            int col = index % cols;
            int row = index / cols;
            int x = Pad + col * (_effectiveCardWidth + Gap);
            int y = Pad + row * (_effectiveCardHeight + Gap);
            return new Rectangle(x, y, _effectiveCardWidth, _effectiveCardHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int radius = (int)Math.Round(16 * DpiScale);
            var fullRect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = GetRoundedRect(fullRect, radius))
            {
                using var bgBrush = new SolidBrush(Color.FromArgb(28, 29, 34));
                g.FillPath(bgBrush, path);
                using var borderPen = new Pen(Color.FromArgb(65, 68, 78), 1.5f);
                g.DrawPath(borderPen, path);
            }

            for (int i = 0; i < _qsDesktops.Count; i++)
            {
                DrawDesktopCard(g, i, GetCardRect(i), i == _selectedIndex);
            }
        }

        private Image? GetDesktopPreviewImage(string desktopId)
        {
            try
            {
                string path = DesktopPreviewGenerator.GetPreviewPath(desktopId);
                if (!File.Exists(path)) return _cachedWallpaper;

                long ticks = File.GetLastWriteTimeUtc(path).Ticks;
                if (_previewCache.TryGetValue(desktopId, out var entry) && entry.ticks == ticks)
                {
                    return entry.bmp;
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        using var ms = new MemoryStream(bytes);
                        using var tmpImg = Image.FromStream(ms);
                        var cloned = new Bitmap(tmpImg);

                        if (_previewCache.TryGetValue(desktopId, out var oldEntry))
                        {
                            oldEntry.bmp.Dispose();
                        }
                        _previewCache[desktopId] = (ticks, cloned);
                        return cloned;
                    }
                    catch (IOException) { Thread.Sleep(15); }
                }
            }
            catch { }
            return _cachedWallpaper;
        }

        private void DrawDesktopCard(Graphics g, int index, Rectangle rect, bool isSelected)
        {
            var d = _qsDesktops[index];
            float scale = DpiScale;
            int cardRadius = (int)Math.Round(10 * scale);
            int inset = (int)Math.Round(4 * scale);

            using (var path = GetRoundedRect(rect, cardRadius))
            {
                Color cardBg = isSelected ? Color.FromArgb(45, 48, 56) : Color.FromArgb(34, 36, 42);
                using var brush = new SolidBrush(cardBg);
                g.FillPath(brush, path);

                if (isSelected)
                {
                    using var selPen = new Pen(Color.FromArgb(190, 230, 253), 2.2f * scale);
                    g.DrawPath(selPen, path);
                }
                else
                {
                    using var normPen = new Pen(Color.FromArgb(55, 58, 66), 1f);
                    g.DrawPath(normPen, path);
                }
            }

            var thumbRect = new Rectangle(rect.X + inset, rect.Y + inset, rect.Width - inset * 2, rect.Height - inset * 2);
            using (var thumbPath = GetRoundedRect(thumbRect, (int)Math.Round(7 * scale)))
            {
                Image? previewImg = GetDesktopPreviewImage(d.Id);
                if (previewImg != null)
                {
                    g.SetClip(thumbPath);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(previewImg, thumbRect);
                    g.ResetClip();
                }
                else
                {
                    using var wpBrush = new LinearGradientBrush(thumbRect, Color.FromArgb(20, 40, 75), Color.FromArgb(10, 20, 40), 45f);
                    g.FillPath(wpBrush, thumbPath);
                }
            }
        }

        private static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            if (d <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            var arc = new Rectangle(bounds.Location, new Size(d, d));
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - d;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - d;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void ClearPreviewCache()
        {
            foreach (var kv in _previewCache.Values)
            {
                try { kv.bmp.Dispose(); } catch { }
            }
            _previewCache.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                _altWatchdogTimer?.Dispose();
                _cachedWallpaper?.Dispose();
                foreach (var kv in _previewCache.Values)
                {
                    kv.bmp.Dispose();
                }
                _previewCache.Clear();
            }
            base.Dispose(disposing);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WorkSpace
{
    public class KeyboardHook : IDisposable
    {
        public const int ACTION_CYCLE_NEXT = 1000;
        public const int ACTION_CYCLE_PREV = 1001;
        public const int ACTION_NAV_LEFT = 1002;
        public const int ACTION_NAV_RIGHT = 1003;
        public const int ACTION_NAV_UP = 1004;
        public const int ACTION_NAV_DOWN = 1005;
        public const int ACTION_SWITCHER_COMMIT = 1006;
        public const int ACTION_SWITCHER_CANCEL = 1007;
        public const int ACTION_DIRECT_DESKTOP = 1200;

        private const string SuspendEventName = @"Local\FluxDesk_SuspendHotkeys";

        private static void Log(string msg) => AppConfig.AppendLog($"[KeyHook] {msg}");

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint WM_QUIT = 0x0012;

        private const uint LLKHF_INJECTED = 0x00000010;
        private const uint LLKHF_ALTDOWN = 0x00000020;

        private const int VK_TAB = 0x09;
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const ushort VK_MENU_MASK = 0xE8; // Benign unassigned VK to mask Alt/Win release

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private sealed class HookSnapshot
        {
            public bool QuickSwitcherEnabled = true;
            public bool HotkeysEnabled = true;
            public string HotkeyModifier = "Alt";
            public Dictionary<int, string> VkToDesktopId = new Dictionary<int, string>();
        }

        private readonly LowLevelKeyboardProc _proc;
        private readonly Action<int, string?> _onHotkey;
        private readonly Func<bool> _isSwitcherVisible;
        private readonly HashSet<int> _swallowedKeys = new HashSet<int>();
        private readonly ManualResetEventSlim _threadReady = new ManualResetEventSlim(false);
        private EventWaitHandle? _suspendEvent;
        private Thread? _hookThread;
        private uint _hookThreadId = 0;
        private IntPtr _hookId = IntPtr.Zero;
        private int _disposed = 0;

        private volatile HookSnapshot _snapshot = new HookSnapshot();

        public KeyboardHook(AppConfig config, Func<bool> isSwitcherVisible, Action<int, string?> onHotkey)
        {
            _isSwitcherVisible = isSwitcherVisible;
            _onHotkey = onHotkey;
            _proc = HookCallback;

            try
            {
                _suspendEvent = new EventWaitHandle(false, EventResetMode.ManualReset, SuspendEventName);
            }
            catch { }

            UpdateHotkeyMap(config);
            SystemEvents.SessionSwitch += OnSessionSwitch;

            // Dedicated ultra-lightweight hook thread so UI/Shell/Wallpaper work NEVER triggers LowLevelHooksTimeout!
            _hookThread = new Thread(HookThreadProc)
            {
                Name = "FluxDesk.KeyHook",
                IsBackground = true
            };
            _hookThread.Start();
            _threadReady.Wait(1500);
        }

        private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            // Clear any swallowed keys if user locked screen / switched session
            lock (_swallowedKeys) _swallowedKeys.Clear();
        }

        private void HookThreadProc()
        {
            _hookThreadId = GetCurrentThreadId();
            try
            {
                IntPtr hMod = GetModuleHandle(null);
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
                Log($"SetWindowsHookEx on dedicated thread {_hookThreadId} returned: {_hookId} (err={Marshal.GetLastWin32Error()})");
            }
            catch (Exception ex)
            {
                Log($"Exception installing hook: {ex.Message}");
            }
            finally
            {
                _threadReady.Set();
            }

            if (_hookId == IntPtr.Zero) return;

            try
            {
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            finally
            {
                IntPtr old = Interlocked.Exchange(ref _hookId, IntPtr.Zero);
                if (old != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(old);
                    Log("Keyboard hook uninstalled cleanly on hook thread.");
                }
            }
        }

        public void UpdateHotkeyMap(AppConfig cfg)
        {
            var map = new Dictionary<int, string>();
            var custom = cfg.DesktopHotkeys ?? new Dictionary<string, int>();
            string mod = string.IsNullOrWhiteSpace(cfg.HotkeyModifier) ? "Alt" : cfg.HotkeyModifier;

            // Two-pass resolution (matches FluxDesk UI 100%):
            // Pass 1: Explicit user-assigned hotkeys always take priority
            foreach (var d in cfg.Desktops)
            {
                if (string.IsNullOrEmpty(d.Id)) continue;
                if (custom.TryGetValue(d.Id, out int boundVk) && boundVk > 0)
                {
                    if (mod == "Alt" && cfg.QuickSwitcherEnabled && (boundVk == (int)Keys.W || boundVk == (int)Keys.Q))
                        continue;
                    if (!map.ContainsKey(boundVk))
                        map[boundVk] = d.Id;
                }
            }

            // Pass 2: Default 1..9 for desktops with no explicit entry (skip already-claimed VKs!)
            for (int i = 0; i < cfg.Desktops.Count && i < 9; i++)
            {
                var d = cfg.Desktops[i];
                if (string.IsNullOrEmpty(d.Id)) continue;
                if (custom.ContainsKey(d.Id)) continue; // 0 = explicitly unassigned, >0 = already mapped in Pass 1

                int defaultVk = (int)Keys.D1 + i;
                if (!map.ContainsKey(defaultVk))
                {
                    map[defaultVk] = d.Id;
                }
            }

            _snapshot = new HookSnapshot
            {
                QuickSwitcherEnabled = cfg.QuickSwitcherEnabled,
                HotkeysEnabled = cfg.HotkeysEnabled,
                HotkeyModifier = mod,
                VkToDesktopId = map
            };
        }

        private static void SendMenuMaskKey()
        {
            try
            {
                var inputs = new INPUT[2];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = VK_MENU_MASK;
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = VK_MENU_MASK;
                inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
                SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            }
            catch { }
        }

        private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private bool IsHotkeysSuspended()
        {
            try { return _suspendEvent != null && _suspendEvent.WaitOne(0); }
            catch { return false; }
        }

        private void PruneReleasedSwallowedKeys()
        {
            if (_swallowedKeys.Count == 0) return;
            List<int>? stale = null;
            foreach (int k in _swallowedKeys)
            {
                if (!IsKeyDown(k))
                {
                    stale ??= new List<int>();
                    stale.Add(k);
                }
            }
            if (stale != null)
            {
                foreach (int k in stale) _swallowedKeys.Remove(k);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Volatile.Read(ref _disposed) == 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // Never intercept our own injected mask keys or other synthetic SendInput calls
                if ((info.flags & LLKHF_INJECTED) != 0)
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                int msg = wParam.ToInt32();
                int vkCode = (int)info.vkCode;
                bool switcherOpen = _isSwitcherVisible();

                if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    // Instant commit when releasing Alt while Quick Switcher is open!
                    if (switcherOpen && (vkCode == VK_LMENU || vkCode == VK_RMENU))
                    {
                        _onHotkey(ACTION_SWITCHER_COMMIT, null);
                    }

                    // Swallow matching key-up for any key whose key-down we swallowed (zero orphan keyups!)
                    bool wasSwallowed;
                    lock (_swallowedKeys) wasSwallowed = _swallowedKeys.Remove(vkCode);
                    if (wasSwallowed)
                    {
                        return (IntPtr)1;
                    }

                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    // While user is recording a hotkey in FluxDesk UI, let keys flow directly to the UI!
                    if (IsHotkeysSuspended())
                    {
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    lock (_swallowedKeys) PruneReleasedSwallowedKeys();

                    bool lAlt = IsKeyDown(VK_LMENU);
                    bool rAlt = IsKeyDown(VK_RMENU);
                    bool lCtrl = IsKeyDown(VK_LCONTROL);
                    bool rCtrl = IsKeyDown(VK_RCONTROL);
                    bool isAltGr = rAlt && lCtrl;

                    bool altDown = lAlt || (rAlt && !isAltGr) || ((info.flags & LLKHF_ALTDOWN) != 0 && !isAltGr);
                    bool ctrlDown = (lCtrl && !isAltGr) || rCtrl;
                    bool winDown = IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
                    bool shiftDown = IsKeyDown(VK_SHIFT);

                    // 1. If Quick Switcher overlay is visible, handle Esc / Arrows / Enter / Tab directly here!
                    if (switcherOpen)
                    {
                        int navAction = vkCode switch
                        {
                            VK_ESCAPE => ACTION_SWITCHER_CANCEL,
                            VK_RETURN or VK_SPACE => ACTION_SWITCHER_COMMIT,
                            VK_LEFT => ACTION_NAV_LEFT,
                            VK_RIGHT => ACTION_NAV_RIGHT,
                            VK_UP => ACTION_NAV_UP,
                            VK_DOWN => ACTION_NAV_DOWN,
                            VK_TAB => shiftDown ? ACTION_NAV_LEFT : ACTION_NAV_RIGHT,
                            _ => 0
                        };

                        if (navAction != 0)
                        {
                            if (altDown || winDown) SendMenuMaskKey();
                            lock (_swallowedKeys) _swallowedKeys.Add(vkCode);
                            _onHotkey(navAction, null);
                            return (IntPtr)1;
                        }
                    }

                    var snap = _snapshot;

                    // 2. Quick Switcher: Alt+W (forward/right), Alt+Q or Alt+Shift+W (backward/left)
                    if (altDown && snap.QuickSwitcherEnabled && !ctrlDown && !winDown)
                    {
                        if (vkCode == (int)Keys.W)
                        {
                            SendMenuMaskKey();
                            lock (_swallowedKeys) _swallowedKeys.Add(vkCode);
                            _onHotkey(shiftDown ? ACTION_CYCLE_PREV : ACTION_CYCLE_NEXT, null);
                            return (IntPtr)1;
                        }
                        if (vkCode == (int)Keys.Q && !shiftDown)
                        {
                            SendMenuMaskKey();
                            lock (_swallowedKeys) _swallowedKeys.Add(vkCode);
                            _onHotkey(ACTION_CYCLE_PREV, null);
                            return (IntPtr)1;
                        }
                    }

                    // 3. Direct desktop hotkeys (Alt, Ctrl, Win, legacy Ctrl+Alt / Win+Alt)
                    if (snap.HotkeysEnabled && !shiftDown && (altDown || ctrlDown || winDown))
                    {
                        bool modMatch = snap.HotkeyModifier switch
                        {
                            "Ctrl" => ctrlDown && !altDown && !winDown,
                            "Win" => winDown && !altDown && !ctrlDown,
                            "Ctrl+Alt" => ctrlDown && altDown && !winDown && !isAltGr,
                            "Win+Alt" => winDown && altDown && !ctrlDown,
                            _ => altDown && !ctrlDown && !winDown
                        };

                        if (modMatch && snap.VkToDesktopId.TryGetValue(vkCode, out string? targetDesktopId))
                        {
                            bool alreadyHeld;
                            lock (_swallowedKeys) alreadyHeld = !_swallowedKeys.Add(vkCode);
                            if (alreadyHeld)
                            {
                                return (IntPtr)1; // Suppress key repeat
                            }

                            if (altDown || winDown) SendMenuMaskKey();
                            _onHotkey(ACTION_DIRECT_DESKTOP, targetDesktopId);
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            SystemEvents.SessionSwitch -= OnSessionSwitch;

            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            try { _hookThread?.Join(1000); } catch { }

            IntPtr remaining = Interlocked.Exchange(ref _hookId, IntPtr.Zero);
            if (remaining != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(remaining); } catch { }
            }

            try { _suspendEvent?.Dispose(); } catch { }
            try { _threadReady.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }
    }
}

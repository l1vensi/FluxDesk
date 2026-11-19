using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WorkSpace
{
    public class IconPosition
    {
        public string Name { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }

        [JsonIgnore]
        public int ListViewIndex { get; set; } = -1;
    }

    public static class IconLayoutManager
    {
        private static readonly object _explorerLock = new object();

        private static void Log(string msg)
        {
            AppConfig.AppendLog($"[IconMgr] {msg}");
        }

        private const int LVM_FIRST = 0x1000;
        private const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
        private const int LVM_SETITEMPOSITION = LVM_FIRST + 15;
        private const int LVM_GETITEMPOSITION = LVM_FIRST + 16;
        private const int LVM_SETITEMPOSITION32 = LVM_FIRST + 49;
        private const int LVM_GETITEMTEXTW = LVM_FIRST + 115;
        private const int LVIF_TEXT = 0x0001;
        private const int WM_SETREDRAW = 0x000B;

        private const uint SMTO_BLOCK = 0x0001;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint SEND_TIMEOUT_MS = 400;

        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEMW
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

        private static bool TrySendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            IntPtr ok = SendMessageTimeoutW(hWnd, msg, wParam, lParam, SMTO_ABORTIFHUNG | SMTO_BLOCK, SEND_TIMEOUT_MS, out result);
            return ok != IntPtr.Zero;
        }

        public static IntPtr GetDesktopListView()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    IntPtr lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                    if (lv != IntPtr.Zero) return lv;
                }
            }

            // Verify both SHELLDLL_DefView AND SysListView32 exist before stopping EnumWindows
            IntPtr foundListView = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                IntPtr shellView = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                {
                    IntPtr lv = FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
                    if (lv != IntPtr.Zero)
                    {
                        foundListView = lv;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            return foundListView;
        }

        public static string GetLayoutFile(string layoutKey)
        {
            string safeKey = AppConfig.IsSafeId(layoutKey) ? layoutKey : "invalid";
            string dir = Path.Combine(AppConfig.ConfigDir, "layouts");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"layout_{safeKey}.json");
        }

        public static void DeleteLayoutFile(string layoutKey)
        {
            try
            {
                if (!AppConfig.IsSafeId(layoutKey)) return;
                string file = GetLayoutFile(layoutKey);
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }

        public static List<IconPosition> GetCurrentIcons()
        {
            lock (_explorerLock)
            {
                return GetCurrentIconsUnsafe();
            }
        }

        private static List<IconPosition> GetCurrentIconsUnsafe()
        {
            var result = new List<IconPosition>();
            IntPtr hList = GetDesktopListView();
            if (hList == IntPtr.Zero) return result;

            if (!TrySendMessage(hList, (uint)LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out IntPtr countPtr))
                return result;
            int count = (int)countPtr;
            if (count <= 0) return result;

            GetWindowThreadProcessId(hList, out uint pid);
            IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
            if (hProc == IntPtr.Zero) return result;

            const int totalRemoteBytes = 4096;
            const int textOffset = 1024;
            const int textBufferBytes = 520;

            IntPtr remoteMem = IntPtr.Zero;
            IntPtr ptLocal = IntPtr.Zero;
            IntPtr lvLocal = IntPtr.Zero;
            IntPtr textLocal = IntPtr.Zero;
            IntPtr zeroTextLocal = IntPtr.Zero;

            try
            {
                remoteMem = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)totalRemoteBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (remoteMem == IntPtr.Zero) return result;

                int ptSize = Marshal.SizeOf<POINT>();
                int lvSize = Marshal.SizeOf<LVITEMW>();

                ptLocal = Marshal.AllocHGlobal(ptSize);
                lvLocal = Marshal.AllocHGlobal(lvSize);
                textLocal = Marshal.AllocHGlobal(textBufferBytes);
                zeroTextLocal = Marshal.AllocHGlobal(textBufferBytes);
                for (int b = 0; b < textBufferBytes; b++) Marshal.WriteByte(zeroTextLocal, b, 0);

                IntPtr remoteTextPtr = IntPtr.Add(remoteMem, textOffset);

                for (int i = 0; i < count; i++)
                {
                    if (!TrySendMessage(hList, (uint)LVM_GETITEMPOSITION, (IntPtr)i, remoteMem, out IntPtr posOk) || posOk == IntPtr.Zero)
                        continue;

                    if (!ReadProcessMemory(hProc, remoteMem, ptLocal, (UIntPtr)ptSize, out _))
                        continue;
                    POINT pt = Marshal.PtrToStructure<POINT>(ptLocal);

                    if (!WriteProcessMemory(hProc, remoteTextPtr, zeroTextLocal, (UIntPtr)textBufferBytes, out _))
                        continue;

                    LVITEMW lvi = new LVITEMW
                    {
                        mask = LVIF_TEXT,
                        iItem = i,
                        iSubItem = 0,
                        pszText = remoteTextPtr,
                        cchTextMax = 259
                    };

                    Marshal.StructureToPtr(lvi, lvLocal, false);
                    if (!WriteProcessMemory(hProc, remoteMem, lvLocal, (UIntPtr)lvSize, out _))
                        continue;

                    if (!TrySendMessage(hList, (uint)LVM_GETITEMTEXTW, (IntPtr)i, remoteMem, out IntPtr charCount) || charCount == IntPtr.Zero)
                        continue;

                    if (!ReadProcessMemory(hProc, remoteTextPtr, textLocal, (UIntPtr)textBufferBytes, out _))
                        continue;
                    Marshal.WriteInt16(textLocal, textBufferBytes - 2, 0);
                    string name = Marshal.PtrToStringUni(textLocal) ?? "";

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.Add(new IconPosition { Name = name, X = pt.X, Y = pt.Y, ListViewIndex = i });
                    }
                }
            }
            finally
            {
                if (ptLocal != IntPtr.Zero) Marshal.FreeHGlobal(ptLocal);
                if (lvLocal != IntPtr.Zero) Marshal.FreeHGlobal(lvLocal);
                if (textLocal != IntPtr.Zero) Marshal.FreeHGlobal(textLocal);
                if (zeroTextLocal != IntPtr.Zero) Marshal.FreeHGlobal(zeroTextLocal);
                if (remoteMem != IntPtr.Zero) VirtualFreeEx(hProc, remoteMem, UIntPtr.Zero, MEM_RELEASE);
                CloseHandle(hProc);
            }

            return result;
        }

        public static List<IconPosition>? TryReadSavedLayout(string layoutKey)
        {
            if (!AppConfig.IsSafeId(layoutKey)) return null;
            string file = GetLayoutFile(layoutKey);
            if (!File.Exists(file)) return null;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    string json = sr.ReadToEnd();
                    return JsonSerializer.Deserialize<List<IconPosition>>(json);
                }
                catch (IOException) { Thread.Sleep(25); }
                catch (UnauthorizedAccessException) { Thread.Sleep(25); }
                catch { return null; }
            }
            return null;
        }

        public static void SaveLayoutFromList(string layoutKey, List<IconPosition> icons)
        {
            if (!AppConfig.IsSafeId(layoutKey) || icons.Count == 0) return;

            string file = GetLayoutFile(layoutKey);
            string tempFile = file + $".{Environment.ProcessId}.{Thread.CurrentThread.ManagedThreadId}.tmp";
            try
            {
                string json = JsonSerializer.Serialize(icons, new JsonSerializerOptions { WriteIndented = true });
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.WriteAllText(tempFile, json);
                        File.Move(tempFile, file, overwrite: true);
                        tempFile = "";
                        Log($"SaveLayout({layoutKey}): saved {icons.Count} icons");
                        return;
                    }
                    catch (IOException) { Thread.Sleep(25); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(25); }
                }
            }
            catch (Exception ex)
            {
                Log($"SaveLayout({layoutKey}) exception: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private static (int x, int y) FindNextFreeGridSlot(List<(int x, int y)> occupiedPositions)
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);

            int startX = area.Left + 22;
            int startY = area.Top + 2;
            int stepX = 76;
            int stepY = 82;
            int maxRows = Math.Max(4, (area.Height - 40) / stepY);
            int maxCols = Math.Max(8, (area.Width - 40) / stepX);

            for (int col = 0; col < maxCols; col++)
            {
                for (int row = 0; row < maxRows; row++)
                {
                    int testX = startX + col * stepX;
                    int testY = startY + row * stepY;

                    bool occupied = false;
                    foreach (var pos in occupiedPositions)
                    {
                        if (Math.Abs(pos.x - testX) < 50 && Math.Abs(pos.y - testY) < 60)
                        {
                            occupied = true;
                            break;
                        }
                    }

                    if (!occupied) return (testX, testY);
                }
            }
            return (startX, startY);
        }

        private static HashSet<string> CollectFolderDisplayNames(string? folderPath, out int fileCount)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            fileCount = 0;
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return names;

            try
            {
                foreach (var entry in Directory.GetFileSystemEntries(folderPath))
                {
                    string fn = Path.GetFileName(entry);
                    if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                        fn.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                        continue;

                    fileCount++;
                    names.Add(fn);
                    if (!Directory.Exists(entry))
                    {
                        string noExt = Path.GetFileNameWithoutExtension(fn);
                        if (!string.IsNullOrWhiteSpace(noExt)) names.Add(noExt);
                    }
                }
            }
            catch { }
            return names;
        }

        public static bool WaitForDesktopChange(
            int oldItemCount,
            string? prevPath,
            string targetPath,
            int timeoutMs = 1500,
            Func<bool>? isCancelled = null)
        {
            var sw = Stopwatch.StartNew();
            IntPtr hList = IntPtr.Zero;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (isCancelled?.Invoke() == true) return false;
                hList = GetDesktopListView();
                if (hList != IntPtr.Zero) break;
                Thread.Sleep(30);
            }

            if (hList == IntPtr.Zero)
            {
                Log("WaitForDesktopChange: hList is Zero after waiting");
                return false;
            }

            var targetNames = CollectFolderDisplayNames(targetPath, out int expectedFileCount);
            var prevNames = CollectFolderDisplayNames(prevPath, out _);
            var exclusiveTargetNames = new HashSet<string>(targetNames, StringComparer.OrdinalIgnoreCase);
            exclusiveTargetNames.ExceptWith(prevNames);

            Log($"WaitForDesktopChange: oldCount={oldItemCount}, expectedFiles={expectedFileCount} in {targetPath}");

            if (oldItemCount <= 2 && expectedFileCount == 0)
            {
                Thread.Sleep(80);
                return isCancelled?.Invoke() != true;
            }

            bool phase1Passed = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (isCancelled?.Invoke() == true) return false;

                if (!TrySendMessage(hList, (uint)LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out IntPtr cPtr))
                {
                    Thread.Sleep(30);
                    continue;
                }
                int count = (int)cPtr;

                if (count != oldItemCount)
                {
                    phase1Passed = true;
                    Log($"Phase 1 passed: count changed {oldItemCount} -> {count} at {sw.ElapsedMilliseconds}ms");
                    break;
                }

                var cur = GetCurrentIcons();
                bool foundExclusive = false;
                foreach (var c in cur)
                {
                    if (exclusiveTargetNames.Count > 0
                        ? exclusiveTargetNames.Contains(c.Name)
                        : targetNames.Contains(c.Name))
                    {
                        foundExclusive = true;
                        break;
                    }
                }

                if (foundExclusive || (expectedFileCount == 0 && cur.Count <= 2))
                {
                    phase1Passed = true;
                    Log($"Phase 1 passed: items matched at {sw.ElapsedMilliseconds}ms");
                    break;
                }

                Thread.Sleep(30);
            }

            if (!phase1Passed)
            {
                Log($"WaitForDesktopChange: timed out waiting for Explorer switch ({timeoutMs}ms)");
                return false;
            }

            // Phase 2: Wait until item count stabilizes (supports 0 icons when expectedFileCount == 0!)
            int lastCount = -1;
            int stableFrames = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (isCancelled?.Invoke() == true) return false;

                if (!TrySendMessage(hList, (uint)LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out IntPtr cPtr))
                {
                    Thread.Sleep(40);
                    continue;
                }
                int count = (int)cPtr;
                bool validCount = (count > 0) || (expectedFileCount == 0 && count == 0);

                if (validCount && count == lastCount)
                {
                    stableFrames++;
                    if (stableFrames >= 2)
                    {
                        Log($"Phase 2 passed: count={count} stable at {sw.ElapsedMilliseconds}ms");
                        return true;
                    }
                }
                else
                {
                    stableFrames = 0;
                }
                lastCount = count;
                Thread.Sleep(45);
            }

            return isCancelled?.Invoke() != true;
        }

        public static void RestoreLayout(string layoutKey, Func<bool>? isCancelled = null, bool allowDirtySave = true)
        {
            if (!AppConfig.IsSafeId(layoutKey)) return;

            lock (_explorerLock)
            {
                try
                {
                    if (isCancelled?.Invoke() == true) return;

                    IntPtr hList = IntPtr.Zero;
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        if (isCancelled?.Invoke() == true) return;
                        hList = GetDesktopListView();
                        if (hList != IntPtr.Zero) break;
                        Thread.Sleep(50);
                    }

                    if (hList == IntPtr.Zero)
                    {
                        Log($"RestoreLayout({layoutKey}): hList is Zero");
                        return;
                    }

                    string file = GetLayoutFile(layoutKey);
                    var savedQueues = new Dictionary<string, Queue<(int x, int y)>>(StringComparer.OrdinalIgnoreCase);
                    var occupiedPositions = new List<(int x, int y)>();
                    var existingSavedList = new List<IconPosition>();

                    if (File.Exists(file))
                    {
                        var savedIcons = TryReadSavedLayout(layoutKey);
                        if (savedIcons == null)
                        {
                            Log($"RestoreLayout({layoutKey}) read error (aborting to protect layout)");
                            return;
                        }
                        existingSavedList = savedIcons;
                        foreach (var s in savedIcons)
                        {
                            if (!savedQueues.TryGetValue(s.Name, out var q))
                            {
                                q = new Queue<(int x, int y)>();
                                savedQueues[s.Name] = q;
                            }
                            q.Enqueue((s.X, s.Y));
                            occupiedPositions.Add((s.X, s.Y));
                        }
                    }

                    if (isCancelled?.Invoke() == true) return;

                    var currentIcons = GetCurrentIconsUnsafe();
                    Log($"RestoreLayout({layoutKey}): current={currentIcons.Count}, saved={occupiedPositions.Count}");
                    if (currentIcons.Count == 0) return;

                    var resolvedTargets = new List<(int lvIndex, string name, int x, int y)>(currentIcons.Count);
                    var newlyAllocated = new List<IconPosition>();

                    var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                    bool canClamp = vs.Width > 100 && vs.Height > 100;
                    int minX = vs.Left;
                    int maxX = vs.Right - 48;
                    int minY = vs.Top;
                    int maxY = vs.Bottom - 48;

                    foreach (var cur in currentIcons)
                    {
                        int targetX, targetY;
                        if (savedQueues.TryGetValue(cur.Name, out var q) && q.Count > 0)
                        {
                            var pos = q.Dequeue();
                            targetX = canClamp ? Math.Clamp(pos.x, minX, maxX) : pos.x;
                            targetY = canClamp ? Math.Clamp(pos.y, minY, maxY) : pos.y;
                        }
                        else
                        {
                            var slot = FindNextFreeGridSlot(occupiedPositions);
                            targetX = slot.x;
                            targetY = slot.y;
                            occupiedPositions.Add(slot);
                            newlyAllocated.Add(new IconPosition { Name = cur.Name, X = targetX, Y = targetY });
                        }

                        resolvedTargets.Add((cur.ListViewIndex, cur.Name, targetX, targetY));
                    }

                    if (isCancelled?.Invoke() == true) return;

                    GetWindowThreadProcessId(hList, out uint pid);
                    IntPtr hProc = IntPtr.Zero;
                    IntPtr remotePt = IntPtr.Zero;
                    IntPtr localPt = IntPtr.Zero;
                    bool redrawDisabled = false;
                    int ptSize = Marshal.SizeOf<POINT>();
                    int repositioned = 0;

                    try
                    {
                        hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_WRITE, false, pid);
                        if (hProc != IntPtr.Zero)
                        {
                            remotePt = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)ptSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                            if (remotePt != IntPtr.Zero)
                            {
                                localPt = Marshal.AllocHGlobal(ptSize);
                            }
                        }

                        if (TrySendMessage(hList, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero, out _))
                        {
                            redrawDisabled = true;
                        }

                        foreach (var item in resolvedTargets)
                        {
                            if (isCancelled?.Invoke() == true) break;
                            if (item.lvIndex < 0) continue;

                            if (remotePt != IntPtr.Zero && localPt != IntPtr.Zero)
                            {
                                Marshal.StructureToPtr(new POINT { X = item.x, Y = item.y }, localPt, false);
                                if (WriteProcessMemory(hProc, remotePt, localPt, (UIntPtr)ptSize, out _))
                                {
                                    TrySendMessage(hList, (uint)LVM_SETITEMPOSITION32, (IntPtr)item.lvIndex, remotePt, out _);
                                    repositioned++;
                                    continue;
                                }
                            }

                            // Fallback only for non-negative 16-bit coords (never wrap negative multi-monitor coords!)
                            if (item.x >= 0 && item.y >= 0 && item.x <= 65535 && item.y <= 65535)
                            {
                                IntPtr lparam = (IntPtr)(uint)(((item.y & 0xFFFF) << 16) | (item.x & 0xFFFF));
                                TrySendMessage(hList, (uint)LVM_SETITEMPOSITION, (IntPtr)item.lvIndex, lparam, out _);
                                repositioned++;
                            }
                        }
                    }
                    finally
                    {
                        if (redrawDisabled)
                        {
                            TrySendMessage(hList, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero, out _);
                            RedrawWindow(hList, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                        }

                        if (localPt != IntPtr.Zero) Marshal.FreeHGlobal(localPt);
                        if (remotePt != IntPtr.Zero && hProc != IntPtr.Zero) VirtualFreeEx(hProc, remotePt, UIntPtr.Zero, MEM_RELEASE);
                        if (hProc != IntPtr.Zero) CloseHandle(hProc);
                    }

                    Log($"RestoreLayout({layoutKey}): repositioned {repositioned}/{currentIcons.Count}");

                    // Merge newly allocated items into existing saved layout instead of discarding temporarily missing icons
                    if (allowDirtySave && newlyAllocated.Count > 0 && isCancelled?.Invoke() != true)
                    {
                        var merged = new List<IconPosition>(existingSavedList.Count + newlyAllocated.Count);
                        merged.AddRange(existingSavedList);
                        merged.AddRange(newlyAllocated);
                        SaveLayoutFromList(layoutKey, merged);
                    }
                }
                catch (Exception ex)
                {
                    Log($"RestoreLayout({layoutKey}) exception: {ex.Message}");
                }
            }
        }
    }
}

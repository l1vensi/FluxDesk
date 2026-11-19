using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WorkSpace
{
    public static class ShellHelper
    {
        private static readonly Guid FOLDERID_Desktop = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const int SHCNE_UPDATEDIR = 0x00001000;
        private const int SHCNF_IDLIST = 0x0000;
        private const int SHCNF_PATHW = 0x0005;

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

        private const uint SPI_GETDESKWALLPAPER = 0x0073;
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        public static string GetLiveDesktopFolder()
        {
            IntPtr pPath = IntPtr.Zero;
            try
            {
                var guid = FOLDERID_Desktop;
                int hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out pPath);
                if (hr == 0 && pPath != IntPtr.Zero)
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

        public static void NotifyDirectoryUpdated(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            IntPtr pPath = IntPtr.Zero;
            try
            {
                pPath = Marshal.StringToHGlobalUni(path);
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, pPath, IntPtr.Zero);
            }
            catch { }
            finally
            {
                if (pPath != IntPtr.Zero) Marshal.FreeHGlobal(pPath);
            }
        }

        public static string GetCurrentWallpaper()
        {
            try
            {
                var sb = new System.Text.StringBuilder(512);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
                {
                    return sb.ToString();
                }
            }
            catch { }
            return "";
        }

        public static bool SetWallpaper(string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return false;
                return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch (Exception ex)
            {
                AppConfig.AppendLog($"[ShellHelper] SetWallpaper error: {ex.Message}");
                return false;
            }
        }

        public static void SetContextMenuIntegration(bool enable, string? uiExePath)
        {
            const string desktopBgKey = @"Software\Classes\DesktopBackground\Shell\FluxDesk";
            const string legacyDirBgKey = @"Software\Classes\Directory\Background\shell\FluxDesk";
            try
            {
                // Clean up older Directory\Background registration so menu only appears on Desktop
                Registry.CurrentUser.DeleteSubKeyTree(legacyDirBgKey, false);

                if (enable && !string.IsNullOrWhiteSpace(uiExePath) && File.Exists(uiExePath))
                {
                    using var key = Registry.CurrentUser.CreateSubKey(desktopBgKey);
                    if (key != null)
                    {
                        key.SetValue("", "FluxDesk");
                        key.SetValue("Icon", $"\"{uiExePath}\",0");
                        using var cmdKey = key.CreateSubKey("command");
                        cmdKey?.SetValue("", $"\"{uiExePath}\"");
                    }
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(desktopBgKey, false);
                }
            }
            catch (Exception ex)
            {
                AppConfig.AppendLog($"[ShellHelper] SetContextMenuIntegration error: {ex.Message}");
            }
        }

        public static bool SetDesktopFolder(string targetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath))
                    return false;

                if (!Directory.Exists(targetPath))
                {
                    Directory.CreateDirectory(targetPath);
                }

                // Official Windows Known Folder API redirection (atomically updates REG_EXPAND_SZ & OneDrive KFM GUIDs)
                var guid = FOLDERID_Desktop;
                int hr = SHSetKnownFolderPath(ref guid, 0, IntPtr.Zero, targetPath);
                if (hr < 0)
                {
                    AppConfig.AppendLog($"[ShellHelper] SHSetKnownFolderPath failed with HRESULT 0x{hr:X8} for '{targetPath}'");
                    return false;
                }

                // Notify Explorer to refresh directory, reload icons and flush cached icon associations
                NotifyDirectoryUpdated(targetPath);
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                AppConfig.AppendLog($"[ShellHelper] SetDesktopFolder error: {ex.Message}");
            }
            return false;
        }

        public static void SetAutoStart(bool enable, string exePath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    if (enable && !string.IsNullOrWhiteSpace(exePath))
                    {
                        key.SetValue("FluxDesk", $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue("FluxDesk", false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppConfig.AppendLog($"[ShellHelper] SetAutoStart error: {ex.Message}");
            }
        }

        /// <summary>
        /// Registers FluxDesk in Windows "Apps &amp; Features" (HKCU Uninstall registry).
        /// When the user clicks "Uninstall" in Windows Settings, FluxDesk UI opens so the user
        /// can use the proper safe uninstall flow (which restores Desktop 1 and creates a backup).
        /// Call this on every startup so the entry is always up-to-date.
        /// </summary>
        public static void RegisterUninstallEntry(string? uiExePath, string version = "2.0.0")
        {
            const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FluxDesk";
            try
            {
                if (string.IsNullOrWhiteSpace(uiExePath) || !File.Exists(uiExePath)) return;

                using var key = Registry.CurrentUser.CreateSubKey(uninstallKey);
                if (key == null) return;

                // "Uninstall" button opens FluxDesk UI with --uninstall flag so the app shows the uninstall dialog
                string uninstallCmd = $"\"{uiExePath}\" --uninstall";

                key.SetValue("DisplayName",          "FluxDesk");
                key.SetValue("DisplayVersion",        version);
                key.SetValue("Publisher",             "@l1vensi");
                key.SetValue("DisplayIcon",           $"\"{uiExePath}\",0");
                key.SetValue("UninstallString",       uninstallCmd);
                key.SetValue("QuietUninstallString",  uninstallCmd);
                key.SetValue("InstallLocation",       Path.GetDirectoryName(uiExePath) ?? "");
                key.SetValue("NoModify",              1, Microsoft.Win32.RegistryValueKind.DWord);
                key.SetValue("NoRepair",              1, Microsoft.Win32.RegistryValueKind.DWord);
                key.SetValue("EstimatedSize",         4096, Microsoft.Win32.RegistryValueKind.DWord); // ~4 MB in KB
                key.SetValue("URLInfoAbout",          "https://github.com/l1vensi/FluxDesk");
            }
            catch (Exception ex)
            {
                AppConfig.AppendLog($"[ShellHelper] RegisterUninstallEntry error: {ex.Message}");
            }
        }

        /// <summary>Removes the FluxDesk entry from Windows "Apps &amp; Features".</summary>
        public static void RemoveUninstallEntry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FluxDesk", false);
            }
            catch { }
        }
    }
}

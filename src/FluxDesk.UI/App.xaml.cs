using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DeskSpace;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    internal const uint WM_FLUXDESK_OPEN_UNINSTALL = 0x8001; // WM_APP + 1

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _singleInstanceMutex = new Mutex(false, @"Local\FluxDesk_UI_SingleInstance");
        bool acquired = false;
        try
        {
            acquired = _singleInstanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            // If launched with --uninstall, bring existing instance and ask it to open uninstall dialog
            if (e.Args.Length > 0 && e.Args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                BringExistingInstanceToFront(openUninstall: true);
            }
            else
            {
                BringExistingInstanceToFront();
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        // If launched with --uninstall (and we ARE the first instance), open uninstall dialog after window loads
        if (e.Args.Length > 0 && e.Args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            Startup += (_, _) =>
            {
                if (MainWindow is MainWindow mw)
                    mw.OpenUninstallDialogOnLoad = true;
            };
        }

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluxDesk");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "log.txt"), $"[{DateTime.Now:HH:mm:ss.fff}] [UI Unhandled] {e.Exception}\n");
        }
        catch { }
    }

    private static void BringExistingInstanceToFront(bool openUninstall = false)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var procs = Process.GetProcessesByName(current.ProcessName);
            bool activated = false;
            foreach (var p in procs)
            {
                try
                {
                    if (!activated && p.Id != current.Id && p.SessionId == current.SessionId && p.MainWindowHandle != IntPtr.Zero)
                    {
                        IntPtr hWnd = p.MainWindowHandle;
                        ShowWindow(hWnd, IsIconic(hWnd) ? SW_RESTORE : SW_SHOW);
                        SetForegroundWindow(hWnd);
                        activated = true;

                        // Send WM_APP+1 to signal the existing instance to open uninstall dialog
                        if (openUninstall)
                        {
                            PostMessage(hWnd, WM_FLUXDESK_OPEN_UNINSTALL, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}

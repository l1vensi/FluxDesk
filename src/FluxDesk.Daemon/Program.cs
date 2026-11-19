using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace WorkSpace
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(false, @"Local\FluxDesk_Daemon_SingleInstance");
            bool acquired = false;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                // Daemon is already running -> bring up FluxDesk UI if launched manually
                try
                {
                    string? uiExe = AppConfig.FindUiExecutable();
                    if (!string.IsNullOrEmpty(uiExe))
                    {
                        using var p = Process.Start(new ProcessStartInfo(uiExe) { UseShellExecute = true });
                    }
                }
                catch { }
                return;
            }

            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                ApplicationConfiguration.Initialize();
                using var ctx = new WorkSpaceContext();
                Application.Run(ctx);
            }
            catch (Exception ex)
            {
                AppConfig.AppendLog($"[Program] Fatal exception: {ex}");
            }
            finally
            {
                DesktopPreviewGenerator.Shutdown();
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }
}

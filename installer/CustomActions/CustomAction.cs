using System;
using System.Diagnostics;
using System.Threading;
using WixToolset.Dtf.WindowsInstaller;

namespace LithicBackup.CustomActions
{
    /// <summary>
    /// Windows Installer custom action(s) for the Lithic Backup MSI.
    /// </summary>
    public static class CustomActions
    {
        // Session-local named event the running GUI listens on (see
        // App.xaml.cs, ShutdownSignalName). Signalling it asks the GUI to close
        // itself gracefully so the upgrade can replace LithicBackup.exe.
        private const string ShutdownEventName = "LithicBackup.Shutdown";

        // Base name (no extension) of the GUI process to wait on. Note this does
        // NOT match "LithicBackup.Worker" — the Worker service is handled
        // separately via ServiceControl/StopServices.
        private const string GuiProcessName = "LithicBackup";

        // Bounded wait for the GUI to exit after being asked to. Long enough for a
        // graceful WPF shutdown, short enough not to stall the install noticeably
        // if the app can't or won't close (e.g. a pre-signal-support build the
        // user declined to close manually).
        private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Immediate custom action, scheduled Before InstallValidate: signal the
        /// running GUI to shut down (releasing its locked .exe), then wait briefly
        /// for it to exit so the file-in-use check sees nothing to close.
        ///
        /// This replaces the old force-kill approach, which failed when the GUI
        /// ran elevated: a Medium-integrity installer action cannot terminate a
        /// High-integrity process. A process can always close ITSELF regardless of
        /// integrity level, so signalling + a graceful self-shutdown needs no
        /// elevation, no taskkill, and no self-elevating bundle.
        ///
        /// Always returns Success — a failure here must never break the install
        /// (worst case it degrades to Windows Installer's own file-in-use handling).
        /// </summary>
        [CustomAction]
        public static ActionResult SignalLithicGuiShutdown(Session session)
        {
            try
            {
                try
                {
                    // OpenExisting throws WaitHandleCannotBeOpenedException when no
                    // GUI is running, or when the running GUI predates this signal
                    // support (no listener). Either way there's nothing to signal.
                    using (var ev = EventWaitHandle.OpenExisting(ShutdownEventName))
                    {
                        ev.Set();
                        session.Log("SignalLithicGuiShutdown: signalled '" + ShutdownEventName + "'.");
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    session.Log("SignalLithicGuiShutdown: no shutdown listener found (GUI not running or predates signal support).");
                }

                // Whether or not we could signal, if a GUI process is present give
                // it bounded time to release its .exe. This also covers the in-app
                // updater path, where the old GUI closes ITSELF around launching
                // the installer — waiting here removes that race even for a build
                // with no listener.
                var deadline = DateTime.UtcNow + ExitWait;
                while (DateTime.UtcNow < deadline && GuiIsRunning())
                {
                    Thread.Sleep(200);
                }

                session.Log(GuiIsRunning()
                    ? "SignalLithicGuiShutdown: GUI still running after wait; deferring to Installer file-in-use handling."
                    : "SignalLithicGuiShutdown: GUI is not running; .exe is free to replace.");
            }
            catch (Exception ex)
            {
                // Never fail the install on a best-effort convenience action.
                session.Log("SignalLithicGuiShutdown: ignored error: " + ex);
            }

            return ActionResult.Success;
        }

        private static bool GuiIsRunning()
        {
            try
            {
                return Process.GetProcessesByName(GuiProcessName).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}

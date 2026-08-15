using System;
using System.Diagnostics;
using System.ServiceProcess;
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
        private const string GuiShutdownEventName = "LithicBackup.Shutdown";

        // Global\ named event the running Worker service listens on (see
        // src\LithicBackup.Worker\ShutdownSignalListener.cs, ShutdownSignalName —
        // this string must match it exactly). Global\ because the service lives in
        // session 0 while this custom action runs in the user's interactive
        // session; a session-local name would not be visible to both.
        private const string WorkerShutdownEventName = @"Global\LithicBackup.Worker.Shutdown";

        // Base names (no extension) of the processes to wait on.
        private const string GuiProcessName = "LithicBackup";
        private const string WorkerProcessName = "LithicBackup.Worker";

        // Service name as registered by Package.wxs / Program.cs.
        private const string WorkerServiceName = "Lithic Backup";

        // Bounded wait for the asked-to-close processes to actually exit. Long
        // enough for a graceful WPF shutdown and for the Worker to abandon a
        // backup in flight, short enough not to stall the install noticeably if
        // something can't or won't close (e.g. a pre-signal-support build the
        // user declined to close manually). Both processes are waited on
        // CONCURRENTLY, so this is the total, not per-process.
        private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(25);

        /// <summary>
        /// Immediate custom action, scheduled Before InstallValidate: ask both the
        /// running GUI and the running Worker service to shut themselves down
        /// (releasing the files they hold in the install folder), then wait
        /// briefly for them to exit so the file-in-use check sees nothing to close.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why not just let Windows Installer handle it.</b> The standard
        /// actions sit in this order:
        /// </para>
        /// <code>
        ///   1399  SignalLithicShutdown   (this action)
        ///   1400  InstallValidate        &lt;- file-in-use check happens HERE
        ///   1500  InstallInitialize      &lt;- UAC elevation happens HERE
        ///   1900  StopServices           &lt;- ServiceControl Stop runs HERE
        /// </code>
        /// <para>
        /// Anything still holding a file at 1400 produces the "setup was unable to
        /// automatically close all requested applications" dialog (error 1611).
        /// The <c>&lt;ServiceControl Stop="both"&gt;</c> in <c>Package.wxs</c> runs
        /// 500 steps too late to prevent that; it exists to keep the service
        /// stopped for the duration of the file transfer, not to pass the check.
        /// </para>
        /// <para>
        /// <b>Why we ask rather than kill/stop.</b> For a double-clicked
        /// per-machine MSI everything before <c>InstallInitialize</c> runs in the
        /// unelevated client process at the invoking user's Medium integrity.
        /// From there, terminating an elevated GUI is Access Denied (UIPI) and
        /// stopping a LocalSystem service is Access Denied (SCM). But a process
        /// can always shut ITSELF down whatever its integrity level — so we
        /// signal, and they comply. No taskkill, no elevation, no self-elevating
        /// bundle.
        /// </para>
        /// <para>
        /// Always returns Success — a failure here must never break the install
        /// (worst case it degrades to Windows Installer's own file-in-use handling).
        /// </para>
        /// </remarks>
        [CustomAction]
        public static ActionResult SignalLithicShutdown(Session session)
        {
            try
            {
                SignalGui(session);
                StopWorker(session);

                // Wait for BOTH concurrently — the GUI and the Worker wind down
                // independently, so serialising two waits would only add latency.
                var deadline = DateTime.UtcNow + ExitWait;
                while (DateTime.UtcNow < deadline
                       && (IsRunning(GuiProcessName) || IsRunning(WorkerProcessName)))
                {
                    Thread.Sleep(200);
                }

                Report(session, "GUI", GuiProcessName);
                Report(session, "Worker", WorkerProcessName);
            }
            catch (Exception ex)
            {
                // Never fail the install on a best-effort convenience action.
                session.Log("SignalLithicShutdown: ignored error: " + ex);
            }

            return ActionResult.Success;
        }

        /// <summary>
        /// Ask the interactive GUI to close itself.
        /// </summary>
        private static void SignalGui(Session session)
        {
            try
            {
                // OpenExisting throws WaitHandleCannotBeOpenedException when no
                // GUI is running, or when the running GUI predates this signal
                // support (no listener). Either way there's nothing to signal.
                using (var ev = EventWaitHandle.OpenExisting(GuiShutdownEventName))
                {
                    ev.Set();
                    session.Log("SignalLithicShutdown: signalled '" + GuiShutdownEventName + "'.");
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                session.Log("SignalLithicShutdown: no GUI shutdown listener found (GUI not running or predates signal support).");
            }
            catch (Exception ex)
            {
                session.Log("SignalLithicShutdown: could not signal the GUI: " + ex.Message);
            }
        }

        /// <summary>
        /// Ask the Worker service to stop, by whichever route this process has the
        /// rights for.
        /// </summary>
        /// <remarks>
        /// The SCM route is tried first because it is the one that also updates the
        /// service's recorded state, so nothing tries to restart it mid-transfer.
        /// It succeeds only when the MSI was launched already-elevated (an admin
        /// command prompt, or the in-app updater running elevated). For the ordinary
        /// double-click it is Access Denied, and the named-event route — which needs
        /// no privilege at all, because the service is shutting ITSELF down — takes
        /// over. Both are attempted regardless: they are idempotent, and belt-and-
        /// braces costs milliseconds against a dialog that blocks the whole upgrade.
        /// </remarks>
        private static void StopWorker(Session session)
        {
            try
            {
                using (var sc = new ServiceController(WorkerServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped
                        && sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        session.Log("SignalLithicShutdown: sent SCM stop to service '" + WorkerServiceName + "'.");
                    }
                    else
                    {
                        session.Log("SignalLithicShutdown: service '" + WorkerServiceName + "' is already stopped/stopping.");
                    }
                }
            }
            catch (Exception ex)
            {
                // InvalidOperationException wrapping Win32 ERROR_ACCESS_DENIED for
                // the unelevated case, or ERROR_SERVICE_DOES_NOT_EXIST on a first
                // install. Neither is a problem — the signal below covers us.
                session.Log("SignalLithicShutdown: SCM stop unavailable (" + ex.Message + "); falling back to the shutdown signal.");
            }

            try
            {
                using (var ev = EventWaitHandle.OpenExisting(WorkerShutdownEventName))
                {
                    ev.Set();
                    session.Log("SignalLithicShutdown: signalled '" + WorkerShutdownEventName + "'.");
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                session.Log("SignalLithicShutdown: no Worker shutdown listener found (service not running or predates signal support).");
            }
            catch (Exception ex)
            {
                session.Log("SignalLithicShutdown: could not signal the Worker: " + ex.Message);
            }
        }

        private static void Report(Session session, string label, string processName)
        {
            session.Log(IsRunning(processName)
                ? "SignalLithicShutdown: " + label + " still running after wait; deferring to Installer file-in-use handling."
                : "SignalLithicShutdown: " + label + " is not running; its files are free to replace.");
        }

        private static bool IsRunning(string processName)
        {
            try
            {
                return Process.GetProcessesByName(processName).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}

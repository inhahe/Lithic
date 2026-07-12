using Microsoft.Win32;
using LithicBackup.Infrastructure.Data;

namespace LithicBackup.Infrastructure.Diagnostics;

/// <summary>
/// Enables Windows Error Reporting (WER) "LocalDumps" so that <em>native</em>
/// crashes — access violations, stack overflows, corrupted-state exceptions,
/// COM/interop faults (e.g. IMAPI2 disc burning) and <c>FailFast</c> — leave a
/// post-mortem <c>.dmp</c> behind.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CrashLogger"/> only catches <em>managed</em> exceptions: it hooks
/// <c>AppDomain.UnhandledException</c>, <c>DispatcherUnhandledException</c> and
/// <c>TaskScheduler.UnobservedTaskException</c>. On modern .NET (5+), corrupted
/// state exceptions such as access violations are <b>not</b> delivered to those
/// handlers — the runtime fast-fails the process — so a whole class of crashes
/// would otherwise vanish without any trace. WER LocalDumps is the OS-level
/// safety net for exactly those cases.
/// </para>
/// <para>
/// Registration writes under
/// <c>HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\&lt;image&gt;.exe</c>,
/// which requires administrative rights. This is best-effort: the Windows
/// Service (LocalSystem) can always register — and it registers for <b>both</b>
/// executables — while the unelevated GUI silently no-ops. Once registered, the
/// keys persist across restarts, so a one-time successful registration by the
/// service covers all future GUI crashes too.
/// </para>
/// </remarks>
public static class NativeCrashDumps
{
    private const string LocalDumpsKey =
        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    /// <summary>Executable image names (as WER sees them) to register.</summary>
    private static readonly string[] ImageNames =
    [
        "LithicBackup.exe",         // WPF GUI
        "LithicBackup.Worker.exe",  // Windows Service
    ];

    /// <summary>
    /// Register WER LocalDumps for the LithicBackup executables so native crashes
    /// produce a <c>.dmp</c> under <see cref="CatalogLocation.DumpsDirectory"/>.
    /// Best-effort and idempotent; never throws.
    /// </summary>
    public static void TryEnableLocalDumps()
    {
        try
        {
            var dumpFolder = CatalogLocation.DumpsDirectory();

            foreach (var image in ImageNames)
            {
                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(
                        LocalDumpsKey + "\\" + image, writable: true);
                    if (key is null)
                        continue;

                    // DumpFolder is REG_EXPAND_SZ per the WER contract. We write a
                    // literal (already-expanded) path; ExpandString keeps the type
                    // WER expects while leaving the plain path untouched.
                    key.SetValue("DumpFolder", dumpFolder, RegistryValueKind.ExpandString);

                    // Keep a rolling set of recent dumps; WER discards the oldest
                    // beyond this count.
                    key.SetValue("DumpCount", 10, RegistryValueKind.DWord);

                    // 1 = mini dump (thread stacks, module list, handle info) —
                    // small yet enough to locate a native fault. 2 = full memory.
                    key.SetValue("DumpType", 1, RegistryValueKind.DWord);
                }
                catch
                {
                    // Unelevated (GUI) or a locked-down box — skip this image.
                    // The service registers the same keys with admin rights.
                }
            }
        }
        catch
        {
            // Diagnostics must never take down startup.
        }
    }
}

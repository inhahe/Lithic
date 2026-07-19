using System.Security.AccessControl;
using System.Security.Principal;

namespace LithicBackup.Infrastructure.Data;

/// <summary>
/// Resolves the on-disk location of the backup catalog database in a way that
/// is shared between the WPF GUI (which runs as the interactive user) and the
/// Windows Service (which runs as LocalSystem).
/// </summary>
/// <remarks>
/// <para>
/// Historically both processes used <see cref="Environment.SpecialFolder.LocalApplicationData"/>,
/// which is <em>per-account</em>. The GUI therefore opened
/// <c>C:\Users\&lt;user&gt;\AppData\Local\LithicBackup\catalog.db</c> while the
/// service (LocalSystem) opened
/// <c>C:\Windows\System32\config\systemprofile\AppData\Local\LithicBackup\catalog.db</c>
/// — two completely different databases. The service was effectively backing up
/// against an empty catalog while the GUI showed the real one, so versions and
/// history created by one were invisible to the other.
/// </para>
/// <para>
/// The fix is to anchor the catalog at <see cref="Environment.SpecialFolder.CommonApplicationData"/>
/// (<c>C:\ProgramData\LithicBackup</c>), which is identical for every account.
/// The directory's ACL is widened so both the interactive user and SYSTEM can
/// read and write the database regardless of which process created it first.
/// On first run after the upgrade, an existing per-user catalog is migrated into
/// the shared location so no history is lost.
/// </para>
/// </remarks>
public static class CatalogLocation
{
    /// <summary>Folder name used under both the shared and legacy roots.</summary>
    private const string AppFolderName = "LithicBackup";

    /// <summary>Database file name.</summary>
    private const string DbFileName = "catalog.db";

    /// <summary>
    /// The shared application-data root (<c>C:\ProgramData\LithicBackup</c>) that
    /// holds the master catalog, per-set databases, logs, and crash dumps. This is
    /// a pure path computation with no directory creation or ACL work, so it is
    /// cheap enough to call on hot paths such as the per-file backup filter.
    /// </summary>
    public static string RootDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppFolderName);

    /// <summary>
    /// True when <paramref name="path"/> is the application's own data directory
    /// (<see cref="RootDirectory"/>) or anything beneath it. The backup engine uses
    /// this to <b>unconditionally</b> skip its own live files — catalog databases,
    /// their WAL/SHM sidecars, logs, and crash dumps. Backing them up is never
    /// wanted: it wastes space and, because the databases are open for writing by
    /// this very process, fails with sharing/lock errors ("File region is locked").
    /// The check is enforced independently of the user's selection tree and glob
    /// exclusions, so an auto-include-new parent (e.g. all of <c>C:\</c>) can never
    /// sweep the app's data directory into a backup.
    /// </summary>
    public static bool IsInsideAppDataDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var root = RootDirectory.TrimEnd('\\');
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the path to the shared catalog database, creating its directory
    /// (with an ACL both the user and SYSTEM can write) and, on first run,
    /// migrating any pre-existing per-user catalog into the shared location.
    /// </summary>
    public static string Resolve()
    {
        var sharedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppFolderName);

        Directory.CreateDirectory(sharedDir);
        TryGrantSharedAccess(sharedDir);

        var sharedDb = Path.Combine(sharedDir, DbFileName);

        if (!File.Exists(sharedDb))
            TryMigrateLegacyCatalog(sharedDb);

        return sharedDb;
    }

    /// <summary>
    /// Returns the shared directory for diagnostic logs and crash reports,
    /// creating it (and widening its ACL) so both the interactive user (GUI) and
    /// LocalSystem (service) can write to it. Lives alongside the catalog under
    /// <c>C:\ProgramData\LithicBackup\logs</c> so a single location holds the
    /// rolling diagnostic log and any crash dumps from either process.
    /// </summary>
    public static string LogsDirectory()
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppFolderName,
            "logs");

        Directory.CreateDirectory(logsDir);
        TryGrantSharedAccess(logsDir);

        return logsDir;
    }

    /// <summary>
    /// Returns the shared directory where Windows Error Reporting writes native
    /// crash dumps (<c>.dmp</c>) for the GUI and service. Lives under
    /// <c>C:\ProgramData\LithicBackup\logs\dumps</c>, ACL-widened so a dump
    /// produced by SYSTEM (service) is still readable by the interactive user.
    /// See <see cref="Diagnostics.NativeCrashDumps"/> for the WER registration.
    /// </summary>
    public static string DumpsDirectory()
    {
        var dumpsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppFolderName,
            "logs",
            "dumps");

        Directory.CreateDirectory(dumpsDir);
        TryGrantSharedAccess(dumpsDir);

        return dumpsDir;
    }

    /// <summary>
    /// Grant the built-in Users group full control of the shared directory, with
    /// inheritance so the database file (and its <c>-wal</c>/<c>-shm</c> sidecars)
    /// are writable no matter which account created them. Without this, a file
    /// created by SYSTEM would leave the interactive user with read-only access
    /// (and vice-versa), recreating the split-catalog problem in a subtler form.
    /// </summary>
    private static void TryGrantSharedAccess(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var security = info.GetAccessControl();

            // S-1-5-32-545 = BUILTIN\Users. Covers the interactive account.
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            // S-1-5-18 = LocalSystem (the service account).
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                system,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch
        {
            // Best-effort. Setting the DACL requires being the directory's owner
            // or holding WRITE_DAC; if it fails the existing inherited ACL stands.
            // SYSTEM can always access the file regardless, so this only matters
            // for the user-writes-SYSTEM-created-file case on locked-down boxes.
        }
    }

    /// <summary>
    /// One-time migration: if the shared catalog does not yet exist but a legacy
    /// per-account catalog does, copy it (and its WAL/SHM sidecars) into the
    /// shared location so existing history carries over. Only the calling
    /// account's own legacy catalog is visible here, which is exactly what we
    /// want — the GUI (interactive user) migrates the real catalog; the service's
    /// near-empty stub is simply abandoned.
    /// </summary>
    private static void TryMigrateLegacyCatalog(string sharedDb)
    {
        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName);
            var legacyDb = Path.Combine(legacyDir, DbFileName);

            if (!File.Exists(legacyDb))
                return;

            // Copy the main database and any live WAL/SHM sidecars so an open
            // (unckeckpointed) catalog migrates without losing recent writes.
            File.Copy(legacyDb, sharedDb, overwrite: false);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var src = legacyDb + suffix;
                if (File.Exists(src))
                    File.Copy(src, sharedDb + suffix, overwrite: false);
            }
        }
        catch
        {
            // Best-effort migration. If it fails, the service/GUI will create a
            // fresh empty catalog at the shared path; the legacy file is left
            // intact for manual recovery.
        }
    }
}

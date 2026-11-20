#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace LithicBackup.Harness;

/// <summary>
/// A harness's scratch folder. Each run makes its own and removes it at its end;
/// the folder of a run that ended without removing it (it threw, or was killed)
/// is removed by the next run of the same harness. Compiled into every harness
/// by tools\Directory.Build.targets.
/// </summary>
internal static class ScratchFolder
{
    /// <summary>
    /// The name of a folder <see cref="Create"/> makes: the harness's prefix, the
    /// id of the process that made it, a fresh id. Nothing else is ever deleted.
    /// </summary>
    private static readonly Regex Name = new(@"^lithic_[a-z_]+_(?<pid>\d+)_[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Make a new, empty folder for this run. Folders an earlier run of the same
    /// harness left behind, once that run's process is gone, are removed first.
    /// </summary>
    /// <param name="prefix">Names the harness: "lithic_", lower-case words, a closing "_" ("lithic_targeted_").</param>
    /// <param name="parent">Where to make it; %TEMP% when null.</param>
    /// <returns>The folder's full path.</returns>
    public static string Create(string prefix, string? parent = null)
    {
        string root = Path.Combine(parent ?? Path.GetTempPath(), $"{prefix}{Environment.ProcessId}_{Guid.NewGuid():N}");
        if (!Name.IsMatch(Path.GetFileName(root)))
            throw new ArgumentException($"not a scratch folder prefix: \"{prefix}\"", nameof(prefix));
        RemoveAbandoned(prefix, Path.GetDirectoryName(root)!);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Remove a folder <see cref="Create"/> made, and everything in it. Call it
    /// once the harness has disposed every catalog it opened there: an open SQLite
    /// file keeps the folder. SQLite's pooled connections are let go first; a delete
    /// that fails is retried for two seconds, since a file another process still
    /// has open is only marked for deletion until it lets go, and read-only files
    /// and folders (a copy keeps the flag) are made writable.
    /// </summary>
    /// <returns>Whether the folder is gone: the harness checks it, so that a leak fails the run. When not, why is printed.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is not a folder <see cref="Create"/> made; nothing is deleted.</exception>
    public static bool Delete(string root)
    {
        if (!Name.IsMatch(Path.GetFileName(Path.TrimEndingDirectorySeparator(root))))
            throw new ArgumentException($"not a scratch folder: {root}", nameof(root));
        ClearSqlitePools();
        Exception? last = null;
        for (int attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (last is null)
                    MakeWritable(root);
                last = ex;
            }
            if (Directory.Exists(root))
                Thread.Sleep(100);
        }
        if (!Directory.Exists(root))
            return true;
        Console.WriteLine($"  could not delete {root}: {last?.Message}");
        return false;
    }

    /// <summary>Remove the folders of earlier runs with this <paramref name="prefix"/> whose process is gone.</summary>
    private static void RemoveAbandoned(string prefix, string parent)
    {
        if (!Directory.Exists(parent))
            return;
        foreach (string folder in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            // The prefix's own folders only: "lithic_est_" is not "lithic_est_inc_".
            var match = Name.Match(Path.GetFileName(folder));
            if (!match.Success || match.Groups["pid"].Index != prefix.Length
                || !int.TryParse(match.Groups["pid"].Value, out int pid) || IsRunning(pid))
                continue;
            Console.WriteLine($"  (removing {folder}, left by a run that did not finish)");
            Delete(folder);
        }
    }

    /// <summary>Whether a process with this id exists; one that cannot be asked counts as running, so its folder stays.</summary>
    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            try
            {
                return !process.HasExited;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>SQLite's pooled connections keep their files open after the catalog is disposed.</summary>
    private static void ClearSqlitePools()
    {
        try
        {
            // By name: not every harness references SQLite.
            Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite")
                ?.GetMethod("ClearAllPools", Type.EmptyTypes)?.Invoke(null, null);
        }
        catch (Exception)
        {
            // Best effort: the delete says whether it mattered.
        }
    }

    /// <summary>Clear the read-only flag of everything under <paramref name="root"/>, entry by entry.</summary>
    private static void MakeWritable(string root)
    {
        // Never through a junction or a link: it can lead out of the folder, and
        // the delete removes the link, not what it leads to.
        var inside = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        try
        {
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", inside))
            {
                try
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone, or not ours to change: the next one may still matter.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder went while it was walked: the delete says whether it mattered.
        }
    }
}

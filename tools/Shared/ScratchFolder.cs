using System;
using System.IO;
using System.Threading;

namespace LithicBackup.Harness;

/// <summary>
/// A harness's scratch folder under %TEMP%. Every run makes a new one and must
/// remove it again: one left behind per run filled %TEMP% with hundreds.
/// Compiled into every harness by tools\Directory.Build.targets.
/// </summary>
internal static class ScratchFolder
{
    /// <summary>Make a new, empty folder for this run.</summary>
    /// <param name="prefix">Names the harness, "lithic_targeted_" say; a fresh id follows it.</param>
    /// <returns>The folder's full path.</returns>
    public static string Create(string prefix)
    {
        string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Remove <paramref name="root"/> and everything in it. Call it once the harness
    /// has disposed every catalog it opened there: an open SQLite file keeps the
    /// folder. SQLite's pooled connections are let go first, read-only files (a
    /// copy keeps the flag) are made writable, and the delete is retried for two
    /// seconds, since a file another process still has open is only marked for
    /// deletion until that process lets go.
    /// </summary>
    /// <returns>Whether the folder is gone: the harness checks it, so that a leak fails the run.</returns>
    public static bool Delete(string root)
    {
        // By name: not every harness references SQLite.
        Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite")
            ?.GetMethod("ClearAllPools", Type.EmptyTypes)?.Invoke(null, null);
        try
        {
            // Never through a junction or a link: it can lead out of the folder
            // (a harness links to a drive's root), and the delete removes the
            // link, not what it leads to.
            var inside = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };
            if (Directory.Exists(root))
                foreach (string file in Directory.EnumerateFiles(root, "*", inside))
                    if (new FileInfo(file) is { IsReadOnly: true } readOnly)
                        readOnly.IsReadOnly = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The delete below says whether it mattered.
        }
        for (int attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something in it is still open: try again shortly.
            }
            if (Directory.Exists(root))
                Thread.Sleep(100);
        }
        return !Directory.Exists(root);
    }
}

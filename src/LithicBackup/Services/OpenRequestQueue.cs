using System.IO;

namespace LithicBackup.Services;

/// <summary>
/// Backup set exports (<c>.lithic</c>) the user opened with Lithic Backup -
/// double-clicked in Explorer, or "Open with" - waiting for the running app to
/// offer them as backup sets.
/// </summary>
/// <remarks>
/// Only one Lithic Backup window runs per session, so opening a file while the
/// app is already running starts a second process that must hand the file over
/// and exit. It writes the path here, one small file per request, and then sets
/// the single-instance "show window" event as before; the running app takes
/// every queued request when it wakes. A folder rather than a pipe: nothing has
/// to be listening at the moment of the hand-over (the running app may still be
/// starting up), several files opened together queue in order, and a request
/// can never be half-delivered. Requests older than <see cref="MaxAge"/> are
/// discarded unread, so one left behind by a crash does not pop up days later.
/// The app's own launch does not use the queue: it offers its own files directly.
/// </remarks>
internal static class OpenRequestQueue
{
    /// <summary>The extension the installer associates with Lithic Backup.</summary>
    public const string ExportExtension = ".lithic";

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Tests point the queue elsewhere, as CacheMaintenance.DirectoryOverride does.</summary>
    internal static string? DirectoryOverride { get; set; }

    private static string QueueDirectory => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LithicBackup", "open-requests");

    /// <summary>
    /// The export files named on a command line (the process's arguments, not
    /// including the executable): existing files with an export's extension.
    /// Flags such as <c>--test-mode</c> are not files and are skipped.
    /// </summary>
    public static List<string> FilesFromArguments(IEnumerable<string> args) =>
        args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Where(a => a.EndsWith(ExportExtension, StringComparison.OrdinalIgnoreCase)
                        || a.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(a =>
            {
                try { return Path.GetFullPath(a); }
                catch { return null; }
            })
            .Where(a => a is not null && File.Exists(a))
            .Select(a => a!)
            .ToList();

    /// <summary>Queue <paramref name="paths"/> for the running app. Best effort.</summary>
    public static void Enqueue(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                Directory.CreateDirectory(QueueDirectory);
                // Sortable by time, unique within it. Written under another
                // name and renamed, so TakeAll never reads one half-written.
                string name = $"{DateTime.UtcNow.Ticks:D20}-{Guid.NewGuid():N}";
                string partial = Path.Combine(QueueDirectory, name + ".partial");
                File.WriteAllText(partial, path);
                File.Move(partial, Path.Combine(QueueDirectory, name + ".txt"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The running app will just come to the front without the file.
            }
        }
    }

    /// <summary>
    /// Remove and return every queued request, oldest first. Also removes a
    /// request a launch began writing and never finished (it crashed between
    /// writing and renaming it), once it is older than <see cref="MaxAge"/>.
    /// </summary>
    public static List<string> TakeAll()
    {
        var paths = new List<string>();
        string[] entries;
        try
        {
            if (!Directory.Exists(QueueDirectory))
                return paths;
            entries = Directory.GetFiles(QueueDirectory, "*.txt");
            foreach (string unfinished in Directory.GetFiles(QueueDirectory, "*.partial"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(unfinished) > MaxAge)
                        File.Delete(unfinished);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return paths;
        }

        Array.Sort(entries, StringComparer.Ordinal);
        foreach (string entry in entries)
        {
            try
            {
                bool fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(entry) <= MaxAge;
                string path = File.ReadAllText(entry).Trim();
                File.Delete(entry);
                if (fresh && path.Length > 0 && File.Exists(path))
                    paths.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Already taken, or not deletable; skip it.
            }
        }
        return paths;
    }
}

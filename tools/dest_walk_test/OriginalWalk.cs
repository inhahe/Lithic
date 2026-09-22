// FROZEN ORACLE — the destination walk exactly as it was before the
// optimisation, lifted verbatim from git (commit 8e16dcc) and changed only
// enough to compile outside the view model:
//
//   * renamed WalkDestination -> WalkOriginal
//   * TryReconstructSourcePath taken as a delegate instead of a private method
//
// Nothing about its behaviour is touched. It exists so the new parallel walker
// can be proved to produce identical verdicts, on the same trees, rather than
// merely plausible ones — this is the code path that decides which files Cleanup
// offers to DELETE.
//
// Do not "improve" this file. Its only job is to disagree when the real walker
// changes meaning.

using System.IO;
using LithicBackup.Services;

namespace DestWalkTest;

internal static class OriginalWalk
{
    // ViewModelBase constant the original referenced; same value.
    private const int ProgressUpdateIntervalMs = 500;

    internal static (List<(string DiscRel, long Size, string? SourcePath)> Untracked,
                    List<(string DiscRel, long Size, string? SourcePath)> CatalogDeleted,
                    int DirectoriesSkipped,
                    int FilesScanned)
        WalkOriginal(
            string targetDir,
            Dictionary<UInt128, DestPathState> discPathLookup,
            Func<string, string?> TryReconstructSourcePath,
            IProgress<string> progress)
    {
        var untracked = new List<(string, long, string?)>();
        var catalogDeleted = new List<(string, long, string?)>();

        var targetInfo = new DirectoryInfo(targetDir);
        if (!targetInfo.Exists)
            throw new DirectoryNotFoundException($"Destination directory not found: {targetDir}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastProgressMs = 0;
        int filesScanned = 0;
        int directoriesSkipped = 0;

        // Iterative DFS using an explicit stack to avoid any concern about
        // deep recursion blowing the thread stack on extremely-nested
        // destinations.  Each directory's file + subdirectory enumerations
        // are wrapped in their own try/catch so a single unreadable folder
        // (UnauthorizedAccessException, PathTooLongException, ArgumentException
        // from weird path chars, COMException from network shares dropping,
        // etc.) is recorded as a skip and the walk continues.
        //
        // We deliberately catch the broad Exception base type at the
        // directory-enumeration boundary — the .NET docs only list a few
        // exception types for these calls, but in practice security
        // providers, antivirus drivers, and reparse points can throw
        // arbitrary derived exceptions, and we never want one of those to
        // silently terminate the entire scan.
        var stack = new Stack<(DirectoryInfo Dir, string RelativeDir)>();
        stack.Push((targetInfo, ""));

        while (stack.Count > 0)
        {
            var (dir, relativeDir) = stack.Pop();

            // Skip shared content-addressed stores at the top level — these
            // aren't user-visible backup files and are managed internally.
            if (relativeDir.Length > 0 && (
                    relativeDir.Equals("_blocks", StringComparison.OrdinalIgnoreCase) ||
                    relativeDir.Equals("_filestore", StringComparison.OrdinalIgnoreCase) ||
                    relativeDir.StartsWith("_blocks\\", StringComparison.OrdinalIgnoreCase) ||
                    relativeDir.StartsWith("_filestore\\", StringComparison.OrdinalIgnoreCase)))
                continue;

            // --- Enumerate files in this directory ---
            FileInfo[]? files = null;
            try
            {
                // Materialise to an array immediately — this makes a single
                // failure point we can guard, rather than the deferred
                // enumeration throwing midway through the foreach.
                files = dir.GetFiles();
            }
            catch (Exception ex)
            {
                directoriesSkipped++;
                System.Diagnostics.Debug.WriteLine(
                    $"WalkDestination: skip files in '{dir.FullName}': {ex.GetType().Name}: {ex.Message}");
            }

            if (files is not null)
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var file = files[i];
                    filesScanned++;
                    if (sw.ElapsedMilliseconds - lastProgressMs >= ProgressUpdateIntervalMs)
                    {
                        lastProgressMs = sw.ElapsedMilliseconds;
                        progress.Report($"Scanning: {filesScanned:N0} files examined — {dir.Name}");
                    }

                    long size;
                    string fileName;
                    try
                    {
                        size = file.Length;
                        fileName = file.Name;
                    }
                    catch (Exception)
                    {
                        // File vanished or became inaccessible between enumeration and stat — skip just this file.
                        continue;
                    }

                    // Skip partial-copy temp files left by an interrupted backup
                    // (DirectoryBackupService.CopyFileAsync writes to "*.lbtmp"
                    // before the atomic rename).  They aren't real backup
                    // content and shouldn't surface as untracked files.
                    if (fileName.EndsWith(".lbtmp", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relativePath = relativeDir.Length == 0
                        ? fileName
                        : relativeDir + "\\" + fileName;

                    // A catalog record's DiscPath for a deduplicated file carries
                    // a ".fileref" / ".dedup" manifest suffix (e.g.
                    // "D\AI\foo.zip.fileref"), while the manifest can later be
                    // MATERIALISED back into a plain, suffix-less file on disk
                    // ("D\AI\foo.zip") whose bytes ARE the referenced content
                    // (DirectoryBackupService.MaterialiseFileRef removes the
                    // manifest and writes the plain file).  So a plain on-disk
                    // file must match not only an exact-path catalog record but
                    // also a "<path>.fileref"/"<path>.dedup" record — otherwise
                    // legitimate, catalog-referenced backup content is wrongly
                    // reported as untracked, and "cleaning" it would delete real
                    // backup data (and it reappears once the worker
                    // re-materialises the reference).  Exact match wins; the
                    // manifest-suffix fallbacks only fire for suffix-less files.
                    if (discPathLookup.TryGetValue(DiscPathKey.From(relativePath), out var state)
                        || discPathLookup.TryGetValue(DiscPathKey.From(relativePath + ".fileref"), out state)
                        || discPathLookup.TryGetValue(DiscPathKey.From(relativePath + ".dedup"), out state))
                    {
                        if (state.HasActive)
                            continue; // Active record exists — file is properly tracked.

                        catalogDeleted.Add((relativePath, size, state.SourcePathWhenAllDeleted));
                    }
                    else
                    {
                        string? reconstructed = TryReconstructSourcePath(relativePath);
                        untracked.Add((relativePath, size, reconstructed));
                    }
                }
            }

            // --- Enumerate subdirectories and push for later traversal ---
            DirectoryInfo[]? subdirs = null;
            try
            {
                subdirs = dir.GetDirectories();
            }
            catch (Exception ex)
            {
                directoriesSkipped++;
                System.Diagnostics.Debug.WriteLine(
                    $"WalkDestination: skip subdirs of '{dir.FullName}': {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // Push in reverse so traversal order matches alphabetical-ish
            // (purely cosmetic — affects only the progress messages).
            for (int i = subdirs.Length - 1; i >= 0; i--)
            {
                var sub = subdirs[i];
                string subRel = relativeDir.Length == 0
                    ? sub.Name
                    : relativeDir + "\\" + sub.Name;
                stack.Push((sub, subRel));
            }
        }

        return (untracked, catalogDeleted, directoriesSkipped, filesScanned);
    }
}

using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Scans source directories according to <see cref="SourceSelection"/> trees
/// and computes diffs against the backup catalog.
/// </summary>
public class FileScanner : IFileScanner
{
    private readonly ICatalogRepository _catalog;

    public FileScanner(ICatalogRepository catalog)
    {
        _catalog = catalog;
    }

    public Task<IReadOnlyList<ScannedFile>> ScanAsync(
        IReadOnlyList<SourceSelection> sources,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        Func<string, bool>? isExcluded = null)
    {
        var results = new List<ScannedFile>();
        int filesFound = 0;
        long totalBytes = 0;

        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested) break;
            ScanNode(source, results, ref filesFound, ref totalBytes, progress, ct, isExcluded);
        }

        return Task.FromResult<IReadOnlyList<ScannedFile>>(results);
    }

    private static void ScanNode(
        SourceSelection node,
        List<ScannedFile> results,
        ref int filesFound,
        ref long totalBytes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct,
        Func<string, bool>? isExcluded = null)
    {
        // Fully excluded — skip this entire subtree.
        if (node.IsSelected == false || ct.IsCancellationRequested)
            return;

        if (node.IsDirectory)
        {
            if (!Directory.Exists(node.Path))
                return;

            progress?.Report(new ScanProgress
            {
                CurrentDirectory = node.Path,
                FilesFound = filesFound,
                TotalBytes = totalBytes,
            });

            // If fully selected (true) or partially selected (null), process children.
            if (node.Children.Count > 0)
            {
                // Has explicit child selections — follow those.
                foreach (var child in node.Children)
                    ScanNode(child, results, ref filesFound, ref totalBytes, progress, ct, isExcluded);

                // For fully-selected directories with AutoIncludeNewSubdirectories,
                // also pick up any children on disk that aren't in the selection tree.
                if (node.IsSelected == true && node.AutoIncludeNewSubdirectories)
                {
                    var knownChildren = new HashSet<string>(
                        node.Children.Select(c => c.Path),
                        StringComparer.OrdinalIgnoreCase);

                    ScanUnlistedEntries(node.Path, knownChildren, results,
                        ref filesFound, ref totalBytes, progress, ct, isExcluded);
                }
            }
            else if (node.IsSelected == true)
            {
                // Fully selected directory with no child overrides — include everything.
                ScanDirectoryRecursive(node.Path, results,
                    ref filesFound, ref totalBytes, progress, ct, isExcluded);
            }
        }
        else
        {
            // It's a file. Include it if selected.
            if (node.IsSelected != false)
                AddFile(node.Path, results, ref filesFound, ref totalBytes, isExcluded);
        }
    }

    /// <summary>
    /// Recursively scan a directory, adding all files.
    /// </summary>
    private static void ScanDirectoryRecursive(
        string directoryPath,
        List<ScannedFile> results,
        ref int filesFound,
        ref long totalBytes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct,
        Func<string, bool>? isExcluded = null)
    {
        if (ct.IsCancellationRequested) return;

        progress?.Report(new ScanProgress
        {
            CurrentDirectory = directoryPath,
            FilesFound = filesFound,
            TotalBytes = totalBytes,
        });

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath))
            {
                if (ct.IsCancellationRequested) return;
                AddFile(filePath, results, ref filesFound, ref totalBytes, isExcluded);

                // Report periodically so large directories don't stall progress.
                if (filesFound % 500 == 0)
                {
                    progress?.Report(new ScanProgress
                    {
                        CurrentDirectory = directoryPath,
                        FilesFound = filesFound,
                        TotalBytes = totalBytes,
                    });
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
            {
                ScanDirectoryRecursive(subDir, results,
                    ref filesFound, ref totalBytes, progress, ct, isExcluded);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access.
        }
        catch (IOException)
        {
            // Skip directories that become unavailable during scan.
        }
    }

    /// <summary>
    /// Scan directory entries that are NOT in <paramref name="knownChildren"/>
    /// (new files/subdirectories added since the selection was configured).
    /// </summary>
    private static void ScanUnlistedEntries(
        string directoryPath,
        HashSet<string> knownChildren,
        List<ScannedFile> results,
        ref int filesFound,
        ref long totalBytes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct,
        Func<string, bool>? isExcluded = null)
    {
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath))
            {
                if (ct.IsCancellationRequested) return;
                if (!knownChildren.Contains(filePath))
                    AddFile(filePath, results, ref filesFound, ref totalBytes, isExcluded);
            }

            foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
            {
                if (ct.IsCancellationRequested) return;
                if (!knownChildren.Contains(subDir))
                {
                    ScanDirectoryRecursive(subDir, results,
                        ref filesFound, ref totalBytes, progress, ct, isExcluded);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static void AddFile(
        string filePath,
        List<ScannedFile> results,
        ref int filesFound,
        ref long totalBytes,
        Func<string, bool>? isExcluded = null)
    {
        try
        {
            // Check exclusion pattern before creating FileInfo for efficiency.
            if (isExcluded is not null && isExcluded(filePath))
                return;

            var info = new FileInfo(filePath);
            if (!info.Exists)
                return;

            results.Add(new ScannedFile
            {
                FullPath = info.FullName,
                SizeBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
            });

            filesFound++;
            totalBytes += info.Length;
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    // ---------------------------------------------------------------
    // Diff
    // ---------------------------------------------------------------

    public async Task<BackupDiff> ComputeDiffAsync(
        IReadOnlyList<ScannedFile> scannedFiles,
        int backupSetId,
        CancellationToken ct = default)
    {
        var newFiles = new List<ScannedFile>();
        var changedFiles = new List<ScannedFile>();

        // Use the lightweight aggregate query instead of loading every
        // FileRecord from every disc.  This returns one small struct per
        // unique source path rather than full FileRecord objects.
        var backedUp = await _catalog.GetLatestVersionInfoAsync(backupSetId, ct);

        // Classify each scanned file.
        var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scanned in scannedFiles)
        {
            if (ct.IsCancellationRequested) break;
            currentPaths.Add(scanned.FullPath);

            if (!backedUp.TryGetValue(scanned.FullPath, out var lastBackup))
            {
                newFiles.Add(scanned);
            }
            else if (scanned.SizeBytes != lastBackup.SizeBytes
                     || scanned.LastWriteUtc > lastBackup.SourceLastWriteUtc)
            {
                changedFiles.Add(scanned);
            }
            // else: unchanged, skip
        }

        // Count files in the catalog that no longer exist on disk.
        // Only the count is used downstream, so build minimal stubs.
        var deletedFiles = backedUp.Keys
            .Where(path => !currentPaths.Contains(path))
            .Select(path => new FileRecord { SourcePath = path })
            .ToList();

        return new BackupDiff
        {
            NewFiles = newFiles,
            ChangedFiles = changedFiles,
            DeletedFiles = deletedFiles,
        };
    }
}

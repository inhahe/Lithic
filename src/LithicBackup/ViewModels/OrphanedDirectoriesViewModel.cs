using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// Shows directories in a backup set that are no longer covered by the
/// current source roots — either because the user removed them or because
/// the directory was deleted from disk.  Also supports scanning for files
/// that match exclusion patterns (e.g. *.log, */.vs/*) so the user can
/// purge them from the catalog.
/// </summary>
public class OrphanedDirectoriesViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly BackupSet _backupSet;
    private bool _isLoading;
    private bool _isPurging;
    private string _summaryText = "Loading...";
    private string _exclusionPatterns = "";

    /// <summary>Cached active files from the catalog, loaded once during init.</summary>
    private List<FileRecord>? _activeFiles;

    private string _purgeStatusText = "";

    /// <summary>Task that completes when initial data loading finishes.</summary>
    private readonly Task _loadTask;

    public event Action? DoneRequested;

    public OrphanedDirectoriesViewModel(ICatalogRepository catalog, BackupSet backupSet)
    {
        _catalog = catalog;
        _backupSet = backupSet;

        Items = [];
        PurgeSelectedCommand = new RelayCommand(_ => PurgeSelected(), _ => !IsPurging && Items.Any(i => i.IsSelected));
        ScanExcludedCommand = new RelayCommand(_ => _ = ScanForExcludedAsync(), _ => !IsLoading && !IsPurging);
        CloseCommand = new RelayCommand(_ => DoneRequested?.Invoke());

        _loadTask = LoadAsync();
    }

    /// <summary>
    /// Await initial data loading. Used by callers that want to show a wait
    /// cursor until the view is ready (e.g. <see cref="MainViewModel"/>).
    /// </summary>
    internal Task WaitForLoadAsync() => _loadTask;

    public ObservableCollection<OrphanedDirectoryItem> Items { get; }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsPurging
    {
        get => _isPurging;
        set => SetProperty(ref _isPurging, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        set => SetProperty(ref _summaryText, value);
    }

    /// <summary>
    /// Comma-separated exclusion patterns. Files in the catalog matching these
    /// patterns are shown as candidates for purging.
    /// </summary>
    public string ExclusionPatterns
    {
        get => _exclusionPatterns;
        set => SetProperty(ref _exclusionPatterns, value);
    }

    /// <summary>
    /// Header checkbox — tristate aggregate of all items.
    /// </summary>
    public bool? IsAllSelected
    {
        get
        {
            if (Items.Count == 0) return false;
            bool allTrue = Items.All(i => i.IsSelected);
            bool allFalse = Items.All(i => !i.IsSelected);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            bool target = value ?? true;
            foreach (var item in Items)
                item.IsSelected = target;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Live progress text shown during purge (e.g. "Purging 3/12: D:\Photos\Old").
    /// </summary>
    public string PurgeStatusText
    {
        get => _purgeStatusText;
        set => SetProperty(ref _purgeStatusText, value);
    }

    public ICommand PurgeSelectedCommand { get; }
    public ICommand ScanExcludedCommand { get; }
    public ICommand CloseCommand { get; }

    // ------------------------------------------------------------------

    private async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            var files = await _catalog.GetAllFilesForBackupSetAsync(_backupSet.Id);
            _activeFiles = files.Where(f => !f.IsDeleted).ToList();
            var sourceRoots = _backupSet.SourceRoots;

            // Group files by parent directory.
            var dirGroups = _activeFiles
                .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                         StringComparer.OrdinalIgnoreCase)
                .ToList();

            var orphaned = new List<OrphanedDirectoryItem>();

            foreach (var group in dirGroups)
            {
                string dir = group.Key;
                bool underSourceRoot = sourceRoots.Any(root =>
                    dir.StartsWith(root, StringComparison.OrdinalIgnoreCase));

                if (!underSourceRoot)
                {
                    orphaned.Add(new OrphanedDirectoryItem
                    {
                        DirectoryPath = dir,
                        Reason = OrphanedReason.RemovedFromSources,
                        FileCount = group.Count(),
                        TotalSizeBytes = group.Sum(f => f.SizeBytes),
                    });
                }
                else if (!Directory.Exists(dir))
                {
                    orphaned.Add(new OrphanedDirectoryItem
                    {
                        DirectoryPath = dir,
                        Reason = OrphanedReason.DeletedFromDisk,
                        FileCount = group.Count(),
                        TotalSizeBytes = group.Sum(f => f.SizeBytes),
                    });
                }
            }

            // Collapse children into their highest orphaned ancestor.
            // Sort shortest paths first so parents come before children.
            orphaned.Sort((a, b) => string.Compare(a.DirectoryPath, b.DirectoryPath, StringComparison.OrdinalIgnoreCase));

            var collapsed = new List<OrphanedDirectoryItem>();
            foreach (var item in orphaned)
            {
                var parent = collapsed.FirstOrDefault(c =>
                    item.DirectoryPath.StartsWith(c.DirectoryPath + "\\", StringComparison.OrdinalIgnoreCase));

                if (parent is not null)
                {
                    // Merge into parent.
                    parent.FileCount += item.FileCount;
                    parent.TotalSizeBytes += item.TotalSizeBytes;
                    // If any child is deleted-from-disk but parent was removed-from-sources,
                    // keep the parent's reason (the root cause is removal).
                }
                else
                {
                    collapsed.Add(item);
                }
            }

            Items.Clear();
            foreach (var item in collapsed.OrderBy(i => i.DirectoryPath, StringComparer.OrdinalIgnoreCase))
            {
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsAllSelected));
                Items.Add(item);
            }

            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            SummaryText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Scan the catalog for files matching the exclusion patterns and add them
    /// to the list as purgeable items.
    /// </summary>
    private async Task ScanForExcludedAsync()
    {
        if (_activeFiles is null)
            return;

        // Remove previous exclusion-pattern items.
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Reason == OrphanedReason.MatchesExclusionPattern)
                Items.RemoveAt(i);
        }

        var patterns = ParsePatterns(ExclusionPatterns);
        if (patterns.Count == 0)
        {
            UpdateSummaryText();
            return;
        }

        var filter = GlobMatcher.CreateFilter(patterns);
        if (filter is null)
        {
            UpdateSummaryText();
            return;
        }

        IsLoading = true;
        SummaryText = "Scanning for excluded files...";

        try
        {
            // Build set of directories already shown (orphaned for other reasons)
            // so we don't double-count files.
            var orphanedDirPrefixes = Items
                .Where(i => i.Reason != OrphanedReason.MatchesExclusionPattern)
                .Select(i => i.DirectoryPath + "\\")
                .ToList();

            // Run the filter on a background thread — could be thousands of files.
            var excluded = await Task.Run(() =>
            {
                return _activeFiles
                    .Where(f =>
                    {
                        // Skip files already covered by an orphaned directory.
                        if (orphanedDirPrefixes.Any(p =>
                            f.SourcePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            return false;

                        return filter(f.SourcePath);
                    })
                    .ToList();
            });

            if (excluded.Count == 0)
            {
                UpdateSummaryText();
                return;
            }

            // Group by parent directory.
            var dirGroups = excluded
                .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                         StringComparer.OrdinalIgnoreCase);

            foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var item = new OrphanedDirectoryItem
                {
                    DirectoryPath = group.Key,
                    Reason = OrphanedReason.MatchesExclusionPattern,
                    FileCount = group.Count(),
                    TotalSizeBytes = group.Sum(f => f.SizeBytes),
                    MatchingSourcePaths = group.Select(f => f.SourcePath).ToList(),
                };
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsAllSelected));
                Items.Add(item);
            }

            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            SummaryText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async void PurgeSelected()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        IsPurging = true;
        SummaryText = "Purging...";
        PurgeStatusText = "";

        try
        {
            // Snapshot data needed by the background thread before leaving
            // the UI thread.  MatchingSourcePaths and DirectoryPath are
            // plain properties, safe to capture.
            var workItems = selected.Select(item => new
            {
                item.DirectoryPath,
                item.Reason,
                item.MatchingSourcePaths,
            }).ToList();

            int backupSetId = _backupSet.Id;
            var progress = new Progress<string>(status => PurgeStatusText = status);

            // Run all DB work on a background thread — the catalog methods
            // are synchronous (ExecuteNonQuery / Task.FromResult) and would
            // otherwise freeze the UI for the entire purge.
            int totalPurged = await Task.Run(() =>
            {
                var tx = _catalog.BeginTransactionAsync().GetAwaiter().GetResult();
                int purged = 0;

                try
                {
                    for (int i = 0; i < workItems.Count; i++)
                    {
                        var wi = workItems[i];

                        var dirName = Path.GetFileName(wi.DirectoryPath.TrimEnd('\\'));
                        ((IProgress<string>)progress).Report(
                            $"Purging {i + 1:N0}/{workItems.Count:N0}: {dirName}");

                        if (wi.Reason == OrphanedReason.MatchesExclusionPattern
                            && wi.MatchingSourcePaths is not null)
                        {
                            purged += _catalog.MarkFilesDeletedBySourcePathsAsync(
                                backupSetId, wi.MatchingSourcePaths).GetAwaiter().GetResult();
                        }
                        else
                        {
                            purged += _catalog.MarkFilesDeletedByDirectoryAsync(
                                backupSetId, wi.DirectoryPath).GetAwaiter().GetResult();
                        }
                    }

                    tx.Complete();
                }
                finally
                {
                    tx.Dispose();
                }

                // Clean the cached active-file list on the same thread
                // (no UI objects touched).
                if (_activeFiles is not null)
                {
                    var purgedPaths = new HashSet<string>(
                        workItems.Where(w => w.MatchingSourcePaths is not null)
                                 .SelectMany(w => w.MatchingSourcePaths!),
                        StringComparer.OrdinalIgnoreCase);
                    var purgedDirs = workItems
                        .Where(w => w.Reason != OrphanedReason.MatchesExclusionPattern)
                        .Select(w => w.DirectoryPath + "\\")
                        .ToList();

                    _activeFiles.RemoveAll(f =>
                        purgedPaths.Contains(f.SourcePath)
                        || purgedDirs.Any(d => f.SourcePath.StartsWith(d, StringComparison.OrdinalIgnoreCase)));
                }

                return purged;
            });

            // Back on the UI thread — update the observable collection.
            foreach (var item in selected)
                Items.Remove(item);

            SummaryText = $"Purged {totalPurged:N0} file record(s). {Items.Count} item{(Items.Count == 1 ? "" : "s")} remaining.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Purge failed: {ex.Message}";
        }
        finally
        {
            PurgeStatusText = "";
            IsPurging = false;
        }
    }

    private void UpdateSummaryText()
    {
        if (Items.Count == 0)
        {
            SummaryText = "No orphaned directories or excluded files found.";
            return;
        }

        int orphanedCount = Items.Count(i => i.Reason != OrphanedReason.MatchesExclusionPattern);
        int excludedCount = Items.Count(i => i.Reason == OrphanedReason.MatchesExclusionPattern);

        var parts = new List<string>();
        if (orphanedCount > 0)
            parts.Add($"{orphanedCount} orphaned director{(orphanedCount == 1 ? "y" : "ies")}");
        if (excludedCount > 0)
        {
            int totalExcludedFiles = Items
                .Where(i => i.Reason == OrphanedReason.MatchesExclusionPattern)
                .Sum(i => i.FileCount);
            parts.Add($"{totalExcludedFiles} excluded file{(totalExcludedFiles == 1 ? "" : "s")} in {excludedCount} director{(excludedCount == 1 ? "y" : "ies")}");
        }

        SummaryText = string.Join(", ", parts) + " found.";
    }

    private static List<string> ParsePatterns(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        return input
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

// ------------------------------------------------------------------

public enum OrphanedReason
{
    /// <summary>The directory still exists on disk but is no longer in the backup sources.</summary>
    RemovedFromSources,

    /// <summary>The directory no longer exists on disk.</summary>
    DeletedFromDisk,

    /// <summary>Files in this directory match an exclusion pattern.</summary>
    MatchesExclusionPattern,
}

/// <summary>
/// One orphaned directory entry in the list.
/// </summary>
public class OrphanedDirectoryItem : ViewModelBase
{
    private bool _isSelected;

    public string DirectoryPath { get; set; } = string.Empty;
    public OrphanedReason Reason { get; set; }
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// For <see cref="OrphanedReason.MatchesExclusionPattern"/> items, the specific
    /// source paths of matching files.  Used for targeted purging (instead of
    /// deleting all files under the directory).
    /// </summary>
    public List<string>? MatchingSourcePaths { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string ReasonText => Reason switch
    {
        OrphanedReason.RemovedFromSources => "Removed from sources",
        OrphanedReason.DeletedFromDisk => "Deleted from disk",
        OrphanedReason.MatchesExclusionPattern => "Matches exclusion pattern",
        _ => "Unknown",
    };

    public string SizeText => FormatBytes(TotalSizeBytes);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }
}
